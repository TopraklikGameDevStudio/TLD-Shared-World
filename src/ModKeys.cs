using System;
using System.Collections.Generic;
using Il2Cpp;

namespace TLDCoop
{
    /// <summary>
    /// The two keys that came with 0.10.0: whistle (setting IslikTusu) and give (setting VerTusu).
    ///
    /// Which keyboard keys the game itself uses is not written in its code: the bindings live in its input maps
    /// (Rewired) and the player can change them in the options. So the mod asks the running game: it reads every
    /// keyboard key the game has bound right now, and if a key of ours is among them it moves to the next free one of
    /// a short list and says so in the log. The state dump shows the keys in use and everything the game has bound.
    /// </summary>
    internal static class ModKeys
    {
        internal sealed class Key
        {
            public string Asked = "";       // what the setting says
            public string Name = "";        // the key in use
            public int Vk, Unity;           // Windows key number, Unity key number
            public string Note = "not checked against the game's own keys yet";
            public string[] Others;         // tried in this order when the asked key is taken
        }

        internal static readonly Key Whistle = new() { Others = new[] { "G", "B", "N", "Y", "U", "O", "F2" } };
        internal static readonly Key Give = new() { Others = new[] { "V", "T", "Z", "K", "P", "F1" } };
        internal static readonly Key Talk = new() { Others = new[] { "T", "Y", "U", "N", "Z", "K", "F2" } };
        /// <summary>Every keyboard key the game has bound, as last read (for the state dump).</summary>
        internal static string GameKeys = "not read yet";
        private static float _next;
        private static bool _saidUnreadable; private static int _unreadable;

        internal static void Configure(string whistle, string give, string talk = "T")
        {
            Set(Whistle, whistle, "G");
            Set(Give, give, "V");
            if (Give.Vk == Whistle.Vk) Set(Give, Whistle.Name == "V" ? "T" : "V", "V"); // one key cannot do both
            Set(Talk, talk, "T");
            if (Talk.Vk == Whistle.Vk || Talk.Vk == Give.Vk) Set(Talk, "Y", "Y");
        }

        private static void Set(Key k, string asked, string fallback)
        {
            asked = (asked ?? "").Trim().ToUpperInvariant();
            if (!TryParse(asked, out int vk, out int unity) || OursAlready(vk))
            {
                if (asked.Length > 0) CoopMod.Log($"Key setting '{asked}' is not usable (a letter, a digit, F1, F2 or F12; F3 to F11 are the mod's own keys): using {fallback}");
                asked = fallback; TryParse(asked, out vk, out unity);
            }
            k.Asked = asked; k.Name = asked; k.Vk = vk; k.Unity = unity;
        }

        /// <summary>F3 to F11 already do something in this mod.</summary>
        private static bool OursAlready(int vk) => vk >= 0x72 && vk <= 0x7A;

        /// <summary>A letter, a digit or F1-F12, as a Windows key number and as Unity's key number.</summary>
        internal static bool TryParse(string s, out int vk, out int unity)
        {
            vk = 0; unity = 0;
            s = (s ?? "").Trim().ToUpperInvariant();
            if (s.Length == 1 && s[0] >= 'A' && s[0] <= 'Z') { vk = s[0]; unity = 97 + (s[0] - 'A'); return true; }
            if (s.Length == 1 && s[0] >= '0' && s[0] <= '9') { vk = s[0]; unity = 48 + (s[0] - '0'); return true; }
            if (s.Length >= 2 && s[0] == 'F' && int.TryParse(s.Substring(1), out int n) && n >= 1 && n <= 12) { vk = 0x70 + n - 1; unity = 282 + n - 1; return true; }
            return false;
        }

        /// <summary>Now and then: look at what the game has bound and keep our two keys clear of it.</summary>
        internal static void Refresh(float now)
        {
            if (now < _next) return;
            _next = now + 15f;
            Dictionary<int, string> bound;
            try { bound = ReadGameKeys(); }
            catch (Exception e)
            {
                GameKeys = "could not be read: " + e.Message;
                // (the first looks come before the game's input is up: only a lasting failure is worth a line)
                if (++_unreadable >= 4 && !_saidUnreadable) { _saidUnreadable = true; CoopMod.Log($"The game's own key bindings could not be read ({e.Message}): the whistle and give keys stay as set, unchecked"); }
                return;
            }
            if (bound == null || bound.Count == 0) { _next = now + 3f; return; } // the game's input is not up yet
            _unreadable = 0;
            var names = new List<string>();
            foreach (var kv in bound) names.Add(((UnityEngine.KeyCode)kv.Key).ToString());
            names.Sort(string.CompareOrdinal);
            GameKeys = string.Join(",", names);
            Pick(Whistle, bound, 0);
            Pick(Give, bound, Whistle.Vk);
            Pick(Talk, bound, Whistle.Vk, Give.Vk);
        }

        private static void Pick(Key k, Dictionary<int, string> bound, int alsoTaken, int alsoTaken2 = -1)
        {
            TryParse(k.Asked, out _, out int askedUnity);
            string why = bound.TryGetValue(askedUnity, out var action) ? $"the game uses for '{action}'" : "the mod's other key has";
            var tries = new List<string> { k.Asked };
            tries.AddRange(k.Others);
            foreach (var name in tries)
            {
                if (!TryParse(name, out int vk, out int unity) || OursAlready(vk) || vk == alsoTaken || vk == alsoTaken2) continue;
                if (bound.ContainsKey(unity)) continue;
                string note = name == k.Asked ? "free: the game binds nothing to it" : $"moved from {k.Asked}, which {why}";
                if (k.Name != name) CoopMod.Log($"Key {k.Asked} is one {why}: the mod uses {name} instead");
                k.Name = name; k.Vk = vk; k.Unity = unity; k.Note = note;
                return;
            }
            k.Note = $"no free key found: {k.Name} is used although {why} it too";
        }

        /// <summary>Unity key number -> the game action bound to it, for every keyboard key in the game's input maps.</summary>
        private static Dictionary<int, string> ReadGameKeys()
        {
            var sys = InputManager.m_InputSystem;
            if (sys == null) return null;
            var found = new Dictionary<int, string>();
            var player = sys.m_Player;
            if (player != null)
            {
                var maps = new Il2CppSystem.Collections.Generic.List<Il2CppRewired.ControllerMap>();
                player.controllers.maps.GetAllMaps(maps);
                for (int i = 0; i < maps.Count; i++) Collect(maps[i], found);
            }
            if (found.Count == 0) Collect(sys.GetKeyboardMap(), found);
            return found;
        }

        private static void Collect(Il2CppRewired.ControllerMap map, Dictionary<int, string> found)
        {
            if (map == null) return;
            var all = map.GetButtonMaps();
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var m = all[i];
                if (m == null) continue;
                int key = (int)m.keyCode;
                if (key == 0) continue; // a mouse or controller button
                string action = "?";
                try { action = m.actionDescriptiveName ?? "?"; } catch { }
                found[key] = action;
            }
        }
    }
}
