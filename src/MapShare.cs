using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Shared charcoal map: when a player finishes mapping with charcoal, the same spot is mapped in every friend's
    /// game too (the game's own "details nearby" check, run at the mapper's position). A friend in another region
    /// gets it when they next enter that region.
    /// </summary>
    internal sealed class MapShare
    {
        internal static MapShare Instance;
        private readonly Network _net;
        private readonly Func<bool> _shared;
        private readonly Dictionary<string, List<Vector3>> _pending = new();
        internal int Sent, Received, Applied;
        internal string Last = "-";
        internal static bool Applying;
        private float _lastHud = -100f;

        public MapShare(Network net, Func<bool> shared)
        {
            Instance = this; _net = net; _shared = shared;
            _net.OnMessage += OnMessage;
            // a friend who arrives gets every spot this game has mapped, and sends theirs (mapped while apart)
            _net.OnRemoteJoined += (id, name) => { _sendLogTo = id; _sendLogAt = Time.realtimeSinceStartup + 3f; };
        }

        // ---- the log of mapped spots, per save: what has been mapped here (by this player or shared by a friend) ----
        private string _logSave; private readonly List<(string Scene, Vector3 Pos)> _log = new();
        private byte _sendLogTo = 255; private float _sendLogAt;
        internal int LogCount => _log.Count;
        private static string SaveName() { try { return SaveGameSystem.GetCurrentSaveName() ?? ""; } catch { return ""; } }
        private static string LogPath(string save) => System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "TLDCoop-map-" + save + ".txt");

        private void LoadLog()
        {
            string save = SaveName();
            if (save == _logSave) return;
            _logSave = save; _log.Clear();
            if (string.IsNullOrEmpty(save)) return;
            try
            {
                var path = LogPath(save);
                if (!System.IO.File.Exists(path)) return;
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    var p = line.Split('\t');
                    if (p.Length < 4) continue;
                    if (float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                        && float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
                        && float.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z))
                        _log.Add((p[0], new Vector3(x, y, z)));
                }
            }
            catch (Exception e) { CoopMod.Log("Shared map: the log could not be read (" + e.Message + ")"); }
        }

        /// <summary>Remember a mapped spot (false = it, or one within 10 m, was known already).</summary>
        private bool Remember(string scene, Vector3 p)
        {
            LoadLog();
            if (string.IsNullOrEmpty(_logSave)) return true;
            foreach (var e in _log) if (e.Scene == scene && (e.Pos - p).sqrMagnitude < 100f) return false;
            if (_log.Count >= 1000) return true;
            _log.Add((scene, p));
            try
            {
                System.IO.File.AppendAllText(LogPath(_logSave), FormattableString.Invariant($"{scene}\t{p.x:0.0}\t{p.y:0.0}\t{p.z:0.0}\n"));
            }
            catch { }
            return true;
        }

        private void SendLog(byte to)
        {
            LoadLog();
            int n = 0;
            foreach (var e in _log)
            {
                var w = _net.Begin(Msg.MapSurvey); w.Put(_net.LocalId); w.Put(e.Scene); w.Put(e.Pos.x); w.Put(e.Pos.y); w.Put(e.Pos.z);
                if (_net.Mode == NetMode.Host) _net.SendTo(to, w, DeliveryMethod.ReliableOrdered); else _net.Send(w, DeliveryMethod.ReliableOrdered);
                n++;
            }
            if (n > 0) CoopMod.Log($"Shared map: sent {n} mapped spot(s) to player {to}");
        }

        private bool Shared() { try { return _shared(); } catch { return false; } }

        /// <summary>This player finished a charcoal survey at their position.</summary>
        internal void LocalSurvey()
        {
            if (Applying) return;
            var t = GameManager.GetPlayerTransform(); if (t == null) return;
            var p = t.position; string scene = CoopMod.CurrentScene() ?? "";
            Remember(scene, p); // also alone: a friend gets it on the next meeting
            if (!_net.IsConnected || !Shared()) return;
            var w = _net.Begin(Msg.MapSurvey); w.Put(_net.LocalId); w.Put(scene); w.Put(p.x); w.Put(p.y); w.Put(p.z);
            _net.Send(w, DeliveryMethod.ReliableOrdered);
            Sent++;
            Last = $"mapped {scene} at {p.x:0},{p.z:0}: sent to friends";
            CoopMod.Log("Shared map: " + Last);
        }

        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (msg != Msg.MapSurvey) return;
            try
            {
                byte id = r.GetByte(); string scene = r.GetString(); var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                if (_net.Mode == NetMode.Host)
                {
                    id = from;
                    var w = _net.Begin(Msg.MapSurvey); w.Put(id); w.Put(scene); w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
                    _net.Send(w, DeliveryMethod.ReliableOrdered, from);
                }
                if (id == _net.LocalId) return;
                Received++;
                bool fresh = true;
                if (WorldSync.InGame()) fresh = Remember(scene, pos);
                if (!fresh) return;
                if (!_pending.TryGetValue(scene, out var list)) _pending[scene] = list = new List<Vector3>();
                list.Add(pos);
                CoopMod.Log($"Shared map: player {id} mapped {scene} at {pos.x:0},{pos.z:0}");
                if (Time.realtimeSinceStartup - _lastHud > 10f) { _lastHud = Time.realtimeSinceStartup; try { HUDMessage.AddMessage(L.T("Arkadaşın haritaya çizdi; senin haritan da güncellendi", "Your friend drew on the map; your map was updated too"), 3f, false, true); } catch { } }
            }
            catch (Exception e) { CoopMod.Log("Shared map message: " + e.Message); }
        }

        public void Update()
        {
            if (_sendLogTo != 255 && Time.realtimeSinceStartup >= _sendLogAt && _net.IsConnected)
            {
                byte to = _sendLogTo; _sendLogTo = 255;
                try { SendLog(to); } catch (Exception e) { CoopMod.Log("Shared map: sending the log: " + e.Message); }
            }
            if (_pending.Count == 0 || !WorldSync.Settled()) return;
            string scene = CoopMod.CurrentScene() ?? "";
            if (!_pending.TryGetValue(scene, out var list)) return;
            _pending.Remove(scene);
            var map = InterfaceManager.GetPanel<Panel_Map>();
            if (map == null) return;
            foreach (var p in list)
            {
                string before = Count(map, scene);
                Applying = true;
                try { map.DoNearbyDetailsCheck(map.m_DetailSurveyRadiusMeters, true, true, p, true); Applied++; }
                catch (Exception e) { CoopMod.Log("Shared map: could not map the friend's spot (" + e.Message + ")"); }
                finally { Applying = false; }
                Remember(scene, p);
                Last = $"friend's survey in {scene} at {p.x:0},{p.z:0} added: map elements {before} -> {Count(map, scene)}";
                CoopMod.Log("Shared map: " + Last);
            }
        }

        /// <summary>How many spots of this region have been mapped (the circles of fog taken away), and how many map elements are known.</summary>
        internal static string Count(Panel_Map map, string scene)
        {
            int pos = -1, el = -1;
            try { var d = map.m_DetailSurveyPositions; if (d != null && d.ContainsKey(scene)) pos = d[scene].Count; else pos = 0; } catch { }
            try { el = map.GetNumberOfUnlockedMapElements(scene); } catch { }
            return $"spots={pos} elements={el}";
        }

        internal void WriteState(System.Text.StringBuilder sb)
        {
            string n = "-"; try { var m = InterfaceManager.GetPanel<Panel_Map>(); if (m != null) n = Count(m, CoopMod.CurrentScene() ?? ""); } catch { }
            sb.Append("map_elements_here: ").Append(n).Append('\n');
            sb.Append("map_sent: ").Append(Sent).Append('\n');
            sb.Append("map_received: ").Append(Received).Append('\n');
            sb.Append("map_applied: ").Append(Applied).Append('\n');
            int pend = 0; foreach (var l in _pending.Values) pend += l.Count;
            sb.Append("map_pending: ").Append(pend).Append('\n');
            sb.Append("map_last: ").Append(Last).Append('\n');
            sb.Append("map_log: ").Append(_logSave ?? "-").Append(" spots=").Append(_log.Count).Append('\n');
        }
    }

    [HarmonyPatch(typeof(CharcoalItem), nameof(CharcoalItem.DetailSurveyComplete))]
    internal static class MapShareSurveyPatch
    {
        private static void Postfix() { try { MapShare.Instance?.LocalSurvey(); } catch (Exception e) { CoopMod.Log("Shared map (survey): " + e.Message); } }
    }
}
