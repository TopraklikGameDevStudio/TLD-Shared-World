using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Il2Cpp;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// The whistle: one key, and every other player in the same region within 250 m knows where you are.
    ///
    /// The hearer gets two things. A sound: one of the game's own sound events, played through the game's audio
    /// (GameAudioManager.Play3DSound) on a small object that sits in the direction the whistle came from, so it is
    /// heard from that side. And a line on the HUD with the direction and a rough distance, always, because the sound
    /// cannot be relied on: the game has no whistle of its own, the events tried (setting IslikSesi, in that order)
    /// are stand-ins, and whether a sound bank is loaded is only known when the game answers with a playing number.
    ///
    /// The game's sounds fade out over a distance nobody outside the game knows, so the object is not put where the
    /// whistler stands but closer, on the same line: 250 m become about 30 m. Direction stays right, far whistles are
    /// quieter than near ones, and none is lost on the way.
    /// </summary>
    internal sealed class WhistleSync
    {
        internal const float Range = 250f, MinGap = 2f;
        private const float MaxSeconds = 2.2f;   // a sound that would go on (a loop) is cut here
        private const byte TestId = 250;

        /// <summary>Tried in this order; the first one the game's audio accepts is the whistle. Names are the game's own (AK.EVENTS).</summary>
        internal static readonly string DefaultSounds = string.Join(",",
            nameof(Il2CppAK.EVENTS.PLAY_STEAMTRAIN_STEAMWHISTLETOOT),
            nameof(Il2CppAK.EVENTS.PLAY_PIPEMETALWHISTLEA),
            nameof(Il2CppAK.EVENTS.PLAY_EXERTIONTHROW));

        private readonly Network _net;
        private readonly Func<string> _soundPref;
        private float _lastSent = -100f;
        private readonly Dictionary<byte, float> _lastFrom = new();
        private readonly Dictionary<byte, GameObject> _emitters = new();
        private readonly List<(uint Id, float At)> _stops = new();
        private readonly HashSet<string> _said = new();
        private string _parsedFrom; private List<(string Name, uint Id)> _parsed = new();

        // ---- for the state dump and the harness ----
        internal int Sent, Heard;
        internal string Last = "-";
        /// <summary>Harness: other sound events to try instead of the setting (null = the setting).</summary>
        internal string TestSounds;
        /// <summary>How far away the farthest whistle seems to be, in metres (0 = play it where the whistler really stands).</summary>
        internal float NearMax = 30f;
        private const float NearMin = 6f, Closest = 2f, OwnDistance = 3f;

        public WhistleSync(Network net, Func<string> soundPref)
        {
            _net = net; _soundPref = soundPref;
            _net.OnMessage += OnMessage;
            _net.OnStopped += () => _lastFrom.Clear();
        }

        internal string SoundList => TestSounds ?? SafePref();
        private string SafePref() { try { return _soundPref() ?? ""; } catch { return ""; } }

        // =====================================================================================
        // whistling
        // =====================================================================================
        /// <summary>The key (or the harness): whistle now. Null = done, else why not.</summary>
        internal string Press()
        {
            if (!WorldSync.InGame()) return "not in game";
            float now = Time.realtimeSinceStartup;
            if (now - _lastSent < MinGap) return $"only one whistle every {MinGap:0} s";
            var t = GameManager.GetPlayerTransform();
            if (t == null) return "no player";
            _lastSent = now;
            var p = t.position; string scene = CoopMod.CurrentScene() ?? "";

            int listeners = 0;
            if (_net.IsConnected)
            {
                var w = _net.Begin(Msg.Whistle); w.Put(_net.LocalId); w.Put(scene); w.Put(p.x); w.Put(p.y); w.Put(p.z);
                _net.Send(w, DeliveryMethod.ReliableOrdered);
                try
                {
                    foreach (var r in CoopMod.Instance.Players.Snapshot())
                        if (r.HasState && r.Age < 5f && r.Scene == scene && Vector3.Distance(r.Pos, p) <= Range) listeners++;
                }
                catch { }
            }
            // the whistler hears it too: a few steps in front, not inside the head (these sounds are made to carry)
            var ear = Ear(t); float yr = t.eulerAngles.y * 0.017453292f;
            string how = Play(_net.LocalId, new Vector3(ear.x + MathF.Sin(yr) * OwnDistance, ear.y, ear.z + MathF.Cos(yr) * OwnDistance));
            Sent++;
            Last = FormattableString.Invariant($"sent to {listeners} player(s) in reach, own sound: {how}");
            CoopMod.Log($"Whistled in {scene}: {listeners} player(s) within {Range:0} m, own sound: {how}");
            Line(!_net.IsConnected ? L.T("Islık çaldın (bağlı değilsin, kimse duymaz)", "You whistled (not connected, nobody hears it)") : listeners > 0 ? L.T("Islık çaldın", "You whistled") : L.T("Islık çaldın (duyacak kadar yakın kimse yok)", "You whistled (nobody is close enough to hear)"), 2.5f);
            return null;
        }

        public void Update()
        {
            if (_stops.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = _stops.Count - 1; i >= 0; i--)
            {
                if (now < _stops[i].At) continue;
                uint id = _stops[i].Id;
                _stops.RemoveAt(i);
                try { GameAudioManager.StopPlayingID(ref id, 350); } catch { }
            }
        }

        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (msg != Msg.Whistle) return;
            try
            {
                byte id = r.GetByte(); string scene = r.GetString();
                var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                if (_net.Mode == NetMode.Host)
                {
                    id = from; // a client speaks only for itself
                    var w = _net.Begin(Msg.Whistle); w.Put(id); w.Put(scene); w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
                    _net.Send(w, DeliveryMethod.ReliableOrdered, from);
                }
                if (id == _net.LocalId || !WorldSync.InGame()) return;
                float now = Time.realtimeSinceStartup;
                if (_lastFrom.TryGetValue(id, out var last) && now - last < MinGap - 0.5f) return; // the whistler's own limit, held here too
                if (scene != CoopMod.CurrentScene()) { CoopMod.Log($"Player {id} whistled in {scene}: another region, not heard here"); return; }
                var t = GameManager.GetPlayerTransform();
                if (t == null) return;
                float d = Vector3.Distance(t.position, pos);
                if (d > Range) { CoopMod.Log($"Player {id} whistled {d:0} m away: too far to hear ({Range:0} m)"); return; }
                _lastFrom[id] = now;
                Hear(id, pos, t);
            }
            catch (Exception e) { CoopMod.Log("Whistle message: " + e.Message); }
        }

        // =====================================================================================
        // hearing
        // =====================================================================================
        private void Hear(byte id, Vector3 src, Transform me)
        {
            var mine = me.position;
            float dx = src.x - mine.x, dy = src.y - mine.y, dz = src.z - mine.z;
            float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            // compass: north is +z, east is +x, as on the game's maps
            float bearing = MathF.Atan2(dx, dz) * 57.29578f; if (bearing < 0f) bearing += 360f;
            float yaw = me.eulerAngles.y;
            try { var cam = GameManager.GetVpFPSCamera(); if (cam != null) yaw = cam.transform.eulerAngles.y; } catch { }
            float rel = bearing - yaw; while (rel > 180f) rel -= 360f; while (rel < -180f) rel += 360f;
            int slice = (int)MathF.Floor((bearing + 22.5f) / 45f) % 8;

            // where the sound is put: on the line to the whistler, but never so far that the game's own fade-out swallows it
            var ear = Ear(me);
            float seems = NearMax <= 0f ? d : MathF.Max(Closest, MathF.Min(d, NearMin + MathF.Max(0f, NearMax - NearMin) * (d / Range)));
            Vector3 at;
            if (d < 0.2f) at = new Vector3(ear.x, ear.y, ear.z + Closest);
            else
            {
                float k = seems / d;
                at = new Vector3(ear.x + dx * k, ear.y + dy * k, ear.z + dz * k);
            }
            string how = Play(id, at);

            Heard++;
            string game = "";
            try
            {
                // what the game itself calls this direction: lets a test check that the mod's north is the game's north
                string s = Utils.GetCompassBearingAndDistance(mine, src, out float angle, out float dist);
                game = FormattableString.Invariant($" game_says='{s}' game_angle={angle:0}");
            }
            catch { }
            Last = FormattableString.Invariant($"from={id} distance={d:0.0} bearing={bearing:0} ({CompassEn[slice]}) relative={rel:0} sound_at={seems:0.0}m played={how}{game}");
            CoopMod.Log($"Heard player {id} whistle: {d:0} m, {CompassEn[slice]} ({bearing:0} deg), {rel:0} deg from where we look; {how}");
            Line(L.T($"{Name(id)} ıslık çaldı: {CompassTr[slice]} ({Side(rel)}), {Rough(d)}", $"{Name(id)} whistled: {CompassWord[slice]} ({Side(rel)}), {Rough(d)}"), 4.5f, true);
        }

        private static readonly string[] CompassTr = { "kuzey", "kuzeydoğu", "doğu", "güneydoğu", "güney", "güneybatı", "batı", "kuzeybatı" };
        private static readonly string[] CompassEn = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        private static readonly string[] CompassWord = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };

        private static string Side(float rel)
        {
            float a = MathF.Abs(rel);
            if (a <= 22.5f) return L.T("önünde", "ahead");
            if (a >= 157.5f) return L.T("arkanda", "behind you");
            bool right = rel > 0f;
            if (a < 67.5f) return right ? L.T("sağ önünde", "ahead on your right") : L.T("sol önünde", "ahead on your left");
            if (a <= 112.5f) return right ? L.T("sağında", "on your right") : L.T("solunda", "on your left");
            return right ? L.T("sağ arkanda", "behind you on the right") : L.T("sol arkanda", "behind you on the left");
        }

        private static string Rough(float d) => d < 20f ? L.T("çok yakın", "very close") : d < 80f ? L.T("yakın", "close") : d < 160f ? L.T("biraz uzak", "a bit far") : L.T("uzak", "far");

        private string Name(byte id)
        {
            if (id == TestId) return "Test";
            return _net.Remotes.TryGetValue(id, out var info) && !string.IsNullOrEmpty(info.Name) ? info.Name : L.T($"Oyuncu {id}", $"Player {id}");
        }

        private static Vector3 Ear(Transform me)
        {
            try { var cam = GameManager.GetVpFPSCamera(); if (cam != null) return cam.transform.position; } catch { }
            var p = me.position;
            return new Vector3(p.x, p.y + 1.6f, p.z);
        }

        private static void Line(string s, float seconds, bool important = false) { try { HUDMessage.AddMessage(s, seconds, important, true); } catch { } }

        // =====================================================================================
        // the sound
        // =====================================================================================
        /// <summary>The sound events to try, with the numbers the game knows them by. Only names the game really has are kept.</summary>
        private List<(string Name, uint Id)> Sounds()
        {
            string text = SoundList;
            if (text == _parsedFrom) return _parsed;
            var list = new List<(string, uint)>();
            foreach (var raw in (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = raw.Trim().ToUpperInvariant();
                PropertyInfo prop = null;
                try { prop = typeof(Il2CppAK.EVENTS).GetProperty(name, BindingFlags.Public | BindingFlags.Static); } catch { }
                if (prop == null)
                {
                    if (_said.Add("name " + name)) CoopMod.Log($"Whistle sound '{name}': the game has no sound event of that name, skipped");
                    continue;
                }
                uint id = 0;
                try { id = (uint)prop.GetValue(null); } catch { } // not readable yet: the event is asked for by name instead
                list.Add((name, id));
            }
            // a list read while the game's audio was not up yet has no numbers: read it again next time
            bool complete = true;
            foreach (var s in list) if (s.Item2 == 0) complete = false;
            _parsed = list; _parsedFrom = complete ? text : null;
            return list;
        }

        /// <summary>Plays the whistle at a spot. Says how: "sound NAME playing_id=N", or "hud only (...)" when the game played nothing.</summary>
        private string Play(byte who, Vector3 at)
        {
            GameObject go;
            try { go = Emitter(who, at); }
            catch (Exception e) { return "hud only (no object to play the sound on: " + e.Message + ")"; }
            var sounds = Sounds();
            if (sounds.Count == 0) return "hud only (no usable sound event in the setting IslikSesi)";
            string tried = "";
            foreach (var s in sounds)
            {
                try
                {
                    uint playing = s.Id != 0 ? PlayById(s.Id, go) : PlayByName(s.Name, go);
                    if (playing != 0)
                    {
                        _stops.Add((playing, Time.realtimeSinceStartup + MaxSeconds));
                        return $"sound {s.Name} playing_id={playing}";
                    }
                    tried += s.Name + "=not played ";
                }
                catch (Exception e)
                {
                    tried += s.Name + "=error ";
                    if (_said.Add("play " + s.Name)) CoopMod.Log($"Whistle sound {s.Name} could not be played: {e.Message}");
                }
            }
            return "hud only (" + tried.Trim() + ")";
        }

        // GameAudioManager.Play3DSound(uint, GameObject) and (string, GameObject). They are looked up by their exact
        // parameters instead of being called directly: a third Play3DSound takes a Wwise type from an assembly this mod
        // is not built against, and a direct call would not compile next to it.
        private static Func<uint, GameObject, uint> _byId;
        private static Func<string, GameObject, uint> _byName;
        private static bool _lookedUp;

        private static void LookUp()
        {
            if (_lookedUp) return;
            _lookedUp = true;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            try
            {
                var m = typeof(GameAudioManager).GetMethod(nameof(GameAudioManager.Play3DSound), flags, null, new[] { typeof(uint), typeof(GameObject) }, null);
                if (m != null) _byId = (Func<uint, GameObject, uint>)Delegate.CreateDelegate(typeof(Func<uint, GameObject, uint>), m);
            }
            catch (Exception e) { CoopMod.Log("Whistle: the game's Play3DSound(number, object) could not be reached: " + e.Message); }
            try
            {
                var m = typeof(GameAudioManager).GetMethod(nameof(GameAudioManager.Play3DSound), flags, null, new[] { typeof(string), typeof(GameObject) }, null);
                if (m != null) _byName = (Func<string, GameObject, uint>)Delegate.CreateDelegate(typeof(Func<string, GameObject, uint>), m);
            }
            catch (Exception e) { CoopMod.Log("Whistle: the game's Play3DSound(name, object) could not be reached: " + e.Message); }
        }

        private static uint PlayById(uint id, GameObject go)
        {
            LookUp();
            if (_byId == null) throw new InvalidOperationException("the game's Play3DSound(number, object) is not there");
            return _byId(id, go);
        }

        private static uint PlayByName(string name, GameObject go)
        {
            LookUp();
            if (_byName == null) throw new InvalidOperationException("the game's Play3DSound(name, object) is not there");
            return _byName(name, go);
        }

        /// <summary>One small object per whistler to play the sound on; a scene change takes it away, then a new one is made.</summary>
        private GameObject Emitter(byte who, Vector3 at)
        {
            _emitters.TryGetValue(who, out var go);
            bool alive = false;
            try { alive = go != null && go.transform != null; } catch { }
            if (!alive)
            {
                go = new GameObject("TLDCoop_Whistle_" + who);
                _emitters[who] = go;
            }
            go.transform.position = at;
            return go;
        }

        // =====================================================================================
        // the harness
        // =====================================================================================
        /// <summary>As if a friend had whistled from dist metres away at this compass bearing (0 north, 90 east). Null = done.</summary>
        internal string TestHear(float dist, float bearing)
        {
            if (!WorldSync.InGame()) return "not in game";
            var t = GameManager.GetPlayerTransform();
            if (t == null) return "no player";
            var p = t.position; float b = bearing * 0.017453292f;
            Hear(TestId, new Vector3(p.x + MathF.Sin(b) * dist, p.y, p.z + MathF.Cos(b) * dist), t);
            return null;
        }

        /// <summary>Play one named sound event dist metres in front of the player. Says what the game answered.</summary>
        internal string TestTry(string name, float dist)
        {
            if (!WorldSync.InGame()) return "not in game";
            var t = GameManager.GetPlayerTransform();
            if (t == null) return "no player";
            string keep = TestSounds;
            TestSounds = name; _parsedFrom = null;
            string how;
            try
            {
                float yaw = t.eulerAngles.y * 0.017453292f; var ear = Ear(t);
                how = Play(TestId, new Vector3(ear.x + MathF.Sin(yaw) * dist, ear.y, ear.z + MathF.Cos(yaw) * dist));
            }
            finally { TestSounds = keep; _parsedFrom = null; }
            return how;
        }

        internal void WriteState(System.Text.StringBuilder sb)
        {
            void Out(string k, object v) => sb.Append(k).Append(": ").Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append('\n');
            Out("whistles_sent", Sent);
            Out("whistles_heard", Heard);
            Out("whistle_last", Last);
            Out("whistle_sounds", SoundList);
            Out("whistle_seems_at_most_m", NearMax.ToString("0.#", CultureInfo.InvariantCulture));
        }
    }
}
