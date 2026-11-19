using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2Cpp;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// The look: the friend's figure wears what the other player really wears. Not the clothes themselves (the
    /// figure has one hat, one scarf, one parka ...), but whether there is a hat, a scarf and gloves at all, and
    /// the colour of each piece.
    ///
    /// Every player reads their own clothes about once a second (the game's own PlayerManager.GetClothingInSlot,
    /// region by region, outermost layer first), turns each visible piece into a colour (a table of keywords in the
    /// item's prefab name; a name nobody thought of gets a colour worked out from the name, always the same one) and
    /// sends flags + 6 colours when that changes, and once after connecting. The host passes it on under the
    /// sender's real id, remembers the last look of everybody and tells a newcomer all of them.
    ///
    /// A wrong guess about the game's clothing calls must never break the drawing of the figure: every call is in a
    /// try, the first failure is logged once, and then the look is simply not sent: the figure keeps its fixed outfit.
    /// </summary>
    internal sealed class LookSync
    {
        // ---- the visible pieces, in the order they travel ----
        internal const int SHat = 0, SScarf = 1, STorso = 2, SHands = 3, SLegs = 4, SBoots = 5, Slots = 6;
        internal static readonly string[] SlotNames = { "hat", "scarf", "torso", "hands", "legs", "boots" };
        internal const byte FHat = 1, FScarf = 2, FGloves = 4;

        internal sealed class Look
        {
            public byte Flags;
            public readonly byte[] Rgb = new byte[Slots * 3];
            public FigureLook Figure;   // the same look as Figure wants it (made when first needed)

            public bool Same(Look o)
            {
                if (o == null || o.Flags != Flags) return false;
                for (int i = 0; i < Rgb.Length; i++) if (Rgb[i] != o.Rgb[i]) return false;
                return true;
            }

            public string Hex(int slot) => $"#{Rgb[slot * 3]:x2}{Rgb[slot * 3 + 1]:x2}{Rgb[slot * 3 + 2]:x2}";

            public string FlagsText
            {
                get
                {
                    string s = ((Flags & FHat) != 0 ? "hat+" : "") + ((Flags & FScarf) != 0 ? "scarf+" : "") + ((Flags & FGloves) != 0 ? "gloves+" : "");
                    return s.Length > 0 ? s.TrimEnd('+') : "none";
                }
            }

            /// <summary>flags and the six colours; with names: the item behind each colour.</summary>
            public string Text(string[] items = null)
            {
                var sb = new StringBuilder("flags=").Append(FlagsText);
                for (int i = 0; i < Slots; i++)
                {
                    sb.Append(' ').Append(SlotNames[i]).Append('=');
                    if (items != null) sb.Append(items[i] ?? "-").Append(' ');
                    sb.Append(Hex(i));
                }
                return sb.ToString();
            }
        }

        private readonly Network _net;
        private readonly RemotePlayers _players;
        private readonly Func<bool> _setting;
        private readonly Dictionary<byte, Look> _looks = new();   // the other players' looks (the host knows everybody's)
        private readonly List<byte> _tell = new();                // host: newcomers who are told every look at the next Update
        private Look _local;
        private readonly string[] _localItems = new string[Slots];
        private float _nextCheck, _localAt = -1f;
        private bool _mustSend, _wasConnected, _wasOn;
        private byte _sentAs = 255;

        // ---- for the state dump ----
        internal int Sent, Received;

        public LookSync(Network net, RemotePlayers players, Func<bool> setting)
        {
            _net = net; _players = players; _setting = setting;
            _net.OnMessage += OnMessage;
            _net.OnRemoteJoined += (id, name) => { if (_net.Mode == NetMode.Host && !_tell.Contains(id)) _tell.Add(id); };
            _net.OnRemoteLeft += id => { _looks.Remove(id); _tell.Remove(id); };
            _net.OnStopped += () => { _looks.Clear(); _tell.Clear(); _wasConnected = false; _sentAs = 255; };
            RemotePlayers.LookOf = For;
            _wasOn = On;
        }

        /// <summary>The setting "KiyafetGorunumu": off = nothing is read, sent or shown, everybody keeps the fixed outfit.</summary>
        internal bool On { get { try { return _setting(); } catch { return true; } } }

        /// <summary>What Figure should put on a player (null = the fixed outfit).</summary>
        internal FigureLook For(byte id)
        {
            if (!On) return null;
            if (!_looks.TryGetValue(id, out var l)) return null;
            return l.Figure ??= ToFigure(l);
        }

        /// <summary>A look as Figure wants it: what is worn, and the pieces in their colours (Figure's colour indices).</summary>
        internal static FigureLook ToFigure(Look l)
        {
            var f = new FigureLook { Hat = (l.Flags & FHat) != 0, Scarf = (l.Flags & FScarf) != 0, Mitts = (l.Flags & FGloves) != 0 };
            if (f.Hat) f.SetTint(Figure.Hat, Colour(l, SHat));          // (the hat's turned-up edge follows as a darker shade)
            if (f.Scarf) f.SetTint(Figure.Scarf, Colour(l, SScarf));
            f.SetTint(Figure.Parka, Colour(l, STorso));
            if (f.Mitts) f.SetTint(Figure.Mitts, Colour(l, SHands));
            f.SetTint(Figure.Trousers, Colour(l, SLegs));
            f.SetTint(Figure.Boots, Colour(l, SBoots));
            return f;
        }

        private static Color Colour(Look l, int slot) => Figure.Rgb(l.Rgb[slot * 3] / 255f, l.Rgb[slot * 3 + 1] / 255f, l.Rgb[slot * 3 + 2] / 255f);

        // =====================================================================================
        // every frame
        // =====================================================================================
        public void Update()
        {
            try
            {
                bool on = On;
                if (on != _wasOn)
                {
                    _wasOn = on;
                    CoopMod.Log(on ? "Look: the setting is on, figures wear what the players wear" : "Look: the setting is off, every figure keeps its fixed outfit");
                    _players.LookChangedAll();
                    if (on) _mustSend = true;
                }

                bool connected = _net.IsConnected;
                if (connected && !_wasConnected) _mustSend = true;   // once after connecting, whatever was sent before
                _wasConnected = connected;
                if (!connected) return;

                // host: a newcomer hears what everybody looks like (the host itself included)
                if (_tell.Count > 0 && _net.Mode == NetMode.Host)
                {
                    foreach (var id in _tell)
                    {
                        if (!_net.Remotes.ContainsKey(id)) continue;
                        int n = 0;
                        if (on && _local != null) { _net.SendTo(id, Write(_net.LocalId, _local), DeliveryMethod.ReliableOrdered); n++; }
                        foreach (var kv in _looks)
                        {
                            if (kv.Key == id) continue;
                            _net.SendTo(id, Write(kv.Key, kv.Value), DeliveryMethod.ReliableOrdered); n++;
                        }
                        CoopMod.Log($"Look: told player {id} the look of {n} player(s)");
                    }
                    _tell.Clear();
                }

                if (!on) return;
                float now = Time.realtimeSinceStartup;
                if (now < _nextCheck) return;
                _nextCheck = now + 1f;
                if (!WorldSync.InGame() || WorldSync.Loading()) return;

                var look = new Look();
                var items = new string[Slots];
                if (ReadLocal(look, items, _net.LocalId) != null) return;
                bool changed = _local == null || !_local.Same(look) || _sentAs != _net.LocalId;
                _local = look; _localAt = now;
                Array.Copy(items, _localItems, Slots);
                if (!changed && !_mustSend) return;
                _mustSend = false; _sentAs = _net.LocalId;
                _net.Send(Write(_net.LocalId, look), DeliveryMethod.ReliableOrdered);
                Sent++;
                CoopMod.Log($"Look sent: {look.Text(items)}");
            }
            catch (Exception e) { Say("update", "Look: " + e.Message + " (said once)"); }
        }

        private NetDataWriter Write(byte id, Look look)
        {
            var w = _net.Begin(Msg.Look);
            w.Put(id); w.Put(look.Flags);
            for (int i = 0; i < look.Rgb.Length; i++) w.Put(look.Rgb[i]);
            return w;
        }

        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (msg != Msg.Look) return;
            try
            {
                byte id = r.GetByte();
                var look = new Look { Flags = r.GetByte() };
                for (int i = 0; i < look.Rgb.Length; i++) look.Rgb[i] = r.GetByte();
                if (_net.Mode == NetMode.Host)
                {
                    id = from; // a client speaks only for itself
                    _net.Send(Write(id, look), DeliveryMethod.ReliableOrdered, from);
                }
                if (id == _net.LocalId) return;
                Received++;
                bool changed = !_looks.TryGetValue(id, out var old) || !old.Same(look);
                _looks[id] = look;
                if (!changed) return;
                CoopMod.Log($"Look of player {id}: {look.Text()}" + (On ? "" : " (not shown: the setting is off)"));
                _players.LookChanged(id);
            }
            catch (Exception e) { CoopMod.Log("Look message: " + e.Message); }
        }

        // =====================================================================================
        // reading the local player's clothes
        // =====================================================================================
        private static readonly HashSet<string> _said = new();
        private static void Say(string key, string text) { if (_said.Add(key)) CoopMod.Log(text); }

        // which of the game's calls gives the worn items: 0 = GetClothingInSlot per layer, 1 = GetAllClothingInRegion, 2 = neither works
        private static int _way;
        private static readonly int[] _layers = { -1, -1, -1, -1, -1, -1, -1, -1 };
        private static string _layersFrom = "-";
        /// <summary>For the state dump: which clothing calls are in use, or what went wrong with them.</summary>
        internal static string ApiText = "not used yet";

        /// <summary>How many layers a region has. The game's own number; if that cannot be asked, what the clothing screen shows.</summary>
        private static int Layers(ClothingRegion region)
        {
            int r = (int)region;
            if (r < 0 || r >= _layers.Length) return 0;
            if (_layers[r] >= 0) return _layers[r];
            int n;
            try { n = PlayerManager.GetNumLayersForClothingRegion(region); _layersFrom = "the game"; }
            catch (Exception e)
            {
                Say("layers", "Look: PlayerManager.GetNumLayersForClothingRegion failed (" + SkinnedBody.Short(e) + "); the layer counts of the clothing screen are used");
                n = region switch { ClothingRegion.Head => 2, ClothingRegion.Hands => 1, ClothingRegion.Chest => 4, ClothingRegion.Legs => 4, ClothingRegion.Feet => 3, ClothingRegion.Accessory => 2, _ => 0 };
                _layersFrom = "assumed";
            }
            if (n < 0) n = 0;
            if (n > (int)ClothingLayer.NumLayers) n = (int)ClothingLayer.NumLayers;
            _layers[r] = n;
            return n;
        }

        /// <summary>What is worn in a region, outermost first. Null = the game's clothing calls do not work here.</summary>
        private static List<GearItem> Worn(PlayerManager pm, ClothingRegion region)
        {
            var list = new List<GearItem>();
            if (_way == 0)
            {
                try
                {
                    // never a layer the region does not have: the game keeps all slots in one row, and a layer too far
                    // would be the next region's slot
                    for (int layer = Layers(region) - 1; layer >= 0; layer--)
                    {
                        var gi = pm.GetClothingInSlot(region, (ClothingLayer)layer);
                        if (gi != null) list.Add(gi);
                    }
                    return list;
                }
                catch (Exception e)
                {
                    Say("slot", "Look: PlayerManager.GetClothingInSlot failed (" + SkinnedBody.Short(e) + "); trying GetAllClothingInRegion instead");
                    _way = 1; list.Clear();
                }
            }
            if (_way == 1)
            {
                try
                {
                    var all = new Il2CppSystem.Collections.Generic.List<GearItem>();
                    pm.GetAllClothingInRegion(region, all);
                    var byLayer = new List<(int Layer, GearItem Item)>();
                    for (int i = 0; i < all.Count; i++)
                    {
                        var gi = all[i];
                        if (gi == null) continue;
                        int layer = 0;
                        try { var ci = gi.m_ClothingItem; if (ci != null) layer = (int)ci.m_EquippedLayer; } catch { }
                        byLayer.Add((layer, gi));
                    }
                    byLayer.Sort((a, b) => b.Layer.CompareTo(a.Layer));
                    foreach (var e in byLayer) list.Add(e.Item);
                    return list;
                }
                catch (Exception e)
                {
                    Say("region", "Look: PlayerManager.GetAllClothingInRegion failed too (" + SkinnedBody.Short(e) + "); clothes cannot be read in this game, figures keep their fixed outfit");
                    _way = 2;
                }
            }
            return null;
        }

        private static string NameOf(GearItem gi)
        {
            try { return ItemSync.PrefabName(gi.gameObject); }
            catch (Exception e) { Say("name", "Look: the name of a worn item cannot be read (" + SkinnedBody.Short(e) + "); its colour is a guess"); return "?"; }
        }

        private static bool Has(string name, string word) => name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// The local player's look, read from the game now. Null = done (look and the item per slot are filled in);
        /// else why not. forId: whose figure it is for (bare feet get that figure's skin colour).
        /// </summary>
        internal static string ReadLocal(Look look, string[] items, byte forId)
        {
            PlayerManager pm;
            try { pm = GameManager.GetPlayerManagerComponent(); } catch (Exception e) { return "no player manager: " + SkinnedBody.Short(e); }
            if (pm == null) return "no player";
            if (_way == 2) return "the game's clothing calls do not work here (see the log)";

            var head = Worn(pm, ClothingRegion.Head);
            var neck = head != null ? Worn(pm, ClothingRegion.Neck) : null;
            var chest = neck != null ? Worn(pm, ClothingRegion.Chest) : null;
            var hands = chest != null ? Worn(pm, ClothingRegion.Hands) : null;
            var legs = hands != null ? Worn(pm, ClothingRegion.Legs) : null;
            var feet = legs != null ? Worn(pm, ClothingRegion.Feet) : null;
            if (feet == null)
            {
                ApiText = "the game's clothing calls do not work here (see the log)";
                return ApiText;
            }
            ApiText = (_way == 0 ? "PlayerManager.GetClothingInSlot(region, layer)" : "PlayerManager.GetAllClothingInRegion(region, list)")
                + $"; layers ({_layersFrom}): head {Layers(ClothingRegion.Head)}, neck {Layers(ClothingRegion.Neck)}, chest {Layers(ClothingRegion.Chest)}, hands {Layers(ClothingRegion.Hands)}, legs {Layers(ClothingRegion.Legs)}, feet {Layers(ClothingRegion.Feet)}";
            Say("api", "Look: clothes are read with " + ApiText);

            // head: a scarf is worn there too; anything else on the head is the hat
            string hat = null, scarf = null;
            foreach (var gi in head)
            {
                string n = NameOf(gi);
                if (Has(n, "scarf")) scarf ??= n; else hat ??= n;
            }
            if (scarf == null && neck.Count > 0) scarf = NameOf(neck[0]);   // (the game lists a neck region; whatever is worn there is a scarf)
            string torso = chest.Count > 0 ? NameOf(chest[0]) : null;       // outer layers first, else inner
            string glove = hands.Count > 0 ? NameOf(hands[0]) : null;
            string leg = legs.Count > 0 ? NameOf(legs[0]) : null;
            string boot = null;                                             // boots, not socks
            foreach (var gi in feet) { string n = NameOf(gi); if (!Has(n, "sock")) { boot = n; break; } }

            look.Flags = (byte)((hat != null ? FHat : 0) | (scarf != null ? FScarf : 0) | (glove != null ? FGloves : 0));
            Put(look, items, SHat, hat, hat != null ? ColourFor(SHat, hat) : default);
            Put(look, items, SScarf, scarf, scarf != null ? ColourFor(SScarf, scarf) : default);
            Put(look, items, STorso, torso, torso != null ? ColourFor(STorso, torso) : Underwear);
            Put(look, items, SHands, glove, glove != null ? ColourFor(SHands, glove) : default);
            Put(look, items, SLegs, leg, leg != null ? ColourFor(SLegs, leg) : Underwear);
            Put(look, items, SBoots, boot, boot != null ? ColourFor(SBoots, boot) : SkinOf(forId));
            return null;
        }

        private static void Put(Look look, string[] items, int slot, string name, (byte R, byte G, byte B) c)
        {
            look.Rgb[slot * 3] = c.R; look.Rgb[slot * 3 + 1] = c.G; look.Rgb[slot * 3 + 2] = c.B;
            if (items != null) items[slot] = string.IsNullOrEmpty(name) ? "-" : name;
        }

        /// <summary>Nothing on the chest or the legs: long underwear.</summary>
        private static readonly (byte, byte, byte) Underwear = (152, 152, 148);

        private static (byte, byte, byte) SkinOf(byte id)
        {
            var c = Figure.BaseColour(Figure.SetFor(id), Figure.Skin);
            return ((byte)(c.r * 255f + 0.5f), (byte)(c.g * 255f + 0.5f), (byte)(c.b * 255f + 0.5f));
        }

        // =====================================================================================
        // an item's colour
        // =====================================================================================
        private const int H = 1 << SHat, S = 1 << SScarf, T = 1 << STorso, G = 1 << SHands, L = 1 << SLegs, B = 1 << SBoots, Any = 63;   // which slots a row is for

        // Keywords in the prefab name (GEAR_...), any case; "a+b" = both must be in the name. The first row that fits
        // the name and the slot gives the colour, so what says most about the look comes first: hides and furs, then
        // garments known by name, then cuts and cloths, then the plain words. Muted on purpose, like the fixed outfits.
        private static readonly (string Words, int For, byte Red, byte Green, byte Blue)[] Table =
        {
            // hides and furs, whatever is made of them
            ("wolfskin", Any, 98, 96, 94), ("bearskin", Any, 54, 40, 30), ("deerskin", Any, 130, 102, 70), ("rabbitskin", Any, 160, 150, 136),
            ("moosehide", Any, 94, 72, 50), ("cougar", Any, 150, 122, 84),
            // known by name
            ("balaclava", H, 38, 40, 38), ("mackinaw", T, 112, 42, 38), ("mariner", T, 32, 40, 60), ("expedition", T, 144, 66, 36),
            ("military", T, 76, 82, 56), ("windbreaker", T, 150, 126, 50), ("cowichan", T, 162, 152, 132), ("fisherman", T, 168, 162, 144),
            ("hoodie", T, 86, 88, 94), ("plaid", T, 116, 48, 42),
            ("jeans", L, 54, 72, 100), ("cargo", L, 110, 102, 74), ("combat", L, 68, 76, 54), ("combat", B, 30, 30, 30), ("combat", Any, 62, 68, 50),
            ("snowpants", L, 36, 40, 54), ("insulated", L, 36, 40, 54), ("insulated", B, 46, 38, 32), ("insulated", T, 50, 64, 52),
            ("mukluk", B, 130, 100, 68), ("gauntlet", G, 58, 46, 34), ("driving", G, 50, 36, 26),
            // two words together
            ("cotton+toque", H, 62, 98, 106), ("wool+toque", H, 106, 106, 110), ("wool+scarf", S, 94, 100, 84), ("cotton+scarf", S, 152, 144, 130),
            ("down+vest", T, 62, 86, 112), ("down+ski", T, 138, 78, 42),
            // cuts
            ("toque", H, 120, 46, 40), ("down", T, 122, 74, 46), ("ski", T, 46, 78, 116), ("ski", L, 42, 62, 94), ("ski", B, 38, 42, 54), ("ski", G, 32, 46, 72),
            ("parka", T, 64, 72, 54), ("work", L, 88, 76, 58), ("work", B, 116, 82, 46), ("work", G, 130, 106, 70),
            ("running", B, 130, 130, 134), ("trail", B, 86, 72, 56), ("hiking", B, 92, 68, 46),
            ("underwear", Any, 152, 152, 148), ("thermal", Any, 150, 146, 138),
            // cloths
            ("fleece", Any, 58, 86, 80), ("wool", Any, 106, 104, 98), ("cotton", Any, 154, 150, 140),
            ("leather", B, 66, 44, 28), ("leather", G, 60, 44, 32), ("leather", Any, 72, 50, 32), ("improvised", Any, 122, 114, 100),
            // plain words
            ("scarf", S, 120, 102, 64), ("wrap", H, 134, 126, 110), ("cap", H, 60, 68, 86), ("hat", H, 114, 106, 98),
            ("jacket", T, 72, 64, 52), ("coat", T, 68, 66, 62), ("sweater", T, 122, 102, 82), ("shirt", T, 132, 114, 98), ("vest", T, 66, 74, 62),
            ("pants", L, 72, 70, 64), ("boot", B, 42, 36, 32), ("shoe", B, 72, 64, 58), ("mitt", G, 110, 54, 48), ("glove", G, 52, 52, 56),
        };

        // an item no row fits: how dark its colour may be, per slot (value from, to); the tint comes from its name
        private static readonly float[] ValueFrom = { 0.30f, 0.30f, 0.22f, 0.15f, 0.18f, 0.10f }, ValueTo = { 0.55f, 0.55f, 0.48f, 0.38f, 0.40f, 0.28f };

        /// <summary>The colour of an item in a slot (how: "table: word" or "from the name").</summary>
        internal static (byte R, byte G, byte B) ColourFor(int slot, string prefab) => ColourFor(slot, prefab, out _);

        internal static (byte R, byte G, byte B) ColourFor(int slot, string prefab, out string how)
        {
            string name = (prefab ?? "").ToLowerInvariant();
            if (name.StartsWith("gear_")) name = name.Substring(5);
            int bit = 1 << slot;
            foreach (var row in Table)
            {
                if ((row.For & bit) == 0) continue;
                bool fits;
                int plus = row.Words.IndexOf('+');
                if (plus < 0) fits = name.Contains(row.Words);
                else fits = name.Contains(row.Words.Substring(0, plus)) && name.Contains(row.Words.Substring(plus + 1));
                if (!fits) continue;
                how = "table: " + row.Words;
                return (row.Red, row.Green, row.Blue);
            }
            // always the same colour for the same name, on every PC (FNV-1a; .NET's own string hash differs from run to run)
            uint h = 2166136261;
            foreach (char ch in name) { h ^= (byte)ch; h *= 16777619; }
            float hue = (h & 0xFFFF) / 65535f * 6f;
            float sat = 0.18f + ((h >> 16) & 0xFF) / 255f * 0.22f;
            float val = ValueFrom[slot] + ((h >> 24) & 0xFF) / 255f * (ValueTo[slot] - ValueFrom[slot]);
            int sector = (int)hue; if (sector > 5) sector = 5;
            float f = hue - sector, p = val * (1f - sat), q = val * (1f - sat * f), t = val * (1f - sat * (1f - f));
            float r, g, b;
            switch (sector)
            {
                case 0: r = val; g = t; b = p; break;
                case 1: r = q; g = val; b = p; break;
                case 2: r = p; g = val; b = t; break;
                case 3: r = p; g = q; b = val; break;
                case 4: r = t; g = p; b = val; break;
                default: r = val; g = p; b = q; break;
            }
            how = "from the name";
            return ((byte)(r * 255f + 0.5f), (byte)(g * 255f + 0.5f), (byte)(b * 255f + 0.5f));
        }

        // =====================================================================================
        // the harness and the state dump
        // =====================================================================================
        /// <summary>Harness: the local look, read now, with the item behind each colour and how the colour was found. ok = it could be read.</summary>
        internal string LocalText(out bool ok)
        {
            var look = new Look(); var items = new string[Slots];
            string no = ReadLocal(look, items, _net.LocalId);
            ok = no == null;
            if (!ok) return "not read: " + no;
            var sb = new StringBuilder(look.Text(items));
            sb.Append(" | colours:");
            for (int i = 0; i < Slots; i++)
            {
                if (items[i] == "-") continue;
                ColourFor(i, items[i], out string how);
                sb.Append(' ').Append(SlotNames[i]).Append(" (").Append(how).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>Harness: the local look as a figure would wear it (for the preview figure). Null = it cannot be read.</summary>
        internal FigureLook LocalFigureLook(out string text)
        {
            var look = new Look(); var items = new string[Slots];
            string no = ReadLocal(look, items, _net.LocalId);
            if (no != null) { text = "not read: " + no; return null; }
            text = look.Text(items);
            return ToFigure(look);
        }

        internal void WriteState(StringBuilder sb)
        {
            void Out(string k, object v) => sb.Append(k).Append(": ").Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append('\n');
            Out("look_setting", On ? "on" : "off (fixed outfits)");
            string local;
            try
            {
                if (!WorldSync.InGame()) local = "not read: not in game";
                else if (!On) local = "not read: the setting is off";
                else local = LocalText(out _);
            }
            catch (Exception e) { local = "? " + e.Message; }
            Out("look_local", local);
            Out("look_api", ApiText);
            Out("look_last_sent", _local == null ? "-" : _local.Text(_localItems) + string.Format(CultureInfo.InvariantCulture, " ({0:0} s ago)", Time.realtimeSinceStartup - _localAt));
            Out("look_sent", Sent);
            Out("look_received", Received);
            foreach (var kv in _looks)
            {
                string drawn = "-";
                try { drawn = _players.LookTextOf(kv.Key); } catch { }
                sb.Append("look_of: ").Append(kv.Key).Append(' ').Append(kv.Value.Text()).Append(" | figure: ").Append(drawn).Append('\n');
            }
        }
    }
}
