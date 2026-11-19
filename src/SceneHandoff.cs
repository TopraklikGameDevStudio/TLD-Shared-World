using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Scene hand-over ("bölge devri"): whoever walks into a region loads the newest state of that region,
    /// from whoever has it — the player standing there right now, else the copy the host keeps, else nobody
    /// (then the first one in makes it and shares it).
    ///
    /// The game keeps every region as one packed entry in its in-memory save slot (key = the region's save name).
    /// It writes that entry when a region is left (SaveGameSystem.SaveSceneData) and reads it when a region is
    /// entered (SaveGameSystem.LoadSceneData). We put the newest entry into the slot just before it is read.
    /// On any doubt (switched off, no answer in time, error) nothing is touched and the game loads as in 0.5.5.
    /// </summary>
    internal sealed class SceneHandoff
    {
        internal static SceneHandoff Instance;
        /// <summary>Test switch: 0 = follow the setting, 1 = on, 2 = off.</summary>
        internal static int TestOverride;

        private readonly Network _net;
        private readonly Func<bool> _shared, _pref;

        // blob kinds on the wire
        private const byte KStored = 0, KLive = 1, KLeft = 2, KSaved = 3, KMade = 4, KLiveReply = 5;
        // answers to an ask
        private const byte VNone = 0, VWait = 1, VOff = 2, VComing = 3, VSame = 4;
        // blob flag: the copy contains every change sent before it, so changes still waiting for that region can be dropped
        private const byte FComplete = 1;
        private const int MaxBlob = 16 * 1024 * 1024;
        // all waits of one change of region together never hold the game longer than this (seconds)
        private const float DoorBudget = 30f;
        // a friend's promise to make a region holds while that friend is connected and still on its way in:
        // at most ClaimMax seconds in all, and ClaimGrace seconds after it said it has arrived
        private const float OwnClaimMax = 45f, ClaimMax = 180f, ClaimGrace = 20f;
        // after "I cannot give it" from the player who stood there: time for that player's last save to get here
        private const float LeftGrace = 3f;

        // ---- where this game is ----
        private string _hereKey, _hereScene;   // fully inside this region: may be asked for its state
        private string _hereSentKey;           // what the host was last told (client)
        private string _lastKey;               // newest save name the game itself used for a region
        private sealed class Transit
        {
            public string SceneName, GuessKey, Key, OldKey, OldScene;
            public float StartedAt, PrefixDoneAt, LoadSeenAt = -1f;
            public float Waited;   // seconds this change of region has held the game so far (all waits together)
            public bool LeaveSaved, LeftSent, ToMenu, Restored;
            public readonly HashSet<string> Loaded = new();
        }
        private Transit _transit;
        private bool _atDoor;                  // inside the game's "leave this region" call, before anything of the old region is taken down

        // ---- questions this game asked for the region it is entering ----
        private sealed class Ask
        {
            public string Key; public int Id; public bool Done, Got, Coming, Waiting, TimedOut; public int Bytes; public byte Source; public byte Kind; public long WaitMs;
            public bool ReadFirst;  // closed only because the game read the region before any answer came
            public int Stamp0;      // host: the region's arrival counter when this game first looked
            public string Outcome = "";
        }
        private readonly Dictionary<string, Ask> _asks = new();
        private readonly HashSet<string> _made = new(); // regions this game generated itself and still has to share
        private int _nextAskId = 1;

        // ---- host: who is where, who is making what, who waits for what ----
        private sealed class Where { public string Key = "", Scene = ""; }
        private readonly Dictionary<byte, Where> _where = new();
        private sealed class Claim { public byte By; public float At; public float ArrivedAt = -1f; }
        private readonly Dictionary<string, Claim> _claims = new();
        private readonly Dictionary<string, List<(byte Id, int AskId)>> _waiters = new();
        private readonly Dictionary<string, (int Bytes, byte From, byte Kind, float At, string Scene)> _storeInfo = new();
        private readonly Dictionary<string, int> _stamp = new();   // goes up each time a region copy arrived at the host
        private readonly Dictionary<string, float> _liveNo = new();
        private readonly Dictionary<string, float> _resolveAt = new(); // waiting friends to be served a little later
        private int _nextReq = 1;
        private float _nextHostTick;

        // ---- saves the game made (noted by the patch, handled in Update on the main thread) ----
        private readonly ConcurrentQueue<string> _saved = new();
        private bool _selfSaving;
        private readonly Dictionary<string, float> _lastUpload = new();
        private readonly Dictionary<string, (float At, byte[] Data)> _liveCache = new();

        internal string LastDecision = "nothing yet";
        private bool _loggedAdditive, _loggedOtherKey;

        public SceneHandoff(Network net, Func<bool> worldShared, Func<bool> pref)
        {
            Instance = this;
            _net = net; _shared = worldShared; _pref = pref;
            _net.OnMessage += OnMessage;
            _net.OnRemoteLeft += OnRemoteLeft;
            _net.OnStopped += () =>
            {
                _where.Clear(); _claims.Clear(); _waiters.Clear(); _stamp.Clear(); _liveNo.Clear(); _storeInfo.Clear(); _resolveAt.Clear();
                _asks.Clear(); _made.Clear(); _hereSentKey = null; _lastUpload.Clear(); _liveCache.Clear();
            };
        }

        private bool _canInject, _canSeeSaves;

        /// <summary>Hook the game's region save/load. Each hook on its own; without the load hook the hand-over stays off.</summary>
        internal void ApplyPatches(HarmonyLib.Harmony h)
        {
            bool Try(string what, Action patch)
            {
                try { patch(); return true; }
                catch (Exception e) { CoopMod.Log($"Scene hand-over: could not hook {what}: {e.Message}"); return false; }
            }
            var t = typeof(HandoffPatches);
            _canInject = Try("SaveGameSystem.LoadSceneData", () => h.Patch(
                AccessTools.Method(typeof(SaveGameSystem), nameof(SaveGameSystem.LoadSceneData), new[] { typeof(string), typeof(string) }),
                prefix: new HarmonyMethod(t, nameof(HandoffPatches.LoadSceneDataPrefix)),
                postfix: new HarmonyMethod(t, nameof(HandoffPatches.LoadSceneDataPostfix))));
            _canSeeSaves = Try("SaveGameSystem.SaveSceneData", () => h.Patch(
                AccessTools.Method(typeof(SaveGameSystem), nameof(SaveGameSystem.SaveSceneData), new[] { typeof(SlotData), typeof(string) }),
                postfix: new HarmonyMethod(t, nameof(HandoffPatches.SaveSceneDataPostfix))));
            Try("GameManager.LoadScene", () => h.Patch(
                AccessTools.Method(typeof(GameManager), nameof(GameManager.LoadScene), new[] { typeof(string), typeof(string) }),
                prefix: new HarmonyMethod(t, nameof(HandoffPatches.LoadScenePrefix))));
            Try("SaveGameSystem.LoadSceneDataAdditive", () => h.Patch(
                AccessTools.Method(typeof(SaveGameSystem), nameof(SaveGameSystem.LoadSceneDataAdditive), new[] { typeof(string), typeof(string) }),
                prefix: new HarmonyMethod(t, nameof(HandoffPatches.LoadSceneDataAdditivePrefix))));
            CoopMod.Log($"Scene hand-over: hooks in place (load {_canInject}, save {_canSeeSaves}), setting {(SafePref() ? "on" : "off")}");
        }

        private static float Now => Time.realtimeSinceStartup;
        private bool Wanted => TestOverride == 1 || (TestOverride == 0 && SafePref());
        private bool SafePref() { try { return _pref(); } catch { return true; } }
        private bool SafeShared() { try { return _shared(); } catch { return false; } }
        /// <summary>Hand-over is in use: switched on, connected, and this game plays the shared world.</summary>
        private bool Enabled
        {
            get
            {
                if (!_canInject || !Wanted || !_net.IsConnected || !SafeShared()) return false;
                if (_net.Mode == NetMode.Host) return true;
                try { return CoopMod.Instance.Transfer.ClientInCoopWorld; } catch { return false; }
            }
        }
        private static bool IsSceneMsg(Msg m) => m >= Msg.SceneAsk && m <= Msg.SceneLiveNo;

        // =====================================================================================
        // slot access
        // =====================================================================================
        private static SlotData CurrentSlot()
        {
            try { var n = SaveGameSystem.GetCurrentSaveName(); return string.IsNullOrEmpty(n) ? null : SaveGameSlots.GetSaveSlotFromName(n); }
            catch { return null; }
        }

        // While a game runs, the game keeps the live copy of every region in a working slot ("autosave"): a door saves the
        // region being left into it and reads the next one from it; the named save only gets a copy at real save points.
        // (Seen in 0.6.1: a copy put into the named save was never read.) So that slot is the one to read and to write.
        private static volatile string _gameSlotName; // the slot the game itself last read a region from / saved one to
        private static SlotData SlotByName(string n)
        {
            try
            {
                if (string.IsNullOrEmpty(n)) return null;
                var s = SaveGameSlots.GetSaveSlotFromName(n);
                return s != null && s.m_Dict != null && s.m_Dict.Count > 0 ? s : null;
            }
            catch { return null; }
        }
        private static SlotData WorkSlot()
        {
            var s = SlotByName(_gameSlotName);
            if (s != null) return s;
            string auto = null; try { auto = SaveGameSlots.AUTOSAVE_SLOT_NAME; } catch { }
            s = SlotByName(auto);
            if (s != null) return s;
            try { var a = SaveGameSlots.m_AutoSaveSlot; if (a != null && a.m_Dict != null && a.m_Dict.Count > 0) return a; } catch { }
            return CurrentSlot();
        }
        internal static string WorkSlotName() { try { return WorkSlot()?.m_InternalName ?? "-"; } catch { return "?"; } }

        /// <summary>Put a copy into the working slot and into the game's other copies of this save (autosave and the named
        /// save), so it is found whichever of them the game reads or copies from next.</summary>
        private static bool WriteEverywhere(string key, byte[] data)
        {
            bool any = false; var seen = new HashSet<IntPtr>();
            string auto = null; try { auto = SaveGameSlots.AUTOSAVE_SLOT_NAME; } catch { }
            foreach (var slot in new[] { WorkSlot(), SlotByName(auto), CurrentSlot() })
            {
                if (slot == null) continue;
                IntPtr ptr; try { ptr = slot.Pointer; } catch { continue; }
                if (!seen.Add(ptr)) continue;
                any |= WriteBlob(slot, key, data);
            }
            return any;
        }

        // copies put in during this change of region, by key: put in again if the game turns out to read another slot
        private readonly Dictionary<string, byte[]> _injected = new();

        private static bool SlotHas(SlotData slot, string key)
        {
            try { return slot != null && !string.IsNullOrEmpty(key) && slot.m_Dict != null && slot.m_Dict.ContainsKey(key); } catch { return false; }
        }

        private static byte[] ReadBlob(SlotData slot, string key)
        {
            if (slot == null || string.IsNullOrEmpty(key)) return null;
            bool locked = false;
            try { slot.Lock(); locked = true; } catch { }
            try
            {
                var d = slot.m_Dict;
                if (d == null || !d.ContainsKey(key)) return null;
                Il2CppStructArray<byte> arr = d[key];
                if (arr == null) return null;
                byte[] copy = arr;
                return copy;
            }
            catch (Exception e) { CoopMod.Log($"Scene {key}: could not read it from the save slot: {e.Message}"); return null; }
            finally { if (locked) { try { slot.UnLock(); } catch { } } }
        }

        /// <summary>Put a region copy into the game's in-memory slot (the game reads regions from there, not from the file).</summary>
        private static bool WriteBlob(SlotData slot, string key, byte[] data)
        {
            if (slot == null || string.IsNullOrEmpty(key) || data == null || data.Length == 0) return false;
            Il2CppStructArray<byte> arr;
            try { arr = new Il2CppStructArray<byte>(data); }
            catch (Exception e) { CoopMod.Log($"Scene {key}: could not prepare the copy: {e.Message}"); return false; }
            bool locked = false;
            try { slot.Lock(); locked = true; } catch { }
            try { slot.m_Dict[key] = arr; return true; }
            catch (Exception e) { CoopMod.Log($"Scene {key}: could not put the copy into the save slot: {e.Message}"); return false; }
            finally { if (locked) { try { slot.UnLock(); } catch { } } }
        }

        /// <summary>Why the region cannot be touched right now (null = it can).</summary>
        private static string BusyReason(bool countLoading = true)
        {
            try { if (SaveGameSystem.IsRestoreInProgress()) return "a save is being restored"; } catch { }
            try { if (SaveGameSystem.IsSceneRestoreInProgress()) return "the region is being restored"; } catch { }
            if (countLoading) { try { if (Il2Cpp.SceneManager.IsLoading()) return "a region is still loading"; } catch { } }
            return null;
        }
        private static bool SceneBusy(bool countLoading = true) => BusyReason(countLoading) != null;
        private string _refusal = "";

        /// <summary>Have the game write the region we stand in into the slot right now, and return that entry.</summary>
        private byte[] SerializeNow(string key)
        {
            _refusal = "";
            if (string.IsNullOrEmpty(key) || _hereKey != key) { _refusal = $"this game stands in '{_hereKey ?? "-"}'"; return null; }
            if (!WorldSync.InGame()) { _refusal = "not in the game world"; return null; }
            // on the way to another region the old one is being taken down: having the game write it out now would
            // store an empty region. Only at the door itself (BeforeLoadScene, the game has not started leaving) is it whole.
            if (_transit != null && !_atDoor) { _refusal = "already on the way out"; return null; }
            if (_liveCache.TryGetValue(key, out var c) && Now - c.At < 1f) return c.Data;
            var busy = BusyReason();
            if (busy != null) { _refusal = busy; return null; }
            var slot = WorkSlot();
            if (slot == null) { _refusal = "no save slot"; return null; }
            // same lock the game's background save writer uses (taking it twice on one thread is fine)
            bool locked = false;
            try { slot.Lock(); locked = true; } catch { }
            _selfSaving = true;
            try { SaveGameSystem.SaveSceneData(slot, key); }
            catch (Exception e) { CoopMod.Log($"Scene {key}: the game could not write it out right now: {e.Message}"); return null; }
            finally { _selfSaving = false; if (locked) { try { slot.UnLock(); } catch { } } }
            var data = ReadBlob(slot, key);
            if (data != null) _liveCache[key] = (Now, data);
            Dump("liveout", key, data);
            return data;
        }

        internal static bool DumpOn; // harness command "blobs on": keep every region copy on disk for looking inside
        private float _recheckAt = -1f; private string _recheckKey;
        // test builds: the loose stones of the world with their ids (stones are what the tests drop)
        private static string StoneList()
        {
            var sb = new System.Text.StringBuilder(); int n = 0;
            try
            {
                foreach (var g in UnityEngine.Object.FindObjectsOfType<GearItem>())
                {
                    try
                    {
                        if (g == null || g.m_InPlayerInventory || g.IsInsideContainer() || ItemSync.PrefabName(g.gameObject) != "GEAR_Stone") continue;
                        if (++n > 12) continue;
                        var p = g.transform.position; string id = null;
                        try { id = ObjectGuid.MaybeGetGuidFromGameObject(g.gameObject); } catch { }
                        sb.Append($" [{p.x:0.0},{p.y:0.0},{p.z:0.0} {(string.IsNullOrEmpty(id) ? "-" : id.Substring(0, Math.Min(8, id.Length)))} active={g.gameObject.activeInHierarchy} scene={g.gameObject.scene.name}]");
                    }
                    catch { }
                }
            }
            catch { }
            return sb.ToString();
        }

        // test builds: keep a copy of every region blob that goes out, comes in or is about to be read, for looking inside later
        private static void Dump(string tag, string key, byte[] data)
        {
            if (!DumpOn || !CoopMod.AllowVersionMismatch || data == null || data.Length == 0) return; // test mode, and only when asked for
            try
            {
                var dir = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "TLDCoop-blobs");
                System.IO.Directory.CreateDirectory(dir);
                string safe = key ?? "none"; foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
                var name = $"{DateTime.Now:HHmmss-fff}-{Environment.ProcessId}-{tag}-{safe}-{Hash(data):x8}.bin";
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name), data);
                CoopMod.Log($"Blob {tag} {key}: {data.Length} bytes, hash {Hash(data):x8}, kept as {name}");
            }
            catch (Exception e) { CoopMod.Log("Blob dump: " + e.Message); }
        }

        private static uint Hash(byte[] d)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < d.Length; i++) { a = (a + d[i]) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }

        private string CurrentKey()
        {
            if (!string.IsNullOrEmpty(_lastKey)) return _lastKey;
            try { var k = GameManager.m_SceneTransitionData?.m_SceneSaveFilenameCurrent; if (!string.IsNullOrEmpty(k)) return k; } catch { }
            return CoopMod.CurrentScene();
        }

        private static bool IsMenuScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return true;
            try { if (GameManager.MatchesMainMenuSceneName(sceneName)) return true; } catch { }
            return sceneName == "Empty" || sceneName == "Boot";
        }

        // =====================================================================================
        // waiting inside the game's own loading code: keep the network alive, handle only hand-over messages
        // =====================================================================================
        private bool Pump(Func<bool> done, Func<float> timeout)
        {
            // what this change of region may still wait in all (outside one, or once that is used up: one look, no waiting)
            var t = _transit;
            float left = t != null ? DoorBudget - t.Waited : 0f;
            var sw = Stopwatch.StartNew();
            var before = _net.OnlyNow;
            _net.OnlyNow = IsSceneMsg;
            try
            {
                while (true)
                {
                    if (done()) return true;
                    if (!_net.IsConnected) return false;
                    _net.PollNow();
                    if (_net.Mode == NetMode.Host) { try { HostDelayed(Now); } catch { } }
                    if (done()) return true;
                    if (sw.Elapsed.TotalSeconds >= Math.Min(timeout(), left)) return false;
                    System.Threading.Thread.Sleep(3);
                }
            }
            finally
            {
                _net.OnlyNow = before;
                if (t != null) t.Waited += (float)sw.Elapsed.TotalSeconds;
            }
        }

        // =====================================================================================
        // game hooks (called from the patches at the bottom)
        // =====================================================================================
        /// <summary>The game is about to leave the current region for another one.</summary>
        internal void BeforeLoadScene(string sceneName, string currentKey)
        {
            try
            {
                if (!string.IsNullOrEmpty(currentKey) && WorldSync.InGame() && _transit == null) _lastKey = currentKey;
                if (!IsMenuScene(sceneName)) AnimalSync.HoldAttachUntil = Time.realtimeSinceStartup + 40f;
                // the game may call this more than once for one change of region: the first call counts
                if (_transit != null && !_transit.ToMenu && _transit.SceneName == sceneName && Now - _transit.StartedAt < 60f && _transit.LoadSeenAt < 0f) return;
                // (only a door taken in normal play finds the old region whole: a change already under way has taken it down)
                bool playing = _transit == null;
                var t = new Transit
                {
                    SceneName = sceneName, StartedAt = Now, PrefixDoneAt = Now, ToMenu = IsMenuScene(sceneName),
                    OldKey = _hereKey ?? currentKey, OldScene = _hereScene ?? CoopMod.CurrentScene(),
                };
                _transit = t;
                _asks.Clear(); _made.Clear(); _injected.Clear();
                if (t.ToMenu) return;
                string guess = null;
                try { guess = GameManager.m_SceneTransitionData?.m_SceneSaveFilenameNextLoad; } catch { }
                // (if the game's note still names the region being left, the region's own name is the better guess)
                if (string.IsNullOrEmpty(guess) || guess == t.OldKey) guess = sceneName;
                t.GuessKey = guess;
                if (!Enabled || guess == t.OldKey) return;
                _atDoor = playing;
                try { Resolve(guess, sceneName); }
                finally { _atDoor = false; t.PrefixDoneAt = Now; }
            }
            catch (Exception e) { HookError("before leaving", e); }
        }

        /// <summary>The game is about to read a region's saved state from the slot.</summary>
        internal void BeforeLoadSceneData(string slotName, string key, bool additive)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) return;
                if (additive)
                {
                    if (!_loggedAdditive) { _loggedAdditive = true; CoopMod.Log($"Scene {key}: the game also reads an add-on part under this name"); }
                    if (_transit == null) return; // only on the way in; never stall normal play
                }
                else
                {
                    _lastKey = key;
                    if (_transit == null) _transit = new Transit { GuessKey = key, StartedAt = Now, OldKey = _hereKey };
                    _transit.Key = key;
                    if (!string.IsNullOrEmpty(_transit.GuessKey) && _transit.GuessKey != key)
                    {
                        // the game keeps this region under another name than expected: what was asked for the guess does not count
                        CoopMod.Log($"Scene {key}: the game keeps it under this name (expected '{_transit.GuessKey}')");
                        _made.Remove(_transit.GuessKey);
                        if (_net.Mode == NetMode.Host)
                        {
                            if (_claims.TryGetValue(_transit.GuessKey, out var gc) && gc.By == Protocol.HostId) _claims.Remove(_transit.GuessKey);
                            ResolveWaiters(_transit.GuessKey);
                        }
                    }
                    // the old region is gone by now, whatever happened to its last save
                    if (_hereKey != null && _hereKey != key) SetNotHere();
                }
                if (!string.IsNullOrEmpty(slotName)) _gameSlotName = slotName;
                if (!Enabled) return;
                string cur = null; try { cur = SaveGameSystem.GetCurrentSaveName(); } catch { }
                // the copy that came for this region must sit in the very slot the game is about to read
                try
                {
                    if (_injected.TryGetValue(key, out var want) && want != null)
                    {
                        var gs = SlotByName(slotName) ?? WorkSlot();
                        var has = ReadBlob(gs, key);
                        if (has == null || has.Length != want.Length || Hash(has) != Hash(want))
                        {
                            bool ok = WriteBlob(gs, key, want);
                            CoopMod.Log($"Scene {key}: the game reads it from slot '{slotName}', put the copy there too ({(ok ? "done" : "FAILED")})");
                        }
                    }
                }
                catch (Exception e) { CoopMod.Log("Scene copy into the game's slot: " + e.Message); }
                if (!additive && CoopMod.AllowVersionMismatch && DumpOn)
                {
                    // test builds: what exactly is the game about to read, and from which slot object
                    try
                    {
                        int same = 0; var all = SaveGameSlots.m_SaveSlots;
                        for (int i = 0; i < all.Count; i++) { var sd = all[i]; if (sd != null && sd.m_InternalName == slotName) same++; }
                        var gameSlot = SaveGameSlots.GetSaveSlotFromName(slotName); var mine = WorkSlot();
                        CoopMod.Log($"Scene {key}: the game reads it from slot '{slotName}' (current save '{cur}', {same} slot(s) with that name, same object as ours: {gameSlot != null && mine != null && gameSlot.Pointer == mine.Pointer})");
                        Dump("preread", key, ReadBlob(gameSlot, key));
                    }
                    catch (Exception e) { CoopMod.Log("Pre-read check: " + e.Message); }
                }
                Resolve(key, _transit.SceneName ?? CoopMod.CurrentScene() ?? key);
            }
            catch (Exception e) { HookError("before reading a region", e); }
        }

        internal void AfterLoadSceneData(string key, bool restored)
        {
            try
            {
                AnimalSync.HoldAttachUntil = Time.realtimeSinceStartup + 0.5f; // the region stands: animals may be made now
                if (_transit == null || string.IsNullOrEmpty(key)) return;
                _injected.Remove(key);
                _transit.Loaded.Add(key);
                _asks.TryGetValue(key, out var a);
                if (_transit.Key == key || _transit.Key == null)
                {
                    _transit.LoadSeenAt = Now; _transit.Restored = restored;
                    if (a != null)
                    {
                        LastDecision = $"{key}: {a.Outcome}; the game restored it from saved data: {restored}";
                        CoopMod.Log($"Scene {key}: the game restored it from saved data: {restored}");
                        // a copy was put in and the game still found nothing: the region was made fresh here after all
                        if (a.Got && !restored) CoopMod.Log($"Scene {key}: WARNING the copy that was put in was not used by the game");
                    }
                }
                // the game has read it: a copy that comes now is too late, so nothing waits for one any more
                if (a != null && !a.Done) { a.Done = true; a.ReadFirst = true; }
                if (CoopMod.AllowVersionMismatch && DumpOn)
                {
                    try
                    {
                        var b = ReadBlob(WorkSlot(), key);
                        int gear = 0, stones = 0;
                        foreach (var g in UnityEngine.Object.FindObjectsOfType<GearItem>())
                        {
                            try { if (g == null || g.m_InPlayerInventory || g.IsInsideContainer()) continue; gear++; if (ItemSync.PrefabName(g.gameObject) == "GEAR_Stone") stones++; } catch { }
                        }
                        CoopMod.Log($"Scene {key}: right after the read the slot holds hash {(b == null ? "none" : Hash(b).ToString("x8"))}; loose items in the world now: {gear} ({stones} stones){StoneList()}");
                        _recheckAt = Now + 4f; _recheckKey = key;
                    }
                    catch (Exception e) { CoopMod.Log("Post-read check: " + e.Message); }
                }
            }
            catch (Exception e) { HookError("after reading a region", e); }
        }

        /// <summary>The game wrote a region into the slot (any thread): look at it on the next update.</summary>
        internal void AfterSaveSceneData(string key, string slotName)
        {
            if (_selfSaving || string.IsNullOrEmpty(key)) return;
            // (which slot a save goes to says nothing about where the game reads from: only reads set the working slot)
            if (!string.IsNullOrEmpty(slotName)) _savedTo[key] = slotName;
            _saved.Enqueue(key);
        }
        private readonly ConcurrentDictionary<string, string> _savedTo = new(); // region -> the slot the game last wrote it to
        private SlotData SavedSlot(string key) => (_savedTo.TryGetValue(key, out var n) ? SlotByName(n) : null) ?? WorkSlot();

        private void HookError(string where, Exception e)
        {
            if (_hookErrors >= 6) return;
            _hookErrors++;
            CoopMod.Log($"Scene hand-over ({where}) failed, loading as before: {(_hookErrors == 1 ? e.ToString() : e.Message)}");
        }
        private int _hookErrors;

        // =====================================================================================
        // entering a region: get the newest copy into the slot before the game reads it
        // =====================================================================================
        private void Resolve(string key, string sceneName)
        {
            if (_asks.TryGetValue(key, out var old) && old.Done) return;
            if (_net.Mode == NetMode.Host) HostResolve(key, sceneName);
            else ClientResolve(key, sceneName);
        }

        private void ClientResolve(string key, string sceneName)
        {
            if (!_asks.TryGetValue(key, out var ask))
            {
                ask = new Ask { Key = key, Id = _nextAskId++ };
                _asks[key] = ask;
                uint have = 0;
                try { var mine = ReadBlob(WorkSlot(), key); if (mine != null) have = Hash(mine); } catch { }
                var w = _net.Begin(Msg.SceneAsk); w.Put(ask.Id); w.Put(key); w.Put(sceneName ?? ""); w.Put(have);
                _net.Send(w, DeliveryMethod.ReliableOrdered);
                CoopMod.Log($"Scene {key}: asked host");
            }
            var sw = Stopwatch.StartNew();
            Pump(() => ask.Done, () => ask.Waiting ? 25f : ask.Coming ? 20f : 6f);
            ask.WaitMs += sw.ElapsedMilliseconds;
            if (!ask.Done)
            {
                // not closed: an answer that still arrives before the game reads the region is used,
                // and the last moment (just before that read) waits once more
                ask.TimedOut = true;
                ask.Outcome = $"timeout after {ask.WaitMs} ms: loading as before";
                CoopMod.Log($"Scene {key}: {ask.Outcome}");
            }
            LastDecision = $"{key}: {ask.Outcome}";
        }

        private void HostResolve(string key, string sceneName)
        {
            // (an ask left open by an earlier look of this change of region is taken up again)
            if (!_asks.TryGetValue(key, out var ask))
            {
                ask = new Ask { Key = key, Id = 0, Stamp0 = Stamp(key) };
                _asks[key] = ask;
            }
            var slot = WorkSlot();
            var sw = Stopwatch.StartNew();
            int stamp0 = Stamp(key);

            // a friend stands in that region right now: take its current state
            byte there = 255;
            foreach (var kv in _where) if (kv.Value.Key == key && _net.Remotes.ContainsKey(kv.Key)) { there = kv.Key; break; }
            if (there != 255)
            {
                _liveNo.Remove(key);
                var w = _net.Begin(Msg.SceneLive); w.Put(_nextReq++); w.Put(key);
                _net.SendTo(there, w, DeliveryMethod.ReliableOrdered);
                CoopMod.Log($"Scene {key}: player {there} is there now, asked for its current state");
                // (a player who leaves the game meanwhile will not send anything: do not wait for it)
                Pump(() => Stamp(key) != stamp0 || !_net.Remotes.ContainsKey(there)
                           || (_liveNo.TryGetValue(key, out var no) && Now - no > LeftGrace), () => 10f);
                ask.WaitMs += sw.ElapsedMilliseconds;
                ask.Got = Stamp(key) != stamp0;
                ask.Outcome = ask.Got
                    ? $"got {Kb(InfoBytes(key))} KB from player {there} (live) after {ask.WaitMs} ms"
                    : !_net.Remotes.ContainsKey(there)
                        ? $"player {there} left the game before sending it: loading this game's own copy as before"
                        : $"player {there} did not send it within {ask.WaitMs} ms: loading this game's own copy as before";
            }
            else if (_claims.TryGetValue(key, out var claim) && claim.By != Protocol.HostId && ClaimAlive(claim, Now))
            {
                // a friend entered it first and is generating it this very moment: wait for that copy
                byte by = claim.By;
                CoopMod.Log($"Scene {key}: player {by} is making it right now, waiting for that copy");
                Pump(() => Stamp(key) != stamp0 || !ClaimHeld(key), () => 25f);
                ask.WaitMs += sw.ElapsedMilliseconds;
                ask.Got = Stamp(key) != stamp0;
                if (!ask.Got && ClaimHeld(key))
                {
                    // that friend is still on its way in: its promise stands, and this ask stays open, so a copy that
                    // comes while this game loads is still used and the last moment before the read looks once more
                    ask.TimedOut = true;
                    ask.Outcome = $"player {by} is still making it after {ask.WaitMs} ms: going on, and looking once more just before the game reads it";
                    CoopMod.Log($"Scene {key}: {ask.Outcome}");
                    LastDecision = $"{key}: {ask.Outcome}";
                    return;
                }
                ask.Outcome = ask.Got
                    ? $"got {Kb(InfoBytes(key))} KB from player {by} (first one in) after {ask.WaitMs} ms"
                    : $"player {by} did not deliver within {ask.WaitMs} ms: loading as before";
            }
            else if (SlotHas(slot, key))
            {
                // (a copy that came in after this game's first look, while it was loading, is already in the slot)
                if (Stamp(key) != ask.Stamp0 && _storeInfo.TryGetValue(key, out var si))
                {
                    ask.Got = true;
                    ask.Outcome = $"got {Kb(si.Bytes)} KB from player {si.From} ({KindName(si.Kind)}) while this game was loading";
                }
                else ask.Outcome = "this game holds the newest copy";
            }
            else
            {
                _claims[key] = NewClaim(key, Protocol.HostId);
                _made.Add(key);
                ask.Outcome = "nobody had it: this game makes it and shares it";
            }
            ask.Done = true;
            CoopMod.Log($"Scene {key}: {ask.Outcome}");
            LastDecision = $"{key}: {ask.Outcome}";
        }

        private int Stamp(string key) => _stamp.TryGetValue(key, out var s) ? s : 0;

        private Claim NewClaim(string key, byte by, bool mayBeThere = false)
        {
            var c = new Claim { By = by, At = Now };
            // (a friend who gave up waiting and already stands there is not on its way in any more)
            if (mayBeThere && by != Protocol.HostId && _where.TryGetValue(by, out var w) && w.Key == key) c.ArrivedAt = c.At;
            return c;
        }

        /// <summary>
        /// Is this region still being made? This game's own promise holds 45 s as before. A friend's promise holds
        /// while that friend is connected and still on its way in (however long its loading takes, up to ClaimMax),
        /// and a short while after it said it has arrived (its copy is sent at that moment).
        /// </summary>
        private bool ClaimAlive(Claim c, float now)
        {
            if (c.By == Protocol.HostId) return now - c.At < OwnClaimMax;
            if (!_net.Remotes.ContainsKey(c.By)) return false;
            if (c.ArrivedAt >= 0f) return now - c.ArrivedAt < ClaimGrace;
            return now - c.At < ClaimMax;
        }
        private bool ClaimHeld(string key) => _claims.TryGetValue(key, out var c) && ClaimAlive(c, Now);
        private int InfoBytes(string key) => _storeInfo.TryGetValue(key, out var i) ? i.Bytes : 0;
        private static string Kb(int bytes) => (bytes / 1024f).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        private static string KindName(byte k) => k switch { KStored => "stored", KLive => "live", KLeft => "left", KSaved => "saved", KMade => "made", KLiveReply => "live", _ => "?" };

        /// <summary>A copy arrived for this game to load: put it into the slot and forget changes it already contains.</summary>
        private bool Inject(string key, string scene, byte[] data, string what, bool complete = true)
        {
            var slot = WorkSlot();
            Dump("before-inject", key, ReadBlob(slot, key));
            if (!WriteEverywhere(key, data)) return false;
            _injected[key] = data;
            Dump("inject", key, data);
            if (!complete) return true; // an older copy: changes waiting for this region still apply on top of it
            int dropped = 0;
            try
            {
                var items = CoopMod.Instance.Items;
                if (items != null)
                {
                    if (!string.IsNullOrEmpty(scene)) dropped += items.DropQueued(scene);
                    if (key != scene) dropped += items.DropQueued(key);
                }
                // changes that reached us before this copy but are still waiting behind it
                if (!string.IsNullOrEmpty(scene)) dropped += _net.MarkHeldStale(scene);
            }
            catch { }
            if (dropped > 0) CoopMod.Log($"Scene {key}: {dropped} waiting change(s) are already inside {what} and were dropped");
            return true;
        }

        // =====================================================================================
        // sending
        // =====================================================================================
        private void SendBlob(byte to, byte kind, int askId, byte source, string key, string scene, byte[] data, byte flags = FComplete)
        {
            var w = _net.Begin(Msg.SceneBlob);
            w.Put(kind); w.Put(flags); w.Put(askId); w.Put(source); w.Put(key ?? ""); w.Put(scene ?? ""); w.Put(data.Length); w.Put(data);
            _net.SendTo(to, w, DeliveryMethod.ReliableOrdered);
        }

        private void Answer(byte to, int askId, string key, byte verdict, int bytes = 0)
        {
            var w = _net.Begin(Msg.SceneAnswer); w.Put(askId); w.Put(key); w.Put(verdict); w.Put(bytes);
            _net.SendTo(to, w, DeliveryMethod.ReliableOrdered);
        }

        private void SendHere(string key, string scene)
        {
            if (_net.Mode != NetMode.Client) return;
            var w = _net.Begin(Msg.SceneHere); w.Put(key ?? ""); w.Put(scene ?? "");
            _net.Send(w, DeliveryMethod.ReliableOrdered);
            _hereSentKey = key ?? "";
        }

        private void SetNotHere()
        {
            _hereKey = null; _hereScene = null;
            _liveCache.Clear();
            if (_net.Mode == NetMode.Client && _net.IsConnected && _hereSentKey != "") SendHere("", "");
        }

        private static bool HostInScene(string scene)
        {
            try
            {
                return !string.IsNullOrEmpty(scene) && CoopMod.Instance.Players.TryGetState(Protocol.HostId, out var s, out _) && s == scene;
            }
            catch { return false; }
        }

        // =====================================================================================
        // every frame
        // =====================================================================================
        public void Update()
        {
            float now = Now;
            if (_recheckAt > 0f && now >= _recheckAt)
            {
                _recheckAt = -1f;
                try { CoopMod.Log($"Scene {_recheckKey}: four seconds after the read, loose stones:{StoneList()}"); } catch { }
            }
            try { HandleSaved(now); } catch (Exception e) { HookError("after a save", e); }
            try { TrackPresence(now); } catch (Exception e) { HookError("presence", e); }
            if (_net.Mode == NetMode.Host)
            {
                try { HostDelayed(now); } catch (Exception e) { HookError("host upkeep", e); }
                if (now >= _nextHostTick)
                {
                    _nextHostTick = now + 1f;
                    try { HostTick(now); } catch (Exception e) { HookError("host upkeep", e); }
                }
            }
        }

        private void HandleSaved(float now)
        {
            while (_saved.TryDequeue(out var key))
            {
                if (!WorldSync.InGame() && _transit == null) continue;
                Dump("gamesave", key, ReadBlob(SavedSlot(key), key));
                // the region being left: its last save has now been written
                bool old = _transit != null && !string.IsNullOrEmpty(_transit.OldKey) && key == _transit.OldKey && !_transit.Loaded.Contains(key);
                if (_transit == null)
                {
                    if (_hereKey != null && _hereKey != key && !_loggedOtherKey)
                    {
                        _loggedOtherKey = true;
                        CoopMod.Log($"Scene hand-over: the game saved region data under '{key}' while this game stands in '{_hereKey}'");
                    }
                    if (_hereKey == null || _hereKey == key) _lastKey = key;
                }
                if (Enabled && _net.Mode == NetMode.Client && (old ? !_transit.LeftSent : key == _hereKey))
                {
                    // the host keeps the newest copy of a region. A save made while still standing here need not go while
                    // the host stands in it itself; the last save on the way out always goes (what this game knows about
                    // where the host is can be old: the host drops the copy itself if it really is there)
                    string scene = old ? _transit.OldScene : (_hereScene ?? CoopMod.CurrentScene());
                    bool due = old || !_lastUpload.TryGetValue(key, out var at) || now - at > 20f;
                    if (due && (old || !HostInScene(scene)))
                    {
                        var data = ReadBlob(SavedSlot(key), key);
                        if (data != null && data.Length <= MaxBlob)
                        {
                            _lastUpload[key] = now;
                            Dump(old ? "leftout" : "savedout", key, data);
                            SendBlob(Protocol.HostId, old ? KLeft : KSaved, 0, _net.LocalId, key, scene, data);
                            CoopMod.Log($"Scene {key}: sent this game's {(old ? "last" : "newest")} save of it to the host ({Kb(data.Length)} KB)");
                        }
                    }
                    if (old) _transit.LeftSent = true;
                }
                if (old && !_transit.LeaveSaved)
                {
                    _transit.LeaveSaved = true;
                    if (_hereKey == key) SetNotHere(); // after the copy above, so the host has it before it hears we left
                    _liveCache.Remove(key);
                }
            }
        }

        private void TrackPresence(float now)
        {
            bool inGame = WorldSync.InGame();
            if (_transit != null)
            {
                var t = _transit;
                // the last save of the old region never came (saving was not allowed): we are gone from it anyway
                if (_hereKey != null && _hereKey == t.OldKey && !t.LeaveSaved && now - t.PrefixDoneAt > 3f) { t.LeaveSaved = true; SetNotHere(); }
                if (t.ToMenu)
                {
                    if (!inGame && now - t.StartedAt > 1f) { if (_hereKey != null) SetNotHere(); _transit = null; }
                    else if (now - t.StartedAt > 60f) _transit = null;
                    return;
                }
                // "still loading" only delays for half a minute, in case the game's loading counter ever sticks
                bool ready = inGame && !SceneBusy(now - t.StartedAt < 30f);
                bool arrived = ready && (t.LoadSeenAt >= 0f
                    ? now - t.LoadSeenAt > 3f
                    : now - t.StartedAt > 10f && (t.SceneName == null || CoopMod.CurrentScene() == t.SceneName));
                if (arrived) Arrive(t);
                else if (now - t.StartedAt > 180f) { CoopMod.Log("Scene hand-over: the region change never finished, giving up on it"); _transit = null; }
                return;
            }
            if (!inGame)
            {
                if (_hereKey != null) SetNotHere();
                return;
            }
            // first time in a game (hosting just started, shared world just entered, reconnect)
            if (_hereKey == null && !SceneBusy(false))
            {
                var key = CurrentKey();
                if (!string.IsNullOrEmpty(key))
                {
                    _hereKey = key; _hereScene = CoopMod.CurrentScene();
                    string td = null; try { td = GameManager.m_SceneTransitionData?.m_SceneSaveFilenameCurrent; } catch { }
                    CoopMod.Log($"Scene hand-over: this game is in '{key}' (region {_hereScene}, the game's own note says '{td}')");
                }
            }
            if (Enabled && _net.Mode == NetMode.Client && _hereKey != null && _hereSentKey != _hereKey) SendHere(_hereKey, _hereScene);
        }

        private void Arrive(Transit t)
        {
            string key = t.Key ?? t.GuessKey ?? CurrentKey();
            _transit = null;
            _hereKey = key; _hereScene = CoopMod.CurrentScene();
            _liveCache.Clear();
            if (t.LoadSeenAt < 0f) CoopMod.Log($"Scene {key}: the game did not read saved data on the way in, so nothing could be handed over");
            if (!Enabled) { _made.Clear(); return; }
            if (_net.Mode == NetMode.Client)
            {
                SendHere(key, _hereScene);
                foreach (var k in new List<string>(_made))
                {
                    byte[] data = k == key ? SerializeNow(k) : ReadBlob(WorkSlot(), k);
                    if (data == null || data.Length > MaxBlob) { CoopMod.Log($"Scene {k}: made here but could not be written out for the host"); continue; }
                    _lastUpload[k] = Now;
                    SendBlob(Protocol.HostId, KMade, 0, _net.LocalId, k, _hereScene, data);
                    CoopMod.Log($"Scene {k}: made here first, sent to the host ({Kb(data.Length)} KB)");
                }
            }
            else
            {
                foreach (var k in _made) { if (_claims.TryGetValue(k, out var c) && c.By == Protocol.HostId) _claims.Remove(k); ResolveWaiters(k); }
                ResolveWaiters(key);
            }
            _made.Clear();
        }

        // =====================================================================================
        // host: the store and the arbiter
        // =====================================================================================
        private void HostTick(float now)
        {
            // someone who promised to make a region never delivered
            foreach (var key in new List<string>(_claims.Keys))
            {
                var c = _claims[key];
                if (!ClaimAlive(c, now))
                {
                    _claims.Remove(key);
                    CoopMod.Log($"Scene {key}: player {c.By} never delivered it");
                    ResolveWaiters(key);
                }
            }
        }

        /// <summary>Friends whose answer was put off for a moment (see SceneLiveNo) are served now.</summary>
        private void HostDelayed(float now)
        {
            if (_resolveAt.Count == 0) return;
            foreach (var key in new List<string>(_resolveAt.Keys))
            {
                if (now < _resolveAt[key]) continue;
                _resolveAt.Remove(key);
                ResolveWaiters(key);
            }
        }

        private void AddWaiter(string key, byte id, int askId)
        {
            if (!_waiters.TryGetValue(key, out var l)) _waiters[key] = l = new List<(byte, int)>();
            l.RemoveAll(x => x.Id == id);
            l.Add((id, askId));
        }

        /// <summary>Friends are waiting for a region: serve them with the best copy there is now.</summary>
        private void ResolveWaiters(string key)
        {
            if (!_waiters.TryGetValue(key, out var list) || list.Count == 0) return;
            list.RemoveAll(x => !_net.Remotes.ContainsKey(x.Id));
            if (list.Count == 0) return;
            if (_claims.TryGetValue(key, out var c) && ClaimAlive(c, Now)) return; // still being made
            byte[] data = null; byte kind = KStored;
            if (_hereKey == key) { data = SerializeNow(key); kind = KLive; }
            if (data == null) { data = ReadBlob(WorkSlot(), key); kind = KStored; }
            if (data != null)
            {
                foreach (var (id, askId) in list)
                {
                    Answer(id, askId, key, VComing, data.Length);
                    SendBlob(id, kind, askId, Protocol.HostId, key, SceneNameFor(key), data);
                    CoopMod.Log($"Scene {key}: sent {Kb(data.Length)} KB to player {id} ({KindName(kind)})");
                }
                list.Clear();
                return;
            }
            // nobody has it after all: the first one waiting makes it, the others wait for that
            var first = list[0]; list.RemoveAt(0);
            _claims[key] = NewClaim(key, first.Id, mayBeThere: true);
            Answer(first.Id, first.AskId, key, VNone);
            CoopMod.Log($"Scene {key}: nobody has it, player {first.Id} makes it");
        }

        private string SceneNameFor(string key)
        {
            if (_hereKey == key && !string.IsNullOrEmpty(_hereScene)) return _hereScene;
            if (_storeInfo.TryGetValue(key, out var i) && !string.IsNullOrEmpty(i.Scene)) return i.Scene;
            return key;
        }

        private void OnRemoteLeft(byte id)
        {
            if (_net.Mode != NetMode.Host) return;
            _where.Remove(id);
            foreach (var key in new List<string>(_claims.Keys))
                if (_claims[key].By == id) { _claims.Remove(key); try { ResolveWaiters(key); } catch { } }
            foreach (var l in _waiters.Values) l.RemoveAll(x => x.Id == id);
        }

        private void HostOnAsk(byte from, int askId, string key, string sceneName, uint have)
        {
            if (!Enabled || !WorldSync.InGame() && _transit == null) { Answer(from, askId, key, VOff); return; }
            bool hereButStuck = false;
            // someone stands there right now
            if (_hereKey == key)
            {
                hereButStuck = true;
                var live = SerializeNow(key);
                if (live != null && live.Length <= MaxBlob)
                {
                    Answer(from, askId, key, VComing, live.Length);
                    SendBlob(from, KLive, askId, Protocol.HostId, key, _hereScene, live);
                    CoopMod.Log($"Scene {key}: player {from} is coming in, sent this game's current state ({Kb(live.Length)} KB, live)");
                    return;
                }
                CoopMod.Log($"Scene {key}: this game stands there but cannot write it out right now ({_refusal}), using the stored copy");
            }
            foreach (var kv in _where)
            {
                if (kv.Key == from || kv.Value.Key != key || !_net.Remotes.ContainsKey(kv.Key)) continue;
                var w = _net.Begin(Msg.SceneLive); w.Put(_nextReq++); w.Put(key);
                _net.SendTo(kv.Key, w, DeliveryMethod.ReliableOrdered);
                AddWaiter(key, from, askId);
                Answer(from, askId, key, VWait);
                CoopMod.Log($"Scene {key}: player {from} is coming in, asked player {kv.Key} (who is there) for its current state");
                return;
            }
            if (_transit != null && _transit.Loaded.Contains(key))
            {
                AddWaiter(key, from, askId);
                Answer(from, askId, key, VWait);
                CoopMod.Log($"Scene {key}: player {from} is coming in while this game is just arriving there: it gets the state in a moment");
                return;
            }
            // someone is generating it this very moment (a friend, or this game on its way in)
            if (_claims.TryGetValue(key, out var c) && c.By != from && ClaimAlive(c, Now))
            {
                AddWaiter(key, from, askId);
                Answer(from, askId, key, VWait);
                CoopMod.Log($"Scene {key}: player {from} is coming in while player {c.By} is still making it: told to wait");
                return;
            }
            var stored = ReadBlob(WorkSlot(), key);
            if (stored != null && stored.Length <= MaxBlob)
            {
                if (!hereButStuck && have != 0 && Hash(stored) == have)
                {
                    Answer(from, askId, key, VSame, stored.Length);
                    CoopMod.Log($"Scene {key}: player {from} is coming in and already holds the newest copy");
                    return;
                }
                Answer(from, askId, key, VComing, stored.Length);
                SendBlob(from, KStored, askId, Protocol.HostId, key, SceneNameFor(key), stored, hereButStuck ? (byte)0 : FComplete);
                CoopMod.Log($"Scene {key}: player {from} is coming in, sent the stored copy ({Kb(stored.Length)} KB)");
                return;
            }
            _claims[key] = NewClaim(key, from);
            Answer(from, askId, key, VNone);
            CoopMod.Log($"Scene {key}: player {from} is coming in and nobody has it: that game makes it");
        }

        private void HostOnBlob(byte from, byte kind, string key, string scene, byte[] data)
        {
            // this game is standing in that region, or has already loaded it on its way in: its own state is the truth
            bool mine = _hereKey == key || (_transit != null && _transit.Loaded.Contains(key));
            bool stored = false;
            if (!mine)
            {
                stored = Inject(key, scene, data, "the copy from player " + from);
                if (stored)
                {
                    _storeInfo[key] = (data.Length, from, kind, Now, scene);
                    _stamp[key] = Stamp(key) + 1;
                    CoopMod.Log($"Scene {key}: stored {Kb(data.Length)} KB from player {from} ({KindName(kind)})");
                }
            }
            else if (kind == KMade)
                CoopMod.Log($"Scene {key}: WARNING player {from} made its own copy while this game is already in it: the two may differ");
            if (_claims.TryGetValue(key, out var c) && c.By == from) _claims.Remove(key);
            // friends waiting for exactly this
            if (_waiters.TryGetValue(key, out var list) && list.Count > 0 && (kind == KMade || kind == KLiveReply || kind == KLeft))
            {
                foreach (var (id, askId) in list)
                {
                    if (id == from || !_net.Remotes.ContainsKey(id)) continue;
                    Answer(id, askId, key, VComing, data.Length);
                    SendBlob(id, kind == KLeft ? KStored : KLive, askId, from, key, scene, data);
                    CoopMod.Log($"Scene {key}: passed {Kb(data.Length)} KB from player {from} on to player {id}");
                }
                list.Clear();
            }
        }

        // =====================================================================================
        // messages
        // =====================================================================================
        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (!IsSceneMsg(msg)) return;
            try
            {
                switch (msg)
                {
                    case Msg.SceneAsk:
                    {
                        if (_net.Mode != NetMode.Host) return;
                        int askId = r.GetInt(); var key = r.GetString(); var scene = r.GetString();
                        uint have = r.AvailableBytes >= 4 ? r.GetUInt() : 0;
                        HostOnAsk(from, askId, key, scene, have);
                        break;
                    }
                    case Msg.SceneAnswer:
                    {
                        if (_net.Mode != NetMode.Client) return;
                        int askId = r.GetInt(); var key = r.GetString(); byte verdict = r.GetByte(); int bytes = r.GetInt();
                        if (!_asks.TryGetValue(key, out var ask) || ask.Id != askId) return;
                        switch (verdict)
                        {
                            case VNone:
                                // (closed only because the game read the region before this answer came: it was made here, so it still gets shared)
                                if (ask.Done && !ask.ReadFirst) return;
                                ask.ReadFirst = false;
                                ask.Done = true; _made.Add(key);
                                ask.Outcome = "nobody had it: this game makes it and shares it";
                                CoopMod.Log($"Scene {key}: {ask.Outcome}");
                                break;
                            case VWait:
                                ask.Waiting = true;
                                CoopMod.Log($"Scene {key}: someone is getting it ready right now, waiting");
                                break;
                            case VOff:
                                if (ask.Done) return;
                                ask.Done = true; ask.Outcome = "the host has hand-over switched off: loading as before";
                                CoopMod.Log($"Scene {key}: {ask.Outcome}");
                                break;
                            case VComing:
                                ask.Coming = true; ask.Bytes = bytes;
                                break;
                            case VSame:
                                if (ask.Done) return;
                                ask.Done = true; ask.Outcome = "this game already holds the newest copy";
                                CoopMod.Log($"Scene {key}: {ask.Outcome}");
                                break;
                        }
                        break;
                    }
                    case Msg.SceneBlob:
                    {
                        byte kind = r.GetByte(); byte flags = r.GetByte(); int askId = r.GetInt(); byte source = r.GetByte();
                        var key = r.GetString(); var scene = r.GetString(); int n = r.GetInt();
                        if (n <= 0 || n > MaxBlob || n > r.AvailableBytes) { CoopMod.Log($"Scene {key}: a broken copy arrived ({n} bytes), ignored"); return; }
                        var data = new byte[n]; r.GetBytes(data, n);
                        if (_net.Mode == NetMode.Host) { if (Enabled) HostOnBlob(from, kind, key, scene, data); return; }
                        ClientOnBlob(kind, askId, source, key, scene, data, (flags & FComplete) != 0);
                        break;
                    }
                    case Msg.SceneLive:
                    {
                        if (_net.Mode != NetMode.Client) return;
                        int req = r.GetInt(); var key = r.GetString();
                        byte[] data = null;
                        if (Enabled) data = SerializeNow(key); else _refusal = "hand-over is off in this game";
                        if (data != null && data.Length <= MaxBlob)
                        {
                            _lastUpload[key] = Now;
                            SendBlob(Protocol.HostId, KLiveReply, req, _net.LocalId, key, _hereScene, data);
                            CoopMod.Log($"Scene {key}: a friend is coming in, sent this game's current state ({Kb(data.Length)} KB)");
                        }
                        else
                        {
                            var w = _net.Begin(Msg.SceneLiveNo); w.Put(req); w.Put(key);
                            _net.Send(w, DeliveryMethod.ReliableOrdered);
                            CoopMod.Log($"Scene {key}: asked for its current state, but cannot give it ({_refusal})");
                            // the host now takes this game as gone from there: if it still stands there, say so again
                            // (TrackPresence does, once this game is not on its way out)
                            if (_hereKey == key) _hereSentKey = null;
                        }
                        break;
                    }
                    case Msg.SceneLiveNo:
                    {
                        if (_net.Mode != NetMode.Host) return;
                        r.GetInt(); var key = r.GetString();
                        _liveNo[key] = Now;
                        if (_where.TryGetValue(from, out var wh) && wh.Key == key) wh.Key = "";
                        // if that player is on its way out, its last save of the region is on its way here and is passed on
                        // when it comes; only if it has not come after a moment do the waiting friends get the stored copy
                        _resolveAt[key] = Now + LeftGrace;
                        break;
                    }
                    case Msg.SceneHere:
                    {
                        if (_net.Mode != NetMode.Host) return;
                        var key = r.GetString(); var scene = r.GetString();
                        if (!_where.TryGetValue(from, out var wh)) _where[from] = wh = new Where();
                        wh.Key = key; wh.Scene = scene;
                        // it has arrived somewhere: whatever it promised to make is sent at this moment, or never
                        if (!string.IsNullOrEmpty(key))
                            foreach (var c in _claims.Values) if (c.By == from && c.ArrivedAt < 0f) c.ArrivedAt = Now;
                        break;
                    }
                }
            }
            catch (Exception e) { CoopMod.Log($"Scene hand-over message {msg}: {e.Message}"); }
        }

        private void ClientOnBlob(byte kind, int askId, byte source, string key, string scene, byte[] data, bool complete)
        {
            _asks.TryGetValue(key, out var ask);
            // only the answer to a question of this very change of region counts: a copy asked for at an earlier door
            // (for the region this game may be leaving right now) must never get into the slot
            if (ask == null || ask.Id != askId)
            {
                CoopMod.Log($"Scene {key}: a copy from player {source} arrived that this game is not waiting for (an earlier question), not used");
                return;
            }
            // too late once the game has read that region
            bool tooLate = _transit == null || _transit.Loaded.Contains(key);
            if (tooLate)
            {
                CoopMod.Log($"Scene {key}: a copy from player {source} arrived after the game had loaded it, not used");
                ask.Done = true;
                return;
            }
            if (ask.Done)
            {
                CoopMod.Log($"Scene {key}: a copy from player {source} arrived after this game had its answer, not used");
                return;
            }
            string from = $"player {source} ({KindName(kind)})";
            bool ok = Inject(key, scene, data, "the copy from " + from, complete);
            bool late = ask.TimedOut;
            ask.Done = true; ask.Got = ok; ask.Bytes = data.Length; ask.Source = source; ask.Kind = kind;
            _made.Remove(key);
            ask.Outcome = ok
                ? $"got {Kb(data.Length)} KB from {from}{(late ? " (late, but still before the game read it)" : "")}"
                : $"a copy from {from} arrived but could not be put in: loading as before";
            CoopMod.Log($"Scene {key}: {ask.Outcome}");
            LastDecision = $"{key}: {ask.Outcome}";
        }

        // =====================================================================================
        // test support
        // =====================================================================================
        internal void WriteState(System.Text.StringBuilder sb)
        {
            try
            {
                sb.Append("scene_handoff_on: ").Append(Enabled).Append(" (wanted ").Append(Wanted).Append(")\n");
                sb.Append("scene_work_slot: ").Append(WorkSlotName()).Append(" (game last used '").Append(_gameSlotName ?? "-").Append("')\n");
                sb.Append("scene_here: key=").Append(_hereKey ?? "-").Append(" region=").Append(_hereScene ?? "-")
                  .Append(" changing=").Append(_transit != null).Append('\n');
                string wait = "";
                foreach (var a in _asks.Values) wait += $" [{a.Key} wait_ms={a.WaitMs} bytes={a.Bytes} got={a.Got}]";
                sb.Append("scene_handoff: ").Append(LastDecision).Append(wait).Append('\n');
                var slot = WorkSlot();
                if (slot == null) return;
                Il2CppSystem.Collections.Generic.List<string> keys = null;
                try { keys = SaveGameSlots.GetSceneKeysForCurrentSaveSlot(); } catch { }
                var names = new List<string>();
                if (keys != null) for (int i = 0; i < keys.Count; i++) names.Add(keys[i]);
                if (names.Count == 0)
                {
                    try { var all = slot.GetAllDataKeys(); for (int i = 0; i < all.Count; i++) names.Add(all[i]); } catch { }
                    names.RemoveAll(k => k == "boot" || k == "global" || k == "info" || k == "sandbox_global" || k == "screenshot");
                }
                names.Sort(StringComparer.Ordinal);
                string line = _net.Mode == NetMode.Host ? "scene_store: " : "scene_slot: ";
                foreach (var k in names)
                {
                    var d = ReadBlob(slot, k);
                    if (d == null) continue;
                    sb.Append(line).Append(k).Append(" bytes=").Append(d.Length).Append(" hash=").Append(Hash(d).ToString("x8"));
                    if (_storeInfo.TryGetValue(k, out var info))
                        sb.Append(" from=").Append(info.From).Append(" kind=").Append(KindName(info.Kind)).Append(" age_s=").Append(((int)(Now - info.At)).ToString());
                    sb.Append('\n');
                }
                if (_net.Mode == NetMode.Host)
                {
                    foreach (var kv in _where) sb.Append("scene_where: player=").Append(kv.Key).Append(" key=").Append(kv.Value.Key).Append(" region=").Append(kv.Value.Scene).Append('\n');
                    foreach (var kv in _claims) sb.Append("scene_claim: key=").Append(kv.Key).Append(" by=").Append(kv.Value.By).Append('\n');
                }
            }
            catch (Exception e) { sb.Append("scene_handoff: error ").Append(e.Message).Append('\n'); }
        }
    }

    // ---------------- patches ----------------
    // Applied by hand (SceneHandoff.ApplyPatches), each on its own: if the game ever changes one of these methods,
    // only the hand-over is lost and every other patch of the mod still goes in.
    internal static class HandoffPatches
    {
        internal static void LoadScenePrefix(string sceneName, string sceneSaveFilenameCurrent)
        {
            try { SceneHandoff.Instance?.BeforeLoadScene(sceneName, sceneSaveFilenameCurrent); } catch { }
        }

        internal static void LoadSceneDataPrefix(string name, string sceneSaveName)
        {
            try { SceneHandoff.Instance?.BeforeLoadSceneData(name, sceneSaveName, false); } catch { }
        }

        internal static void LoadSceneDataPostfix(string sceneSaveName, bool __result)
        {
            try { SceneHandoff.Instance?.AfterLoadSceneData(sceneSaveName, __result); } catch { }
        }

        internal static void LoadSceneDataAdditivePrefix(string name, string sceneSaveName)
        {
            try { SceneHandoff.Instance?.BeforeLoadSceneData(name, sceneSaveName, true); } catch { }
        }

        internal static void SaveSceneDataPostfix(SlotData slot, string sceneSaveName)
        {
            string slotName = null;
            try { slotName = slot?.m_InternalName; } catch { }
            try { SceneHandoff.Instance?.AfterSaveSceneData(sceneSaveName, slotName); } catch { }
        }
    }
}
