using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.PDID;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Stage 2: shared items. When someone picks an item up, drops one, or changes a container,
    /// the change is sent to everyone. Changes for a region you're not in wait until you get there.
    /// </summary>
    internal sealed partial class ItemSync
    {
        internal static ItemSync Instance;
        private readonly Network _net;
        private readonly Func<bool> _worldShared;

        private abstract class Op { public string Scene; public abstract void Apply(); }

        private readonly Dictionary<string, List<Op>> _queued = new();
        private string _appliedScene;
        private float _sceneReadyAt;
        internal static bool Applying; // true while we change the world because of a remote message

        public ItemSync(Network net, Func<bool> worldShared)
        {
            Instance = this;
            _net = net;
            _worldShared = worldShared;
            _net.OnMessage += OnMessage;
            _net.OnStopped += () => { _queued.Clear(); _sentInitial.Clear(); };
        }

        private bool Active => _net.IsConnected && _worldShared() && WorldSync.InGame();
        internal bool SharedWorld { get { try { return _worldShared(); } catch { return false; } } }

        // ---------------- identity ----------------
        internal struct ItemRef
        {
            public string Guid; public int InstanceId; public string Prefab; public Vector3 Pos;
            public void Write(NetDataWriter w) { w.Put(Guid ?? ""); w.Put(InstanceId); w.Put(Prefab ?? ""); w.Put(Pos.x); w.Put(Pos.y); w.Put(Pos.z); }
            public static ItemRef Read(NetDataReader r) => new() { Guid = r.GetString(), InstanceId = r.GetInt(), Prefab = r.GetString(), Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()) };
        }

        internal static string PrefabName(GameObject go)
        {
            var n = go.name ?? "";
            int i = n.IndexOf('(');
            if (i > 0) n = n.Substring(0, i);
            return n.Trim();
        }

        /// <summary>Stack size (e.g. pills in an antibiotics bottle). The field can be empty, the getter isn't.</summary>
        internal static int UnitsOf(GearItem g)
        {
            try { var st = g.GetStackableItem(); if (st != null) return st.m_Units; } catch { }
            return 1;
        }

        internal static string PrefabName2(string n)
        {
            n ??= "";
            int i = n.IndexOf('(');
            if (i > 0) n = n.Substring(0, i);
            return n.Trim();
        }

        internal static string GuidOf(GameObject go)
        {
            try { return ObjectGuid.MaybeGetGuidFromGameObject(go); } catch { return null; }
        }

        private static ItemRef RefOf(GearItem gi) => new()
        {
            Guid = GuidOf(gi.gameObject),
            InstanceId = gi.m_InstanceID,
            Prefab = PrefabName(gi.gameObject),
            Pos = gi.transform.position,
        };

        private static GearItem Find(ItemRef r)
        {
            if (!string.IsNullOrEmpty(r.Guid))
            {
                try
                {
                    var go = PdidTable.GetGameObject(r.Guid);
                    if (go != null) { var g = go.GetComponent<GearItem>(); if (g != null && !g.m_InPlayerInventory) return g; }
                }
                catch { }
            }
            // GearManager.m_Gear does not hold every item in the scene, so look at all GearItems
            // TLD keeps its own item ids in the save, so two copies of the same save share them
            GearItem best = null; float bestD = 1.5f, nearestSame = float.MaxValue;
            var all = UnityEngine.Object.FindObjectsOfType<GearItem>();
            if (r.InstanceId != 0)
                foreach (var g in all)
                    if (g != null && !g.m_InPlayerInventory && g.m_InstanceID == r.InstanceId && PrefabName(g.gameObject) == r.Prefab) return g;
            foreach (var g in all)
            {
                if (g == null || g.m_InPlayerInventory) continue;
                if (PrefabName(g.gameObject) != r.Prefab) continue;
                float d = Vector3.Distance(g.transform.position, r.Pos);
                if (d < nearestSame) nearestSame = d;
                if (d < bestD) { bestD = d; best = g; }
            }
            if (best == null)
                CoopMod.Log($"Find {r.Prefab}: id {r.InstanceId}, guid '{r.Guid}' not found here, nearest same item {(nearestSame == float.MaxValue ? "none" : nearestSame.ToString("0.00") + " m")} away");
            return best;
        }

        // ---------------- local events (from Harmony patches) ----------------
        internal void OnLocalPickup(GearItem gi)
        {
            if (Applying || !Active || gi == null || gi.m_InPlayerInventory) return;
            var r = RefOf(gi);
            var w = _net.Begin(Msg.ItemRemoved); w.Put(CoopMod.CurrentScene()); r.Write(w);
            _net.Send(w, DeliveryMethod.ReliableOrdered);
        }

        internal void OnLocalDrop(GearItem dropped, bool quiet = false)
        {
            if (Applying || !Active || dropped == null) return;
            var go = dropped.gameObject;
            var guid = GuidOf(go);
            if (string.IsNullOrEmpty(guid))
            {
                guid = System.Guid.NewGuid().ToString();
                try { ObjectGuid.MaybeAttachObjectGuidAndRegister(go, guid); } catch (Exception e) { CoopMod.Log("Attach guid: " + e.Message); }
            }
            if (!quiet) CoopMod.Log($"Dropped {PrefabName(go)}");
            var w = _net.Begin(Msg.ItemSpawned);
            w.Put(CoopMod.CurrentScene());
            w.Put(guid);
            w.Put(PrefabName(go));
            var p = go.transform.position; var q = go.transform.rotation;
            w.Put(p.x); w.Put(p.y); w.Put(p.z); w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w);
            w.Put(dropped.CurrentHP);
            w.Put(UnitsOf(dropped));
            byte lit = 0; try { lit = LightSync.LitKind(dropped); } catch { }
            w.Put(lit);
            string lightState = "";
            try
            {
                if (lit == LightSync.Flare) lightState = dropped.m_FlareItem.Serialize() ?? "";
                else if (lit == LightSync.Torch) lightState = dropped.m_TorchItem.Serialize() ?? "";
            }
            catch { }
            w.Put(lightState);
            if (lit != 0 && !quiet) CoopMod.Log($"Dropped a lit {PrefabName(go)} (light {lit})");
            _net.Send(w, DeliveryMethod.ReliableOrdered);
            if (!quiet) { try { SendDroppedCarcass(dropped); } catch { } }
        }

        private readonly List<(GearItem Item, float At)> _pendingDrops = new();

        /// <summary>Placed or thrown items: send once they have come to rest.</summary>
        internal void DropLater(GearItem gi, float delay)
        {
            if (Applying || gi == null) return;
            _pendingDrops.Add((gi, Time.realtimeSinceStartup + delay));
        }

        private sealed class Tracked { public GearItem Item; public Vector3 Sent; public float Quiet, Until, Next; }
        private readonly List<Tracked> _tracked = new();

        /// <summary>A thrown item: send it right away, then follow it until it comes to rest.</summary>
        internal void Track(GearItem gi)
        {
            if (Applying || gi == null || !Active) return;
            foreach (var t in _tracked) if (t.Item != null && t.Item.Pointer == gi.Pointer) return;
            OnLocalDrop(gi);
            float now = Time.realtimeSinceStartup;
            _tracked.Add(new Tracked { Item = gi, Sent = gi.transform.position, Quiet = now, Until = now + 10f, Next = now + 0.15f });
        }

        private void FollowTracked()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                var t = _tracked[i];
                if (now < t.Next) continue;
                t.Next = now + 0.15f;
                try { var inv = GameManager.GetInventoryComponent(); if (inv != null && t.Item != null && inv.FindByInstanceID(t.Item.m_InstanceID) != null) { _tracked.RemoveAt(i); continue; } } catch { }
                Vector3 p;
                try
                {
                    if (t.Item == null || t.Item.gameObject == null || !t.Item.gameObject.activeInHierarchy) { _tracked.RemoveAt(i); continue; }
                    p = t.Item.transform.position;
                }
                catch { _tracked.RemoveAt(i); continue; }
                if ((p - t.Sent).sqrMagnitude > 0.0025f) { OnLocalDrop(t.Item, true); t.Sent = p; t.Quiet = now; }
                else if (now - t.Quiet > 1.5f || now > t.Until)
                {
                    OnLocalDrop(t.Item, true); // final resting place
                    CoopMod.Log($"{PrefabName(t.Item.gameObject)} came to rest");
                    _tracked.RemoveAt(i);
                }
            }
        }

        // a freshly made flare resets itself on its first frame, so light it a moment later (and check)
        private sealed class Relit { public GearItem Item; public byte Kind; public string State; public float At; public int Tries; }
        private readonly List<Relit> _relights = new();

        internal void RelightSoon(GearItem gi, byte kind, string state)
        {
            if (gi == null) return;
            foreach (var r in _relights) if (r.Item != null && r.Item.Pointer == gi.Pointer) return;
            try { if (LightSync.LitKind(gi) == kind) return; } catch { }
            _relights.Add(new Relit { Item = gi, Kind = kind, State = state, At = Time.realtimeSinceStartup + 0.2f });
        }

        private void DoRelights()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = _relights.Count - 1; i >= 0; i--)
            {
                var r = _relights[i];
                if (now < r.At) continue;
                bool done = true;
                try
                {
                    if (r.Item != null && r.Item.gameObject != null && LightSync.LitKind(r.Item) != r.Kind)
                    {
                        Applying = true;
                        try { LightSync.Relight(r.Item, r.Kind, r.State); } finally { Applying = false; }
                        if (LightSync.LitKind(r.Item) != r.Kind && ++r.Tries < 3) { done = false; r.At = now + 0.4f * r.Tries; }
                    }
                }
                catch (Exception e) { CoopMod.Log("Relight: " + e.Message); }
                if (done) _relights.RemoveAt(i);
            }
        }

        private void FlushPendingDrops()
        {
            DoRelights();
            FollowTracked();
            float now = Time.realtimeSinceStartup;
            for (int i = _pendingDrops.Count - 1; i >= 0; i--)
            {
                if (now < _pendingDrops[i].At) continue;
                var gi = _pendingDrops[i].Item;
                _pendingDrops.RemoveAt(i);
                try
                {
                    if (gi == null || gi.gameObject == null || !gi.gameObject.activeInHierarchy) continue;
                }
                catch { continue; }
                OnLocalDrop(gi);
            }
        }

        private readonly HashSet<string> _sentInitial = new();

        /// <summary>First time this container is opened here: share what's inside (the loot roll).</summary>
        internal void OnLocalContainerOpened(Container c)
        {
            if (Applying || !Active || c == null) return;
            var key = ContainerKey(c);
            if (!_sentInitial.Add(key)) return;
            string text;
            try { text = c.Serialize(); } catch (Exception e) { CoopMod.Log("Container serialize: " + e.Message); return; }
            if (string.IsNullOrEmpty(text)) return;
            var w = _net.Begin(Msg.ContainerState);
            WriteContainer(w, c);
            w.Put(text);
            _net.Send(w, DeliveryMethod.ReliableOrdered);
        }

        private static void WriteContainer(NetDataWriter w, Container c)
        {
            w.Put(CoopMod.CurrentScene());
            w.Put(GuidOf(c.gameObject) ?? "");
            var p = c.transform.position; w.Put(p.x); w.Put(p.y); w.Put(p.z);
        }

        internal static Container FindContainer(string guid, Vector3 pos)
        {
            Container c = null;
            if (!string.IsNullOrEmpty(guid))
            {
                try { var go = PdidTable.GetGameObject(guid); if (go != null) c = go.GetComponent<Container>(); } catch { }
            }
            if (c == null)
            {
                float best = 0.6f;
                foreach (var cand in UnityEngine.Object.FindObjectsOfType<Container>())
                {
                    float d = Vector3.Distance(cand.transform.position, pos);
                    if (d < best) { best = d; c = cand; }
                }
            }
            if (c != null && c.m_NotPopulated)
            {
                try { c.InstantiateContents(); } catch (Exception e) { CoopMod.Log("InstantiateContents: " + e.Message); }
            }
            return c;
        }

        private static string ContainerKey(Container c)
        {
            var g = GuidOf(c.gameObject);
            if (!string.IsNullOrEmpty(g)) return g;
            var p = c.transform.position;
            return $"{p.x:0.0},{p.y:0.0},{p.z:0.0}";
        }

        // ---------------- remote events ----------------
        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (msg != Msg.ItemRemoved && msg != Msg.ItemSpawned && msg != Msg.ContainerState
                && msg != Msg.ContainerItemAdded && msg != Msg.ContainerItemRemoved && msg != Msg.FireState
                && msg != Msg.CarcassState && msg != Msg.CarcassGone && msg != Msg.CarcassRequest) return;
            if (msg == Msg.CarcassRequest) { if (_net.Mode == NetMode.Host && _worldShared()) { try { AnswerCarcassRequest(r); } catch (Exception e) { CoopMod.Log("Carcass request: " + e.Message); } } return; }
            if (!_worldShared()) return;

            // host relays everything to the other clients first (raw copy of the payload)
            if (_net.Mode == NetMode.Host)
            {
                var relay = _net.Begin(msg);
                relay.Put(r.RawData, r.Position, r.AvailableBytes);
                _net.Send(relay, DeliveryMethod.ReliableOrdered, from);
            }
            // a region copy that already contains this change was loaded meanwhile (scene hand-over): passing it on was all
            if (Network.RelayOnly) return;

            Op op = msg switch
            {
                Msg.ItemRemoved => new RemoveOp { Scene = r.GetString(), Ref = ItemRef.Read(r) },
                Msg.ItemSpawned => SpawnOp.Read(r),
                Msg.ContainerState => ContainerOp.Read(r),
                Msg.FireState => FireOp.Read(r),
                Msg.CarcassState => CarcassOp.Read(r, false),
                Msg.CarcassGone => CarcassOp.Read(r, true),
                _ => ContainerItemOp.Read(r, msg == Msg.ContainerItemAdded),
            };
            Enqueue(op);
        }

        /// <summary>Scene hand-over: a full copy of this region is about to be loaded, so changes waiting for it are already in it.</summary>
        internal int DropQueued(string scene)
        {
            if (scene == null || !_queued.TryGetValue(scene, out var list)) return 0;
            int n = list.Count; list.Clear();
            return n;
        }

        private void Enqueue(Op op)
        {
            if (WorldSync.InGame() && op.Scene == CoopMod.CurrentScene() && Time.realtimeSinceStartup >= _sceneReadyAt && _appliedScene == op.Scene)
            {
                SafeApply(op);
                return;
            }
            if (!_queued.TryGetValue(op.Scene ?? "", out var list)) _queued[op.Scene ?? ""] = list = new List<Op>();
            list.Add(op);
        }

        private static void SafeApply(Op op)
        {
            Applying = true;
            try { op.Apply(); }
            catch (Exception e) { CoopMod.Log($"Apply {op.GetType().Name} failed: {e.Message}"); }
            finally { Applying = false; }
        }

        // ---------------- container watcher ----------------
        // The game moves items in and out of containers through internal calls that Harmony can't see,
        // so we watch the open container window and compare its contents.
        private static bool _loggedTakeError, _loggedDiffError, _loggedCarcassError;
        private struct Snap { public int Iid; public string Prefab; public float Hp; public int Units; }
        private Panel_Container _panel;
        private float _nextPoll, _nextPanelSearch;
        private bool _wasOpen, _loggedInactive;

        private static List<Snap> Take(Container c)
        {
            var list = new List<Snap>();
            // items the game has already created
            var inst = new Il2CppSystem.Collections.Generic.List<GearItem>();
            c.GetItems(inst);
            for (int i = 0; i < inst.Count; i++)
            {
                var g = inst[i];
                if (g == null) continue;
                list.Add(new Snap { Iid = g.m_InstanceID, Prefab = PrefabName(g.gameObject), Hp = g.CurrentHP, Units = UnitsOf(g) });
            }
            // entries known only by name (not created yet) — GetItems() leaves these out
            try
            {
                var items = c.m_Items;
                for (int i = 0; items != null && i < items.Count; i++)
                {
                    var e = items[i];
                    if (e.m_GearItem == null && !string.IsNullOrEmpty(e.m_GearItemName))
                        list.Add(new Snap { Iid = 0, Prefab = PrefabName2(e.m_GearItemName), Hp = 100f, Units = 1 });
                }
            }
            catch (Exception ex) { if (!_loggedTakeError) { _loggedTakeError = true; CoopMod.Log("Reading container entries: " + ex.Message); } }
            return list;
        }

        // Every container near the player is compared with its last known contents, whether it is
        // changed through the container window, the search-and-take screen, or anything else.
        private readonly Dictionary<IntPtr, List<Snap>> _known = new();
        private readonly HashSet<IntPtr> _seenUnsearched = new();
        private readonly Dictionary<IntPtr, float> _searchedAt = new();
        private Container[] _all;
        private float _nextList;

        private void ResetContainers() { _known.Clear(); _seenUnsearched.Clear(); _searchedAt.Clear(); _all = null; _nextList = 0f; }

        /// <summary>After a remote change to a container, don't report it back as ours, and refresh the window.</summary>
        internal void ResnapIfWatched(Container c)
        {
            if (c == null) return;
            try { if (c.m_Inspected) _known[c.Pointer] = Take(c); } catch { }
            try { if (_panel != null && _panel.IsEnabled() && _panel.m_Container != null && _panel.m_Container.Pointer == c.Pointer) _panel.ForceRefresh(); }
            catch (Exception e) { CoopMod.Log("Container refresh: " + e.Message); }
        }

        private static string Names(List<Snap> l)
        {
            string names = "";
            foreach (var sn in l) names += sn.Prefab + (sn.Iid == 0 ? "(?)" : "") + (sn.Units != 1 ? "x" + sn.Units : "") + " ";
            return names;
        }

        private void WatchContainers()
        {
            float now = Time.realtimeSinceStartup;
            if (_panel == null && now >= _nextPanelSearch)
            {
                _nextPanelSearch = now + 1f;
                try { _panel = InterfaceManager.GetPanel<Panel_Container>(); } catch (Exception e) { CoopMod.Log("Container panel: " + e.Message); }
            }
            Container open = null;
            try { if (_panel != null && _panel.IsEnabled()) open = _panel.m_Container; } catch { _panel = null; }
            bool isOpen = open != null;
            if (isOpen != _wasOpen) { _wasOpen = isOpen; CoopMod.Log(isOpen ? $"Container window opened: {PrefabName(open.gameObject)}" : "Container window closed"); }

            if (now < _nextPoll) return;
            _nextPoll = now + 0.25f;
            if (_all == null || now >= _nextList)
            {
                _nextList = now + 5f;
                try { _all = UnityEngine.Object.FindObjectsOfType<Container>(); } catch (Exception e) { CoopMod.Log("Container list: " + e.Message); }
            }
            var pt = GameManager.GetPlayerTransform();
            if (pt == null) return;
            var me = pt.position;
            if (open != null) Check(open, now);
            if (_all == null) return;
            foreach (var c in _all)
            {
                if (c == null || (open != null && c.Pointer == open.Pointer)) continue;
                Vector3 cp;
                try { cp = c.transform.position; } catch { continue; }
                if ((cp - me).sqrMagnitude > 25f) continue; // 5 m
                Check(c, now);
            }
        }

        private void Check(Container c, float now)
        {
            var ptr = c.Pointer;
            bool search, insp;
            try { search = c.m_SearchInProgress; insp = c.m_Inspected; } catch { return; }
            if (!insp)
            {
                // right after loading, containers briefly look unsearched before the save is restored
                if (_appliedScene == CoopMod.CurrentScene() && now >= _sceneReadyAt) _seenUnsearched.Add(ptr);
                return;
            }
            if (search) return;
            if (!_known.TryGetValue(ptr, out var old))
            {
                if (_seenUnsearched.Contains(ptr))
                {
                    // searched here just now: let the found items settle, then share the result
                    if (!_searchedAt.TryGetValue(ptr, out var t)) { _searchedAt[ptr] = now; CoopMod.Log($"Searched {PrefabName(c.gameObject)} here"); return; }
                    if (now - t < 1f) return;
                }
                var first = Take(c);
                _known[ptr] = first;
                CoopMod.Log($"Watching {PrefabName(c.gameObject)}: {Names(first)}");
                OnLocalContainerOpened(c);
                return;
            }
            _known[ptr] = Diff(c, old);
        }

        private List<Snap> Diff(Container c, List<Snap> _snap)
        {
            List<Snap> cur;
            try { cur = Take(c); }
            catch (Exception ex) { if (!_loggedDiffError) { _loggedDiffError = true; CoopMod.Log("Container diff: " + ex); } return _snap; }
            var prefabs = new HashSet<string>();
            foreach (var o in _snap) prefabs.Add(o.Prefab);
            foreach (var n in cur) prefabs.Add(n.Prefab);
            foreach (var p in prefabs)
            {
                var olds = _snap.FindAll(x => x.Prefab == p);
                var news = cur.FindAll(x => x.Prefab == p);
                int oldUnits = 0, newUnits = 0;
                foreach (var x in olds) oldUnits += x.Units;
                foreach (var x in news) newUnits += x.Units;
                if (olds.Count == news.Count && oldUnits == newUnits) continue;
                if (news.Count < olds.Count)
                {
                    // removed: prefer reporting the ones that no longer exist by id
                    var gone = olds.FindAll(o => o.Iid == 0 || !news.Exists(n => n.Iid == o.Iid));
                    for (int k = 0; k < olds.Count - news.Count; k++) SendContainerItem(c, k < gone.Count ? gone[k] : olds[k], false);
                }
                else if (news.Count > olds.Count)
                {
                    var added = news.FindAll(n => n.Iid == 0 || !olds.Exists(o => o.Iid == n.Iid));
                    for (int k = 0; k < news.Count - olds.Count; k++) SendContainerItem(c, k < added.Count ? added[k] : news[k], true);
                }
                else
                {
                    // same count, different amount (part of a stack taken): replace one entry
                    for (int k = 0; k < news.Count; k++)
                    {
                        var o = olds.Find(x => x.Iid == news[k].Iid && x.Iid != 0);
                        if (o.Prefab != null && o.Units != news[k].Units) { SendContainerItem(c, o, false); SendContainerItem(c, news[k], true); break; }
                    }
                }
            }
            return cur;
        }

        private void SendContainerItem(Container c, Snap it, bool added)
        {
            if (Applying || !Active) return;
            var w = _net.Begin(added ? Msg.ContainerItemAdded : Msg.ContainerItemRemoved);
            CoopMod.Log($"Container {(added ? "+" : "-")} {it.Prefab} x{it.Units} @ {PrefabName(c.gameObject)}");
            WriteContainer(w, c);
            w.Put(it.Prefab); w.Put(it.Iid); w.Put(it.Hp); w.Put(it.Units);
            _net.Send(w, DeliveryMethod.ReliableOrdered);
        }

        public void Update()
        {
            if (!WorldSync.InGame()) { _appliedScene = null; return; }
            var scene = CoopMod.CurrentScene();
            if (scene != _appliedScene)
            {
                // new scene: give the game a few seconds to restore its own objects first
                _appliedScene = scene;
                ResetContainers(); ResetFires(); ResetCarcasses();
                _sceneReadyAt = Time.realtimeSinceStartup + 4f;
                return;
            }
            // what friends changed here while we were elsewhere comes first, so the watchers below
            // never report our stale copy of something as news
            if (Time.realtimeSinceStartup >= _sceneReadyAt && scene != null && _queued.TryGetValue(scene, out var list) && list.Count > 0)
            {
                CoopMod.Log($"Applying {list.Count} queued change(s) for {scene}");
                foreach (var op in list) SafeApply(op);
                list.Clear();
            }
            if (Active)
            {
                _loggedInactive = false;
                try { WatchContainers(); } catch (Exception e) { CoopMod.Log("Container watch: " + e); }
                try { FlushPendingDrops(); } catch (Exception e) { CoopMod.Log("Pending drops: " + e.Message); }
                try { WatchCarcasses(); DoCarcassRetries(); } catch (Exception e) { if (!_loggedCarcassError) { _loggedCarcassError = true; CoopMod.Log("Carcass watch: " + e); } }
                try { WatchFires(); } catch (Exception e) { if (!_loggedFireError) { _loggedFireError = true; CoopMod.Log("Fire watch: " + e); } }
            }
            else if (_net.Mode != NetMode.Off && !_loggedInactive)
            {
                _loggedInactive = true;
                CoopMod.Log($"Item sync is OFF here (connected {_net.IsConnected}, shared world {_worldShared()})");
            }
        }

        // ---------------- operations ----------------
        private sealed class RemoveOp : Op
        {
            public ItemRef Ref;
            public override void Apply()
            {
                var gi = Find(Ref);
                if (gi == null) { CoopMod.Log($"Remove: {Ref.Prefab} not found"); return; }
                Instance._lastRemoteRemoval = Time.realtimeSinceStartup;
                GearManager.DestroyGearObject(gi);
            }
        }

        private sealed class SpawnOp : Op
        {
            public string Guid, Prefab; public Vector3 Pos; public Quaternion Rot; public float Hp; public int Units; public byte Lit; public string LightState;
            public static SpawnOp Read(NetDataReader r) => new()
            {
                Scene = r.GetString(), Guid = r.GetString(), Prefab = r.GetString(),
                Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
                Rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()),
                Hp = r.GetFloat(), Units = r.GetInt(),
                Lit = r.AvailableBytes > 0 ? r.GetByte() : (byte)0,
                LightState = r.AvailableBytes > 0 ? r.GetString() : "",
            };
            public override void Apply()
            {
                if (!string.IsNullOrEmpty(Guid))
                {
                    GameObject here = null;
                    try { here = PdidTable.GetGameObject(Guid); } catch { }
                    if (here != null)
                    {
                        // already here (e.g. it was placed again somewhere else): just move it
                        here.transform.position = Pos;
                        here.transform.rotation = Rot;
                        var hg = here.GetComponent<GearItem>();

                        if (Lit != 0 && hg != null) Instance.RelightSoon(hg, Lit, LightState);
                        return;
                    }
                }
                var gi = GearItem.InstantiateGearItem(Prefab);
                if (gi == null) { CoopMod.Log($"Spawn: unknown prefab {Prefab}"); return; }
                var go = gi.gameObject;
                go.transform.position = Pos;
                go.transform.rotation = Rot;
                try { gi.CurrentHP = Hp; } catch { }
                try { var st = gi.GetStackableItem(); if (st != null && Units > 0) st.m_Units = Units; } catch { }
                if (!string.IsNullOrEmpty(Guid))
                {
                    try { ObjectGuid.MaybeAttachObjectGuidAddOrReplace(go, Guid); } catch (Exception e) { CoopMod.Log("Spawn guid: " + e.Message); }
                }
                go.SetActive(true);
                CoopMod.Log($"Got dropped {Prefab}");
                if (Lit != 0) Instance.RelightSoon(gi, Lit, LightState);
            }
        }

        private sealed class ContainerOp : Op
        {
            public string Guid, Text; public Vector3 Pos;
            public static ContainerOp Read(NetDataReader r) => new()
            {
                Scene = r.GetString(), Guid = r.GetString(),
                Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
                Text = r.GetString(),
            };
            public override void Apply()
            {
                var c = FindContainer(Guid, Pos);
                if (c == null) { CoopMod.Log("Container not found at " + Pos); return; }
                Instance._sentInitial.Add(ContainerKey(c)); // its contents are now known on both sides
                if (c.m_Inspected) return; // already searched here: later changes arrive item by item
                var items = new Il2CppSystem.Collections.Generic.List<GearItem>();
                c.GetItems(items);
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var gi = items[i];
                    c.RemoveGear(gi, false);
                    GearManager.DestroyGearObject(gi);
                }
                try { c.m_GearToInstantiate?.Clear(); } catch { }
                c.Deserialize(Text, new Il2CppSystem.Collections.Generic.List<GearItem>());
                Instance.ResnapIfWatched(c);
            }
        }

        private sealed class ContainerItemOp : Op
        {
            public bool Added; public string Guid, Prefab; public Vector3 Pos; public int Iid, Units; public float Hp;
            public static ContainerItemOp Read(NetDataReader r, bool added) => new()
            {
                Added = added, Scene = r.GetString(), Guid = r.GetString(),
                Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
                Prefab = r.GetString(), Iid = r.GetInt(), Hp = r.GetFloat(), Units = r.GetInt(),
            };
            public override void Apply()
            {
                var c = FindContainer(Guid, Pos);
                CoopMod.Log($"Got container {(Added ? "+" : "-")} {Prefab} x{Units}" + (c == null ? " (container not found)" : ""));
                if (c == null) return;
                if (Added)
                {
                    var gi = GearItem.InstantiateGearItem(Prefab);
                    if (gi == null) { CoopMod.Log("Container add: unknown prefab " + Prefab); return; }
                    try { gi.CurrentHP = Hp; } catch { }
                    try { var st = gi.GetStackableItem(); if (st != null && Units > 0) st.m_Units = Units; } catch { }
                    c.AddGear(gi);
                    Instance.ResnapIfWatched(c);
                    return;
                }
                try { c.InstantiateContents(); } catch { }
                var items = new Il2CppSystem.Collections.Generic.List<GearItem>();
                c.GetItems(items);
                GearItem match = null;
                for (int i = 0; i < items.Count && match == null; i++)
                    if (items[i] != null && items[i].m_InstanceID == Iid && PrefabName(items[i].gameObject) == Prefab) match = items[i];
                // no id match (items a friend put in get a new id here): the same kind with the same amount, else any of that kind
                for (int i = 0; i < items.Count && match == null; i++)
                    if (items[i] != null && PrefabName(items[i].gameObject) == Prefab && UnitsOf(items[i]) == Units) match = items[i];
                for (int i = 0; i < items.Count && match == null; i++)
                    if (items[i] != null && PrefabName(items[i].gameObject) == Prefab) match = items[i];
                if (match == null) { CoopMod.Log($"Container remove: {Prefab} not in container"); return; }
                c.RemoveGear(match, false);
                GearManager.DestroyGearObject(match);
                Instance.ResnapIfWatched(c);
            }
        }
    }

    // ---------------- Harmony patches ----------------
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.ProcessPickupItemInteraction))]
    internal static class PickupPatch
    {
        private static void Prefix(GearItem item)
        {
            try { ItemSync.Instance?.OnLocalPickup(item); } catch (Exception e) { CoopMod.Log("Pickup hook: " + e.Message); }
        }
    }

    // placing an item with the placement view (from the bag or moving one on the ground)
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.PlaceMeshInWorld))]
    internal static class PlacePatch
    {
        private static void Prefix(PlayerManager __instance, out GearItem __state)
        {
            __state = null;
            try { __state = __instance.m_ObjectToPlaceGearItem; } catch { }
        }
        private static void Postfix(GearItem __state)
        {
            try { if (__state != null) { CoopMod.Log($"Placed {ItemSync.PrefabName(__state.gameObject)}"); ItemSync.Instance?.DropLater(__state, 0.3f); } }
            catch (Exception e) { CoopMod.Log("Place hook: " + e.Message); }
        }
    }

    // thrown flares, stones, etc.: the game makes a copy that flies off; send it once it has landed
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.InstantiateCopyOfThrownObject))]
    internal static class ThrowPatch
    {
        private static void Postfix(GearItem __result)
        {
            try { if (__result != null) { CoopMod.Log($"Threw {ItemSync.PrefabName(__result.gameObject)}"); ItemSync.Instance?.Track(__result); } }
            catch (Exception e) { CoopMod.Log("Throw hook: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Throw))]
    internal static class ThrowStartPatch
    {
        private static void Prefix(GearItem gi)
        {
            try { if (gi != null) CoopMod.Log($"Throwing {ItemSync.PrefabName(gi.gameObject)}"); } catch { }
        }
    }

    // the moment the item leaves the hand
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.ReleaseThrownObject))]
    internal static class ThrowReleasePatch
    {
        private static void Postfix(PlayerManager __instance)
        {
            try
            {
                var gi = __instance.m_ThrownItem;
                CoopMod.Log($"Released thrown item: {(gi != null ? ItemSync.PrefabName(gi.gameObject) : "none")}");
                if (gi != null) ItemSync.Instance?.Track(gi);
            }
            catch (Exception e) { CoopMod.Log("Throw release hook: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(GearItem), nameof(GearItem.Drop))]
    internal static class DropPatch
    {
        private static void Postfix(GearItem __result)
        {
            try { ItemSync.Instance?.OnLocalDrop(__result); } catch (Exception e) { CoopMod.Log("Drop hook: " + e.Message); }
        }
    }

}
