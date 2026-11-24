using System;
using System.Collections.Generic;
using Il2Cpp;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Loose items on the ground are the same for everyone: the host's.
    /// The game places some loose items (sticks, cans, stones) at random each time a scene loads, so two games
    /// that load the same save can lay them out differently. When a client is in the same scene as the host, it asks
    /// for the host's list of loose items and makes its own scene match: items both have are moved to the host's spot
    /// and given the host's id, items only the host has are made, and items only this game made are removed.
    /// From then on a pickup in one game removes the same item everywhere (by its shared id).
    /// </summary>
    internal sealed partial class ItemSync
    {
        private string _groundScene;          // the scene visit the ground items were matched for
        private bool _groundDone;
        private float _groundNextAsk;
        private int _groundAsks;
        internal string GroundLast = "-";
        internal int GroundMatched, GroundMade, GroundRemoved, GroundMoved, GroundLists;
        private readonly List<(GearItem Item, float At)> _recentLocalDrops = new();

        private void InitGround()
        {
            _net.OnMessage += OnGroundMessage;
            _net.OnStopped += ResetGround;
            _net.OnRemoteJoined += (id, name) => ResetGround();
        }

        private void ResetGround() { _groundScene = null; _groundDone = false; _groundNextAsk = 0f; _groundAsks = 0; }

        /// <summary>Remember what this game dropped itself, so a list from the host that is a moment older does not remove it.</summary>
        internal void NoteLocalDrop(GearItem gi)
        {
            if (gi == null) return;
            _recentLocalDrops.Add((gi, Time.realtimeSinceStartup));
            if (_recentLocalDrops.Count > 64) _recentLocalDrops.RemoveAt(0);
        }

        private bool DroppedHereLately(GearItem gi, float now)
        {
            foreach (var (item, at) in _recentLocalDrops)
                if (item != null && now - at < 30f && item.Pointer == gi.Pointer) return true;
            return false;
        }

        /// <summary>
        /// Loose items lying in the scene: not carried, not in a container, with a known prefab. Items the game has
        /// switched off for now (far from the player) count too, so two players standing apart still compare everything.
        /// </summary>
        private static List<GearItem> LooseItems()
        {
            var list = new List<GearItem>();
            foreach (var g in UnityEngine.Object.FindObjectsOfType<GearItem>(true))
            {
                if (g == null || g.m_InPlayerInventory) continue;
                var go = g.gameObject;
                if (go == null) continue;
                try { var sc = go.scene; if (!sc.IsValid() || sc.name == "DontDestroyOnLoad") continue; } catch { continue; }
                try { var t = go.transform.parent; if (t != null && t.GetComponentInParent<GearItem>() != null) continue; } catch { }
                try { if (g.IsInsideContainer()) continue; } catch { }
                if (string.IsNullOrEmpty(PrefabName(go))) continue;
                list.Add(g);
            }
            return list;
        }

        /// <summary>Test: loose items here, how many of a kind, and a checksum of kind and spot (rounded to 0.5 m) to compare two games.</summary>
        internal string GroundSummary(string prefab)
        {
            var items = LooseItems();
            var keys = new List<string>();
            int kind = 0;
            foreach (var g in items)
            {
                var n = PrefabName(g.gameObject); var p = g.transform.position;
                if (prefab != null && n.IndexOf(prefab, StringComparison.OrdinalIgnoreCase) < 0) continue;
                kind++;
                keys.Add($"{n}@{Mathf.Round(p.x * 2f)},{Mathf.Round(p.z * 2f)}");
            }
            keys.Sort(StringComparer.Ordinal);
            uint h = 2166136261;
            foreach (var k in keys) foreach (char c in k) { h ^= c; h *= 16777619; }
            int off = 0; foreach (var g in items) { try { if (!g.gameObject.activeInHierarchy) off++; } catch { } }
            return $"loose={items.Count} (switched off {off}) {(prefab ?? "all")}={kind} sum={h:x8}";
        }

        internal void GroundAskAgain() { _groundDone = false; _groundNextAsk = 0f; }

        private static bool IsLit(GearItem g) { try { return LightSync.LitKind(g) != 0; } catch { return false; } }

        /// <summary>Client: ask the host for its loose items once per scene visit, when both are in the same scene.</summary>
        private void TickGround(string scene, float now)
        {
            if (_net.Mode != NetMode.Client || string.IsNullOrEmpty(scene)) return;
            if (scene != _groundScene) { _groundScene = scene; _groundDone = false; _groundNextAsk = 0f; _groundAsks = 0; }
            if (_groundDone || now < _sceneReadyAt + 2f || now < _groundNextAsk) return;
            string hostScene = null;
            try { CoopMod.Instance.Players.TryGetState(Protocol.HostId, out hostScene, out _); } catch { }
            if (hostScene != scene) { _groundNextAsk = now + 5f; return; }
            _groundNextAsk = now + 12f;
            _groundAsks++;
            var w = _net.Begin(Msg.GroundRequest); w.Put(scene);
            _net.Send(w, DeliveryMethod.ReliableOrdered);
            if (_groundAsks == 1) CoopMod.Log($"Ground items: asking the host for its loose items in {scene}");
        }

        private void OnGroundMessage(byte from, NetDataReader r, Msg msg)
        {
            if (msg == Msg.GroundRequest)
            {
                if (_net.Mode != NetMode.Host) return;
                string scene = r.GetString();
                try { AnswerGround(from, scene); } catch (Exception e) { CoopMod.Log("Ground items (answer): " + e.Message); }
            }
            else if (msg == Msg.GroundList)
            {
                if (_net.Mode != NetMode.Client) return;
                try { ApplyGround(r); } catch (Exception e) { CoopMod.Log("Ground items (apply): " + e); }
            }
        }

        /// <summary>Host: send every loose item of this scene (each gets a shared id if it had none), or "not here yet".</summary>
        private void AnswerGround(byte to, string scene)
        {
            float now = Time.realtimeSinceStartup;
            bool ready = _worldShared() && WorldSync.InGame() && scene == CoopMod.CurrentScene() && _appliedScene == scene && now >= _sceneReadyAt + 1f;
            var w = _net.Begin(Msg.GroundList);
            w.Put(scene);
            if (!ready) { w.Put(-1); _net.SendTo(to, w, DeliveryMethod.ReliableOrdered); return; }
            var items = LooseItems();
            w.Put(items.Count);
            foreach (var g in items)
            {
                var go = g.gameObject;
                var guid = GuidOf(go);
                if (string.IsNullOrEmpty(guid))
                {
                    guid = System.Guid.NewGuid().ToString();
                    try { ObjectGuid.MaybeAttachObjectGuidAndRegister(go, guid); } catch { }
                }
                var p = go.transform.position; var q = go.transform.rotation;
                w.Put(guid ?? ""); w.Put(g.m_InstanceID); w.Put(PrefabName(go));
                w.Put(p.x); w.Put(p.y); w.Put(p.z); w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w);
                float hp = 0f; try { hp = g.CurrentHP; } catch { }
                w.Put(hp); w.Put(UnitsOf(g)); w.Put(IsLit(g)); w.Put(go.activeInHierarchy);
            }
            _net.SendTo(to, w, DeliveryMethod.ReliableOrdered);
            GroundLists++;
            CoopMod.Log($"Ground items: sent my {items.Count} loose items in {scene} to player {to}");
        }

        private struct HostItem { public string Guid, Prefab; public int Iid, Units; public Vector3 Pos; public Quaternion Rot; public float Hp; public bool Lit, Active; }

        /// <summary>Client: make this scene's loose items the host's.</summary>
        private void ApplyGround(NetDataReader r)
        {
            string scene = r.GetString();
            int n = r.GetInt();
            if (scene != CoopMod.CurrentScene() || scene != _groundScene) return;
            if (n < 0) { _groundNextAsk = Time.realtimeSinceStartup + 5f; return; }
            var host = new List<HostItem>(n);
            for (int i = 0; i < n; i++)
                host.Add(new HostItem
                {
                    Guid = r.GetString(), Iid = r.GetInt(), Prefab = r.GetString(),
                    Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
                    Rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()),
                    Hp = r.GetFloat(), Units = r.GetInt(), Lit = r.GetBool(), Active = r.GetBool(),
                });
            float now = Time.realtimeSinceStartup;
            var local = LooseItems();
            if (n == 0 && local.Count > 20) { CoopMod.Log($"Ground items: the host lists none but {local.Count} lie here; left as they are"); _groundDone = true; return; }

            var used = new HashSet<IntPtr>();
            var match = new GearItem[host.Count];
            // 1) same id
            var byGuid = new Dictionary<string, GearItem>();
            foreach (var g in local) { var id = GuidOf(g.gameObject); if (!string.IsNullOrEmpty(id)) byGuid[id] = g; }
            for (int i = 0; i < host.Count; i++)
                if (!string.IsNullOrEmpty(host[i].Guid) && byGuid.TryGetValue(host[i].Guid, out var g) && PrefabName(g.gameObject) == host[i].Prefab && used.Add(g.Pointer)) match[i] = g;
            // 2) the game's own item id (both games loaded the same save)
            for (int i = 0; i < host.Count; i++)
            {
                if (match[i] != null || host[i].Iid == 0) continue;
                foreach (var g in local)
                    if (!used.Contains(g.Pointer) && g.m_InstanceID == host[i].Iid && PrefabName(g.gameObject) == host[i].Prefab) { match[i] = g; used.Add(g.Pointer); break; }
            }
            // 3) the same kind of item lying (nearly) in the same spot
            for (int i = 0; i < host.Count; i++)
            {
                if (match[i] != null) continue;
                GearItem best = null; float bestD = 1.0f;
                foreach (var g in local)
                {
                    if (used.Contains(g.Pointer) || PrefabName(g.gameObject) != host[i].Prefab) continue;
                    float d = Vector3.Distance(g.transform.position, host[i].Pos);
                    if (d < bestD) { bestD = d; best = g; }
                }
                if (best != null) { match[i] = best; used.Add(best.Pointer); }
            }

            int matched = 0, moved = 0, made = 0, removed = 0, kept = 0;
            Applying = true;
            try
            {
                for (int i = 0; i < host.Count; i++)
                {
                    var h = host[i];
                    var g = match[i];
                    if (g != null)
                    {
                        matched++;
                        var go = g.gameObject;
                        if (Vector3.Distance(go.transform.position, h.Pos) > 0.25f) { go.transform.position = h.Pos; go.transform.rotation = h.Rot; moved++; }
                        if (!string.IsNullOrEmpty(h.Guid) && GuidOf(go) != h.Guid) { try { ObjectGuid.MaybeAttachObjectGuidAddOrReplace(go, h.Guid); } catch { } }
                        try { var st = g.GetStackableItem(); if (st != null && h.Units > 0) st.m_Units = h.Units; } catch { }
                        continue;
                    }
                    if (h.Lit) continue; // lit flares and torches come with their own drop message
                    var ng = GearItem.InstantiateGearItem(h.Prefab);
                    if (ng == null) continue;
                    var ngo = ng.gameObject;
                    ngo.transform.position = h.Pos; ngo.transform.rotation = h.Rot;
                    try { ng.CurrentHP = h.Hp; } catch { }
                    try { var st = ng.GetStackableItem(); if (st != null && h.Units > 0) st.m_Units = h.Units; } catch { }
                    if (!string.IsNullOrEmpty(h.Guid)) { try { ObjectGuid.MaybeAttachObjectGuidAddOrReplace(ngo, h.Guid); } catch { } }
                    ngo.SetActive(true);
                    made++;
                }
                foreach (var g in local)
                {
                    if (g == null || used.Contains(g.Pointer)) continue;
                    if (IsLit(g) || DroppedHereLately(g, now)) { kept++; continue; }
                    try { GearManager.DestroyGearObject(g); removed++; } catch { }
                }
            }
            finally { Applying = false; }
            _groundDone = true;
            GroundMatched += matched; GroundMoved += moved; GroundMade += made; GroundRemoved += removed;
            GroundLast = $"{scene}: host {host.Count}, here {local.Count}: matched {matched} (moved {moved}), made {made}, removed {removed}, kept {kept}";
            CoopMod.Log("Ground items now follow the host: " + GroundLast);
            if (made + removed + moved > 0)
                try { HUDMessage.AddMessage(L.T("Yerdeki eşyalar sunucuyla eşitlendi", "Items on the ground now match the host's"), 2f, false, true); } catch { }
        }
    }
}
