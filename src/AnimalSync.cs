using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.PDID;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Stage 4: shared wildlife. When the host is in the same region as you, the host's game decides
    /// what every animal does; your game shows the same animals ("puppets") moving the same way.
    /// Shots and hits on a puppet are sent to the game that runs the animal, which applies them to the real one.
    /// When you are alone in a region, its animals run on your own game as usual.
    ///
    /// 0.9.0, ownership. The game's AI only ever notices the player of the game it runs on, so a wolf that runs on
    /// the host never stalks a friend. Every shared animal therefore has one owner game at a time (the host unless
    /// handed over). The owner's game runs the real AI; every other game, the host included, shows a puppet.
    ///   - The host decides who owns what. A predator goes to the game of the player it should be dealing with:
    ///     the player who is clearly nearest (30 % nearer than the owner's player and within the animal's own
    ///     senses), or the player its puppet has just noticed. Never while it stalks, attacks or struggles.
    ///   - Handing over: the old owner's copy becomes a puppet first, then the new owner lets its copy live again
    ///     (health and wounds as they were, nobody in mind, wandering), so two games never decide at once.
    ///   - The owner decides health and death and tells the host; the host stays the keeper of carcasses.
    ///   - Setting HayvanSahipligi off, or a hand-over that fails: the animal stays with the host as before 0.9.0.
    /// </summary>
    internal sealed class AnimalSync
    {
        internal static AnimalSync Instance;
        private readonly Network _net;
        private readonly Func<bool> _shared, _ownPref;

        public AnimalSync(Network net, Func<bool> shared, Func<bool> ownership = null)
        {
            Instance = this;
            _net = net;
            _shared = shared;
            _ownPref = ownership;
            _net.OnMessage += OnMessage;
            _net.OnStopped += () => { ReleaseAll(); ResetHost(); };
            _net.OnRemoteJoined += (id, name) => _resendInfoAt = 0f;
            _net.OnRemoteLeft += OwnerLeft;
        }

        /// <summary>The one hook that is placed by hand, so that a game version without it only loses this and nothing else.</summary>
        internal void ApplyPatches(HarmonyLib.Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(BaseAi), nameof(BaseAi.Serialize), Type.EmptyTypes);
                if (m == null) throw new MissingMethodException("this game version has no such method");
                h.Patch(m, prefix: new HarmonyMethod(typeof(AiSavePatch), nameof(AiSavePatch.Prefix)), postfix: new HarmonyMethod(typeof(AiSavePatch), nameof(AiSavePatch.Postfix)));
                SaveHook = true;
            }
            catch (Exception e) { CoopMod.Log("Animals: could not hook the animal save (" + e.Message + "): an animal that a friend's game runs is saved standing idle on the host"); }
        }
        internal static bool SaveHook;
        /// <summary>Host: this body follows a friend's game right now; in the host's save it must not end up as the idle puppet it is here.</summary>
        internal static bool SavesAsLiving(IntPtr p) => Instance != null && Instance._net.Mode == NetMode.Host && _puppets.Contains(p);

        private static string PrefabName(GameObject go) => ItemSync.PrefabName(go);

        /// <summary>The kind of animal: "WILDLIFE_Doe_4" and "WILDLIFE_Doe(Clone)" are both "WILDLIFE_Doe".</summary>
        private static string Kind(GameObject go)
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
        private static string KindOf(BaseAi ai) { try { return ai != null ? Kind(ai.gameObject) : "animal"; } catch { return "animal"; } }
        private bool InShared => _net.IsConnected && _shared() && WorldSync.InGame();

        public void Update()
        {
            _on = OwnershipOn;
            try { TrackStruggle(); } catch { }
            if (!InShared) { if (_puppets.Count > 0 || _ownedHere.Count > 0) ReleaseAll(); return; }
            try
            {
                if (_net.Mode == NetMode.Host) HostUpdate();
                else ClientUpdate();
            }
            catch (Exception e) { if (!_loggedError) { _loggedError = true; CoopMod.Log("Animals: " + e); } }
        }
        private bool _loggedError;

        // =============================== ownership: what both sides share ===============================
        /// <summary>Test harness: 0 = the setting decides, 1 = on, 2 = off.</summary>
        internal static int TestOwnOverride;
        internal bool OwnershipOn
        {
            get
            {
                if (TestOwnOverride != 0) return TestOwnOverride == 1;
                try { return _ownPref == null || _ownPref(); } catch { return true; }
            }
        }
        private bool _on = true, _wasOn = true;

        internal const byte NoTarget = 255, OtherTarget = 254;                // what an animal has in mind: nobody / something that is not a player
        private const byte Yours = 1, GiveBack = 2, TakenBack = 3, No = 4;     // host -> client (Msg.AnimalOwner)
        private const byte Took = 1, CouldNot = 2, Back = 3, Busy = 4, Want = 5, Died = 6, Sensed = 7; // client -> host (Msg.AnimalOwnerNote)
        private const float NearerBy = 0.7f;      // a new owner's player must be at least 30 % nearer than the owner's
        private const float MinHold = 5f;         // no change of hands more often than this
        private const float SensedHold = 12f;     // after it went to the player it noticed, the distance rule waits this long
        private const float OfferWait = 4f, AskBackWait = 4f, SilentWait = 5f;

        /// <summary>Hand-overs this game saw through (the host: all of them; a client: those to or from it) and those that failed.</summary>
        internal int Handovers, HandoverFails;
        private readonly HashSet<string> _said = new();
        private void Once(string key, string text) { if (_said.Add(key)) CoopMod.Log(text); }

        /// <summary>What an animal takes along when another game takes over: where it is, what it is doing, how hurt it is.</summary>
        private struct OwnState
        {
            public Vector3 Pos; public float Yaw, Hp, BleedElapsed, BleedDeath, WoundedElapsed;
            public byte Mode, Target; public bool Bleeding, Wounded, Known;
        }

        private OwnState Capture(BaseAi ai)
        {
            var s = new OwnState { Target = NoTarget, Hp = -1f, Known = true };
            var t = ai.transform; s.Pos = t.position; s.Yaw = t.eulerAngles.y;
            s.Mode = (byte)(int)ai.GetAiMode();
            try { s.Hp = ai.m_CurrentHP; } catch { }
            try { s.Bleeding = ai.m_BleedingOut; s.BleedElapsed = ai.m_ElapsedBleedingOutMinutes; s.BleedDeath = ai.m_DeathAfterBleeingOutMinutes; } catch { }
            try { s.Wounded = ai.m_Wounded; s.WoundedElapsed = ai.m_ElapsedWoundedMinutes; } catch { }
            s.Target = TargetOf(ai);
            return s;
        }

        /// <summary>Whom the animal has in mind on this game: this game's player, something else, or nobody.</summary>
        private byte TargetOf(BaseAi ai)
        {
            try
            {
                var t = ai.m_CurrentTarget;
                if (t == null) return NoTarget;
                return t.IsPlayer() ? _net.LocalId : OtherTarget;
            }
            catch { return NoTarget; }
        }

        private static void Put(NetDataWriter w, OwnState s)
        {
            w.Put(s.Pos.x); w.Put(s.Pos.y); w.Put(s.Pos.z); w.Put((short)Mathf.RoundToInt(s.Yaw * 100f / 2f)); w.Put(s.Mode); w.Put(s.Target); w.Put(s.Hp);
            w.Put((byte)((s.Bleeding ? 1 : 0) | (s.Wounded ? 2 : 0) | (s.Known ? 4 : 0)));
            w.Put(s.BleedElapsed); w.Put(s.BleedDeath); w.Put(s.WoundedElapsed);
        }

        private static OwnState GetState(NetDataReader r)
        {
            var s = new OwnState { Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()), Yaw = r.GetShort() * 2f / 100f, Mode = r.GetByte(), Target = r.GetByte(), Hp = r.GetFloat() };
            byte f = r.GetByte();
            s.Bleeding = (f & 1) != 0; s.Wounded = (f & 2) != 0; s.Known = (f & 4) != 0;
            s.BleedElapsed = r.GetFloat(); s.BleedDeath = r.GetFloat(); s.WoundedElapsed = r.GetFloat();
            return s;
        }

        /// <summary>Wolves, bears, moose and cougars (by the game's own types): the animals that go for a player.</summary>
        private static bool PredatorKind(BaseAi ai)
        {
            try { var st = ai.m_AiSubType; if (st == AiSubType.Wolf || st == AiSubType.Bear || st == AiSubType.Moose || st == AiSubType.Cougar) return true; } catch { }
            try { if (ai.BaseWolf != null || ai.Bear != null || ai.Moose != null || ai.Cougar != null) return true; } catch { }
            return false;
        }

        /// <summary>
        /// Modes an animal can change hands in by the rules: it is dealing with nobody, and the other game can start it
        /// again from cold. Everything else (stalking, attacking, struggling, holding ground, fleeing, stunned, a pack's
        /// moves, scripted scenes, dead) stays where it is until it is over.
        /// </summary>
        private static bool CanChangeHands(AiMode m)
        {
            switch (m)
            {
                case AiMode.Wander: case AiMode.WanderPaused: case AiMode.Idle: case AiMode.GoToPoint:
                case AiMode.Investigate: case AiMode.InvestigateSmell: case AiMode.InvestigateFood:
                case AiMode.FollowWaypoints: case AiMode.PatrolPointsOfInterest: case AiMode.ScratchingAntlers:
                case AiMode.Feeding: case AiMode.Sleep:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>A test command may ask in more modes, but never in the middle of an attack or a struggle.</summary>
        private static bool CanBeAsked(AiMode m)
        {
            switch (m)
            {
                case AiMode.Attack: case AiMode.Struggle: case AiMode.PassingAttack: case AiMode.Stunned:
                case AiMode.Dead: case AiMode.ScriptedSequence: case AiMode.Disabled:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>Is this animal fighting this game's own player right now.</summary>
        private static bool EngagedHere(BaseAi ai)
        {
            try { var m = ai.GetAiMode(); if (m == AiMode.Attack || m == AiMode.Struggle || m == AiMode.PassingAttack) return true; } catch { }
            return IsStrugglePartner(ai);
        }

        private static bool IsStrugglePartner(BaseAi ai)
        {
            try
            {
                var ps = GameManager.GetPlayerStruggleComponent();
                if (ps == null || !ps.InStruggle()) return false;
                var p = ps.m_PartnerBaseAi;
                return p != null && p.Pointer == ai.Pointer;
            }
            catch { return false; }
        }

        // Some animals take a moment to get going again (the moose finishes the walk it is on first): asked again for a few seconds.
        private sealed class Waking { public AiMode Want; public float Until, Next; }

        /// <summary>
        /// A body that only followed gets its own will back: as hurt as its last owner had it, with nobody in mind yet,
        /// in a mode the game can start from cold (fleeing if it fled, its old route if this game knows one, else wandering).
        /// From there the game's own senses decide. Call it once the body is no longer a puppet, or the mode is refused.
        /// </summary>
        private AiMode MakeLiving(BaseAi ai, OwnState s, AiMode route, Waking wake, byte lastOwner)
        {
            float now = Time.realtimeSinceStartup;
            try { if (s.Hp > 0f) ai.m_CurrentHP = s.Hp; } catch { }
            if (s.Known)
            {
                try
                {
                    ai.m_Wounded = s.Wounded; ai.m_ElapsedWoundedMinutes = s.WoundedElapsed;
                    ai.m_BleedingOut = s.Bleeding;
                    if (s.Bleeding) { ai.m_ElapsedBleedingOutMinutes = s.BleedElapsed; ai.m_DeathAfterBleeingOutMinutes = s.BleedDeath; }
                }
                catch { }
                // a puppet that fell behind: to where its owner had it (through the path-walker, a plain move is undone)
                try
                {
                    var d = s.Pos - ai.transform.position;
                    if (d.magnitude > 4f)
                    {
                        var ma = ai.GetMoveAgent();
                        bool ok = ma != null && ma.Warp(s.Pos, 2f, true, -1);
                        if (!ok && ma != null) { var ag = ma.m_Agent; if (ag != null && ag.enabled && ag.isOnNavMesh) ag.Move(d); }
                    }
                }
                catch { }
            }
            // whoever the puppet "saw" while it could not act is forgotten: the game only reacts to a target that is new to it
            try { ai.ClearTarget(); } catch { }
            var want = (AiMode)s.Mode == AiMode.Flee ? AiMode.Flee
                : (route == AiMode.FollowWaypoints || route == AiMode.PatrolPointsOfInterest) ? route : AiMode.Wander;
            if (want == AiMode.Flee)
            {
                // it was running from the player of the game that had it: here it runs from where that player stands
                try { if (CoopMod.Instance.Players.TryGetState(lastOwner, out var sc, out var from) && sc == CoopMod.CurrentScene()) ai.m_FleeFromPos = from; } catch { }
            }
            ai.SetAiMode(want);
            var got = ai.GetAiMode();
            if (got == AiMode.GoToPoint) { try { ai.m_TargetMode = want; } catch { } } // it will take it up where its walk ends
            if (wake != null) { wake.Want = want; wake.Until = now + 6f; wake.Next = now + 0.5f; }
            return got;
        }

        private void Settle(BaseAi ai, Waking wake, float now)
        {
            if (wake.Until <= 0f || now < wake.Next) return;
            wake.Next = now + 0.5f;
            AiMode m;
            try { m = ai.GetAiMode(); } catch { wake.Until = 0f; return; }
            if (m != AiMode.Idle && m != AiMode.GoToPoint && m != AiMode.None) { wake.Until = 0f; return; } // it has a will of its own again
            if (now >= wake.Until)
            {
                wake.Until = 0f;
                Once("idle" + KindOf(ai), $"{KindOf(ai)}: after a hand-over the game would not start its {wake.Want} again (it stands in {m})");
                return;
            }
            try { ai.SetAiMode(wake.Want); if (ai.GetAiMode() == AiMode.GoToPoint) ai.m_TargetMode = wake.Want; } catch { }
        }

        /// <summary>While an animal struggles with a player, its puppet stays on that player, where this game shows them.</summary>
        private Vector3 SeenAt(byte mode, byte owner, Vector3 reported)
        {
            if (mode != (byte)(int)AiMode.Struggle || owner == _net.LocalId) return reported;
            try
            {
                if (CoopMod.Instance.Players.TryGetState(owner, out var sc, out var pp) && sc == CoopMod.CurrentScene()) return pp;
            }
            catch { }
            return reported;
        }

        // =============================== host ===============================
        private sealed class HostAnimal
        {
            public ushort Id; public BaseAi Ai; public string Guid, Region = ""; public Vector3 RegionPos;
            // ---- 0.9.0: which game runs its AI ----
            public byte Owner = Protocol.HostId;     // the host unless handed over
            public Puppet Pup;                       // set while a friend's game runs it: this game's copy only follows
            public byte Pending;                     // 0 settled, 1 offered (waiting for "took it"), 2 asked back (waiting for it)
            public ushort Round;                     // goes up with every change of hands: a late answer to an old question is told apart
            public float PendingSince, LastChangeAt = -100f, HoldUntil, NextRule, NextAskBack, RetryAt;
            public bool Predator, Pinned, NoHandover; public int NotReady;
            public float Range = 60f, FeedRange = 10f, SleepRange = 6f;
            public AiMode PreMode = AiMode.Wander;   // what it was doing here before it was handed over
            public readonly Waking Wake = new();
        }
        private readonly Dictionary<IntPtr, HostAnimal> _hostAnimals = new();
        private readonly Dictionary<ushort, HostAnimal> _hostById = new();
        private readonly List<HostAnimal> _away = new();                      // animals a friend's game runs right now
        private readonly List<(byte Id, Vector3 Pos)> _cands = new();         // players an animal could be dealing with (here, on their feet)
        private readonly HashSet<byte> _present = new();                      // friends standing in this region at all
        private readonly Dictionary<byte, float> _refusing = new();           // friends whose game has ownership switched off (until when we believe that)
        private readonly Dictionary<byte, float> _hereSince = new();          // since when each friend has been standing in this region
        private const float SettleIn = 8f;                                    // a friend who has only just walked in has no bodies for the animals yet
        private ushort _nextId = 1;
        private BaseAi[] _hostList;
        private float _nextHostList, _nextHostSend, _resendInfoAt, _nextOwnTick;
        private string _hostScene;
        private int _friendsHere;

        private void ResetHost()
        {
            // (the bodies are gone with the region, or the shared game is over: nothing here follows anybody any more)
            foreach (var ha in _hostAnimals.Values) if (ha.Pup != null) { Release(ha.Pup); ha.Pup = null; }
            _hostAnimals.Clear(); _hostById.Clear(); _away.Clear(); _hereSince.Clear(); _hostList = null; _nextHostList = 0f; _hostScene = null; _friendsHere = 0;
        }

        private void HostUpdate()
        {
            float now = Time.realtimeSinceStartup;
            var scene = CoopMod.CurrentScene();
            if (scene != _hostScene) { ResetHost(); _hostScene = scene; _nextHostList = now + 3f; return; } // let the region load
            if (now >= _nextHostList)
            {
                _nextHostList = now + 2f;
                _hostList = UnityEngine.Object.FindObjectsOfType<BaseAi>();
                // gone animals
                var alive = new HashSet<IntPtr>();
                foreach (var ai in _hostList) if (ai != null) alive.Add(ai.Pointer);
                foreach (var key in new List<IntPtr>(_hostAnimals.Keys))
                {
                    if (alive.Contains(key)) continue;
                    var gone = _hostAnimals[key]; _hostAnimals.Remove(key); _hostById.Remove(gone.Id);
                    if (gone.Pup != null) { Release(gone.Pup); gone.Pup = null; _away.Remove(gone); }
                    var g = _net.Begin(Msg.AnimalGone); g.Put(scene); g.Put(gone.Id);
                    _net.Send(g, DeliveryMethod.ReliableOrdered);
                }
                // new animals
                foreach (var ai in _hostList)
                {
                    if (ai == null || _hostAnimals.ContainsKey(ai.Pointer)) continue;
                    // a corpse is shared only if it lies in a spawn region (so the friend's game can make the animal);
                    // old corpses from the save without one are left to the carcass sync
                    try { if (ai.GetAiMode() == AiMode.Dead && ai.GetSpawnRegionParent() == null) continue; } catch { continue; }
                    var ha = new HostAnimal { Id = _nextId++, Ai = ai, Guid = GuidFor(ai.gameObject) };
                    if (_nextId == 0) _nextId = 1;
                    _hostAnimals[ai.Pointer] = ha; _hostById[ha.Id] = ha;
                    Describe(ha);
                    SendInfo(scene, ha);
                }
            }
            // a friend who just walked in here should not wait for the next round of descriptions
            int here = 0;
            try { here = CoopMod.Instance.Players.CountInScene(scene); } catch { }
            if (here > _friendsHere && _resendInfoAt > now + 0.3f) _resendInfoAt = now + 0.3f;
            _friendsHere = here;
            if (now >= _resendInfoAt && _hostList != null)
            {
                _resendInfoAt = now + 10f;
                foreach (var ha in _hostAnimals.Values) { try { SendInfo(scene, ha); } catch { } }
                if (_hostAnimals.Count > 0) CoopMod.Log($"Sharing {_hostAnimals.Count} animals in {scene}" + (here > 0 ? $" with {here} friend(s) here" : ""));
            }
            // animals a friend's game runs: this game's copy follows them, the way a friend's puppet follows ours
            for (int i = _away.Count - 1; i >= 0; i--) FollowAway(_away[i], now);
            if (now >= _nextOwnTick)
            {
                _nextOwnTick = now + 0.25f;
                try { OwnershipTick(now, scene); } catch (Exception e) { Once("tick", "Animal ownership: " + e); }
            }
            if (now < _nextHostSend || _hostAnimals.Count == 0) return;
            _nextHostSend = now + 0.1f;
            // several small packets: one unreliable packet holds about 1000 bytes
            NetDataWriter w = null; int countPos = 0; ushort n = 0;
            void Flush()
            {
                if (w == null || n == 0) return;
                var data = w.Data; data[countPos] = (byte)(n & 0xFF); data[countPos + 1] = (byte)(n >> 8);
                _net.Send(w, DeliveryMethod.Unreliable);
                w = null; n = 0;
            }
            foreach (var ha in _hostAnimals.Values)
            {
                if (ha.Ai == null) continue;
                Vector3 p; float yaw, hp = -1f; byte mode, target = NoTarget;
                try
                {
                    var fp = ha.Pup;
                    if (fp != null)
                    {
                        // a friend's game runs it: pass on what that game says, not where our copy has got to
                        p = fp.Reported; yaw = fp.Yaw; mode = fp.Mode; hp = fp.Hp; target = fp.TargetId;
                    }
                    else
                    {
                        var t = ha.Ai.transform; p = t.position; yaw = t.eulerAngles.y;
                        mode = (byte)(int)ha.Ai.GetAiMode();
                        try { hp = ha.Ai.m_CurrentHP; } catch { }
                        target = TargetOf(ha.Ai);
                    }
                }
                catch { continue; }
                if (w == null) { w = _net.Begin(Msg.AnimalStates); w.Put(scene); countPos = w.Length; w.Put((ushort)0); }
                w.Put(ha.Id); w.Put(p.x); w.Put(p.y); w.Put(p.z); w.Put((short)Mathf.RoundToInt(yaw * 100f / 2f)); w.Put(mode);
                w.Put(ha.Owner); w.Put(target); w.Put(hp);
                if (++n >= 32) Flush();
            }
            Flush();
        }

        private void SendInfo(string scene, HostAnimal ha)
        {
            var w = _net.Begin(Msg.AnimalInfo);
            w.Put(scene);
            w.Put(ha.Id);
            w.Put(ha.Guid ?? "");
            // a dead animal no longer knows its spawn region: keep what it had while alive
            try
            {
                var sr = ha.Ai.GetSpawnRegionParent();
                if (sr != null) { ha.Region = GuidFor(sr.gameObject) ?? ""; ha.RegionPos = sr.transform.position; }
            }
            catch { }
            w.Put(ha.Region); w.Put(ha.RegionPos.x); w.Put(ha.RegionPos.y); w.Put(ha.RegionPos.z);
            w.Put(Kind(ha.Ai.gameObject));
            byte wm = 0; try { wm = (byte)(int)ha.Ai.m_WildlifeMode; } catch { }
            w.Put(wm);
            _net.Send(w, DeliveryMethod.ReliableOrdered);
        }

        private static string GuidFor(GameObject go)
        {
            string g = null;
            try { g = ObjectGuid.MaybeGetGuidFromGameObject(go); } catch { }
            if (string.IsNullOrEmpty(g))
            {
                g = System.Guid.NewGuid().ToString();
                try { ObjectGuid.MaybeAttachObjectGuidAndRegister(go, g); } catch { }
            }
            return g;
        }

        // ---------------- host: who owns what ----------------
        /// <summary>What kind of animal this is for the ownership rules, and how far its senses reach.</summary>
        private static void Describe(HostAnimal ha)
        {
            var ai = ha.Ai;
            ha.Predator = PredatorKind(ai);
            float sight = 0f, smell = 0f;
            try { sight = ai.m_DetectionRange; } catch { }
            try { smell = ai.m_SmellRange; } catch { }
            // a little beyond what it can see or smell, so it changes hands before it would notice, not after
            ha.Range = Mathf.Clamp(Mathf.Max(sight, smell) * 1.25f, 30f, 150f);
            try { ha.FeedRange = Mathf.Clamp(ai.m_DetectionRangeWhileFeeding, 6f, ha.Range); } catch { }
            try { ha.SleepRange = Mathf.Clamp(ai.m_HearFootstepsRangeWhileSleeping, 4f, ha.Range); } catch { }
        }

        /// <summary>
        /// How near a player must be to take the animal over. A feeding or sleeping animal cannot be put back to its
        /// meal or its sleep on another game, so it only changes hands once the player is so close that it would have
        /// been disturbed anyway.
        /// </summary>
        private static float Reach(HostAnimal ha, AiMode mode) => mode == AiMode.Feeding ? ha.FeedRange : mode == AiMode.Sleep ? ha.SleepRange : ha.Range;
        private static bool Rested(HostAnimal ha, float now) => now - ha.LastChangeAt >= MinHold && now >= ha.HoldUntil;
        private bool Refusing(byte id, float now) => _refusing.TryGetValue(id, out var until) && now < until;

        private static void Where(HostAnimal ha, out Vector3 pos, out AiMode mode)
        {
            if (ha.Pup != null) { pos = ha.Pup.Reported; mode = (AiMode)ha.Pup.Mode; }
            else { pos = ha.Ai.transform.position; mode = ha.Ai.GetAiMode(); }
        }

        private void OwnershipTick(float now, string scene)
        {
            if (_on != _wasOn)
            {
                _wasOn = _on;
                CoopMod.Log(_on ? "Animal ownership is on: a predator runs on the game of the player it is dealing with"
                                : "Animal ownership is switched off (HayvanSahipligi): every animal stays with the host, as before 0.9.0");
            }
            // who is here, and who of them an animal could be dealing with (in this region, heard from lately, on their feet)
            _cands.Clear(); _present.Clear();
            try
            {
                var pt = GameManager.GetPlayerTransform();
                bool down = false; try { down = DownedSync.Instance != null && DownedSync.Instance.LocalDowned; } catch { }
                if (pt != null && !down) _cands.Add((Protocol.HostId, pt.position));
            }
            catch { }
            try
            {
                foreach (var r in CoopMod.Instance.Players.Snapshot())
                {
                    if (!r.HasState || r.Scene != scene || !_net.Remotes.ContainsKey(r.Id)) continue;
                    if (r.Age < 8f) _present.Add(r.Id);
                    if (!_present.Contains(r.Id)) continue;
                    if (!_hereSince.TryGetValue(r.Id, out var since)) _hereSince[r.Id] = since = now;
                    if (r.Age < 3f && r.Downed == 0 && now - since >= SettleIn) _cands.Add((r.Id, r.Pos));
                }
                if (_hereSince.Count > _present.Count)
                    foreach (var id in new List<byte>(_hereSince.Keys)) if (!_present.Contains(id)) _hereSince.Remove(id);
            }
            catch { }
            foreach (var ha in _hostAnimals.Values)
            {
                if (ha.Ai == null) continue;
                try { OwnershipOf(ha, now); }
                catch (Exception e) { Once("own" + ha.Id, $"Animal ownership ({KindOf(ha.Ai)} {ha.Id}): {e.Message}"); }
            }
        }

        private void OwnershipOf(HostAnimal ha, float now)
        {
            var pup = ha.Pup;
            if (pup == null) Settle(ha.Ai, ha.Wake, now);
            else
            {
                // things that end a friend's ownership whatever the animal is doing
                if (!_present.Contains(ha.Owner)) { ComeHome(ha, null, $"player {ha.Owner} is no longer in this region", true, true); return; }
                // (no answer is no proof that it can never work: that game may be behind a loading screen. Tried again later, a few times.)
                if (ha.Pending == 1 && now - ha.PendingSince > OfferWait) { Failed(ha, 1, "no answer", now, true); return; }
                if (ha.Pending == 2 && now - ha.PendingSince > AskBackWait) { ComeHome(ha, null, "its owner did not answer", true, true); return; }
                if (ha.Pending == 0 && now - pup.LastStateAt > SilentWait) { ComeHome(ha, null, "its owner went silent", true, true); return; }
                if (!_on) { AskBack(ha, "animal ownership was switched off", now); return; }
            }
            if (!_on || !ha.Predator || ha.Pinned || ha.Pending != 0 || now < ha.NextRule) return;
            ha.NextRule = now + 0.5f;
            if (!Rested(ha, now)) return;
            Where(ha, out var pos, out var mode);
            if (!CanChangeHands(mode)) return;
            float dOwner = float.MaxValue, dBest = float.MaxValue; byte best = 255;
            foreach (var c in _cands)
            {
                float d = Vector3.Distance(c.Pos, pos);
                if (c.Id == ha.Owner) dOwner = d;
                if (d < dBest) { dBest = d; best = c.Id; }
            }
            bool ownerAround = dOwner < 1e6f;
            if (best != 255 && best != ha.Owner && dBest <= Reach(ha, mode) && dBest <= dOwner * NearerBy)
            {
                string why = $"player {best} is nearest ({dBest:0} m, its owner's player " + (ownerAround ? $"{dOwner:0} m)" : "is not around)");
                if (pup != null) AskBack(ha, why, now); // home first; if a third player is the one, the next round hands it on
                else if (!ha.NoHandover && now >= ha.RetryAt && !Refusing(best, now)) Offer(ha, best, why, false, now);
            }
            else if (pup != null && dOwner > ha.Range * 1.3f + 10f)
                AskBack(ha, ownerAround ? $"its owner's player moved away ({dOwner:0} m)" : "its owner's player is not around", now);
        }

        /// <summary>
        /// Host: hand an animal to a friend's game. This game's copy becomes a puppet at once, so from here on nothing
        /// is decided twice; if the friend's game cannot take it, it comes straight back (Failed).
        /// </summary>
        private bool Offer(HostAnimal ha, byte to, string why, bool pin, float now)
        {
            var ai = ha.Ai;
            if (ai == null || ha.Pup != null || ha.Pending != 0) return false;
            OwnState s;
            try { s = Capture(ai); ha.PreMode = (AiMode)s.Mode; }
            catch (Exception e) { Once("offer" + ha.Id, $"{KindOf(ai)} {ha.Id}: could not be handed over ({e.Message}); it stays with the host"); ha.NoHandover = true; return false; }
            var pup = new Puppet
            {
                Id = ha.Id, Ai = ai, AiPtr = ai.Pointer, HadAi = true, Owner = to, Last = s,
                Target = s.Pos, Reported = s.Pos, LastPos = s.Pos, LastStateTarget = s.Pos, LastStateAt = now, Yaw = s.Yaw, Mode = s.Mode, Hp = s.Hp,
            };
            ha.Pup = pup; ha.Owner = to; ha.Pending = 1; ha.PendingSince = now; ha.Round++; ha.Pinned = pin;
            _byAi[ai.Pointer] = pup; _puppets.Add(ai.Pointer); _away.Add(ha);
            SetModeOn(ai, AiMode.Idle);
            SendOwner(to, ha.Id, ha.Round, Yours, (byte)(pin ? 1 : 0), why, s);
            CoopMod.Log($"{KindOf(ai)} {ha.Id} ({(AiMode)s.Mode}, hp {s.Hp:0.#}): offered to player {to}, {why}");
            return true;
        }

        /// <summary>Host: ask the friend's game that runs an animal to give it back (it may say it is busy fighting).</summary>
        private void AskBack(HostAnimal ha, string why, float now)
        {
            if (ha.Pup == null || ha.Pending != 0 || now < ha.NextAskBack) return;
            ha.Pending = 2; ha.PendingSince = now; ha.NextAskBack = now + 1f;
            SendOwner(ha.Owner, ha.Id, ha.Round, GiveBack, 0, why, default);
            CoopMod.Log($"{KindOf(ha.Ai)} {ha.Id}: asking player {ha.Owner} to give it back, {why}");
        }

        /// <summary>
        /// Host: an animal a friend's game was running is this game's again. "given" is what that game sent with it;
        /// without it (the friend left, went silent, never answered) it carries on from what was last heard of it.
        /// </summary>
        private void ComeHome(HostAnimal ha, OwnState? given, string why, bool tellOwner, bool count)
        {
            float now = Time.realtimeSinceStartup;
            var pup = ha.Pup; byte was = ha.Owner;
            ha.Owner = Protocol.HostId; ha.Pending = 0; ha.Pinned = false; ha.Round++; ha.LastChangeAt = now;
            if (pup == null) return;
            var s = given ?? pup.Last;
            if (given == null) { s.Pos = pup.Reported; s.Yaw = pup.Yaw; s.Mode = pup.Mode; if (pup.Hp > 0f) s.Hp = pup.Hp; }
            Release(pup); ha.Pup = null; _away.Remove(ha);
            if (count) Handovers++;
            if (tellOwner && _net.Remotes.ContainsKey(was)) SendOwner(was, ha.Id, ha.Round, TakenBack, 0, why, default);
            var ai = ha.Ai;
            if (ai == null) return;
            if ((AiMode)s.Mode == AiMode.Dead)
            {
                // it died on its owner's game: it dies here too, where that game saw it fall, and its carcass is the host's to describe
                pup.Target = s.Pos; pup.Yaw = s.Yaw; pup.Mode = s.Mode;
                KillPuppet(pup);
                try { CoopMod.Instance.Items?.ShareCarcassSoon(ai); } catch { }
                CoopMod.Log($"{KindOf(ai)} {ha.Id} died on player {was}'s game; the host keeps its carcass");
                return;
            }
            try
            {
                var entered = MakeLiving(ai, s, ha.PreMode, ha.Wake, was);
                CoopMod.Log($"{KindOf(ai)} {ha.Id} runs on the host's game again ({why}): {entered}, hp {s.Hp:0.#}" + (s.Bleeding ? ", bleeding" : ""));
            }
            catch (Exception e) { Once("home" + ha.Id, $"{KindOf(ai)} {ha.Id} came back to the host but would not start again: {e.Message}"); }
        }

        /// <summary>Host: a hand-over did not work. The animal is the host's again; how we go on depends on why.</summary>
        private void Failed(HostAnimal ha, byte code, string reason, float now, bool tell)
        {
            byte who = ha.Owner; string kind = KindOf(ha.Ai);
            HandoverFails++;
            ComeHome(ha, null, "the hand-over did not work", tell, false);
            if (code == 1)
            {
                // that game has no body for it yet (it is still making its puppets): worth another try, each time a little later
                if (++ha.NotReady >= 6)
                {
                    ha.NoHandover = true;
                    Once("fail" + ha.Id, $"{kind} {ha.Id}: player {who}'s game still has no body for it ({reason}); it stays with the host, as before 0.9.0");
                }
                else
                {
                    ha.RetryAt = now + Mathf.Min(60f, 10f * (1 << (ha.NotReady - 1)));
                    Once("notready" + ha.Id, $"{kind} {ha.Id}: player {who}'s game could not take it yet ({reason}); trying again later");
                }
            }
            else if (code == 2)
            {
                _refusing[who] = now + 120f;
                Once("refuse" + who, $"Player {who}'s game has animal ownership switched off: animals near them stay with the host, as before 0.9.0");
            }
            else
            {
                ha.NoHandover = true;
                Once("fail" + ha.Id, $"{kind} {ha.Id}: the hand-over to player {who} failed ({reason}); it stays with the host, as before 0.9.0");
            }
        }

        /// <summary>Host: a player is gone; whatever their game was running comes back at once, as it was last heard of.</summary>
        private void OwnerLeft(byte id)
        {
            if (_net.Mode != NetMode.Host) return;
            try
            {
                for (int i = _away.Count - 1; i >= 0; i--)
                    if (i < _away.Count && _away[i].Owner == id) ComeHome(_away[i], null, $"player {id} left the game", false, true);
                _refusing.Remove(id);
            }
            catch (Exception e) { CoopMod.Log("Animals of a player who left: " + e.Message); }
        }

        /// <summary>Host: this game's copy of an animal a friend's game runs follows that game, like any puppet.</summary>
        private void FollowAway(HostAnimal ha, float now)
        {
            var pup = ha.Pup;
            if (pup == null || ha.Ai == null) return;
            if (!KeepAwake(pup, now)) return;
            Sense(pup, now, ha);
            if (now < pup.NextSteer) return;
            pup.NextSteer = now + 0.4f;
            Steer(pup);
        }

        private void SendOwner(byte to, ushort id, ushort round, byte what, byte detail, string reason, OwnState s)
        {
            var w = _net.Begin(Msg.AnimalOwner);
            w.Put(_hostScene ?? ""); w.Put(id); w.Put(round); w.Put(what); w.Put(detail); w.Put(reason ?? ""); Put(w, s);
            _net.SendTo(to, w, DeliveryMethod.ReliableOrdered);
        }

        // host: what a friend's game says about an animal's ownership
        private void OnOwnerNote(byte from, NetDataReader r)
        {
            var scene = r.GetString(); var id = r.GetUShort(); var round = r.GetUShort();
            byte what = r.GetByte(), detail = r.GetByte(); var reason = r.GetString(); var s = GetState(r);
            float now = Time.realtimeSinceStartup;
            if (scene != CoopMod.CurrentScene() || scene != _hostScene) return;
            if (!_hostById.TryGetValue(id, out var ha) || ha.Ai == null)
            {
                if (what == Want) SendOwner(from, id, round, No, 0, "the host does not have that animal", default);
                return;
            }
            string kind = KindOf(ha.Ai);
            switch (what)
            {
                case Took:
                    if (ha.Owner != from || ha.Round != round || ha.Pending != 1) return; // an answer to an offer that is no longer open
                    ha.Pending = 0; ha.LastChangeAt = now; ha.NotReady = 0; Handovers++;
                    if (ha.Pup != null) ha.Pup.LastStateAt = now;
                    CoopMod.Log($"{kind} {id} now runs on player {from}'s game (it went into {(AiMode)detail} there)");
                    break;
                case CouldNot:
                    if (ha.Owner != from || ha.Round != round) return;
                    Failed(ha, detail, reason, now, false);
                    break;
                case Back:
                    if (ha.Owner != from) return;
                    // (detail 1: that game says it never ran it, so it has nothing to send along)
                    ComeHome(ha, detail == 1 ? (OwnState?)null : s, string.IsNullOrEmpty(reason) ? "given back" : reason, false, true);
                    break;
                case Died:
                    if (ha.Owner != from) return;
                    s.Mode = (byte)(int)AiMode.Dead;
                    ComeHome(ha, s, "it died there", false, true);
                    break;
                case Busy:
                    if (ha.Owner != from || ha.Pending != 2) return;
                    ha.Pending = 0; ha.NextAskBack = now + 3f;
                    _answerId = id; _answerAt = now; _answer = $"player {from}'s game keeps it for now: {reason}";
                    CoopMod.Log($"{kind} {id}: {_answer}");
                    break;
                case Want:
                {
                    // a test command on that game asks for it, whatever the distances; never in the middle of an attack
                    string no = null;
                    if (!_on) no = "animal ownership is switched off on the host";
                    else if (ha.Owner == from) no = "it already runs on your game";
                    else if (ha.Pup != null) no = $"player {ha.Owner}'s game runs it";
                    else if (ha.Pending != 0) no = "it is changing hands right now";
                    else
                    {
                        AiMode m = AiMode.None; try { m = ha.Ai.GetAiMode(); } catch { }
                        if (!CanBeAsked(m)) no = $"it is busy on the host ({m}): never in the middle of an attack";
                    }
                    if (no != null) { SendOwner(from, id, ha.Round, No, 0, no, default); CoopMod.Log($"{kind} {id}: player {from} asked for it, refused ({no})"); return; }
                    ha.NoHandover = false; ha.NotReady = 0;
                    if (!Offer(ha, from, "a test command asked for it", true, now)) SendOwner(from, id, ha.Round, No, 0, "the host could not hand it over (see the host's log)", default);
                    break;
                }
                case Sensed:
                {
                    // its puppet on that game noticed that game's player: by the game's own senses, that is who it should deal with
                    if (!_on || !ha.Predator || ha.Pinned || ha.Pending != 0 || ha.Owner == from || !Rested(ha, now)) return;
                    Where(ha, out var pos, out var mode);
                    if (!CanChangeHands(mode)) return;
                    bool near = false;
                    foreach (var c in _cands) if (c.Id == from && Vector3.Distance(c.Pos, pos) <= Reach(ha, mode)) near = true;
                    if (!near) return;
                    string why = $"it noticed player {from} ({(AiMode)detail})";
                    if (ha.Pup != null) { ha.HoldUntil = now + SensedHold; AskBack(ha, why, now); }
                    else if (!ha.NoHandover && now >= ha.RetryAt && !Refusing(from, now) && Offer(ha, from, why, false, now)) ha.HoldUntil = now + SensedHold;
                    break;
                }
            }
        }

        // host: where the friend's game that runs them has its animals right now
        private void OnOwnerStates(byte from, NetDataReader r)
        {
            var scene = r.GetString();
            int n = r.GetUShort();
            if (scene != _hostScene) return;
            float tnow = Time.realtimeSinceStartup;
            for (int i = 0; i < n; i++)
            {
                var id = r.GetUShort(); var s = GetState(r);
                if (!_hostById.TryGetValue(id, out var ha) || ha.Owner != from || ha.Pup == null) continue;
                var pup = ha.Pup;
                pup.Last = s; pup.Reported = s.Pos; pup.Hp = s.Hp; pup.TargetId = s.Target;
                Heard(pup, _on ? SeenAt(s.Mode, from, s.Pos) : s.Pos, s.Yaw, s.Mode, tnow);
            }
        }

        // a hit that landed on a puppet in another game: for the game that runs the animal
        private void OnDamage(byte from, NetDataReader r)
        {
            var scene = r.GetString(); var id = r.GetUShort();
            float dmg = r.GetFloat(), bleed = r.GetFloat(); int src = r.GetInt(); var col = r.GetString();
            var weapon = r.AvailableBytes > 0 ? (WeaponSource)r.GetByte() : WeaponSource.Unspecified;
            if (scene != CoopMod.CurrentScene()) return;
            if (_net.Mode == NetMode.Host)
            {
                if (!_hostById.TryGetValue(id, out var ha) || ha.Ai == null) return;
                if (ha.Pup == null) { ApplyDamageHere(ha.Ai, dmg, bleed, src, col, weapon); return; }
                // a friend's game runs it: the hit goes on to that game (never back to the game it just came from)
                if (ha.Owner == from) { Once("lost" + id, $"A hit on {KindOf(ha.Ai)} {id} could not be delivered: player {from}'s game sent it back while it still owns the animal"); return; }
                SendDamage(ha.Owner, scene, id, dmg, bleed, src, col, weapon);
                return;
            }
            if (_byId.TryGetValue(id, out var pup) && pup.Mine && pup.Ai != null) { ApplyDamageHere(pup.Ai, dmg, bleed, src, col, weapon); return; }
            // not running here (any more): back to the host, who knows where it runs now
            SendDamage(Protocol.HostId, scene, id, dmg, bleed, src, col, weapon);
        }

        private void SendDamage(byte to, string scene, ushort id, float dmg, float bleed, int src, string col, WeaponSource weapon)
        {
            var w = _net.Begin(Msg.AnimalDamage);
            w.Put(scene); w.Put(id); w.Put(dmg); w.Put(bleed); w.Put(src); w.Put(col ?? "");
            w.Put((byte)weapon);
            _net.SendTo(to, w, DeliveryMethod.ReliableOrdered);
        }

        private void ApplyDamageHere(BaseAi ai, float dmg, float bleed, int src, string col, WeaponSource weapon)
        {
            CoopMod.Log($"Friend hit {PrefabName(ai.gameObject)} for {dmg:0.#} ({col}, {weapon})");
            // has it got this game's player in its jaws right now? (with ownership off: handled as before 0.9.0)
            bool inJaws = _on && IsStrugglePartner(ai);
            Forwarding = true;
            try
            {
                ai.ApplyDamage(dmg, bleed, (DamageSource)src, col);
                var mode = ai.GetAiMode();
                if (inJaws)
                {
                    // the damage counts; a friend's shot (or its death) also makes it let go, out of the struggle the game's own way.
                    // (a hit without a weapon, as the test command makes, only hurts it: the struggle goes on)
                    if (mode == AiMode.Dead || weapon != WeaponSource.Unspecified)
                    {
                        var ps = GameManager.GetPlayerStruggleComponent();
                        if (mode != AiMode.Dead) { try { ps.MakePartnerFlee(); } catch { } }
                        ps.BreakStruggle();
                        CoopMod.Log($"{KindOf(ai)} let go of this game's player: a friend {(mode == AiMode.Dead ? "killed it" : "shot it")}");
                    }
                }
                // the shot itself happened far away in a friend's game: make it react like to our own shot
                else if (mode != AiMode.Dead)
                {
                    if (weapon != WeaponSource.Unspecified) ai.MaybeFleeOrAttackFromProjectileHit(weapon);
                    else ai.SetAiMode(AiMode.Flee);
                }
            }
            catch (Exception e) { CoopMod.Log("Remote damage: " + e.Message); }
            finally { Forwarding = false; }
        }

        // =============================== client ===============================
        private sealed class Info { public ushort Id; public string Guid, Region, Prefab; public Vector3 RegionPos; public byte Wildlife; }
        // A local animal body that follows another game's animal. A client has one for every host animal; since 0.9.0
        // the host has one for each of its animals that a friend's game runs.
        private sealed class Puppet
        {
            public ushort Id; public BaseAi Ai; public IntPtr AgentPtr;
            public Vector3 Target, LastPos; public float Yaw; public byte Mode; public float Speed; public bool Dead;
            public int Warps; public float SpawnTries, NextMode, NextSteer, HostSpeed, LastStateAt; public Vector3 LastStateTarget, HostVel; public bool HadAi; public float DiedAt;
            public UnityEngine.AI.NavMeshAgent Agent; public IntPtr AiPtr; public float NextWake;
            // ---- 0.9.0: ownership ----
            public byte Owner;                        // the game that runs it, as the host says (on the host: the friend it was handed to)
            public bool Mine;                         // client: its AI runs on this game for now (then it is no puppet: nothing here steers it)
            public IntPtr MinePtr, CorpsePtr;         // remembered addresses, as with AiPtr
            public ushort Round; public float Hp = -1f; public byte TargetId = NoTarget;
            public Vector3 Reported;                  // where its owner has it (Target can differ: during a struggle the puppet stays on the player)
            public OwnState Last;                     // host: the last full report of the friend's game that runs it
            public float OddSince = -1f, MineSince;   // since when the host's word and ours disagree about who runs it
            public float UrgeAt, NextUrge, ClearTargetAt; public AiMode UrgeMode; // it noticed this game's player and was held back
            public bool PredKnown, Pred;
            public readonly Waking Wake = new();
        }

        private readonly Dictionary<ushort, Info> _infos = new();
        private readonly Dictionary<ushort, Puppet> _byId = new();
        private static readonly HashSet<IntPtr> _puppets = new();      // BaseAi and MoveAgent pointers we drive
        private static readonly HashSet<IntPtr> _ownedHere = new();    // client: host animals whose AI runs on this game for now
        private readonly Dictionary<IntPtr, Puppet> _mineByAi = new();
        private readonly Dictionary<IntPtr, Puppet> _corpses = new();  // client: dead host animals lying here (for the state dump)
        private string _statesScene, _infosScene;
        private float _lastStatesAt = -10f, _followingSince = -1f, _nextCleanup, _nextCount, _nextOwnerSend;

        /// <summary>The host is here and decides what the animals do; our own spawning is off.</summary>
        public static bool Following => Instance != null && Instance.IsFollowing;
        private bool IsFollowing => _net.Mode == NetMode.Client && InShared
            && Time.realtimeSinceStartup - _lastStatesAt < 2f && _statesScene == CoopMod.CurrentScene();

        public static bool IsPuppet(IntPtr p) => _puppets.Contains(p);
        /// <summary>A body that stands for one of the host's animals here, as a puppet or run by this game: this game may not take it away on its own.</summary>
        public static bool IsKept(IntPtr p) => _puppets.Contains(p) || _ownedHere.Contains(p);
        internal static bool Forwarding;
        private static bool _loggedKept;
        internal static void NoteKeptAlive(BaseAi ai)
        {
            if (_loggedKept) return;
            _loggedKept = true;
            try { CoopMod.Log($"Kept {Kind(ai.gameObject)}: this game wanted to remove an animal the host still has"); } catch { }
        }

        private void ClientUpdate()
        {
            float now = Time.realtimeSinceStartup;
            if (!IsFollowing)
            {
                if (_followingSince >= 0f) { CoopMod.Log("Animals here run on this game again"); ReleaseAll(); _followingSince = -1f; }
                return;
            }
            if (_followingSince < 0f) { _followingSince = now; CoopMod.Log("Following the host's animals here"); }

            foreach (var pup in _byId.Values)
            {
                if (pup.Mine)
                {
                    // it runs on this game: nothing here steers it, we only watch over it and report it
                    try { MineTick(pup, now); } catch (Exception e) { Once("mine" + pup.Id, $"Animal {pup.Id} that runs on this game: {e.Message}"); }
                    continue;
                }
                if (pup.OddSince >= 0f && now - pup.OddSince > 3f)
                {
                    // the host believes this game runs it; it does not (an offer was lost on the way): say so, the host takes it back
                    pup.OddSince = now;
                    SendNote(pup.Id, pup.Round, Back, 1, "it does not run on this game", default);
                }
                if (pup.Dead)
                {
                    // the corpse comes to rest where the host's does (for a few seconds after death)
                    try
                    {
                        if (pup.Ai != null && now - pup.DiedAt < 6f && now >= pup.NextSteer)
                        {
                            pup.NextSteer = now + 0.5f;
                            var ct = pup.Ai.transform;
                            if (Vector3.Distance(ct.position, pup.Target) > 0.5f) { ct.position = pup.Target; ct.rotation = Quaternion.Euler(0f, pup.Yaw, 0f); }
                        }
                    }
                    catch { }
                    continue; // a carcass that is used up is not made again
                }
                if (pup.Ai == null)
                {
                    // gone here (often because the area is being left): wait a moment before making it again
                    if (pup.HadAi) { pup.HadAi = false; Release(pup); pup.SpawnTries = now + 2f; _vanished++; }
                    if (now >= pup.SpawnTries) Attach(pup, now);
                    continue;
                }
                // our game may switch an animal off on its own (its population rules): the host still has it, so it stays on
                if (!KeepAwake(pup, now)) continue;
                if (pup.Mode == (byte)AiMode.Dead) { KillPuppet(pup); continue; }
                Sense(pup, now, null);
                // walk it with the game's own movement to where the host has it, so legs and ground contact look right
                if (now < pup.NextSteer) continue;
                pup.NextSteer = now + 0.4f;
                Steer(pup);
            }
            SendOwnerStates(now);

            // animals our game spawned on its own that the host doesn't have: remove them
            bool allKnown = true;
            foreach (var id in _byId.Keys) if (!_infos.ContainsKey(id)) { allKnown = false; break; }
            if (now >= _nextCleanup && now - _followingSince > 3f && (allKnown || now - _followingSince > 15f))
            {
                _nextCleanup = now + 2f;
                int removed = 0;
                foreach (var ai in UnityEngine.Object.FindObjectsOfType<BaseAi>())
                {
                    if (ai == null || _puppets.Contains(ai.Pointer) || _ownedHere.Contains(ai.Pointer)) continue;
                    try { if (ai.GetAiMode() == AiMode.Dead) continue; } catch { }
                    // (one that is fighting this game's player right now is not pulled away in the middle of it)
                    if (_on && EngagedHere(ai)) continue;
                    try { ai.Despawn(); removed++; } catch { try { UnityEngine.Object.Destroy(ai.gameObject); removed++; } catch { } }
                }
                if (removed > 0) CoopMod.Log($"Removed {removed} animal(s) only this game had");
                if (now >= _nextCount)
                {
                    _nextCount = now + 10f;
                    int all = 0; foreach (var ai in UnityEngine.Object.FindObjectsOfType<BaseAi>()) if (ai != null) all++;
                    int linked = 0; foreach (var p in _byId.Values) if (p.Ai != null) linked++;
                    int warps = 0; foreach (var p in _byId.Values) { warps += p.Warps; p.Warps = 0; }
                    CoopMod.Log($"Animals here: {all} in this game, {linked} of the host's {_byId.Count} linked, {warps} jump(s) to catch up" + (_vanished > 0 ? $", {_vanished} had to be made again" : "") + (_woken > 0 ? $", {_woken} woken up" : "")
                        + (_mineByAi.Count > 0 ? $", {_mineByAi.Count} run on this game" : ""));
                    _vanished = 0; _woken = 0;
                }
            }
        }

        /// <summary>Our game may switch a puppet's animal off on its own; its owner still has it out, so it stays on. False while it is still off.</summary>
        private bool KeepAwake(Puppet pup, float now)
        {
            try
            {
                if (!pup.Ai.gameObject.activeInHierarchy)
                {
                    if (now >= pup.NextWake)
                    {
                        pup.NextWake = now + 2f;
                        pup.Ai.gameObject.SetActive(true);
                        if (pup.Ai.gameObject.activeInHierarchy)
                        {
                            _woken++;
                            pup.Ai.transform.position = pup.Target;
                            try { pup.Ai.GetMoveAgent()?.Warp(pup.Target, 2f, true, -1); } catch { }
                        }
                    }
                    if (!pup.Ai.gameObject.activeInHierarchy) return false;
                }
            }
            catch { }
            return true;
        }

        /// <summary>Walk a puppet with the game's own movement to where its owner has it.</summary>
        private void Steer(Puppet pup)
        {
            try
            {
                // as hurt as its owner has it (so it limps like the real one, and a hand-over starts from the right health)
                if (_on && pup.Hp > 0f) { try { if (Mathf.Abs(pup.Ai.m_CurrentHP - pup.Hp) > 0.05f) pup.Ai.m_CurrentHP = Mathf.Max(pup.Hp, 0.5f); } catch { } }
                var t = pup.Ai.transform;
                var aim = pup.HostSpeed > 0.5f ? pup.Target + pup.HostVel * 0.4f : pup.Target;
                float dist = Vector3.Distance(t.position, pup.Target);
                if (dist > 25f)
                {
                    pup.Warps++;
                    t.position = pup.Target;
                    t.rotation = Quaternion.Euler(0f, pup.Yaw, 0f);
                    try { pup.Ai.GetMoveAgent()?.Warp(pup.Target, 2f, true, -1); } catch { }
                    SetModeOn(pup.Ai, AiMode.Idle);
                }
                else if (dist > 0.6f)
                {
                    // as fast as the host's animal, plus a bit more the further behind it is
                    float speed = Mathf.Clamp(pup.HostSpeed * 1.1f + dist * 0.6f, 1.2f, 16f);
                    bool going = false;
                    try { going = pup.Ai.GetAiMode() == AiMode.GoToPoint && Vector3.Distance(pup.Ai.m_GoToPoint, aim) < 2.5f; } catch { }
                    if (!going)
                    {
                        SettingMode = true;
                        try { pup.Ai.GoToPoint(aim, AiMode.Idle, speed); } finally { SettingMode = false; }
                    }
                }
            }
            catch (Exception e) { if (!_loggedSteer) { _loggedSteer = true; CoopMod.Log("Animal steer: " + e.Message); } }
        }

        /// <summary>
        /// A puppet that noticed this game's player (and was held back) says so once: by the game's own senses that is
        /// who the animal should be dealing with. A moment later it forgets them, so it can notice again.
        /// </summary>
        private void Sense(Puppet pup, float now, HostAnimal ha)
        {
            if (pup.UrgeAt > 0f)
            {
                pup.UrgeAt = 0f;
                if (_on)
                {
                    if (ha != null)
                    {
                        if (ha.Predator && !ha.Pinned && ha.Pending == 0 && Rested(ha, now) && CanChangeHands((AiMode)pup.Mode))
                        {
                            ha.HoldUntil = now + SensedHold;
                            AskBack(ha, $"it noticed the host's player ({pup.UrgeMode})", now);
                        }
                    }
                    else if (CanChangeHands((AiMode)pup.Mode)) SendNote(pup.Id, pup.Round, Sensed, (byte)(int)pup.UrgeMode, "", default);
                }
            }
            // (while its owner has it busy with somebody the answer would be no anyway: it keeps the target until it is free)
            if (pup.ClearTargetAt > 0f && now >= pup.ClearTargetAt && CanChangeHands((AiMode)pup.Mode))
            {
                pup.ClearTargetAt = 0f;
                if (_on) { try { pup.Ai.ClearTarget(); } catch { } }
            }
        }

        /// <summary>From the mode hook: a puppet wanted to go for this game's player. Only noted here; Update acts on it.</summary>
        internal static void NoteUrge(BaseAi ai, AiMode mode)
        {
            var me = Instance;
            if (me == null || !me._on || !me._byAi.TryGetValue(ai.Pointer, out var pup) || pup.Dead) return;
            switch (mode)
            {
                case AiMode.Stalking: case AiMode.Attack: case AiMode.PassingAttack: case AiMode.HoldGround: case AiMode.HideAndSeek:
                case AiMode.Investigate: case AiMode.InvestigateSmell: case AiMode.InvestigateFood:
                    break;
                default:
                    return;
            }
            float now = Time.realtimeSinceStartup;
            if (now < pup.NextUrge) return;
            pup.NextUrge = now + 4f;
            if (!pup.PredKnown) { pup.PredKnown = true; pup.Pred = PredatorKind(ai); }
            if (!pup.Pred) return; // (prey stays with the host)
            pup.UrgeAt = now; pup.UrgeMode = mode; pup.ClearTargetAt = now + 3f;
        }

        // ---------------- client: animals that run on this game ----------------
        private void SendNote(ushort id, ushort round, byte what, byte detail, string reason, OwnState s)
        {
            var w = _net.Begin(Msg.AnimalOwnerNote);
            w.Put(CoopMod.CurrentScene() ?? ""); w.Put(id); w.Put(round); w.Put(what); w.Put(detail); w.Put(reason ?? ""); Put(w, s);
            _net.SendTo(Protocol.HostId, w, DeliveryMethod.ReliableOrdered);
        }

        // client: the host's word on who runs an animal
        private void OnOwnerWord(NetDataReader r)
        {
            var scene = r.GetString(); var id = r.GetUShort(); var round = r.GetUShort();
            byte what = r.GetByte(), detail = r.GetByte(); var reason = r.GetString(); var s = GetState(r);
            float now = Time.realtimeSinceStartup;
            _byId.TryGetValue(id, out var pup);
            if (scene != CoopMod.CurrentScene()) pup = null; // about a region we are not in (any more)
            switch (what)
            {
                case Yours:
                    TakeOver(pup, id, round, s, reason, now);
                    break;
                case GiveBack:
                    if (pup == null || !pup.Mine) { SendNote(id, round, Back, 1, "it does not run on this game", default); break; }
                    if (EngagedHere(pup.Ai)) { SendNote(id, round, Busy, 0, "it is fighting this game's player", default); break; }
                    ReturnIt(pup, reason, now);
                    break;
                case TakenBack:
                    if (pup != null && pup.Mine)
                    {
                        BecomePuppet(pup, now);
                        Handovers++;
                        CoopMod.Log($"{KindOf(pup.Ai)} {id}: the host took it back ({reason}); it follows the host again");
                    }
                    break;
                case No:
                    _answerId = id; _answerAt = now; _answer = reason;
                    CoopMod.Log($"Animal {id}: the host says no ({reason})");
                    break;
            }
        }

        /// <summary>Client: the host hands us an animal. Our puppet of it becomes a living animal again, right where it stands.</summary>
        private void TakeOver(Puppet pup, ushort id, ushort round, OwnState s, string why, float now)
        {
            string no = null; byte code = 3;
            if (!OwnershipOn) { no = "animal ownership is switched off on this game"; code = 2; }
            else if (pup == null || pup.Dead || pup.Ai == null) { no = "there is no body for it here yet"; code = 1; }
            else
            {
                try { if (!pup.Ai.gameObject.activeInHierarchy) { no = "its body here is switched off"; code = 1; } }
                catch { no = "its body here is gone"; code = 1; }
            }
            if (no != null)
            {
                HandoverFails++;
                _answerId = id; _answerAt = now; _answer = "this game could not take it: " + no;
                SendNote(id, round, CouldNot, code, no, default);
                if (code == 2) Once("off-here", "Animal ownership is switched off on this game (HayvanSahipligi): the host keeps its animals, as before 0.9.0");
                else CoopMod.Log($"Animal {id}: the host offered it, but {no}");
                return;
            }
            var ai = pup.Ai;
            if (pup.Mine) { SendNote(id, round, Took, (byte)(int)SafeMode(ai), why, s); return; } // (asked twice: it already runs here)
            AiMode entered;
            try
            {
                Release(pup); // from here on the game's own AI decides for it again
                pup.Mine = true; pup.MinePtr = ai.Pointer; pup.Owner = _net.LocalId; pup.Round = round; pup.MineSince = now; pup.OddSince = -1f;
                pup.UrgeAt = 0f; pup.ClearTargetAt = 0f;
                _ownedHere.Add(ai.Pointer); _mineByAi[ai.Pointer] = pup;
                entered = MakeLiving(ai, s, AiMode.None, pup.Wake, Protocol.HostId);
            }
            catch (Exception e)
            {
                // it would not start: it goes on following, and the host keeps it
                try { BecomePuppet(pup, now); } catch { }
                HandoverFails++;
                _answerId = id; _answerAt = now; _answer = "this game could not take it: " + e.Message;
                SendNote(id, round, CouldNot, 3, e.Message, default);
                Once("take" + id, $"{KindOf(ai)} {id}: could not be taken over here ({e.Message}); it stays with the host, as before 0.9.0");
                return;
            }
            Handovers++;
            SendNote(id, round, Took, (byte)(int)entered, why, s);
            CoopMod.Log($"{KindOf(ai)} {id} now runs on this game ({why}): {entered}, hp {s.Hp:0.#}" + (s.Bleeding ? ", bleeding" : ""));
        }

        private static AiMode SafeMode(BaseAi ai) { try { return ai.GetAiMode(); } catch { return AiMode.None; } }

        /// <summary>Client: an animal that ran on this game follows its owner again (the reverse of TakeOver).</summary>
        private void BecomePuppet(Puppet pup, float now)
        {
            DropMine(pup);
            var ai = pup.Ai;
            if (ai == null) return;
            // taken back in the middle of a struggle (only when the host had to): out of it the game's own way first
            try
            {
                if (IsStrugglePartner(ai))
                {
                    GameManager.GetPlayerStruggleComponent().BreakStruggle();
                    CoopMod.Log($"{KindOf(ai)} {pup.Id} was taken back while it struggled with this game's player: the struggle was ended");
                }
            }
            catch { }
            pup.AiPtr = ai.Pointer; pup.Agent = null; pup.HadAi = true;
            _byAi[ai.Pointer] = pup; _puppets.Add(ai.Pointer);
            try { var p = ai.transform.position; pup.Target = pup.Reported = pup.LastPos = pup.LastStateTarget = p; } catch { }
            pup.LastStateAt = now; pup.HostVel = Vector3.zero; pup.HostSpeed = 0f; pup.NextSteer = now + 0.4f;
            SetModeOn(ai, AiMode.Idle);
        }

        private void DropMine(Puppet pup)
        {
            if (pup.MinePtr != IntPtr.Zero) { _ownedHere.Remove(pup.MinePtr); _mineByAi.Remove(pup.MinePtr); pup.MinePtr = IntPtr.Zero; }
            pup.Mine = false; pup.Wake.Until = 0f; pup.OddSince = -1f;
        }

        /// <summary>Client: give an animal that runs here back to the host, with everything that happened to it here.</summary>
        private void ReturnIt(Puppet pup, string why, float now)
        {
            var ai = pup.Ai;
            OwnState s; bool known = true;
            try { s = Capture(ai); } catch { s = default; known = false; }
            BecomePuppet(pup, now);
            Handovers++;
            SendNote(pup.Id, pup.Round, Back, (byte)(known ? 0 : 1), why, s);
            CoopMod.Log($"{KindOf(ai)} {pup.Id} was given back to the host ({why}): {(AiMode)s.Mode}, hp {s.Hp:0.#}" + (s.Bleeding ? ", bleeding" : ""));
        }

        /// <summary>Client, every frame, for an animal that runs on this game.</summary>
        private void MineTick(Puppet pup, float now)
        {
            var ai = pup.Ai;
            if (ai == null)
            {
                // its body is gone here: the host's copy carries on from what we last reported, and we make a puppet again
                DropMine(pup);
                SendNote(pup.Id, pup.Round, Back, 1, "its body is gone on this game", default);
                pup.HadAi = false; pup.SpawnTries = now + 2f; _vanished++;
                CoopMod.Log($"Animal {pup.Id} ran on this game but its body is gone here: back to the host");
                return;
            }
            bool active = true;
            try { active = ai.gameObject.activeInHierarchy; } catch { }
            if (!active)
            {
                // our game switched it off on its own (its population rules): the host still has it, so it stays on
                if (now >= pup.NextWake) { pup.NextWake = now + 2f; try { ai.gameObject.SetActive(true); _woken++; } catch { } }
                return;
            }
            AiMode mode = ai.GetAiMode();
            if (mode == AiMode.Dead)
            {
                // the owner decides a death: tell the host, who lets its copy die and describes the carcass for everyone
                OwnState s = Capture(ai);
                var ptr = pup.MinePtr;
                DropMine(pup);
                pup.Dead = true; pup.DiedAt = now; pup.Mode = s.Mode; pup.Target = pup.Reported = s.Pos; pup.Yaw = s.Yaw; pup.Owner = Protocol.HostId;
                if (ptr != IntPtr.Zero) { pup.CorpsePtr = ptr; _corpses[ptr] = pup; }
                Handovers++;
                SendNote(pup.Id, pup.Round, Died, 0, "", s);
                CoopMod.Log($"{KindOf(ai)} {pup.Id} died on this game; the host was told and keeps its carcass");
                return;
            }
            Settle(ai, pup.Wake, now);
            if (!_on && !EngagedHere(ai)) { ReturnIt(pup, "animal ownership was switched off on this game", now); return; }
            if (pup.OddSince >= 0f && now - pup.OddSince > 2.5f && now - pup.MineSince > 3f)
            {
                // the host keeps saying another game runs it: its word counts
                BecomePuppet(pup, now);
                CoopMod.Log($"{KindOf(ai)} {pup.Id}: the host says player {pup.Owner}'s game runs it, so this game's copy follows again");
            }
        }

        /// <summary>Client: tell the host where the animals that run on this game are, ten times a second.</summary>
        private void SendOwnerStates(float now)
        {
            if (_mineByAi.Count == 0 || now < _nextOwnerSend) return;
            _nextOwnerSend = now + 0.1f;
            NetDataWriter w = null; int countPos = 0; ushort n = 0;
            void Flush()
            {
                if (w == null || n == 0) return;
                var data = w.Data; data[countPos] = (byte)(n & 0xFF); data[countPos + 1] = (byte)(n >> 8);
                _net.Send(w, DeliveryMethod.Unreliable);
                w = null; n = 0;
            }
            foreach (var pup in _mineByAi.Values)
            {
                OwnState s;
                try { if (pup.Ai == null) continue; s = Capture(pup.Ai); } catch { continue; }
                pup.Target = pup.Reported = s.Pos; pup.Yaw = s.Yaw; pup.Mode = s.Mode; pup.Hp = s.Hp; pup.TargetId = s.Target; pup.LastStateAt = now;
                if (w == null) { w = _net.Begin(Msg.AnimalOwnerStates); w.Put(CoopMod.CurrentScene() ?? ""); countPos = w.Length; w.Put((ushort)0); }
                w.Put(pup.Id); Put(w, s);
                if (++n >= 20) Flush();
            }
            Flush();
        }

        private readonly Dictionary<IntPtr, Puppet> _byAi = new();
        internal static bool SettingMode;
        private int _vanished, _woken;
        private bool _loggedSteer;

        private static void SetModeOn(BaseAi ai, AiMode mode)
        {
            SettingMode = true;
            try { ai.SetAiMode(mode); } catch { } finally { SettingMode = false; }
        }

        /// <summary>After the game moved a puppet this frame: if it has fallen behind, ease it toward the host's spot.</summary>
        internal static void Nudge(BaseAi ai)
        {
            var me = Instance;
            if (me == null || !me._byAi.TryGetValue(ai.Pointer, out var pup) || pup.Dead) return;
            var t = ai.transform;
            // where the host's animal is right now (its last report plus the time since)
            var d = pup.Target + pup.HostVel * Mathf.Min(Time.realtimeSinceStartup - pup.LastStateAt, 0.3f) - t.position;
            d.y = 0f;
            float dist = d.magnitude;
            if (dist < 0.75f || dist > 25f) return;
            // close most of the gap within a quarter second, never overshooting
            float step = (dist - 0.75f) * (1f - Mathf.Exp(-5f * Time.deltaTime));
            var off = d / dist * step;
            // the game's path-walker owns the position: a plain move of the model is undone next frame, so move the walker
            if (!me._agentMoveBroken)
            {
                try
                {
                    if (pup.Agent == null) { var ma = ai.GetMoveAgent(); if (ma != null) pup.Agent = ma.m_Agent; }
                    var ag = pup.Agent;
                    if (ag != null && ag.enabled && ag.isOnNavMesh) { ag.Move(off); return; }
                }
                catch (Exception e) { me._agentMoveBroken = true; CoopMod.Log("Animal catch-up through the path-walker is not possible here: " + e.Message); }
            }
            t.position += off;
        }
        private bool _agentMoveBroken;

        /// <summary>Right after the game updated a puppet: put it where the host has it and animate it.</summary>
        internal static void Drive(BaseAi ai)
        {
            var me = Instance;
            if (me == null || !me._byAi.TryGetValue(ai.Pointer, out var pup) || pup.Dead) return;
            float dt = Time.deltaTime;
            var t = ai.transform;
            var pos = t.position;
            float dist = Vector3.Distance(pos, pup.Target);
            t.position = dist > 10f ? pup.Target : Vector3.Lerp(pos, pup.Target, 1f - Mathf.Exp(-10f * dt));
            t.rotation = Quaternion.Slerp(t.rotation, Quaternion.Euler(0f, pup.Yaw, 0f), 1f - Mathf.Exp(-10f * dt));
            // walk/run animation from how fast it actually moves
            float v = dt > 0f ? Vector3.Distance(t.position, pup.LastPos) / dt : 0f;
            pup.LastPos = t.position;
            pup.Speed = Mathf.Lerp(pup.Speed, Mathf.Min(v, 15f), 1f - Mathf.Exp(-6f * dt));
            try
            {
                ai.AnimSetFloat(ai.m_AnimParameter_Speed, pup.Speed);
                ai.AnimSetFloat(ai.m_AnimParameter_ActualSpeed, pup.Speed);
            }
            catch { }
        }

        /// <summary>Find (by id from the save) or create the local copy of a host animal.</summary>
        /// <summary>While a region is still being loaded its spawn regions are not there yet: no animal is made before this time.</summary>
        internal static float HoldAttachUntil;

        private void Attach(Puppet pup, float now)
        {
            if (now < HoldAttachUntil) { pup.SpawnTries = now + 0.3f; return; }
            pup.SpawnTries = now + (_failed.Contains(pup.Id) ? 10f : 3f);
            if (!_infos.TryGetValue(pup.Id, out var info)) return;
            if (pup.Mode == (byte)AiMode.Dead && string.IsNullOrEmpty(info.Guid)) return;
            BaseAi ai = null;
            if (!string.IsNullOrEmpty(info.Guid))
            {
                try { var go = PdidTable.GetGameObject(info.Guid); if (go != null) ai = go.GetComponent<BaseAi>(); } catch { }
            }
            // this very animal is fighting this game's player right now (it ran here while the host was elsewhere): not in the middle of that
            if (ai != null && _on && EngagedHere(ai)) { pup.SpawnTries = now + 2f; return; }
            bool woke = false;
            if (ai != null)
            {
                // the game keeps some saved animals switched off until it wants them; the host has this one out, so wake ours
                try
                {
                    var go = ai.gameObject;
                    if (!go.activeInHierarchy)
                    {
                        go.SetActive(true);
                        woke = go.activeInHierarchy;
                        if (!woke) ai = null; // it sits inside something that is switched off: make a fresh one instead
                    }
                }
                catch { ai = null; }
            }
            if (ai == null) ai = Spawn(info, pup.Target, pup.Yaw);
            if (ai == null) return;
            if (woke)
            {
                try
                {
                    ai.transform.position = pup.Target; ai.transform.rotation = Quaternion.Euler(0f, pup.Yaw, 0f);
                    ai.GetMoveAgent()?.Warp(pup.Target, 2f, true, -1);
                }
                catch { }
                _woken++;
            }
            Release(pup);
            pup.Ai = ai; pup.HadAi = true; pup.Agent = null; pup.AiPtr = ai.Pointer; pup.PredKnown = false; pup.UrgeAt = 0f; pup.ClearTargetAt = 0f;
            _byAi[ai.Pointer] = pup;
            pup.LastPos = ai.transform.position;
            _puppets.Add(ai.Pointer);
            SetModeOn(ai, AiMode.Idle);
            _failed.Remove(pup.Id);
            CoopMod.Log($"Animal {info.Prefab} now follows the host");
        }

        private BaseAi Spawn(Info info, Vector3 pos, float yaw)
        {
            SpawnRegion region = null;
            if (!string.IsNullOrEmpty(info.Region))
            {
                try { var go = PdidTable.GetGameObject(info.Region); if (go != null) region = go.GetComponent<SpawnRegion>(); } catch { }
            }
            if (region == null)
            {
                float best = 5f;
                foreach (var sr in UnityEngine.Object.FindObjectsOfType<SpawnRegion>())
                {
                    float d = Vector3.Distance(sr.transform.position, info.RegionPos);
                    if (d < best) { best = d; region = sr; }
                }
            }
            var wm = (WildlifeMode)info.Wildlife;
            if (region == null)
            {
                // the host could not say which region (e.g. the animal was already dead): try the regions nearest to it
                var all = new List<SpawnRegion>(UnityEngine.Object.FindObjectsOfType<SpawnRegion>());
                all.Sort((x, y) => Vector3.Distance(x.transform.position, pos).CompareTo(Vector3.Distance(y.transform.position, pos)));
                for (int k = 0; k < all.Count && k < 8; k++)
                {
                    var made = TrySpawnFrom(all[k], info, pos, yaw, wm, 2, false);
                    if (made != null) return made;
                }
                if (_failed.Add(info.Id)) CoopMod.Log($"No spawn region here can make a {info.Prefab}");
                return null;
            }
            var ai0 = TrySpawnFrom(region, info, pos, yaw, wm, 4, true);
            if (ai0 == null && _failed.Add(info.Id)) CoopMod.Log($"Couldn't make a {info.Prefab} here");
            return ai0;
        }

        // which kind each animal asset of the game turned out to be (learned the first time it is made)
        private readonly Dictionary<string, string> _assetKind = new();

        private BaseAi MakeFrom(SpawnRegion region, AssetReferenceBaseAi asset, Info info, Vector3 pos, float yaw, WildlifeMode wm, out string got)
        {
            got = null;
            if (asset == null) return null;
            string key = null; try { key = asset.AssetGUID; } catch { }
            if (!string.IsNullOrEmpty(key) && _assetKind.TryGetValue(key, out var known) && known != info.Prefab) { got = known; return null; }
            BaseAi ai = null;
            Spawning = true;
            try { ai = region.InstantiateSpawn(asset, pos, Quaternion.Euler(0f, yaw, 0f), AiMode.Wander, wm); }
            catch (Exception e) { CoopMod.Log("Animal spawn: " + e.Message); }
            finally { Spawning = false; }
            if (ai == null) return null;
            got = Kind(ai.gameObject);
            if (!string.IsNullOrEmpty(key)) _assetKind[key] = got;
            if (got == info.Prefab)
            {
                if (!string.IsNullOrEmpty(info.Guid)) { try { ObjectGuid.MaybeAttachObjectGuidAddOrReplace(ai.gameObject, info.Guid); } catch { } }
                return ai;
            }
            try { ai.Despawn(); } catch { UnityEngine.Object.Destroy(ai.gameObject); }
            return null;
        }

        private BaseAi TrySpawnFrom(SpawnRegion region, Info info, Vector3 pos, float yaw, WildlifeMode wm, int tries, bool log)
        {
            // a region can make several kinds (doe or stag): go through its list instead of rolling the dice
            try
            {
                var list = region.m_SpawnList;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var e = list[i]; if (e == null) continue;
                        AssetReferenceBaseAi first = null;
                        try { first = e.GetAnimalForMode(wm); } catch { }
                        try { if (first == null || !first.RuntimeKeyIsValid()) first = e.m_Normal; } catch { first = e.m_Normal; }
                        var ai = MakeFrom(region, first, info, pos, yaw, wm, out _);
                        if (ai != null) return ai;
                    }
                }
            }
            catch (Exception e) { CoopMod.Log("Animal list: " + e.Message); }
            for (int i = 0; i < tries; i++)
            {
                AssetReferenceBaseAi asset = null;
                try { asset = region.PickSpawnAnimal(wm); } catch { }
                var ai = MakeFrom(region, asset, info, pos, yaw, wm, out var got);
                if (ai != null) return ai;
                if (i == 0 && log && got != null) CoopMod.Log($"Region gave a {got} instead of a {info.Prefab}, trying again");
            }
            return null;
        }
        internal static bool Spawning;
        private readonly HashSet<ushort> _failed = new();

        private void KillPuppet(Puppet pup)
        {
            if (pup.Dead) return;
            pup.Dead = true; pup.DiedAt = Time.realtimeSinceStartup;
            try
            {
                var t = pup.Ai.transform;
                if (Vector3.Distance(t.position, pup.Target) > 0.5f)
                {
                    t.position = pup.Target; t.rotation = Quaternion.Euler(0f, pup.Yaw, 0f);
                    try { pup.Ai.GetMoveAgent()?.Warp(pup.Target, 2f, true, -1); } catch { }
                }
            }
            catch { }
            // (client: remembered as one of the host's, so the state dump can still say whose it was)
            if (_net.Mode == NetMode.Client) { try { pup.CorpsePtr = pup.Ai.Pointer; _corpses[pup.CorpsePtr] = pup; } catch { } }
            Release(pup);
            try { pup.Ai.SetAiMode(AiMode.Dead); } catch (Exception e) { CoopMod.Log("Animal death: " + e.Message); }
            // some animals (the moose) ignore that while walking somewhere: kill it the game's own way
            bool dead = false; try { dead = pup.Ai.GetAiMode() == AiMode.Dead; } catch { }
            if (!dead)
            {
                Forwarding = true;
                try { pup.Ai.ApplyDamage(100000f, 0f, DamageSource.Player, "capsule_torso"); }
                catch (Exception e) { CoopMod.Log("Animal death (damage): " + e.Message); }
                finally { Forwarding = false; }
                try { dead = pup.Ai.GetAiMode() == AiMode.Dead; } catch { }
            }
            CoopMod.Log($"{PrefabName(pup.Ai.gameObject)} died" + (dead ? "" : " (but is still standing here)"));
        }

        private void Release(Puppet pup)
        {
            // by the remembered address: a destroyed animal no longer answers as "not null"
            if (pup.AiPtr != IntPtr.Zero) { _puppets.Remove(pup.AiPtr); _byAi.Remove(pup.AiPtr); pup.AiPtr = IntPtr.Zero; }
            if (pup.AgentPtr != IntPtr.Zero) { _puppets.Remove(pup.AgentPtr); pup.AgentPtr = IntPtr.Zero; }
            pup.Agent = null;
        }

        private void ReleaseAll()
        {
            // host: whatever a friend's game was running is this game's again, as it was last heard of
            for (int i = _away.Count - 1; i >= 0; i--)
            {
                if (i >= _away.Count) continue;
                try { ComeHome(_away[i], null, "the shared game ended here", false, false); } catch { }
            }
            _away.Clear();
            // client: what ran on this game simply goes on running here; the puppets are let go as before
            foreach (var p in _byId.Values) { Release(p); p.Mine = false; }
            _byId.Clear(); _puppets.Clear(); _byAi.Clear(); _failed.Clear(); _lastReact.Clear();
            _ownedHere.Clear(); _mineByAi.Clear(); _corpses.Clear();
            _lastStatesAt = -10f; _followingSince = -1f; _agentMoveBroken = false;
        }

        // a hit landed on a puppet → send it to the game that runs the animal (a client's goes through the host)
        internal bool ForwardDamage(BaseAi ai, float dmg, float bleed, DamageSource src, string col)
        {
            if (!_byAi.TryGetValue(ai.Pointer, out var pup) || pup.Ai == null) return false;
            bool host = _net.Mode == NetMode.Host;
            SendDamage(host ? pup.Owner : Protocol.HostId, CoopMod.CurrentScene(), pup.Id, dmg, bleed, (int)src, col, HeldWeapon());
            CoopMod.Log($"Hit {PrefabName(ai.gameObject)} for {dmg:0.#}, sent to " + (host ? $"player {pup.Owner}, whose game runs it" : "the host"));
            return true;
        }

        /// <summary>What the local player is shooting with (for how the animal reacts).</summary>
        private static WeaponSource HeldWeapon()
        {
            try
            {
                var gi = GameManager.GetPlayerManagerComponent()?.m_ItemInHands;
                if (gi == null) return WeaponSource.Unspecified;
                if (gi.m_BowItem != null) return WeaponSource.Arrow;
                var gun = gi.m_GunItem;
                if (gun != null)
                    return gun.m_GunType switch
                    {
                        GunType.Rifle => WeaponSource.Rifle,
                        GunType.Revolver => WeaponSource.Revolver,
                        GunType.Shotgun => WeaponSource.Shotgun,
                        GunType.FlareGun => WeaponSource.FlareGun,
                        _ => WeaponSource.Unspecified,
                    };
            }
            catch { }
            return WeaponSource.Unspecified;
        }

        // our game wanted a puppet to flee or be stunned (it saw us, or a stone hit it): let its owner decide the same
        private readonly Dictionary<IntPtr, float> _lastReact = new();
        internal static void ForwardReaction(BaseAi ai, AiMode mode)
        {
            var me = Instance;
            if (me == null || !me._byAi.TryGetValue(ai.Pointer, out var pup)) return;
            float now = Time.realtimeSinceStartup;
            if (me._lastReact.TryGetValue(ai.Pointer, out var t) && now - t < 1.5f) return;
            me._lastReact[ai.Pointer] = now;
            bool host = me._net.Mode == NetMode.Host;
            var p = GameManager.GetPlayerTransform()?.position ?? ai.transform.position;
            me.SendReaction(host ? pup.Owner : Protocol.HostId, CoopMod.CurrentScene(), pup.Id, (byte)(int)mode, p);
            CoopMod.Log($"{Kind(ai.gameObject)} wants to {mode} here, asking " + (host ? $"player {pup.Owner}, whose game runs it" : "the host"));
        }

        private void SendReaction(byte to, string scene, ushort id, byte mode, Vector3 p)
        {
            var w = _net.Begin(Msg.AnimalReact);
            w.Put(scene); w.Put(id); w.Put(mode);
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
            _net.SendTo(to, w, DeliveryMethod.ReliableOrdered);
        }

        // a puppet in another game wanted to flee or was stunned: for the game that runs the animal
        private void OnReaction(byte from, NetDataReader r)
        {
            var scene = r.GetString(); var id = r.GetUShort(); byte m = r.GetByte();
            var at = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            if (scene != CoopMod.CurrentScene()) return;
            BaseAi ai = null;
            if (_net.Mode == NetMode.Host)
            {
                if (!_hostById.TryGetValue(id, out var ha) || ha.Ai == null) return;
                if (ha.Pup != null) { if (ha.Owner != from) SendReaction(ha.Owner, scene, id, m, at); return; } // on to the friend's game that runs it
                ai = ha.Ai;
            }
            else if (_byId.TryGetValue(id, out var pup) && pup.Mine) ai = pup.Ai;
            if (ai == null) return;
            var mode = (AiMode)m;
            try
            {
                var cur = ai.GetAiMode();
                if (cur == AiMode.Dead) return;
                if (mode == AiMode.Stunned) ai.Stun(ai.transform.position);
                else if (cur != AiMode.Flee && cur != AiMode.Attack && cur != AiMode.Struggle) ai.SetAiMode(AiMode.Flee);
                CoopMod.Log($"{Kind(ai.gameObject)} reacts to a friend: {mode}");
            }
            catch (Exception e) { CoopMod.Log("Remote reaction: " + e.Message); }
        }

        // =============================== messages ===============================
        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (msg != Msg.AnimalInfo && msg != Msg.AnimalStates && msg != Msg.AnimalGone && msg != Msg.AnimalDamage && msg != Msg.AnimalReact
                && msg != Msg.AnimalOwner && msg != Msg.AnimalOwnerNote && msg != Msg.AnimalOwnerStates) return;
            if (!_shared()) return;
            if (msg == Msg.AnimalDamage) { OnDamage(from, r); return; }
            if (msg == Msg.AnimalReact) { OnReaction(from, r); return; }
            if (msg == Msg.AnimalOwnerNote) { if (_net.Mode == NetMode.Host) OnOwnerNote(from, r); return; }
            if (msg == Msg.AnimalOwnerStates) { if (_net.Mode == NetMode.Host) OnOwnerStates(from, r); return; }
            if (_net.Mode == NetMode.Host) return;
            if (msg == Msg.AnimalOwner) { OnOwnerWord(r); return; }
            var scene = r.GetString();
            switch (msg)
            {
                case Msg.AnimalInfo:
                {
                    var info = new Info
                    {
                        Id = r.GetUShort(), Guid = r.GetString(), Region = r.GetString(),
                        RegionPos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
                        Prefab = r.GetString(), Wildlife = r.GetByte(),
                    };
                    if (scene != CoopMod.CurrentScene()) return;
                    if (_infosScene != scene) { _infos.Clear(); _infosScene = scene; }
                    _infos[info.Id] = info;
                    // an animal that was waiting for its description is made right away
                    if (_byId.TryGetValue(info.Id, out var waiting) && waiting.Ai == null && !waiting.Dead && !_failed.Contains(info.Id)) waiting.SpawnTries = 0f;
                    break;
                }
                case Msg.AnimalGone:
                {
                    var id = r.GetUShort();
                    if (_byId.TryGetValue(id, out var pup))
                    {
                        _byId.Remove(id);
                        if (pup.Mine) DropMine(pup); // the host no longer has it: it goes here too, whoever ran it
                        if (pup.CorpsePtr != IntPtr.Zero) { _corpses.Remove(pup.CorpsePtr); pup.CorpsePtr = IntPtr.Zero; }
                        Release(pup);
                        if (pup.Ai != null && !pup.Dead) { try { pup.Ai.Despawn(); } catch { } }
                    }
                    _infos.Remove(id);
                    break;
                }
                case Msg.AnimalStates:
                {
                    if (scene != CoopMod.CurrentScene()) return;
                    if (_statesScene != scene) { ReleaseAll(); _statesScene = scene; }
                    _lastStatesAt = Time.realtimeSinceStartup;
                    int n = r.GetUShort();
                    for (int i = 0; i < n; i++)
                    {
                        var id = r.GetUShort();
                        var p = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                        float yaw = r.GetShort() * 2f / 100f;
                        byte mode = r.GetByte();
                        byte owner = r.GetByte(), target = r.GetByte(); float hp = r.GetFloat();
                        if (!_byId.TryGetValue(id, out var pup)) _byId[id] = pup = new Puppet { Id = id, Target = p, Yaw = yaw, Reported = p };
                        float tnow = Time.realtimeSinceStartup;
                        pup.Owner = owner;
                        bool saysMine = owner == _net.LocalId;
                        if (pup.Mine)
                        {
                            // it runs here: what comes back from the host is our own report; only "who runs it" matters
                            if (saysMine) pup.OddSince = -1f; else if (pup.OddSince < 0f) pup.OddSince = tnow;
                            continue;
                        }
                        if (!saysMine) pup.OddSince = -1f; else if (pup.OddSince < 0f) pup.OddSince = tnow;
                        pup.Reported = p; pup.Hp = hp; pup.TargetId = target;
                        Heard(pup, _on ? SeenAt(mode, owner, p) : p, yaw, mode, tnow);
                    }
                    break;
                }
            }
        }

        /// <summary>A new report of where its owner has it: remember it, and how fast it is going.</summary>
        private static void Heard(Puppet pup, Vector3 p, float yaw, byte mode, float tnow)
        {
            if (pup.LastStateAt > 0f && tnow - pup.LastStateAt > 0.05f)
            {
                var vel = (p - pup.LastStateTarget) / (tnow - pup.LastStateAt);
                if (vel.magnitude > 15f) vel = vel.normalized * 15f;
                pup.HostVel = Vector3.Lerp(pup.HostVel, vel, 0.4f);
                pup.HostSpeed = pup.HostVel.magnitude;
            }
            pup.LastStateAt = tnow; pup.LastStateTarget = p;
            pup.Target = p; pup.Yaw = yaw; pup.Mode = mode;
        }

        // =============================== the local player's struggles (for the harness and the state dump) ===============================
        private bool _inStruggle; private float _struggleAt, _struggleCond; private string _struggleKind = "-", _struggleId = "-"; private BaseAi _strugglePartner;
        internal int Struggles; internal string LastStruggle = "-";

        private void TrackStruggle()
        {
            // only where somebody looks at it: a shared game or the test mode
            if (_net.Mode == NetMode.Off && !CoopMod.AllowVersionMismatch) return;
            bool now = false; PlayerStruggle ps = null;
            if (WorldSync.InGame()) { ps = GameManager.GetPlayerStruggleComponent(); now = ps != null && ps.InStruggle(); }
            if (now == _inStruggle) return;
            _inStruggle = now;
            float t = Time.realtimeSinceStartup;
            if (now)
            {
                Struggles++; _struggleAt = t; _struggleCond = DownedSync.ConditionPercent();
                _strugglePartner = null; _struggleKind = "-"; _struggleId = "-";
                try { _strugglePartner = ps.m_PartnerBaseAi; if (_strugglePartner != null) { _struggleKind = KindOf(_strugglePartner); _struggleId = IdText(_strugglePartner); } } catch { }
                CoopMod.Log($"Struggle with {_struggleKind} (shared id {_struggleId}) began, condition {_struggleCond:0.0} %");
            }
            else
            {
                string after = "gone", hp = "?";
                try { if (_strugglePartner != null) { after = _strugglePartner.GetAiMode().ToString(); hp = _strugglePartner.m_CurrentHP.ToString("0.#", CultureInfo.InvariantCulture); } } catch { }
                LastStruggle = string.Format(CultureInfo.InvariantCulture, "animal={0} id={1} seconds={2:0.0} condition={3:0.0}->{4:0.0} animal_after={5} animal_hp={6}",
                    _struggleKind, _struggleId, t - _struggleAt, _struggleCond, DownedSync.ConditionPercent(), after, hp);
                CoopMod.Log("Struggle over: " + LastStruggle);
                _strugglePartner = null;
            }
        }

        private string IdText(BaseAi ai)
        {
            try
            {
                var ptr = ai.Pointer;
                if (_hostAnimals.TryGetValue(ptr, out var ha)) return ha.Id.ToString();
                if (_mineByAi.TryGetValue(ptr, out var own)) return own.Id.ToString();
                if (_byAi.TryGetValue(ptr, out var pup)) return pup.Id.ToString();
            }
            catch { }
            return "-";
        }

        /// <summary>Test helper: the struggle the local player is in right now, if any.</summary>
        internal string StruggleLine()
        {
            var ci = CultureInfo.InvariantCulture;
            bool active = false; string kind = "-", id = "-"; float secs = 0f, gameSecs = 0f, tap = 0f;
            try
            {
                var ps = WorldSync.InGame() ? GameManager.GetPlayerStruggleComponent() : null;
                if (ps != null && ps.InStruggle())
                {
                    active = true;
                    var p = ps.m_PartnerBaseAi;
                    if (p != null) { kind = KindOf(p); id = IdText(p); }
                    secs = _inStruggle ? Time.realtimeSinceStartup - _struggleAt : 0f;
                    try { gameSecs = ps.m_StruggleTimeSeconds; tap = ps.m_TapBarNormalized; } catch { }
                }
            }
            catch { }
            return string.Format(ci, "active={0} animal={1} id={2} seconds={3:0.0} game_seconds={4:0.0} tap_bar={5:0.00} condition={6:0.0} struggles_here={7}",
                active, kind, id, secs, gameSecs, tap, DownedSync.ConditionPercent(), Struggles);
        }

        // =============================== test helpers ===============================
        /// <summary>Test helper: host animals known here, how many have a body in this game, how many of those are switched off.</summary>
        internal static (int Known, int Linked, int Asleep) TestCounts()
        {
            var me = Instance; int linked = 0, asleep = 0;
            if (me == null) return (0, 0, 0);
            foreach (var p in me._byId.Values)
            {
                try { if (p.Ai == null) continue; linked++; if (!p.Ai.gameObject.activeInHierarchy) asleep++; } catch { }
            }
            return (me._byId.Count, linked, asleep);
        }

        /// <summary>Test helper: living shared animals whose AI runs on this game, and (host) how many a friend's game runs.</summary>
        internal (int Here, int Away) TestOwned()
        {
            if (!InShared) return (0, 0);
            if (_net.Mode != NetMode.Host) return (_mineByAi.Count, 0);
            int here = 0;
            foreach (var ha in _hostAnimals.Values)
            {
                try { if (ha.Ai != null && ha.Pup == null && ha.Ai.GetAiMode() != AiMode.Dead) here++; } catch { }
            }
            return (here, _away.Count);
        }

        /// <summary>
        /// Test helper for the "animal:" lines: the shared id, which player's game runs it (owner), whether that is
        /// this game (mine), what it is doing there (ai) and whom it has in mind (target); for a puppet also how far
        /// it is from where its owner has it.
        /// </summary>
        internal static string TestInfo(BaseAi ai)
        {
            var me = Instance;
            if (me == null || ai == null) return "";
            try
            {
                var ci = CultureInfo.InvariantCulture;
                byte owner = me._net.LocalId, target = NoTarget; bool mine = true, told = false;
                AiMode mode = AiMode.None; string id = "", tail = "";
                var ptr = ai.Pointer;
                Puppet pup = null;
                if (me._hostAnimals.TryGetValue(ptr, out var ha))
                {
                    id = $" id={ha.Id}"; owner = ha.Owner; pup = ha.Pup; mine = pup == null;
                }
                else if (me._byAi.TryGetValue(ptr, out pup)) { id = $" id={pup.Id}"; owner = pup.Owner; mine = false; }
                else if (me._mineByAi.TryGetValue(ptr, out var own)) id = $" id={own.Id}";
                else if (me._corpses.TryGetValue(ptr, out var dead)) { id = $" id={dead.Id}"; owner = dead.Owner; mine = false; mode = AiMode.Dead; told = true; }
                if (pup != null && !mine)
                {
                    mode = (AiMode)pup.Mode; target = pup.TargetId; told = true;
                    float age = Time.realtimeSinceStartup - pup.LastStateAt;
                    var pos = ai.transform.position;
                    float err = Vector3.Distance(pos, pup.Target);
                    float errNow = Vector3.Distance(pos, pup.Target + pup.HostVel * Mathf.Min(age, 0.3f));
                    tail = $" err={err.ToString("0.00", ci)} errnow={errNow.ToString("0.00", ci)} age={age.ToString("0.00", ci)} hostspeed={pup.HostSpeed.ToString("0.0", ci)} hostmode={(AiMode)pup.Mode}";
                }
                if (!told) { mode = ai.GetAiMode(); target = me.TargetOf(ai); }
                string t = target == NoTarget ? "none" : target == OtherTarget ? "other" : "player";
                return $"{id} owner={owner} mine={mine} ai={mode} target={t}" + (target < OtherTarget ? $" target_id={target}" : "") + tail;
            }
            catch { }
            return "";
        }

        // the last "no" (or "busy") about a test command's request, for the harness to report
        private ushort _answerId; private float _answerAt = -100f; private string _answer = "";

        /// <summary>Test helper: which player's game runs this animal as far as this game knows (255 = it is not a shared animal).</summary>
        internal byte TestOwnerOf(BaseAi ai)
        {
            try
            {
                var ptr = ai.Pointer;
                if (_hostAnimals.TryGetValue(ptr, out var ha)) return ha.Owner;
                if (_mineByAi.ContainsKey(ptr)) return _net.LocalId;
                if (_byAi.TryGetValue(ptr, out var pup)) return pup.Owner;
            }
            catch { }
            return 255;
        }

        /// <summary>Test helper: is this one of the host's animals that runs on this (client) game for now.</summary>
        internal bool TestMine(BaseAi ai) { try { return _mineByAi.ContainsKey(ai.Pointer); } catch { return false; } }

        /// <summary>Test helper: does this animal's AI run on this game (also true for an animal that is not shared at all).</summary>
        internal bool TestRunsHere(BaseAi ai) { try { return !_puppets.Contains(ai.Pointer); } catch { return false; } }

        /// <summary>
        /// Test command "own" / "disown": ask for this animal to run on this game, or on the host's, whatever the
        /// distances. On the host both mean "bring it home". Null = asked (see TestOwnResult); "already" = nothing to do.
        /// </summary>
        internal string TestOwn(BaseAi ai, bool own, out string already)
        {
            already = null;
            float now = Time.realtimeSinceStartup;
            if (!InShared) return "not in a shared world";
            var ptr = ai.Pointer;
            if (_net.Mode == NetMode.Host)
            {
                if (!_hostAnimals.TryGetValue(ptr, out var ha)) return "the host does not share this animal (yet)";
                if (ha.Pup == null) { already = "it already runs on the host's game"; return null; }
                if (ha.Pending != 0) return "it is changing hands right now";
                ha.Pinned = false; ha.NextAskBack = 0f;
                AskBack(ha, "a test command asked for it", now);
                return null;
            }
            if (!IsFollowing) return "the host is not in this region: its animals run on this game anyway";
            if (own)
            {
                if (_mineByAi.ContainsKey(ptr)) { already = "it already runs on this game"; return null; }
                if (!_byAi.TryGetValue(ptr, out var pup)) return "this animal is not one of the host's (no shared id here)";
                if (!OwnershipOn) return "animal ownership is switched off on this game";
                SendNote(pup.Id, pup.Round, Want, 0, "", default);
                return null;
            }
            if (!_mineByAi.TryGetValue(ptr, out var mine)) { already = "it does not run on this game"; return null; }
            if (EngagedHere(ai)) return "it is fighting this game's player: never in the middle of an attack";
            ReturnIt(mine, "a test command gave it back", now);
            return null;
        }

        /// <summary>Test helper: 0 = not settled yet, 1 = it runs where it was asked to, 2 = refused (text says why).</summary>
        internal int TestOwnResult(BaseAi ai, bool own, float askedAt, out string text)
        {
            text = "";
            try
            {
                var ptr = ai.Pointer; ushort id = 0; bool done = false;
                if (_net.Mode == NetMode.Host)
                {
                    if (!_hostAnimals.TryGetValue(ptr, out var ha)) { text = "the animal is gone"; return 2; }
                    id = ha.Id; done = ha.Pup == null;
                    if (done) text = $"id={id} owner=0 mine=True ai={SafeMode(ai)}";
                }
                else if (own)
                {
                    done = _mineByAi.TryGetValue(ptr, out var mine);
                    if (done) { id = mine.Id; text = $"id={id} owner={_net.LocalId} mine=True ai={SafeMode(ai)} target={(TargetOf(ai) == NoTarget ? "none" : TargetOf(ai) == OtherTarget ? "other" : "player")}"; }
                    else if (_byAi.TryGetValue(ptr, out var pup)) id = pup.Id;
                }
                else
                {
                    // given back: settled once the host's own word says the host runs it
                    if (_byAi.TryGetValue(ptr, out var pup)) { id = pup.Id; done = pup.Owner == Protocol.HostId && Time.realtimeSinceStartup - pup.LastStateAt < 1f && pup.LastStateAt > askedAt; }
                    if (done) text = $"id={id} owner=0 mine=False ai={(AiMode)pup.Mode}";
                }
                if (done) return 1;
                if (_answerAt >= askedAt && _answerId == id) { text = _answer; return 2; }
            }
            catch (Exception e) { text = e.Message; return 2; }
            return 0;
        }

        /// <summary>
        /// Test command "aggro": make an animal that runs on this game notice the local player and go for them, through
        /// the calls the game itself makes when it has detected a target. Null = done ("did" says which calls); else why not.
        /// </summary>
        internal string TestAggro(BaseAi ai, string how, out string did)
        {
            did = "";
            if (_puppets.Contains(ai.Pointer))
            {
                byte o = TestOwnerOf(ai);
                return $"it runs on {(o == Protocol.HostId ? "the host's" : $"player {o}'s")} game, not here: send aggro there, or 'own' it first";
            }
            AiTarget me = null;
            try { var t = ai.m_PlayerTarget; if (t != null && t.IsPlayer()) me = t; } catch { }
            if (me == null)
            {
                try
                {
                    var go = GameManager.GetPlayerObject();
                    var t = go != null ? (go.GetComponent<AiTarget>() ?? go.GetComponentInChildren<AiTarget>()) : null;
                    if (t != null && t.IsPlayer()) me = t;
                }
                catch { }
            }
            if (me == null) return "this game's player has no target marker an animal could notice";
            var pos = GameManager.GetPlayerTransform().position;
            bool GoingFor(AiMode m) => m == AiMode.Attack || m == AiMode.Stalking || m == AiMode.Struggle || m == AiMode.PassingAttack || m == AiMode.HoldGround || m == AiMode.HideAndSeek;
            ai.m_CurrentTarget = me;
            if (how != "attack" && how != "stalk")
            {
                // what the game does at the moment an animal has detected somebody: its own roll between stalking, attacking and fleeing
                ai.ChangeModeWhenTargetDetected();
                did = $"ChangeModeWhenTargetDetected={ai.GetAiMode()}";
                if (GoingFor(ai.GetAiMode())) return null;
                ai.m_CurrentTarget = me; // (the roll may have dropped the target)
            }
            if (how != "stalk")
            {
                bool ok = ai.EnterAttackModeIfPossible(pos, false);
                did += (did.Length > 0 ? " " : "") + $"EnterAttackModeIfPossible={ok}";
                if (ok || GoingFor(ai.GetAiMode())) return null;
            }
            bool st = ai.EnterStalkingModeIfPossible(pos);
            did += (did.Length > 0 ? " " : "") + $"EnterStalkingModeIfPossible={st}";
            return null;
        }
    }

    // ---------------- patches ----------------
    // Applied by hand (AnimalSync.ApplyPatches). The host's save is everybody's world: an animal that a friend's game
    // runs at the moment of saving is written as wandering, not as the idle puppet it is on the host. Only the saved
    // mode differs; the animal itself is put back exactly as it was before the game's own save code returns.
    internal static class AiSavePatch
    {
        internal static void Prefix(BaseAi __instance, out int __state)
        {
            __state = -1;
            try
            {
                if (__instance == null || !AnimalSync.SavesAsLiving(__instance.Pointer)) return;
                var m = __instance.m_CurrentMode;
                if (m != AiMode.Idle && m != AiMode.GoToPoint) return;
                __instance.m_CurrentMode = AiMode.Wander;
                __state = (int)m;
            }
            catch { }
        }

        internal static void Postfix(BaseAi __instance, int __state)
        {
            if (__state < 0) return;
            try { __instance.m_CurrentMode = (AiMode)__state; } catch { }
        }
    }

    [HarmonyPatch(typeof(BaseAi), nameof(BaseAi.Update))]
    internal static class AiNudgePatch
    {
        private static void Postfix(BaseAi __instance)
        {
            if (!AnimalSync.IsPuppet(__instance.Pointer)) return;
            try { AnimalSync.Nudge(__instance); } catch { }
        }
    }

    // a puppet doesn't make its own decisions
    [HarmonyPatch(typeof(BaseAi), nameof(BaseAi.SetAiMode))]
    internal static class AiModePatch
    {
        private static bool Prefix(BaseAi __instance, AiMode mode)
        {
            if (AnimalSync.SettingMode || !AnimalSync.IsPuppet(__instance.Pointer)) return true;
            switch (mode)
            {
                case AiMode.Dead: case AiMode.Idle: case AiMode.Wander: case AiMode.WanderPaused:
                case AiMode.GoToPoint: case AiMode.Sleep: case AiMode.Feeding:
                    return true; // calm everyday behaviour, including arriving where its owner sent it
                case AiMode.Flee: case AiMode.Stunned:
                    try { AnimalSync.ForwardReaction(__instance, mode); } catch { } // its owner's animal reacts, ours follows it
                    return false;
                default:
                    // attacking, stalking... is decided on the game that runs it; that it wanted to is worth knowing there
                    try { AnimalSync.NoteUrge(__instance, mode); } catch { }
                    return false;
            }
        }
    }

    [HarmonyPatch(typeof(BaseAi), nameof(BaseAi.Stun))]
    internal static class AiStunPatch
    {
        private static bool Prefix(BaseAi __instance)
        {
            if (!AnimalSync.IsPuppet(__instance.Pointer)) return true;
            try { AnimalSync.ForwardReaction(__instance, AiMode.Stunned); } catch { }
            return false;
        }
    }



    // our game must not take a host animal away on its own (population limits, distance culling),
    // whether it follows here as a puppet or runs on this game for now
    [HarmonyPatch(typeof(BaseAi), nameof(BaseAi.Despawn))]
    internal static class AiDespawnPatch
    {
        private static bool Prefix(BaseAi __instance)
        {
            if (!AnimalSync.IsKept(__instance.Pointer)) return true;
            AnimalSync.NoteKeptAlive(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(BaseAiManager), nameof(BaseAiManager.Destroy))]
    internal static class AiDestroyPatch
    {
        private static bool Prefix(BaseAi bai)
        {
            if (bai == null || !AnimalSync.IsKept(bai.Pointer)) return true;
            AnimalSync.NoteKeptAlive(bai);
            return false;
        }
    }

    [HarmonyPatch(typeof(SpawnRegion), nameof(SpawnRegion.RemoveActiveSpawns))]
    internal static class RemoveSpawnsPatch { private static bool Prefix() => !AnimalSync.Following; }

    // while following the host, our game makes no animals of its own
    [HarmonyPatch(typeof(SpawnRegion), nameof(SpawnRegion.Spawn))]
    internal static class SpawnPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (AnimalSync.Following && !AnimalSync.Spawning) { __result = false; return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(BaseAi), nameof(BaseAi.ApplyDamage), new[] { typeof(float), typeof(float), typeof(DamageSource), typeof(string) })]
    internal static class AiDamagePatch
    {
        private static bool Prefix(BaseAi __instance, float damage, float bleedOutMintues, DamageSource damageSource, string collider)
        {
            if (AnimalSync.Forwarding || !AnimalSync.IsPuppet(__instance.Pointer)) return true;
            try { if (AnimalSync.Instance.ForwardDamage(__instance, damage, bleedOutMintues, damageSource, collider)) return false; }
            catch (Exception e) { CoopMod.Log("Damage hook: " + e.Message); }
            return true;
        }
    }

    [HarmonyPatch(typeof(BaseAi), nameof(BaseAi.ApplyDamage), new[] { typeof(float), typeof(DamageSource), typeof(string) })]
    internal static class AiDamagePatch3
    {
        private static bool Prefix(BaseAi __instance, float damage, DamageSource damageSource, string collider)
        {
            if (AnimalSync.Forwarding || !AnimalSync.IsPuppet(__instance.Pointer)) return true;
            try { if (AnimalSync.Instance.ForwardDamage(__instance, damage, 0f, damageSource, collider)) return false; }
            catch (Exception e) { CoopMod.Log("Damage hook: " + e.Message); }
            return true;
        }
    }
}
