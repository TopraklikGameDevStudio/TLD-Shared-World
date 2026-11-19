using System;
using System.Collections.Generic;
using Il2Cpp;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Shared carcasses: meat, hide and guts taken from a dead animal are gone for everyone.
    /// The host decides how much a new carcass holds; after that, whoever harvests sends the new state.
    /// </summary>
    internal sealed partial class ItemSync
    {
        private struct CarcassSig { public int MeatG; public int Hide, Gut; public Vector3 Pos; public string Kind, Guid; public float RemoteAt; }
        private readonly Dictionary<IntPtr, CarcassSig> _carcass = new();
        private BodyHarvest[] _bodies;
        private float _nextBodyList, _nextBodyPoll;

        internal float _lastRemoteRemoval = -10f;
        private void ResetCarcasses() { _carcass.Clear(); _bodies = null; _nextBodyList = 0f; _diedElsewhere.Clear(); }

        // Host: animals that have just died on a friend's game (the game that ran them decided the death). The host
        // stays the keeper of carcasses: once its own copy has fallen, what the carcass holds is sent from here,
        // however far away the host's player stands.
        private readonly List<(BaseAi Ai, float At)> _diedElsewhere = new();
        internal void ShareCarcassSoon(BaseAi ai)
        {
            if (ai != null && _net.Mode == NetMode.Host) _diedElsewhere.Add((ai, Time.realtimeSinceStartup + 1.5f));
        }

        private void ShareDiedElsewhere(float now)
        {
            for (int i = _diedElsewhere.Count - 1; i >= 0; i--)
            {
                var (ai, at) = _diedElsewhere[i];
                if (now < at) continue;
                _diedElsewhere.RemoveAt(i);
                try
                {
                    if (ai == null) continue;
                    var b = ai.GetComponent<BodyHarvest>();
                    if (b != null && IsCarcass(b)) SendCarcass(b, "died on a friend's game");
                    else CoopMod.Log($"{KindOf(ai.gameObject)} died on a friend's game, but the host's copy is no carcass (yet): nothing sent");
                }
                catch (Exception e) { CoopMod.Log("Carcass of an animal that died on a friend's game: " + e.Message); }
            }
        }

        private static string KindOf(GameObject go)
        {
            var n = PrefabName(go);
            int u = n.LastIndexOf('_');
            if (u > 0 && u < n.Length - 1)
            {
                bool digits = true;
                for (int i = u + 1; i < n.Length; i++) if (!char.IsDigit(n[i])) { digits = false; break; }
                if (digits) n = n.Substring(0, u);
            }
            return n;
        }

        /// <summary>Only real carcasses: living animals carry this component too.</summary>
        private static bool IsCarcass(BodyHarvest b)
        {
            try
            {
                if (!b.enabled || !b.gameObject.activeInHierarchy) return false;
                var ai = b.GetComponent<BaseAi>();
                if (ai != null && ai.GetAiMode() != AiMode.Dead) return false;
                return true;
            }
            catch { return false; }
        }

        private static CarcassSig SigOf(BodyHarvest b)
        {
            var s = new CarcassSig();
            try { s.MeatG = Mathf.RoundToInt(b.m_MeatAvailableKG.ToQuantity(1f) * 1000f); } catch { }
            try { s.Hide = b.m_HideAvailableUnits; } catch { }
            try { s.Gut = b.m_GutAvailableUnits; } catch { }
            try { s.Pos = b.transform.position; s.Kind = KindOf(b.gameObject); } catch { }
            try { s.Guid = GuidOf(b.gameObject) ?? ""; } catch { s.Guid = ""; }
            return s;
        }

        private void WatchCarcasses()
        {
            float now = Time.realtimeSinceStartup;
            if (_diedElsewhere.Count > 0) ShareDiedElsewhere(now);
            if (now < _nextBodyPoll || now < _sceneReadyAt) return;
            _nextBodyPoll = now + 0.5f;
            if (_bodies == null || now >= _nextBodyList)
            {
                _nextBodyList = now + 3f;
                _bodies = UnityEngine.Object.FindObjectsOfType<BodyHarvest>();
            }
            var pt = GameManager.GetPlayerTransform();
            if (pt == null) return;
            var me = pt.position;
            var seen = new HashSet<IntPtr>();
            foreach (var b in _bodies)
            {
                if (b == null || !IsCarcass(b)) continue;
                var ptr = b.Pointer;
                seen.Add(ptr);
                var cur = SigOf(b);
                if (!_carcass.TryGetValue(ptr, out var old))
                {
                    _carcass[ptr] = cur;
                    // a new carcass: the host's roll of how much meat/hide/guts it has counts for everyone
                    bool animal = false; try { animal = b.GetComponent<BaseAi>() != null; } catch { }
                    // harvesting a rabbit (or similar) turns the dead animal into a carcass item on the ground: share that item
                    if (!animal && now - _sceneReadyAt > 5f && (cur.Pos - me).sqrMagnitude < 3f * 3f)
                    {
                        GearItem gi = null; try { gi = b.GetComponent<GearItem>(); } catch { }
                        string g = null; try { if (gi != null) g = ObjectGuid.MaybeGetGuidFromGameObject(gi.gameObject); } catch { }
                        if (gi != null && string.IsNullOrEmpty(g))
                        {
                            CoopMod.Log($"New carcass item {cur.Kind} appeared here, sharing it");
                            OnLocalDrop(gi);
                        }
                    }
                    if (animal && _net.Mode == NetMode.Host && (cur.Pos - me).sqrMagnitude < 150f * 150f) SendCarcass(b, "new");
                    // a carcass showed up here (e.g. an animal the host killed before we arrived): ask the host what is left on it
                    if (animal && _net.Mode == NetMode.Client && now - _sceneReadyAt > 2f)
                    {
                        var rq = _net.Begin(Msg.CarcassRequest);
                        rq.Put(CoopMod.CurrentScene()); rq.Put(cur.Kind ?? ""); rq.Put(cur.Pos.x); rq.Put(cur.Pos.y); rq.Put(cur.Pos.z); rq.Put(GuidOf(b.gameObject) ?? "");
                        _net.SendTo(Protocol.HostId, rq, DeliveryMethod.ReliableOrdered);
                    }
                    continue;
                }
                cur.RemoteAt = old.RemoteAt;
                _carcass[ptr] = cur;
                if (cur.MeatG == old.MeatG && cur.Hide == old.Hide && cur.Gut == old.Gut) continue;
                if ((cur.Pos - me).sqrMagnitude > 10f * 10f) continue; // only what we harvest ourselves
                SendCarcass(b, "harvested");
            }
            // carcasses that vanished right next to us were used up or cut into quarters
            foreach (var key in new List<IntPtr>(_carcass.Keys))
            {
                if (seen.Contains(key)) continue;
                var last = _carcass[key];
                _carcass.Remove(key);
                // only when we used it up ourselves: right next to it, and not just after a friend's change to it
                if ((last.Pos - me).sqrMagnitude > 3.5f * 3.5f) continue;
                if (Time.realtimeSinceStartup - last.RemoteAt < 4f) continue;
                if (Time.realtimeSinceStartup - _lastRemoteRemoval < 1.5f) continue; // a friend picked it up
                var w = _net.Begin(Msg.CarcassGone);
                w.Put(CoopMod.CurrentScene()); w.Put(last.Kind ?? ""); w.Put(last.Pos.x); w.Put(last.Pos.y); w.Put(last.Pos.z);
                w.Put(last.Guid ?? "");
                _net.Send(w, DeliveryMethod.ReliableOrdered);
                CoopMod.Log($"Carcass {last.Kind} used up here");
            }
        }

        private void SendCarcass(BodyHarvest b, string why)
        {
            if (Applying || !Active) return;
            string text;
            try { text = b.Serialize(); } catch (Exception e) { CoopMod.Log("Carcass serialize: " + e.Message); return; }
            if (string.IsNullOrEmpty(text)) return;
            var s = SigOf(b);
            CoopMod.Log($"Carcass {why}: {s.Kind} meat {s.MeatG / 1000f:0.00} kg, hide {s.Hide}, guts {s.Gut}");
            var w = _net.Begin(Msg.CarcassState);
            w.Put(CoopMod.CurrentScene()); w.Put(s.Kind ?? ""); w.Put(s.Pos.x); w.Put(s.Pos.y); w.Put(s.Pos.z); w.Put(text);
            w.Put(GuidOf(b.gameObject) ?? "");
            _net.Send(w, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Host: a friend asks what is left on a carcass.</summary>
        internal void AnswerCarcassRequest(NetDataReader r)
        {
            var scene = r.GetString(); var kind = r.GetString();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()); var guid = r.GetString();
            if (scene != CoopMod.CurrentScene()) return;
            var b = FindCarcass(kind, pos, guid);
            if (b != null) SendCarcass(b, "asked");
        }

        /// <summary>A carcass item someone put down: its contents go with it.</summary>
        internal void SendDroppedCarcass(GearItem gi)
        {
            BodyHarvest b = null;
            try { b = gi.GetComponent<BodyHarvest>(); } catch { }
            if (b != null) SendCarcass(b, "dropped");
        }

        private static BodyHarvest FindCarcass(string kind, Vector3 pos, string guid, float radius = 15f)
        {
            if (!string.IsNullOrEmpty(guid))
            {
                try
                {
                    var go = Il2CppTLD.PDID.PdidTable.GetGameObject(guid);
                    if (go != null)
                    {
                        var bh = go.GetComponent<BodyHarvest>() ?? go.GetComponentInChildren<BodyHarvest>();
                        if (bh != null && IsCarcass(bh)) return bh;
                    }
                }
                catch { }
            }
            BodyHarvest best = null; float bestD = radius;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<BodyHarvest>())
            {
                if (b == null || !IsCarcass(b)) continue;
                if (!string.IsNullOrEmpty(kind) && KindOf(b.gameObject) != kind) continue;
                float d = Vector3.Distance(b.transform.position, pos);
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        private sealed class CarcassOp : Op
        {
            public string Kind, Text, Guid; public Vector3 Pos; public bool Gone;
            public static CarcassOp Read(NetDataReader r, bool gone)
            {
                var op = new CarcassOp { Gone = gone, Scene = r.GetString(), Kind = r.GetString(), Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()) };
                if (!gone) op.Text = r.GetString();
                op.Guid = r.AvailableBytes > 0 ? r.GetString() : "";
                return op;
            }
            public override void Apply()
            {
                // removing must hit the right one: by its id, else only one lying on that very spot
                var b = FindCarcass(Kind, Pos, Guid, Gone ? 3f : 15f);
                if (b == null) { CoopMod.Log($"Carcass {Kind} not found here (it may not have died here yet)"); Instance.RetryCarcass(this); return; }
                if (Gone)
                {
                    Instance._carcass.Remove(b.Pointer);
                    // take the whole thing away, not just its harvest data
                    GearItem gi = null; BaseAi ai = null;
                    try { gi = b.GetComponent<GearItem>(); ai = b.GetComponent<BaseAi>(); } catch { }
                    try { b.BodyHarvestDestroy(); } catch (Exception e) { CoopMod.Log("Carcass remove: " + e.Message); }
                    try
                    {
                        if (gi != null) GearManager.DestroyGearObject(gi);
                        else if (ai != null) UnityEngine.Object.Destroy(ai.gameObject);
                    }
                    catch (Exception e) { CoopMod.Log("Carcass remove (object): " + e.Message); }
                    CoopMod.Log($"Carcass {Kind} used up by a friend");
                    return;
                }
                b.Deserialize(Text, false);
                var s = SigOf(b);
                s.RemoteAt = Time.realtimeSinceStartup;
                Instance._carcass[b.Pointer] = s;
                CoopMod.Log($"Got carcass: {s.Kind} meat {s.MeatG / 1000f:0.00} kg, hide {s.Hide}, guts {s.Gut}");
            }
        }

        // the animal may still be falling over here when the carcass state arrives: try again shortly
        private readonly List<(CarcassOp Op, float At, int Tries)> _carcassRetry = new();
        private void RetryCarcass(CarcassOp op)
        {
            int tries = 0;
            for (int i = _carcassRetry.Count - 1; i >= 0; i--) if (_carcassRetry[i].Op == op) { tries = _carcassRetry[i].Tries; _carcassRetry.RemoveAt(i); }
            if (tries < 5) _carcassRetry.Add((op, Time.realtimeSinceStartup + 1.5f, tries + 1));
        }
        private void DoCarcassRetries()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = _carcassRetry.Count - 1; i >= 0; i--)
            {
                var (op, at, tries) = _carcassRetry[i];
                if (now < at) continue;
                _carcassRetry[i] = (op, float.MaxValue, tries);
                SafeApply(op);
                for (int k = _carcassRetry.Count - 1; k >= 0; k--) if (_carcassRetry[k].Op == op && _carcassRetry[k].At == float.MaxValue) _carcassRetry.RemoveAt(k);
            }
        }
    }
}
