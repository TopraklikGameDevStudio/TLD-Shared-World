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

        // ---- postcards (the game calls them polaroids) and region maps: what one player reveals on the map, all get ----
        private readonly List<(byte Kind, string Key, bool Flag)> _revealLog = new();
        private string _revealSave;
        private readonly List<(byte Kind, string Key, bool Flag)> _revealPending = new();
        internal int RevealsSent, RevealsApplied;
        private static string RevealPath(string save) => System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "TLDCoop-map-" + save + "-reveal.txt");

        internal static void ApplyPatches(HarmonyLib.Harmony h)
        {
            int ok = 0;
            try
            {
                var m = AccessTools.Method(typeof(Panel_Map), nameof(Panel_Map.RevealOnPolaroidDiscovery), new[] { typeof(string), typeof(bool) });
                if (m != null) { h.Patch(m, postfix: new HarmonyMethod(typeof(MapRevealPatches), nameof(MapRevealPatches.PolaroidPostfix))); ok++; }
            }
            catch (Exception e) { CoopMod.Log("Shared map: postcard hook failed (" + e.Message + ")"); }
            try
            {
                var m = AccessTools.Method(typeof(Panel_Map), nameof(Panel_Map.UnlockRegionMap), new[] { typeof(string) });
                if (m != null) { h.Patch(m, postfix: new HarmonyMethod(typeof(MapRevealPatches), nameof(MapRevealPatches.RegionPostfix))); ok++; }
            }
            catch (Exception e) { CoopMod.Log("Shared map: region map hook failed (" + e.Message + ")"); }
            CoopMod.Log($"Shared map: {ok} of 2 reveal hooks in place (postcards, region maps)");
        }

        private void LoadReveals()
        {
            string save = SaveName();
            if (save == _revealSave) return;
            _revealSave = save; _revealLog.Clear();
            if (string.IsNullOrEmpty(save)) return;
            try
            {
                var path = RevealPath(save);
                if (!System.IO.File.Exists(path)) return;
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    var p = line.Split('\t');
                    if (p.Length < 3 || !byte.TryParse(p[0], out var k)) continue;
                    _revealLog.Add((k, p[1], p[2] == "1"));
                }
            }
            catch { }
        }

        private bool RememberReveal(byte kind, string key, bool flag)
        {
            LoadReveals();
            foreach (var e in _revealLog) if (e.Kind == kind && e.Key == key) return false;
            _revealLog.Add((kind, key, flag));
            if (!string.IsNullOrEmpty(_revealSave))
                try { System.IO.File.AppendAllText(RevealPath(_revealSave), $"{kind}\t{key}\t{(flag ? 1 : 0)}\n"); } catch { }
            return true;
        }

        /// <summary>This player found a postcard or a region map (the game's own call, seen by a hook).</summary>
        internal void LocalReveal(byte kind, string key, bool flag)
        {
            if (Applying || string.IsNullOrEmpty(key)) return;
            bool fresh = RememberReveal(kind, key, flag);
            if (!fresh || !_net.IsConnected || !Shared()) return;
            SendReveal(kind, key, flag, 255);
            RevealsSent++;
            CoopMod.Log($"Shared map: {(kind == 1 ? "postcard" : "region map")} '{key}' found here: sent to friends");
        }

        private void SendReveal(byte kind, string key, bool flag, byte to)
        {
            var w = _net.Begin(Msg.MapReveal); w.Put(_net.LocalId); w.Put(kind); w.Put(key); w.Put(flag);
            if (to != 255 && _net.Mode == NetMode.Host) _net.SendTo(to, w, DeliveryMethod.ReliableOrdered);
            else _net.Send(w, DeliveryMethod.ReliableOrdered);
        }

        private void ApplyReveals()
        {
            if (_revealPending.Count == 0 || !WorldSync.Settled()) return;
            var map = InterfaceManager.GetPanel<Panel_Map>();
            if (map == null) return;
            foreach (var (kind, key, flag) in _revealPending)
            {
                Applying = true;
                try
                {
                    if (kind == 1) map.RevealOnPolaroidDiscovery(key, flag);
                    else if (kind == 2) map.UnlockRegionMap(key);
                    RevealsApplied++;
                    CoopMod.Log($"Shared map: friend's {(kind == 1 ? "postcard" : "region map")} '{key}' revealed here too");
                }
                catch (Exception e) { CoopMod.Log($"Shared map: could not reveal '{key}' ({e.Message})"); }
                finally { Applying = false; }
                RememberReveal(kind, key, flag);
            }
            _revealPending.Clear();
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
            LoadReveals();
            foreach (var e in _revealLog) { SendReveal(e.Kind, e.Key, e.Flag, to); n++; }
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
            if (msg == Msg.MapReveal)
            {
                try
                {
                    byte id = r.GetByte(), kind = r.GetByte(); string key = r.GetString(); bool flag = r.GetBool();
                    if (_net.Mode == NetMode.Host)
                    {
                        id = from;
                        var w = _net.Begin(Msg.MapReveal); w.Put(id); w.Put(kind); w.Put(key); w.Put(flag);
                        _net.Send(w, DeliveryMethod.ReliableOrdered, from);
                    }
                    if (id == _net.LocalId) return;
                    LoadReveals();
                    foreach (var e in _revealLog) if (e.Kind == kind && e.Key == key) return;
                    _revealPending.Add((kind, key, flag));
                    if (Time.realtimeSinceStartup - _lastHud > 10f) { _lastHud = Time.realtimeSinceStartup; try { HUDMessage.AddMessage(L.T("Arkadaşın haritada yeni bir yer açtı; senin haritan da güncellendi", "Your friend revealed a new place on the map; your map was updated too"), 3f, false, true); } catch { } }
                }
                catch (Exception e) { CoopMod.Log("Shared map reveal message: " + e.Message); }
                return;
            }
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
            try { ApplyReveals(); } catch (Exception e) { CoopMod.Log("Shared map: reveals: " + e.Message); }
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
            sb.Append("map_reveals: sent=").Append(RevealsSent).Append(" applied=").Append(RevealsApplied).Append(" pending=").Append(_revealPending.Count).Append(" known=").Append(_revealLog.Count).Append('\n');
        }
    }

    /// <summary>Applied by hand (MapShare.ApplyPatches).</summary>
    internal static class MapRevealPatches
    {
        public static void PolaroidPostfix(string __0, bool __1) { try { MapShare.Instance?.LocalReveal(1, __0, __1); } catch { } }
        public static void RegionPostfix(string __0) { try { MapShare.Instance?.LocalReveal(2, __0, false); } catch { } }
    }

    [HarmonyPatch(typeof(CharcoalItem), nameof(CharcoalItem.DetailSurveyComplete))]
    internal static class MapShareSurveyPatch
    {
        private static void Postfix() { try { MapShare.Instance?.LocalSurvey(); } catch (Exception e) { CoopMod.Log("Shared map (survey): " + e.Message); } }
    }
}
