using System;
using System.Collections.Generic;
using Il2Cpp;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Shared fires: stoves, fireplaces, barrels and campfires. When a fire is lit, goes out,
    /// or gets more fuel, its full state (as the game saves it) is sent; the other side loads it.
    /// Burning down happens on its own on both sides because the clock is shared.
    /// </summary>
    internal sealed partial class ItemSync
    {
        private struct FireSig { public bool Burning; public float Remain; }

        private readonly Dictionary<IntPtr, FireSig> _fireSig = new();
        private Fire[] _fires;
        private float _nextFireList, _nextFirePoll, _nextFireBeat;
        private int _fireBeats;
        private static bool _loggedFireError;

        private void ResetFires() { _fireSig.Clear(); _fires = null; _nextFireList = 0f; }

        private static FireSig SigOf(Fire f)
        {
            var s = new FireSig();
            try { s.Burning = f.IsBurning(); } catch { }
            try { s.Remain = f.GetRemainingLifeTimeHours(); } catch { }
            return s;
        }

        private void WatchFires()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextFirePoll) return;
            _nextFirePoll = now + 0.5f;
            if (now < _sceneReadyAt) return; // let the game restore its own fires first
            if (_fires == null || now >= _nextFireList)
            {
                _nextFireList = now + 3f;
                _fires = UnityEngine.Object.FindObjectsOfType<Fire>();
            }
            foreach (var f in _fires)
            {
                if (f == null) continue;
                var ptr = f.Pointer;
                var cur = SigOf(f);
                if (!_fireSig.TryGetValue(ptr, out var old)) { _fireSig[ptr] = cur; continue; }
                _fireSig[ptr] = cur;
                bool lit = cur.Burning && !old.Burning;
                bool died = !cur.Burning && old.Burning;
                bool fed = cur.Burning && cur.Remain > old.Remain + 0.02f;
                if (!lit && !died && !fed) continue;
                // while it's still catching, wait: the state is sent once it is properly burning
                try
                {
                    var st = f.GetFireState();
                    if (lit && (st == FireState.Starting_FireStarterUse || st == FireState.Starting_TinderSmolder || st == FireState.Starting_TinderSmallFlame))
                    { _fireSig[ptr] = old; continue; }
                }
                catch { }
                SendFire(f, lit ? "lit" : died ? "out" : "fuel");
            }
            // the host's fires are the real ones: wind and shelter make a fire burn at its own pace in each game,
            // so every 15 s the host sends each burning fire again and the clients' copies are set to it
            if (_net.Mode == NetMode.Host && _net.Remotes.Count > 0 && now >= _nextFireBeat)
            {
                _nextFireBeat = now + 15f;
                int n = 0;
                foreach (var f in _fires)
                {
                    if (f == null) continue;
                    bool burning = false; try { burning = f.IsBurning(); } catch { }
                    if (!burning) continue;
                    SendFire(f, "beat", quiet: true); n++;
                }
                if (n > 0 && _fireBeats++ == 0) CoopMod.Log($"Fires: the host's {n} burning fire(s) are sent every 15 s, the clients' copies follow");
            }
        }

        private void SendFire(Fire f, string why, bool quiet = false)
        {
            if (Applying || !Active) return;
            string text;
            try { text = f.Serialize(); } catch (Exception e) { CoopMod.Log("Fire serialize: " + e.Message); return; }
            if (string.IsNullOrEmpty(text)) return;
            var go = f.gameObject;
            var guid = GuidOf(go);
            if (string.IsNullOrEmpty(guid))
            {
                guid = System.Guid.NewGuid().ToString();
                try { ObjectGuid.MaybeAttachObjectGuidAndRegister(go, guid); } catch { }
            }
            string address = "";
            try { address = f.PrefabAddress ?? ""; } catch { }
            float remain = 0f; try { remain = f.GetRemainingLifeTimeHours(); } catch { }
            if (!quiet) CoopMod.Log($"Fire {why}: {PrefabName(go)} ({remain:0.0} h left)");
            var w = _net.Begin(Msg.FireState);
            w.Put(CoopMod.CurrentScene());
            w.Put(guid ?? "");
            w.Put(address);
            var p = go.transform.position; var q = go.transform.rotation;
            w.Put(p.x); w.Put(p.y); w.Put(p.z); w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w);
            w.Put(text);
            _net.Send(w, DeliveryMethod.ReliableOrdered);
        }

        private void RememberFire(Fire f) { if (f != null) _fireSig[f.Pointer] = SigOf(f); _nextFireList = 0f; }

        private sealed class FireOp : Op
        {
            public string Guid, Address, Text; public Vector3 Pos; public Quaternion Rot;
            public static FireOp Read(NetDataReader r) => new()
            {
                Scene = r.GetString(), Guid = r.GetString(), Address = r.GetString(),
                Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
                Rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()),
                Text = r.GetString(),
            };

            public override void Apply()
            {
                Fire f = null;
                if (!string.IsNullOrEmpty(Guid)) { try { f = FireManager.FindFireByGuid(Guid); } catch { } }
                if (f == null)
                {
                    float best = 0.75f;
                    foreach (var cand in UnityEngine.Object.FindObjectsOfType<Fire>())
                    {
                        float d = Vector3.Distance(cand.transform.position, Pos);
                        if (d < best) { best = d; f = cand; }
                    }
                }
                bool made = false;
                if (f == null && !string.IsNullOrEmpty(Address))
                {
                    // a campfire the other player built
                    try { f = FireManager.InstantiateFireFromAddress(Address); } catch (Exception e) { CoopMod.Log("Campfire spawn: " + e.Message); }
                    if (f != null)
                    {
                        made = true;
                        f.transform.position = Pos;
                        f.transform.rotation = Rot;
                        if (!string.IsNullOrEmpty(Guid)) { try { ObjectGuid.MaybeAttachObjectGuidAddOrReplace(f.gameObject, Guid); } catch { } }
                    }
                }
                if (f == null) { CoopMod.Log($"Fire not found at {Pos} ({Address})"); return; }
                float before = 0f; try { before = f.GetRemainingLifeTimeHours(); } catch { }
                f.Deserialize(Text);
                bool burning = false; try { burning = f.IsBurning(); } catch { }
                float remain = 0f; try { remain = f.GetRemainingLifeTimeHours(); } catch { }
                // the host's every-15-s fire beat: only worth a line when the two had drifted apart
                if (!made && Mathf.Abs(remain - before) < 0.05f && burning) { Instance.RememberFire(f); return; }
                CoopMod.Log($"Got fire: {PrefabName(f.gameObject)}{(made ? " (new campfire)" : "")} burning {burning}, {remain:0.0} h left" + (made ? "" : $" (was {before:0.0} h here)"));
                Instance.RememberFire(f);
            }
        }
    }
}
