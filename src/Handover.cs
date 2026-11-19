using System;
using System.Collections.Generic;
using System.Globalization;
using Il2Cpp;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Handing the item in your hands to the friend standing in front of you (one key, setting VerTusu).
    ///
    /// The item is never in two bags and never in none:
    ///   1. The giver sends an offer: a number for this hand-over and the item exactly as the game would save it
    ///      (condition, fuel or charge, rounds in a gun). The item stays in the giver's hands.
    ///   2. The receiver checks that it can take something right now (in the game, on its feet, not away, same
    ///      region, close by), builds the item from the offer in its own bag, checks that it really is there, and
    ///      only then answers "taken". Otherwise it answers why not, and nothing was made.
    ///   3. Only on "taken" does the item leave the giver's hands and bag. On "no", on a friend who leaves, or on no
    ///      answer at all, the giver simply still has it.
    /// A message that does not arrive changes nothing: the giver repeats the offer under the same number, and the
    /// receiver answers a number it has seen before the same way again without building the item a second time.
    /// The one case no order of messages can cover is a connection that dies between the receiver's "taken" and the
    /// giver hearing it: then both have the item. That was chosen over the other order, where it would be lost.
    /// </summary>
    internal sealed class HandoverSync
    {
        internal const float Reach = 2.5f, Cone = 50f;
        private const float ResendFirst = 1.5f;
        private const int MaxSends = 7;
        private const byte Nobody = 255;
        // answers
        private const byte Taken = 1, NoDowned = 2, NoAway = 3, NoScene = 4, NoFar = 5, NoLoading = 6, NoShared = 7, NoMake = 8, NoDead = 9, NoGone = 10, NoGame = 11;

        private readonly Network _net;
        private readonly RemotePlayers _players;
        private readonly Func<bool> _shared;
        private readonly HashSet<string> _said = new();

        // ---- giving: the one offer that waits for its answer ----
        private sealed class Offer
        {
            public uint Number; public byte To; public GearItem Item; public int InstanceId;
            public string Scene, Prefab, Name, LightState, Data; public Vector3 Pos; public float Hp; public int Units; public byte Lit;
            public float Since, NextSend; public int Sends; public bool SaidWaiting;
        }
        private Offer _out;
        private uint _nextNumber = (uint)new System.Random().Next(1000, int.MaxValue);
        private bool _pressed;

        // ---- taking: every offer answered in this connection, so one that arrives twice is taken once ----
        private readonly Dictionary<ulong, (byte Answer, string Detail)> _answered = new();

        // ---- the line on the HUD ----
        private string _prompt, _promptShown; private byte _promptTo = Nobody; private float _promptNext;

        // ---- for the state dump and the harness ----
        internal int Given, Received, Refused, Repeats;
        internal string Last = "-";
        /// <summary>Harness: swallow the next offer / the next answer this game sends, once, as if the network had lost it.</summary>
        internal bool LoseNextOffer, LoseNextAnswer;
        internal bool Pending => _out != null;

        public HandoverSync(Network net, RemotePlayers players, Func<bool> worldShared)
        {
            _net = net; _players = players; _shared = worldShared;
            _net.OnMessage += OnMessage;
            _net.OnStopped += () =>
            {
                if (_out != null) Keep(_out, "the connection ended before the answer came", null);
                _answered.Clear(); _prompt = null; _promptTo = Nobody;
            };
        }

        private bool SafeShared() { try { return _shared(); } catch { return false; } }
        private static void Line(string s, float seconds, bool important = true) { try { HUDMessage.AddMessage(s, seconds, important, true); } catch { } }
        private static bool Same(GearItem x, GearItem y) { try { return x != null && y != null && x.Pointer == y.Pointer; } catch { return false; } }
        private string Name(byte id) => _net.Remotes.TryGetValue(id, out var info) && !string.IsNullOrEmpty(info.Name) ? info.Name : L.T($"Oyuncu {id}", $"Player {id}");
        private static string ItemName(GearItem gi)
        {
            try { var n = gi.DisplayName; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            try { return ItemSync.PrefabName(gi.gameObject); } catch { return L.T("eşya", "item"); }
        }

        internal void KeyPressed() { _pressed = true; }

        // =====================================================================================
        // every frame
        // =====================================================================================
        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            try { TickWaiting(now); } catch (Exception e) { Oops("waiting for the answer", e); }
            try { TickGiver(now); } catch (Exception e) { Oops("the give key", e); }
            _pressed = false;
        }

        private void Oops(string where, Exception e)
        {
            if (_said.Add(where)) CoopMod.Log($"Hand-over, {where}: {e.Message} (said once)");
        }

        /// <summary>
        /// Is there an item in the hands and a friend within reach, in front, who can take it? "blocked" names the
        /// friend who stands there but cannot take anything right now.
        /// </summary>
        private bool Candidate(out byte to, out float dist, out GearItem item, out string why, out byte blocked, out string blockedWhy)
        {
            to = Nobody; dist = 0f; item = null; why = null; blocked = Nobody; blockedWhy = null;
            if (!WorldSync.InGame()) { why = "not in game"; return false; }
            if (!_net.IsConnected) { why = "not connected"; return false; }
            if (!SafeShared()) { why = "not in the shared world"; return false; }
            var pm = GameManager.GetPlayerManagerComponent(); var t = GameManager.GetPlayerTransform();
            if (pm == null || t == null) { why = "no player"; return false; }
            if (DownedSync.Instance != null && DownedSync.Instance.LocalDowned) { why = "this player is down"; return false; }
            if (SleepSync.LocalSleep() != 0) { why = "this player is asleep or passing time"; return false; }
            if (pm.PlayerIsDead()) { why = "this player is dead"; return false; }
            // with the inventory open, the item selected there is the one given (tools, food, clothes: anything in the bag)
            bool fromBag = BagSelection(out var picked);
            if (!fromBag)
            {
                var mode = pm.GetControlMode();
                if (mode != PlayerControlMode.Normal) { why = "the player is busy (" + mode + ")"; return false; }
                if (InterfaceManager.IsOverlayActiveImmediate()) { why = "a menu or screen is open"; return false; }
                item = pm.m_ItemInHands;
                if (item == null) { why = "nothing in the hands"; return false; }
            }
            else
            {
                item = picked;
                if (item == null) { why = "nothing selected in the inventory"; return false; }
            }

            string scene = CoopMod.CurrentScene();
            var me = t.position;
            float yaw = t.eulerAngles.y;
            try { var cam = GameManager.GetVpFPSCamera(); if (cam != null) yaw = cam.transform.eulerAngles.y; } catch { }
            float yr = yaw * 0.017453292f, fx = MathF.Sin(yr), fz = MathF.Cos(yr);
            float best = float.MaxValue, bestBlocked = float.MaxValue;
            foreach (var r in _players.Snapshot())
            {
                if (!r.HasState || r.Age > 3f || r.Scene != scene) continue;
                float dx = r.Pos.x - me.x, dz = r.Pos.z - me.z, flat = MathF.Sqrt(dx * dx + dz * dz);
                if (flat > Reach || MathF.Abs(r.Pos.y - me.y) > 1.6f) continue;
                // roughly in front (someone standing right on top of us is in front whichever way we look)
                if (flat > 0.4f && (dx * fx + dz * fz) / flat < MathF.Cos(Cone * 0.017453292f)) continue;
                string no = r.Downed != 0 ? L.T("yerde yatıyor", "is down") : r.Away ? L.T("oyunda değil (menüde ya da başka pencerede)", "is not in the game (in a menu or another window)") : null;
                if (no != null) { if (flat < bestBlocked) { bestBlocked = flat; blocked = r.Id; blockedWhy = no; } continue; }
                if (flat < best) { best = flat; to = r.Id; dist = flat; }
            }
            if (to != Nobody) return true;
            why = blocked != Nobody ? $"player {blocked} is within reach but cannot take anything right now" : $"no friend within {Reach:0.0} m in front";
            return false;
        }

        /// <summary>Test: stands in for the item selected in an open inventory.</summary>
        internal GearItem TestBagItem;
        internal bool TestBagOpen;

        /// <summary>Is the inventory screen open (and nothing else over it)? picked = its selected item, or null.</summary>
        private bool BagSelection(out GearItem picked)
        {
            picked = null;
            if (TestBagOpen) { picked = TestBagItem; return true; }
            try
            {
                var inv = InterfaceManager.GetPanel<Panel_Inventory>();
                if (inv == null || !inv.IsEnabled()) return false;
                var sel = inv.GetCurrentlySelectedItem();
                picked = sel != null ? sel.m_GearItem : null;
                return true;
            }
            catch { return false; }
        }

        private static void RefreshBag()
        {
            try { var inv = InterfaceManager.GetPanel<Panel_Inventory>(); if (inv != null && inv.IsEnabled()) inv.RefreshTable(); } catch { }
        }

        private void TickGiver(float now)
        {
            bool pressed = _pressed;
            if (_out != null)
            {
                _prompt = null;
                if (pressed) Line(L.T($"{_out.Name}: önceki verme işlemi sürüyor", $"{_out.Name}: the last hand-over is still going"), 2.5f);
                return;
            }
            bool can = Candidate(out byte to, out _, out var item, out _, out byte blocked, out string blockedWhy);
            if (!can)
            {
                // pressing the key next to a friend who cannot take anything says why; anywhere else the key does nothing
                if (pressed && blocked != Nobody && item != null) Line(L.T($"{Name(blocked)} şu an alamaz: {blockedWhy}", $"{Name(blocked)} cannot take it now: {blockedWhy}"), 3.5f);
                if (_prompt != null) { _prompt = null; _promptTo = Nobody; _promptNext = MathF.Min(_promptNext, now + 2f); }
                return;
            }
            string name = ItemName(item);
            // with more than one friend around, say who would get it
            string text = L.T($"{name} ver: [{ModKeys.Give.Name}]", $"Give {name}: [{ModKeys.Give.Name}]") + (_net.Remotes.Count > 1 ? $" ({Name(to)})" : "");
            if (pressed) { Begin(to, item, name, now); return; }
            // shown when it starts to apply, and again every few seconds while it does (a low-priority line: it never
            // holds back what the game itself has to say)
            _prompt = text; _promptTo = to;
            if (now >= _promptNext || text != _promptShown) { Line(text, 3.5f, false); _promptShown = text; _promptNext = now + 8f; }
        }

        // =====================================================================================
        // giving
        // =====================================================================================
        private void Begin(byte to, GearItem item, string name, float now)
        {
            var o = new Offer { Number = _nextNumber++, To = to, Item = item, Name = name, Since = now };
            o.InstanceId = item.m_InstanceID;
            o.Prefab = ItemSync.PrefabName(item.gameObject);
            o.Scene = CoopMod.CurrentScene() ?? "";
            o.Pos = GameManager.GetPlayerTransform().position;
            o.Hp = item.CurrentHP;
            o.Units = ItemSync.UnitsOf(item);
            try { o.Lit = LightSync.LitKind(item); } catch { }
            o.LightState = "";
            try
            {
                // (the same two a dropped lit item carries)
                if (o.Lit == LightSync.Flare) o.LightState = item.m_FlareItem.Serialize() ?? "";
                else if (o.Lit == LightSync.Torch) o.LightState = item.m_TorchItem.Serialize() ?? "";
            }
            catch { }
            // the item as the game itself writes it into a save: everything it knows about it
            try { o.Data = item.SerializeToString() ?? ""; }
            catch (Exception e) { o.Data = ""; CoopMod.Log($"Hand-over: {o.Prefab} could not be written out in full ({e.Message}): only its condition and amount go across"); }
            if (o.Data.Length > 20000) { CoopMod.Log($"Hand-over: the description of {o.Prefab} is very long ({o.Data.Length} characters): only its condition and amount go across"); o.Data = ""; }
            _out = o; _prompt = null;
            CoopMod.Log($"Hand-over {o.Number}: offering {o.Prefab} (condition {o.Hp:0.0}, x{o.Units}, light {o.Lit}, {o.Data.Length} characters of state) to player {to}");
            Send(o);
        }

        private void Send(Offer o)
        {
            o.Sends++;
            o.NextSend = Time.realtimeSinceStartup + ResendFirst * (1 << Math.Min(o.Sends - 1, 5));
            if (LoseNextOffer) { LoseNextOffer = false; CoopMod.Log($"Hand-over {o.Number}: this offer was swallowed on purpose (test): it will be repeated"); return; }
            var w = _net.Begin(Msg.HandOffer);
            WriteOffer(w, o.To, _net.LocalId, o.Number, o.Scene, o.Pos, o.Prefab, o.Hp, o.Units, o.Lit, o.LightState, o.Data);
            if (_net.Mode == NetMode.Host) _net.SendTo(o.To, w, DeliveryMethod.ReliableOrdered);
            else _net.Send(w, DeliveryMethod.ReliableOrdered);
        }

        private static void WriteOffer(NetDataWriter w, byte to, byte giver, uint number, string scene, Vector3 pos, string prefab, float hp, int units, byte lit, string lightState, string data)
        {
            w.Put(to); w.Put(giver); w.Put(number); w.Put(scene ?? ""); w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
            w.Put(prefab ?? ""); w.Put(hp); w.Put(units); w.Put(lit); w.Put(lightState ?? ""); w.Put(data ?? "");
        }

        private void TickWaiting(float now)
        {
            var o = _out;
            if (o == null) return;
            if (!_net.IsConnected || !_net.Remotes.ContainsKey(o.To))
            {
                Keep(o, "the other player left before answering", L.T($"{Name(o.To)} ayrıldı: {o.Name} sende kaldı", $"{Name(o.To)} left: you kept {o.Name}"));
                return;
            }
            if (now >= o.NextSend && o.Sends < MaxSends)
            {
                Repeats++;
                CoopMod.Log($"Hand-over {o.Number}: no answer after {now - o.Since:0.0} s, offering again (same number, so it cannot be taken twice)");
                Send(o);
            }
            if (!o.SaidWaiting && now - o.Since > 5f)
            {
                o.SaidWaiting = true;
                Line(L.T($"{Name(o.To)} henüz yanıt vermedi: {o.Name} hâlâ sende", $"{Name(o.To)} has not answered yet: you still have {o.Name}"), 4f);
            }
        }

        /// <summary>The hand-over is off and the item was never touched.</summary>
        private void Keep(Offer o, string why, string hud)
        {
            _out = null;
            Last = FormattableString.Invariant($"kept {o.Prefab} (hand-over {o.Number} to player {o.To}): {why}");
            CoopMod.Log($"Hand-over {o.Number}: {why}: {o.Prefab} stays here");
            if (hud != null && WorldSync.InGame()) Line(hud, 4f);
        }

        private void Answered(byte from, uint number, byte answer, string detail)
        {
            var o = _out;
            if (o == null || o.Number != number)
            {
                CoopMod.Log($"Hand-over {number}: an answer from player {from} for a hand-over that is not waiting any more: nothing to do");
                return;
            }
            float took = Time.realtimeSinceStartup - o.Since;
            if (answer != Taken)
            {
                Refused++;
                Keep(o, $"player {o.To} could not take it ({ReasonEn(answer)}{(string.IsNullOrEmpty(detail) ? "" : ": " + detail)})", L.T($"{Name(o.To)} alamadı ({ReasonTr(answer)}): {o.Name} sende kaldı", $"{Name(o.To)} could not take it ({ReasonEn(answer)}): you kept {o.Name}"));
                return;
            }
            _out = null;
            string how;
            try { how = TakeAway(o); }
            catch (Exception e) { how = "taking it away here failed: " + e.Message; }
            Given++;
            Last = FormattableString.Invariant($"gave {o.Prefab} x{o.Units} to player {o.To} (hand-over {o.Number}): confirmed after {took:0.00} s and {o.Sends} offer(s), {how}{(string.IsNullOrEmpty(detail) ? "" : "; there: " + detail)}");
            CoopMod.Log($"Hand-over {o.Number}: player {o.To} has the {o.Prefab} ({detail}); here it was {how}");
            if (WorldSync.InGame()) Line(L.T($"{o.Name} verildi: {Name(o.To)} aldı", $"{o.Name} handed over: {Name(o.To)} took it"), 4f);
        }

        /// <summary>The receiver has the item: now, and only now, it leaves this game. Says where it was taken from.</summary>
        private string TakeAway(Offer o)
        {
            var gi = o.Item;
            bool alive = false;
            try { alive = gi != null && gi.gameObject != null; } catch { }
            if (!alive) return "already gone here (nothing left to take away)";
            var pm = GameManager.GetPlayerManagerComponent(); var inv = GameManager.GetInventoryComponent();
            bool inHands = pm != null && Same(pm.m_ItemInHands, gi);
            bool inBag = inHands;
            if (!inBag) { try { inBag = gi.m_InPlayerInventory || InBag(inv, gi); } catch { } }
            if (!inBag)
            {
                // put down or dropped since the offer: it lies in the world and the friends' games show it there
                try { ItemSync.Instance?.OnLocalPickup(gi); } catch { }
                GearManager.DestroyGearObject(gi);
                return "taken from the ground (it had been put down meanwhile)";
            }
            // nothing we do to it now is news for the item sync
            bool was = ItemSync.Applying; ItemSync.Applying = true;
            try
            {
                if (inHands) { try { pm.UnequipItemInHandsSkipAnimation(); } catch (Exception e) { Oops("emptying the hands", e); } }
                if (pm != null && Same(pm.m_ItemInHands, gi)) { try { pm.UnequipImmediate(false); } catch { } }
                // switched off before it goes: an item that left the hand and is still around, switched on, would be
                // taken for a thrown one and announced to everybody
                try { gi.gameObject.SetActive(false); } catch { }
                // out of the bag the way the container window takes things out, then gone for good (the two calls the
                // mod already uses for a friend's change to a container)
                inv.RemoveGear(gi.gameObject, true);
                GearManager.DestroyGearObject(gi);
                // the game remembers what was last in the hands, to take it out again after the next interaction: not this one
                if (pm != null && Same(pm.m_LastUnequippedItem, gi)) { try { pm.ClearLastUnequippedItem(); } catch { } }
            }
            finally { ItemSync.Applying = was; }
            RefreshBag();
            bool gone = !InBag(inv, gi) && !(pm != null && Same(pm.m_ItemInHands, gi));
            return (inHands ? "taken from the hands" : "taken from the bag") + (gone ? "" : " (but the game still lists it: look at the inventory)");
        }

        private static bool InBag(Inventory inv, GearItem gi)
        {
            try
            {
                var items = inv.m_Items;
                for (int i = 0; items != null && i < items.Count; i++)
                    if (Same(items[i].m_GearItem, gi)) return true;
            }
            catch { }
            return false;
        }

        private static int UnitsInBag(Inventory inv, string prefab)
        {
            int n = 0;
            try
            {
                var items = inv.m_Items;
                for (int i = 0; items != null && i < items.Count; i++)
                {
                    var g = items[i].m_GearItem;
                    if (g != null && ItemSync.PrefabName(g.gameObject) == prefab) n += Math.Max(1, ItemSync.UnitsOf(g));
                }
            }
            catch { }
            return n;
        }

        // =====================================================================================
        // messages
        // =====================================================================================
        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (msg != Msg.HandOffer && msg != Msg.HandAnswer) return;
            try
            {
                if (msg == Msg.HandOffer)
                {
                    byte to = r.GetByte(), giver = r.GetByte(); uint number = r.GetUInt(); string scene = r.GetString();
                    var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    string prefab = r.GetString(); float hp = r.GetFloat(); int units = r.GetInt(); byte lit = r.GetByte();
                    string lightState = r.GetString(), data = r.GetString();
                    if (_net.Mode == NetMode.Host)
                    {
                        giver = from; // a client speaks only for itself
                        if (to != _net.LocalId)
                        {
                            if (_net.Remotes.TryGetValue(to, out var target) && target.Welcomed)
                            {
                                var w = _net.Begin(Msg.HandOffer);
                                WriteOffer(w, to, giver, number, scene, pos, prefab, hp, units, lit, lightState, data);
                                _net.SendTo(to, w, DeliveryMethod.ReliableOrdered);
                            }
                            else SendAnswer(giver, to, number, NoGone, "");
                            return;
                        }
                    }
                    else if (to != _net.LocalId) return;
                    Offered(giver, number, scene, pos, prefab, hp, units, lit, lightState, data);
                    return;
                }
                byte forGiver = r.GetByte(), receiver = r.GetByte(); uint num = r.GetUInt(); byte answer = r.GetByte(); string detail = r.GetString();
                if (_net.Mode == NetMode.Host)
                {
                    receiver = from;
                    if (forGiver != _net.LocalId) { SendAnswer(forGiver, receiver, num, answer, detail, relayed: true); return; }
                }
                else if (forGiver != _net.LocalId) return;
                Answered(receiver, num, answer, detail);
            }
            catch (Exception e) { CoopMod.Log($"Hand-over message {msg}: {e.Message}"); }
        }

        private void SendAnswer(byte giver, byte receiver, uint number, byte answer, string detail, bool relayed = false)
        {
            if (!relayed && LoseNextAnswer) { LoseNextAnswer = false; CoopMod.Log($"Hand-over {number}: this answer was swallowed on purpose (test): the offer will come again"); return; }
            var w = _net.Begin(Msg.HandAnswer); w.Put(giver); w.Put(receiver); w.Put(number); w.Put(answer); w.Put(detail ?? "");
            if (_net.Mode == NetMode.Host) _net.SendTo(giver, w, DeliveryMethod.ReliableOrdered);
            else _net.Send(w, DeliveryMethod.ReliableOrdered);
        }

        // =====================================================================================
        // taking
        // =====================================================================================
        private void Offered(byte giver, uint number, string scene, Vector3 giverPos, string prefab, float hp, int units, byte lit, string lightState, string data)
        {
            ulong key = ((ulong)giver << 32) | number;
            if (_answered.TryGetValue(key, out var before))
            {
                // the same offer again: our answer did not get through. The item was made (or refused) once; say so again.
                Repeats++;
                CoopMod.Log($"Hand-over {number} from player {giver} arrived again: answering as before ({ReasonEn(before.Answer)}), nothing made twice");
                SendAnswer(giver, _net.LocalId, number, before.Answer, before.Detail);
                return;
            }
            byte answer; string detail = "";
            try { answer = Take(giver, number, scene, giverPos, prefab, hp, units, lit, lightState, data, out detail); }
            catch (Exception e) { answer = NoMake; detail = e.Message; CoopMod.Log($"Hand-over {number}: taking {prefab} failed: {e.Message}"); }
            _answered[key] = (answer, detail);
            if (answer != Taken)
            {
                Refused++;
                Last = FormattableString.Invariant($"did not take {prefab} from player {giver} (hand-over {number}): {ReasonEn(answer)}{(string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")")}");
                CoopMod.Log($"Hand-over {number}: {prefab} from player {giver} not taken: {ReasonEn(answer)} {detail}");
            }
            SendAnswer(giver, _net.LocalId, number, answer, detail);
        }

        private byte Take(byte giver, uint number, string scene, Vector3 giverPos, string prefab, float hp, int units, byte lit, string lightState, string data, out string detail)
        {
            detail = "";
            if (!WorldSync.InGame()) return NoGame;
            if (WorldSync.Loading()) return NoLoading;
            if (!SafeShared()) return NoShared;
            var pm = GameManager.GetPlayerManagerComponent(); var t = GameManager.GetPlayerTransform(); var inv = GameManager.GetInventoryComponent();
            if (pm == null || t == null || inv == null) return NoGame;
            if (DownedSync.Instance != null && DownedSync.Instance.LocalDowned) return NoDowned;
            if (pm.PlayerIsDead()) return NoDead;
            if (PauseSync.LocalAway) return NoAway;
            if (scene != CoopMod.CurrentScene()) return NoScene;
            float d = Vector3.Distance(t.position, giverPos);
            // (the giver measured its reach; a little more here, because both may have moved since)
            if (d > Reach + 2f) { detail = FormattableString.Invariant($"{d:0.0} m apart"); return NoFar; }

            GearItem gi = null; string made = "full state";
            int before = UnitsInBag(inv, prefab);
            bool was = ItemSync.Applying; ItemSync.Applying = true; // our own item appearing here is not a drop to tell the others about
            try
            {
                if (!string.IsNullOrEmpty(data))
                {
                    try { gi = GearItem.InstantiateAndDeserializeGearItem(prefab, data, null, false, false); }
                    catch (Exception e) { CoopMod.Log($"Hand-over {number}: {prefab} could not be built from its full description ({e.Message}): building it from condition and amount"); }
                }
                if (gi == null)
                {
                    gi = GearItem.InstantiateGearItem(prefab);
                    made = "condition and amount only";
                    if (gi != null) { try { gi.CurrentHP = hp; } catch { } }
                }
                if (gi == null) { detail = "unknown item " + prefab; return NoMake; }
                try { var st = gi.GetStackableItem(); if (st != null && units > 0) st.m_Units = units; } catch { }
                // the number the item had in the giver's game may belong to something in this bag already
                try { var other = inv.FindByInstanceID(gi.m_InstanceID); if (other != null && !Same(other, gi)) gi.m_InstanceID = gi.GetInstanceID(); } catch { }

                GearItem bagged = null;
                try { bagged = pm.AddItemToPlayerInventory(gi, false, false); }
                catch (Exception e) { detail = "the game refused it: " + e.Message; }
                int after = UnitsInBag(inv, prefab);
                if (after < before + Math.Max(1, units))
                {
                    // not in the bag (or not all of it): nothing may be left behind, and the giver keeps the item
                    try { if (InBag(inv, gi)) inv.RemoveGear(gi.gameObject, true); } catch { }
                    try { GearManager.DestroyGearObject(gi); } catch { }
                    if (detail.Length == 0) detail = $"the bag held {before} before and {after} after";
                    return NoMake;
                }
                var mine = bagged ?? gi;
                string light = "";
                if (lit != LightSync.None) light = ", " + KeepLight(pm, mine, lit, lightState);
                Received++;
                string name = ItemName(mine);
                detail = FormattableString.Invariant($"{made}, bag {before}->{after}{light}");
                Last = FormattableString.Invariant($"took {prefab} x{units} from player {giver} (hand-over {number}): {detail}, condition {hp:0.0}");
                CoopMod.Log($"Hand-over {number}: took {prefab} x{units} from player {giver} ({d:0.0} m away): {detail}");
                Line(L.T($"{Name(giver)} sana {name} verdi", $"{Name(giver)} gave you {name}"), 4.5f);
                return Taken;
            }
            finally { ItemSync.Applying = was; }
        }

        /// <summary>
        /// A light that was burning when it was handed over: with free hands it is taken into the hand, still lit;
        /// otherwise it goes into the bag put out (a burning flare cannot be put out and burns on).
        /// </summary>
        private string KeepLight(PlayerManager pm, GearItem gi, byte lit, string lightState)
        {
            try
            {
                bool free = pm.m_ItemInHands == null && pm.GetControlMode() == PlayerControlMode.Normal && !InterfaceManager.IsOverlayActiveImmediate();
                if (free && pm.ItemCanEquipInHands(gi))
                {
                    pm.EquipItem(gi, false);
                    // (the helper that lights a friend's dropped light here: it looks again a moment later and lights it if needed)
                    ItemSync.Instance?.RelightSoon(gi, lit, lightState);
                    return "taken into the hand, lit";
                }
            }
            catch (Exception e) { Oops("taking the light into the hand", e); }
            try
            {
                switch (lit)
                {
                    case LightSync.Torch: gi.m_TorchItem?.Extinguish(TorchState.Extinguished); return "put out for the bag";
                    case LightSync.Lamp: gi.m_KeroseneLampItem?.TurnOff(false); return "switched off for the bag";
                    case LightSync.Flashlight: gi.m_FlashlightItem?.TurnOff(); return "switched off for the bag";
                }
            }
            catch (Exception e) { Oops("putting the light out", e); }
            return "in the bag as it was";
        }

        private static string ReasonEn(byte a) => a switch
        {
            Taken => "taken", NoDowned => "down", NoAway => "away", NoScene => "in another region", NoFar => "too far apart", NoLoading => "loading",
            NoShared => "not in the shared world", NoMake => "the item could not be put in the bag", NoDead => "dead", NoGone => "left the game", NoGame => "not in the game", _ => "reason " + a,
        };

        private static string ReasonTr(byte a) => a switch
        {
            NoDowned => "yerde yatıyor", NoAway => "oyunda değil", NoScene => "başka bir bölgede", NoFar => "yeterince yakın değil", NoLoading => "yükleme ekranında",
            NoShared => "ortak dünyada değil", NoMake => "eşya çantasına konamadı", NoDead => "hayatta değil", NoGone => "oyundan ayrıldı", NoGame => "oyunda değil", _ => "bilinmeyen neden",
        };

        // =====================================================================================
        // the harness
        // =====================================================================================
        /// <summary>As if the give key were pressed now. Null = an offer went out, else why not.</summary>
        internal string TestPress()
        {
            if (_out != null) return $"hand-over {_out.Number} is still waiting for its answer";
            if (!Candidate(out byte to, out _, out var item, out string why, out _, out _)) return why;
            Begin(to, item, ItemName(item), Time.realtimeSinceStartup);
            return null;
        }

        internal void WriteState(System.Text.StringBuilder sb)
        {
            void Out(string k, object v) => sb.Append(k).Append(": ").Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append('\n');
            Out("handovers_given", Given);
            Out("handovers_received", Received);
            Out("handover_last", Last);
            Out("handovers_refused", Refused);
            Out("handover_repeats", Repeats);
            Out("handover_waiting", _out == null ? "-" : FormattableString.Invariant($"{_out.Number}: {_out.Prefab} to player {_out.To}, {Time.realtimeSinceStartup - _out.Since:0.0} s, {_out.Sends} offer(s) sent"));
            Out("handover_prompt", _prompt ?? "-");
            string can;
            try { can = Candidate(out byte to, out float d, out var item, out string why, out _, out _) ? FormattableString.Invariant($"yes: {ItemSync.PrefabName(item.gameObject)} to player {to} at {d:0.0} m") : "no: " + why; }
            catch (Exception e) { can = "? " + e.Message; }
            Out("handover_possible", can);
        }
    }
}
