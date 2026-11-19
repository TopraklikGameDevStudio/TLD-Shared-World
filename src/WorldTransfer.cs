using System;
using System.Collections.Generic;
using System.IO;
using Il2Cpp;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// On join, the host saves and ships its whole world file; the joiner merges in their own survivor
    /// and loads it as the "ORTAK DUNYA" slot. From then on both sides play on copies of the same world.
    /// </summary>
    internal sealed class WorldTransfer
    {
        private readonly Network _net;

        // host side
        private sealed class Pending { public byte Id; public float Started; public string Path; public DateTime OldMtime; public bool SaveRequested; public float NextSave; public bool SaidWaiting; }
        private readonly List<Pending> _pending = new();

        // client side
        private enum ClientState { Idle, WaitingForWorld, WaitingForSlot, WaitingForUser, Loading, InCoopWorld }
        private float _nextPanelCheck;
        private bool _refreshedChooser, _loggedChooser;
        public bool Busy => _state == ClientState.WaitingForWorld || _state == ClientState.WaitingForSlot || _state == ClientState.Loading;
        private ClientState _state = ClientState.Idle;
        private float _stateSince;
        private float _nextRetry;
        private SaveMerge.Result _built;
        private bool _freshSlot, _startChecked;
        private string _slotName;
        public Func<string> GetLastCoopSlot = () => null;
        public Action<string> RememberCoopSlot = _ => { };

        public bool ClientInCoopWorld => _state == ClientState.InCoopWorld;
        /// <summary>Client: the host's world of THIS connection has arrived (an older co-op copy opened before that is not the shared world).</summary>
        public bool FreshWorldHere => _state == ClientState.WaitingForUser || _state == ClientState.Loading || _state == ClientState.InCoopWorld;
        /// <summary>Host: a friend is waiting for the world, so the game must keep running until its save is written.</summary>
        public bool HostSending => _pending.Count > 0;
        /// <summary>Test harness: where the client's join stands.</summary>
        internal string ClientStateName => _state.ToString();
        private bool _warnedEarly;

        public WorldTransfer(Network net)
        {
            _net = net;
            _net.OnMessage += OnMessage;
            _net.OnRemoteJoined += (id, name) =>
            {
                if (_net.Mode == NetMode.Client && id == Protocol.HostId) BeginJoin();
            };
            _net.OnStopped += () => { _pending.Clear(); _state = ClientState.Idle; };
        }

        // ---------------- client ----------------
        private void BeginJoin()
        {
            if (WorldSync.InGame())
            {
                // already inside some game: we can't swap worlds under the player's feet
                CoopMod.Hud(L.T("Ortak dünyaya geçmek için ana menüye dön ve tekrar katıl (ORTAK OYUN > KATIL)", "To move to the shared world, go back to the main menu and join again (CO-OP > JOIN)"));
                _state = ClientState.Idle;
                return;
            }
            RequestWorld();
        }

        private void RequestWorld()
        {
            _state = ClientState.WaitingForWorld;
            _stateSince = Time.realtimeSinceStartup;
            _net.Send(_net.Begin(Msg.RequestWorld), DeliveryMethod.ReliableOrdered);
            CoopMod.Log("Requested host world");
            CoopMod.Status(L.T("Sunucunun dünyası isteniyor…", "Asking the server for its world…"));
        }

        /// <summary>The player opened a save before the host's world got here: say so once (it is not the shared world).</summary>
        private void WarnEarlyLoad()
        {
            if (!WorldSync.InGame()) { _warnedEarly = false; return; }
            if (_warnedEarly) return;
            _warnedEarly = true;
            CoopMod.Log("A save was opened before the host's world arrived: this is not the shared world");
            CoopMod.Popup(L.T("ORTAK DÜNYA", "SHARED WORLD"), L.T("Sunucunun dünyası daha gelmeden bir kayıt açtın. Bu ortak dünya değil: ana menüye dön, \"ORTAK DÜNYA HAZIR\" yazısını bekle.", "You opened a save before the server's world arrived. This is not the shared world: go back to the main menu and wait for \"SHARED WORLD READY\"."));
        }

        private void OnWorldData(byte[] file)
        {
            if (WorldSync.InGame())
            {
                // a game is running (an older co-op copy opened before the new world arrived, or any other save):
                // the save cannot be swapped under the player's feet
                _state = ClientState.Idle;
                CoopMod.Log("The host's world arrived while a game is already running here: not used. Back to the main menu and join again.");
                CoopMod.Popup(L.T("ORTAK DÜNYA", "SHARED WORLD"), L.T("Sunucunun dünyası geldi ama sen zaten bir oyunun içindesin. Ana menüye dön ve tekrar katıl.", "The server's world arrived but you are already in a game. Go back to the main menu and join again."));
                return;
            }
            try
            {
                CoopMod.Status(L.T($"Dünya alındı ({file.Length / 1024} KB), hazırlanıyor…", $"World received ({file.Length / 1024} KB), getting it ready…"));
                var last = GetLastCoopSlot();
                var own = SaveMerge.FindOwnCharacterSlot(last);

                uint id = SaveMerge.FixedCoopGameId;
                string name = SaveMerge.FixedCoopSlot;
                _slotName = name;

                _built = SaveMerge.BuildCoopSlot(file, own, name, id);
                CoopMod.Log($"Co-op world built as {name}: {_built.FileBytes.Length} bytes, character from {_built.CharacterSource}, scene {_built.Scene}");

                File.WriteAllBytes(Path.Combine(SaveMerge.SaveDir(), name), _built.FileBytes);
                RememberCoopSlot(name);

                // Never delete the slot (the game deletes asynchronously and raced with our write).
                // Overwrite the file and hand the same data to the game's in-memory slot list.
                // (Re-reading the whole save folder never finished in time.)
                // The game read this slot's OLD contents when it started and keeps them in memory. Just adding the new data
                // leaves two entries with the same name and the game loads the first one: last session's world.
                // So take the old entries out of the list first, then let the game add the new one in the same place.
                bool added = false; int oldCount = 0, oldIndex = -1; IntPtr oldPtr = IntPtr.Zero; SlotData oldSlot = null;
                Il2CppSystem.Collections.Generic.List<SlotData> slots = null;
                try
                {
                    slots = SaveGameSlots.m_SaveSlots;
                    for (int i = slots.Count - 1; i >= 0; i--)
                    {
                        var sd = slots[i];
                        if (sd == null || sd.m_InternalName != _slotName) continue;
                        oldSlot = sd; oldPtr = sd.Pointer; oldIndex = i; oldCount++;
                        slots.RemoveAt(i);
                    }
                }
                catch (Exception e) { CoopMod.Log("Old co-op slot could not be taken out: " + e.Message); }
                try { added = SaveGameSlots.CreateSlotFromData(_built.OuterJson, _slotName, _built.FileBytes.Length); }
                catch (Exception e) { CoopMod.Log("CreateSlotFromData: " + e.Message); }
                int copies = 0; bool fresh = false;
                try
                {
                    slots = SaveGameSlots.m_SaveSlots; // read again: the first read may have failed
                    int at = -1;
                    for (int i = 0; i < slots.Count; i++) { var sd = slots[i]; if (sd != null && sd.m_InternalName == _slotName) { copies++; at = i; } }
                    if (copies == 0 && oldSlot != null)
                    {
                        // nothing was added: better the old copy than no slot at all (the load screen needs the entry)
                        slots.Insert(Math.Min(Math.Max(oldIndex, 0), slots.Count), oldSlot);
                    }
                    else if (copies == 1 && oldIndex >= 0 && at != oldIndex && oldIndex < slots.Count)
                    {
                        var sd = slots[at]; slots.RemoveAt(at); slots.Insert(oldIndex, sd); // same place in the list as before
                    }
                    var nowSlot = SaveGameSlots.GetSaveSlotFromName(_slotName);
                    fresh = nowSlot != null && nowSlot.Pointer != oldPtr;
                }
                catch (Exception e) { CoopMod.Log("Co-op slot check: " + e.Message); }
                _freshSlot = fresh; _startChecked = false;
                // the load screen keeps its own short description of every slot: build those again from the new data
                try { SaveGameSlotHelper.RefreshSandboxSaveSlots(); } catch (Exception e) { CoopMod.Log("Load list refresh: " + e.Message); }
                CoopMod.Log($"Slot data handed to the game: {added}, slot known: {SafeHas(_slotName)}, old copies removed: {oldCount}, copies now: {copies}, game will load the new one: {fresh}");
                if (!fresh) CoopMod.Hud(L.T("UYARI: ortak dünya güncellenemedi, eski kopya açılabilir (oyunu yeniden başlat)", "WARNING: the shared world could not be updated, an old copy may open (restart the game)"));
                _state = ClientState.WaitingForSlot;
                _stateSince = Time.realtimeSinceStartup;
            }
            catch (Exception e)
            {
                CoopMod.Log("World build failed: " + e);
                CoopMod.Hud(L.T("Ortak dünya hazırlanamadı (log'a bak)", "The shared world could not be prepared (see the log)"));
                _state = ClientState.Idle;
            }
        }

        // the co-op slot is the one loaded, its scene is up and the game has put the player in place
        private bool StartCheckReady()
        {
            try
            {
                if (SaveGameSystem.GetCurrentSaveName() != _slotName) return false;
                if (!string.IsNullOrEmpty(_built.Scene) && CoopMod.CurrentScene() != _built.Scene) return false;
                if (SaveGameSystem.IsRestoreInProgress() || SaveGameSystem.IsSceneRestoreInProgress()) return false;
                var pm = GameManager.GetPlayerManagerComponent();
                if (pm == null || pm.IsTeleportPending()) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool SafeHas(string name)
        {
            try { return SaveGameSlots.HasSaveSlot(name); } catch { return false; }
        }

        private bool TryClickCoopSlot()
        {
            try
            {
                var chooser = UnityEngine.Object.FindObjectOfType<Panel_ChooseSandbox>();
                if (chooser == null || !chooser.gameObject.activeInHierarchy) return false;
                if (!_loggedChooser)
                {
                    _loggedChooser = true;
                    var names = "";
                    for (int i = 0; i < 40; i++) { SaveSlotInfo s = null; try { s = chooser.GetSaveSlotInfo(i); } catch { } if (s == null) break; names += s.m_SaveSlotName + " "; }
                    CoopMod.Log($"Load screen open, looking for {_slotName} among: {names}");
                }
                for (int i = 0; i < 40; i++)
                {
                    SaveSlotInfo info = null;
                    try { info = chooser.GetSaveSlotInfo(i); } catch { }
                    if (info == null) break;
                    if (info.m_SaveSlotName == _slotName)
                    {
                        CoopMod.Log($"Clicking co-op slot {_slotName} in the Load screen (row {i})");
                        chooser.OnLoadSlotClicked(i);
                        return true;
                    }
                }
                if (!_refreshedChooser)
                {
                    // the list was built before the slot existed: rebuild it once
                    _refreshedChooser = true;
                    chooser.Enable(false);
                    chooser.Enable(true);
                }
            }
            catch (Exception e) { CoopMod.Log("Load screen: " + e.Message); }
            return false;
        }

        // ---------------- host ----------------
        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (_net.Mode == NetMode.Host && msg == Msg.RequestWorld)
            {
                string cur = null;
                try { cur = SaveGameSystem.GetCurrentSaveName(); } catch { }
                if (SaveMerge.IsCoopSlot(cur))
                {
                    var w0 = _net.Begin(Msg.WorldNotReady); w0.Put(L.Both("Sunucu ORTAK DUNYA kaydında, kendi kaydını yüklemeli", "The host is in the ORTAK DUNYA (shared world) save and must load their own save"));
                    _net.SendTo(from, w0, DeliveryMethod.ReliableOrdered);
                    CoopMod.Hud(L.T("Sunucu olarak kendi kaydını yükle (ORTAK DUNYA değil)", "As the host, load your own save (not ORTAK DUNYA)"));
                    return;
                }
                if (!WorldSync.InGame())
                {
                    var w = _net.Begin(Msg.WorldNotReady); w.Put(L.Both("Sunucu henüz oyuna girmedi", "The host is not in a game yet"));
                    _net.SendTo(from, w, DeliveryMethod.ReliableOrdered);
                    return;
                }
                _pending.RemoveAll(p => p.Id == from);
                _pending.Add(new Pending { Id = from, Started = Time.realtimeSinceStartup });
                CoopMod.Hud(L.T("Dünya arkadaşına gönderiliyor…", "Sending the world to your friend…"));
            }
            else if (_net.Mode == NetMode.Client)
            {
                if (msg == Msg.WorldData)
                {
                    int n = r.GetInt();
                    var data = new byte[n];
                    r.GetBytes(data, n);
                    OnWorldData(data);
                }
                else if (msg == Msg.WorldNotReady)
                {
                    CoopMod.Status(L.Pick(r.GetString()) + L.T(" — bekleniyor", " — waiting"));
                    _nextRetry = Time.realtimeSinceStartup + 5f;
                }
            }
        }

        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (_net.Mode == NetMode.Host) HostUpdate(now);
            else if (_net.Mode == NetMode.Client) ClientUpdate(now);
        }

        private void HostUpdate(float now)
        {
            if (_pending.Count == 0) return;
            var p = _pending[0];
            if (!p.SaveRequested)
            {
                // the host's own load (and its fade-in) has to be over before the game can take a save request
                if (!WorldSync.Settled())
                {
                    p.Started = now;
                    if (!p.SaidWaiting) { p.SaidWaiting = true; CoopMod.Log("A friend asked for the world while this game is still loading: the save is requested once it is in"); }
                    return;
                }
                var slot = SaveGameSystem.GetCurrentSaveName();
                p.Path = SaveMerge.FindSlotFile(slot);
                p.OldMtime = p.Path != null ? File.GetLastWriteTimeUtc(p.Path) : DateTime.MinValue;
                try { GameManager.ForceSaveGame(); } catch (Exception e) { CoopMod.Log("ForceSaveGame: " + e.Message); }
                p.SaveRequested = true;
                CoopMod.Log($"Saving '{slot}' before sending ({p.Path})");
                return;
            }
            if (p.Path == null) p.Path = SaveMerge.FindSlotFile(SaveGameSystem.GetCurrentSaveName());
            bool written = p.Path != null && File.Exists(p.Path) && File.GetLastWriteTimeUtc(p.Path) > p.OldMtime;
            // a brand-new game may not take a save straight away: ask again every few seconds, for up to 40 s
            if (!written && now - p.Started < 40f)
            {
                if (now >= p.NextSave)
                {
                    p.NextSave = now + 4f;
                    if (now - p.Started > 3f)
                    {
                        bool allowed = true; try { allowed = !GameManager.SaveIsBlockedDueToRestoreGame(); } catch { }
                        // Asking again while the game still holds the first request, or while it stands still, froze the
                        // host for good (0.7.0 test: host paused the moment the friend joined). So: only when it is free.
                        bool pendingSave = false, paused = false;
                        try { pendingSave = GameManager.m_PendingSave; } catch { }
                        try { paused = GameManager.m_IsPaused || Time.timeScale == 0f; } catch { }
                        if (pendingSave || paused)
                        {
                            CoopMod.Log($"Save not written yet, waiting (game holds a save request: {pendingSave}, game stands still: {paused})");
                            return;
                        }
                        try { GameManager.ForceSaveGame(); } catch { }
                        if (now - p.Started > 8f && now - p.Started < 12.5f)
                            CoopMod.Hud(L.T("Dünya kaydediliyor… (yeni oyunda biraz sürebilir)", "Saving the world… (may take a while in a new game)"));
                        CoopMod.Log($"Save not written yet, asking again (allowed {allowed})");
                    }
                }
                return;
            }
            // give the writer a moment to finish
            if (written && now - p.Started < 1.5f) return;

            _pending.RemoveAt(0);
            if (p.Path == null || !File.Exists(p.Path))
            {
                CoopMod.Popup(L.T("ORTAK DÜNYA", "SHARED WORLD"), L.T("Oyun kaydı henüz yazılmadı. Oyunda biraz ilerle ya da bir kez uyu/kaydet, sonra arkadaşın tekrar katılsın.", "The game has not been saved yet. Play a little or sleep/save once, then your friend joins again."));
                var nr = _net.Begin(Msg.WorldNotReady); nr.Put(L.Both("Sunucunun kaydı henüz yazılmadı, birazdan tekrar denenecek", "The host's game has not been saved yet, trying again shortly"));
                _net.SendTo(p.Id, nr, DeliveryMethod.ReliableOrdered);
                return;
            }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(p.Path); }
            catch (IOException) { _pending.Insert(0, p); return; } // still being written, try next frame
            var w = _net.Begin(Msg.WorldData);
            // PutBytesWithLength uses a 16-bit length (max 64 KB); saves are bigger, so use a 32-bit length
            w.Put(bytes.Length);
            w.Put(bytes);
            _net.SendTo(p.Id, w, DeliveryMethod.ReliableOrdered);
            CoopMod.Log($"World sent to {p.Id}: {bytes.Length} bytes");
        }

        private void ClientUpdate(float now)
        {
            switch (_state)
            {
                case ClientState.WaitingForWorld:
                    WarnEarlyLoad();
                    if (_nextRetry > 0 && now >= _nextRetry) { _nextRetry = 0; RequestWorld(); }
                    break;
                case ClientState.WaitingForSlot:
                {
                    WarnEarlyLoad();
                    bool ready = false;
                    ready = SafeHas(_slotName);
                    if (!ready && now - _stateSince < 15f) return;
                    if (!ready)
                    {
                        CoopMod.Popup(L.T("ORTAK DÜNYA", "SHARED WORLD"), L.T("Ortak dünya kaydı hazırlandı ama oyun listesine eklenemedi. Oyunu yeniden başlatıp tekrar katıl.", "The shared world save was made but could not be added to the game's list. Restart the game and join again."));
                        _state = ClientState.Idle; return;
                    }
                    // The game only loads a save cleanly through its own Load screen (calling the loader
                    // directly skips setup, and "Continue" may pick another slot). So: the player opens
                    // Survival > Load and the mod clicks the co-op slot for them.
                    CoopMod.Popup(L.T("ORTAK DÜNYA HAZIR", "SHARED WORLD READY"), L.T("Hayatta Kalma > Yükle ekranını aç. ORTAK DUNYA kendiliğinden açılacak (açılmazsa listeden sen seç).", "Open Survival > Load. ORTAK DUNYA (the shared world) opens by itself (if not, pick it from the list)."));
                    CoopMod.Status(L.T("Ortak dünya hazır: Hayatta Kalma > Yükle'yi aç", "Shared world ready: open Survival > Load"));
                    _state = ClientState.WaitingForUser;
                    _refreshedChooser = false; _loggedChooser = false;
                    _stateSince = now;
                    break;
                }
                case ClientState.WaitingForUser:
                {
                    if (WorldSync.InGame()) { _state = ClientState.Loading; _stateSince = now - 5f; break; } // loaded by hand
                    if (now < _nextPanelCheck) break;
                    _nextPanelCheck = now + 0.5f;
                    if (TryClickCoopSlot()) { _state = ClientState.Loading; _stateSince = now; }
                    break;
                }
                case ClientState.Loading when _net.IsConnected:
                    // the very first moment in the world: are we standing where the host's save put us?
                    if (!_startChecked && _built != null && _built.HasStart && WorldSync.InGame() && StartCheckReady())
                    {
                        _startChecked = true;
                        try
                        {
                            var want = new Vector3(_built.StartX, _built.StartY, _built.StartZ);
                            float off = Vector3.Distance(GameManager.GetPlayerTransform().position, want);
                            CoopMod.Log($"Loaded into the co-op world {off:0.0} m from where the host's save puts us" + (off > 6f ? " - THIS LOOKS LIKE AN OLDER COPY OF THE WORLD" : ""));
                            if (off > 6f) CoopMod.Hud(L.T("UYARI: eski bir dünya kopyası açılmış olabilir", "WARNING: an old copy of the world may have opened"));
                        }
                        catch { }
                    }
                    if (now - _stateSince > 5f && WorldSync.InGame())
                    {
                        string cur = null;
                        try { cur = SaveGameSystem.GetCurrentSaveName(); } catch { }
                        if (cur == _slotName) { _state = ClientState.InCoopWorld; CoopMod.DismissPopups(); CoopMod.Hud(L.T("Ortak dünyadasın", "You are in the shared world")); }
                        else if (!string.IsNullOrEmpty(cur))
                        {
                            _state = ClientState.Idle;
                            CoopMod.Hud(L.T($"Yanlış kayıt açıldı ({cur}). Ana menüye dön, Yükle'den ORTAK DUNYA'yı seç", $"The wrong save opened ({cur}). Go back to the main menu and pick ORTAK DUNYA under Load"));
                            CoopMod.Log($"Expected {_slotName}, but {cur} was loaded: item sync stays off");
                        }
                    }
                    break;
            }
        }
    }
}
