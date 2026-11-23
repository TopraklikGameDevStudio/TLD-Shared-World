using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.Gameplay;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Downed and revive: in a shared world nobody dies for good.
    ///
    /// How the game does it: when condition reaches zero the game calls Condition.PlayerDeath, which hands over to
    /// GameManager.HandlePlayerDeath. From there come the "cheat death" screen (Panel_LifeAfterDeath, when the save has
    /// that option) or the death screen (Panel_Log.EnableDeathView), and in the end the save is deleted.
    /// In a shared world that would take the world away from everybody, so there:
    ///   - Condition.PlayerDeath never runs. The hook only notes "the game wanted this player dead"; everything else
    ///     happens in Update, outside the game's own call.
    ///   - the player is "downed" instead: held at a sliver of condition, unable to move or act, for YerdeKalmaDakika
    ///     minutes. A friend standing next to them lifts them by holding F3; holding F3 while downed gives up.
    ///   - time up, given up, or nobody in the world who could come: everything carried except worn clothing is left on
    ///     the ground where they fell, and the player comes to at a safe place in the same region.
    ///   - behind that, the later steps of the game's death (its two screens, its own respawn, every way it deletes a
    ///     save) are hooked too, so a death that got past the first hook still cannot take the save with it.
    /// "Shared world" here: a host that is hosting, a client in the co-op world, and the co-op copy opened without a
    /// connection (it is a copy of somebody else's world and holds the co-op survivor: it is never deleted by a death).
    /// A plain single-player game, story mode, or the setting switched off: nothing here touches the game.
    /// </summary>
    internal sealed class DownedSync
    {
        internal static DownedSync Instance;
        internal const byte Nobody = 255;
        internal const string KeyName = "F3";
        private const int VK_KEY = 0x72;                // F3: hold next to a downed friend to lift them; hold while downed to give up
        internal const float ReviveRange = 2.5f, NoticeRange = 40f, ReviveHoldSeconds = 5f, GiveUpHoldSeconds = 3f;
        private const float FloorPercent = 2f, RevivePercent = 15f, RespawnPercent = 35f;
        private const long DeathContextMs = 60000;      // how long after the game's own death was seen under way the save cannot be deleted
        private const long StickyGuardMs = 15000;       // a guarded world stays guarded this long through a loading screen
        private const float FadeTarget = 0.5f;          // the HUD is drawn under the fade: any darker and the line on screen cannot be read (0.75 was too dim in the 0.8.0 test)

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

        private readonly Network _net;
        private readonly RemotePlayers _players;
        private readonly Func<bool> _shared, _pref;
        private readonly Func<int> _minutes;
        private readonly Action<string> _rememberSleepPlace;

        // ---- hooks ----
        private readonly List<(string Name, bool Ok)> _hooks = new();
        private bool _hookDeath, _hookCondSave, _hookPlayerSave;
        private readonly HashSet<string> _said = new();
        /// <summary>Read by the hooks if anything goes wrong inside them: what the last frame decided.</summary>
        internal static volatile bool GuardCached;
        /// <summary>Read by the damage hook on every hit: is the local player down right now.</summary>
        internal static volatile bool DownFast;

        // ---- is this a world where nobody dies for good ----
        private bool _guarded, _guardIsCoopSlot; private string _guardWhy = "not looked yet";
        // (these three run on the plain system clock: the hooks that read them must work even where the game's own clock does not)
        private long _lastGuardedMs = -1000000, _deathSeenMs = -1000000, _nextCoopLookMs;
        private float _nextSlotLook; private bool _coopLoaded;
        private string _guardSlot; private uint _guardGameId; private bool _guardGameIdKnown;
        private bool _saidKept;

        // ---- the local player on the ground ----
        private bool _down;
        private string _pendingCause;                   // a hook saw the game's death: go down at the next Update
        private float _downAt, _deadline; private string _downScene = "", _downCause = ""; private Vector3 _downPos;
        private bool _settle; private float _settleUntil;       // still falling when it happened: wait for the ground
        private float _giveUpSince = -1f; private bool _giveUpNow; private byte _reviveBy = Nobody;
        private float _calories, _thirst, _fatigue, _freezing; private bool _needsKnown;
        private bool _lockedByUs, _heldMove, _prevFreeze, _fadeHeld, _fadeAsked; private float _fadeAskedAt, _unfadeCheckAt = -1f;
        // the game's own switches we flip (never die, invulnerable, unseen by animals): "own" = flipped by us, "orig" = what to put back
        private bool _ownNeverDie, _origNeverDie, _ownInvulnerable, _origInvulnerable, _ownGhost, _origGhost, _origCheats;
        private bool _swappedCond, _swappedPlayer;
        private float _unsharedSince = -1f;
        // nobody left on their feet: the game ends for real after a short countdown (see TickDowned / RealDeath)
        private float _nobodySince = -1f;
        private bool _realDeath; private float _realDeathAt; private bool _realDeathPushed;
        internal bool RealDeathUnderWay => _realDeath;
        internal int RealDeaths;
        private float _nextNotice, _nextPin, _zeroSince = -1f;
        private string _notice;
        private int _pinBacks, _panelsClosed;
        private float _checkRespawnAt = -1f;

        // ---- places to come back to ----
        private string _trackScene; private bool _entryPending, _entryValid; private Vector3 _entryPos; private Quaternion _entryRot; private string _entryScene;
        private string _sleepSave, _sleepScene; private Vector3 _sleepPos; private bool _sleepValid, _wasSleeping;
        private Vector3 _lastGround; private string _lastGroundScene; private float _nextGround;

        // ---- helping a friend up ----
        private byte _holdTarget = Nobody; private float _holdSince = -1f, _helperNextLine, _helperCooldown;
        private float _testHoldFor;                     // harness: the key counts as held for exactly this long (0 = no test hold)
        private byte _nearId = Nobody; private float _nearDist;

        // ---- for the state dump and the harness ----
        internal int Downs, Deaths, Revived, RevivesSent, DeathAttempts, DeletesBlocked, LastDropped, LastKept, LastCured;
        internal string LastDeathScene = "-", RespawnReason = "-", RespawnPoint = "-", LastTrigger = "-", LastHelperResult = "-", LastNote = "-", LastKeptNames = "";
        internal Vector3 LastDeathPos, RespawnPos; internal bool HasDeath;

        public DownedSync(Network net, RemotePlayers players, Func<bool> worldShared, Func<bool> pref, Func<int> minutes, string sleepPlace, Action<string> rememberSleepPlace)
        {
            Instance = this;
            _net = net; _players = players; _shared = worldShared; _pref = pref; _minutes = minutes; _rememberSleepPlace = rememberSleepPlace;
            ReadSleepPlace(sleepPlace);
            _net.OnMessage += OnMessage;
            _net.OnStopped += () => { _holdSince = -1f; _holdTarget = Nobody; _nearId = Nobody; _testHoldFor = 0f; };
        }

        // =====================================================================================
        // hooks
        // =====================================================================================
        /// <summary>Each hook on its own: one that cannot be placed is named in the log and in the state dump, the others still stand.</summary>
        internal void ApplyPatches(HarmonyLib.Harmony h)
        {
            var t = typeof(DownedPatches);
            bool Hook(string what, Func<System.Reflection.MethodBase> find, string prefix, string postfix = null)
            {
                bool ok = false;
                try
                {
                    var m = find();
                    if (m == null) throw new MissingMethodException("this game version has no such method");
                    h.Patch(m, prefix: prefix != null ? new HarmonyMethod(t, prefix) : null, postfix: postfix != null ? new HarmonyMethod(t, postfix) : null);
                    ok = true;
                }
                catch (Exception e) { CoopMod.Log($"Downed and revive: could not hook {what} ({e.Message})"); }
                _hooks.Add((what, ok));
                return ok;
            }

            // the game's death, from its first step to its last screen
            _hookDeath = Hook("PlayerDeath", () => AccessTools.Method(typeof(Condition), nameof(Condition.PlayerDeath), new[] { typeof(string) }), nameof(DownedPatches.PlayerDeathPrefix));
            Hook("HandlePlayerDeath", () => AccessTools.Method(typeof(GameManager), nameof(GameManager.HandlePlayerDeath), new[] { typeof(string) }), nameof(DownedPatches.HandlePlayerDeathPrefix));
            Hook("DeathView", () => AccessTools.Method(typeof(Panel_Log), nameof(Panel_Log.EnableDeathView), Type.EmptyTypes), nameof(DownedPatches.EnableDeathViewPrefix));
            Hook("LifeAfterDeathPanel", () => AccessTools.Method(typeof(Panel_LifeAfterDeath), nameof(Panel_LifeAfterDeath.ShowLifeAfterDeath), Type.EmptyTypes), nameof(DownedPatches.ShowLifeAfterDeathPrefix));
            Hook("LifeAfterDeathRespawn", () => AccessTools.Method(typeof(LifeAfterDeathManager), nameof(LifeAfterDeathManager.PerformRespawn), new[] { typeof(LifeAfterDeathSpawnPointType), typeof(bool) }), nameof(DownedPatches.PerformRespawnPrefix));
            // every way the game deletes a save
            Hook("DeleteSaveFiles", () => AccessTools.Method(typeof(SaveGameSystem), nameof(SaveGameSystem.DeleteSaveFiles), new[] { typeof(string) }), nameof(DownedPatches.DeleteByNamePrefix));
            Hook("DeleteSaveFilesForGameId", () => AccessTools.Method(typeof(SaveGameSystem), nameof(SaveGameSystem.DeleteSaveFilesForGameId), new[] { typeof(uint) }), nameof(DownedPatches.DeleteByIdPrefix));
            Hook("DeleteSlotByName", () => AccessTools.Method(typeof(SaveGameSlots), nameof(SaveGameSlots.DeleteSlot), new[] { typeof(string) }), nameof(DownedPatches.DeleteSlotByNamePrefix));
            Hook("DeleteSlotByData", () => AccessTools.Method(typeof(SaveGameSlots), nameof(SaveGameSlots.DeleteSlot), new[] { typeof(SlotData) }), nameof(DownedPatches.DeleteSlotByDataPrefix));
            Hook("DeleteAllSlotsForGameId", () => AccessTools.Method(typeof(SaveGameSlots), nameof(SaveGameSlots.DeleteAllSlotsForGameId), new[] { typeof(uint) }), nameof(DownedPatches.DeleteByIdPrefix));
            Hook("DeleteSaveSlotInfo", () => AccessTools.Method(typeof(SaveGameSlotHelper), nameof(SaveGameSlotHelper.DeleteSaveSlotInfo), new[] { typeof(SaveSlotInfo) }), nameof(DownedPatches.DeleteSlotInfoPrefix));
            // no damage while on the ground
            Hook("NoDamageWhileDowned", () => AccessTools.Method(typeof(Condition), nameof(Condition.AddHealth), new[] { typeof(float), typeof(DamageSource), typeof(bool) }), nameof(DownedPatches.AddHealthPrefix));
            Hook("NoDamageWhileDowned2", () => AccessTools.Method(typeof(Condition), nameof(Condition.AddHealth), new[] { typeof(float), typeof(DamageSource) }), nameof(DownedPatches.AddHealthPrefix));
            Hook("NoDamageWhileDowned3", () => AccessTools.Method(typeof(Condition), nameof(Condition.AddHealthWithNoHudNotification), new[] { typeof(float), typeof(DamageSource) }), nameof(DownedPatches.AddHealthPrefix));
            // the switches we flip while somebody is down (never die, invulnerable, unseen by animals) are written into
            // the save by the game; these two keep them out of it. Without them those switches are simply not used.
            _hookCondSave = Hook("ConditionSave", () => AccessTools.Method(typeof(Condition), nameof(Condition.Serialize), Type.EmptyTypes), nameof(DownedPatches.ConditionSavePrefix), nameof(DownedPatches.ConditionSavePostfix));
            _hookPlayerSave = Hook("PlayerSave", () => AccessTools.Method(typeof(PlayerManager), nameof(PlayerManager.Serialize), Type.EmptyTypes), nameof(DownedPatches.PlayerSavePrefix), nameof(DownedPatches.PlayerSavePostfix));

            int ok = 0; foreach (var x in _hooks) if (x.Ok) ok++;
            CoopMod.Log($"Downed and revive: {ok} of {_hooks.Count} hooks in place, setting {(SafePref() ? "on" : "off")}, {SafeMinutes()} min on the ground"
                + (_hookDeath ? "" : " - the game's death call could not be hooked: the game's own never-die switch is used instead"));
        }

        internal string HookSummary()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var x in _hooks) sb.Append(sb.Length > 0 ? " " : "").Append(x.Name).Append('=').Append(x.Ok);
            return sb.Length > 0 ? sb.ToString() : "no hooks placed";
        }

        // =====================================================================================
        // small helpers
        // =====================================================================================
        private static long NowMs => Environment.TickCount64;
        private bool SafePref() { try { return _pref(); } catch { return true; } }
        private bool SafeShared() { try { return _shared(); } catch { return false; } }
        private int SafeMinutes() { try { return Mathf.Clamp(_minutes(), 1, 120); } catch { return 10; } }
        private void Oops(string step, Exception e) { if (_said.Add(step)) CoopMod.Log($"Downed and revive ({step}): {e.Message}"); }
        private string Name(byte id) => _net.Remotes.TryGetValue(id, out var info) && !string.IsNullOrEmpty(info.Name) ? info.Name : L.T($"Oyuncu {id}", $"Player {id}");
        private static string Clock(float seconds) { int s = Mathf.Max(0, Mathf.CeilToInt(seconds)); return $"{s / 60}:{s % 60:00}"; }
        private static string P(Vector3 p) => string.Format(CultureInfo.InvariantCulture, "{0:0.00},{1:0.00},{2:0.00}", p.x, p.y, p.z);
        private static float Floor(Condition c) => Mathf.Max(0.5f, c.m_MaxHP * FloorPercent / 100f);
        private static float Share(Condition c, float percent)
        {
            float hp = c.m_MaxHP * percent / 100f;
            try { float cap = c.GetAdjustedMaxHP(); if (cap > 1f) hp = Mathf.Min(hp, cap); } catch { }
            return hp;
        }
        private static bool KeyDown() { try { return Application.isFocused && (GetAsyncKeyState(VK_KEY) & 0x8000) != 0; } catch { return false; } }
        private static void Line(string s, float seconds) { try { HUDMessage.AddMessage(s, seconds, true, true); } catch { } }
        private static bool Same(GearItem x, GearItem y) { try { return x != null && y != null && x.Pointer == y.Pointer; } catch { return false; } }

        // =====================================================================================
        // views for the state packet, the state dump and the harness
        // =====================================================================================
        internal bool LocalDowned => _down;
        internal bool Guarded => _guarded;
        internal string GuardWhy => _guardWhy;
        internal float SecondsLeft => _down ? Mathf.Max(0f, _deadline - Time.realtimeSinceStartup) : 0f;
        /// <summary>What goes out with every player state: 1 while on the ground, and the seconds left.</summary>
        internal byte LocalByte => _down ? (byte)1 : (byte)0;
        internal ushort LocalSecondsLeft => (ushort)Mathf.Clamp(Mathf.CeilToInt(SecondsLeft), 0, 65000);

        internal static float ConditionPercent()
        {
            try { var c = GameManager.GetConditionComponent(); return c != null && c.m_MaxHP > 0f ? c.m_CurrentHP / c.m_MaxHP * 100f : -1f; } catch { return -1f; }
        }

        /// <summary>Other players close enough to lift the local player right now.</summary>
        internal string ReviversInRange()
        {
            string ids = "";
            try
            {
                var t = GameManager.GetPlayerTransform(); string scene = CoopMod.CurrentScene();
                if (t == null) return "-";
                foreach (var r in _players.Snapshot())
                    if (r.HasState && r.Age <= 5f && r.Downed == 0 && r.Scene == scene && Vector3.Distance(r.Pos, t.position) <= ReviveRange)
                        ids += (ids.Length > 0 ? "," : "") + r.Id;
            }
            catch { }
            return ids.Length > 0 ? ids : "-";
        }

        internal string Summary() => _down ? $"down, {Clock(SecondsLeft)} left, trigger {LastTrigger}" : $"standing, condition {ConditionPercent():0.0} %, co-op deaths {Deaths}";

        internal void WriteState(System.Text.StringBuilder sb)
        {
            void Out(string k, object v) => sb.Append(k).Append(": ").Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append('\n');
            string F(float v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);
            try
            {
                Out("downed", _down);
                Out("downed_seconds_left", F(SecondsLeft, "0.0"));
                Out("downed_can_be_revived_by", _down ? ReviversInRange() : "-");
                Out("deaths_coop", Deaths);
                Out("last_death_pos", HasDeath ? P(LastDeathPos) : "-");
                Out("last_death_scene", LastDeathScene);
                Out("respawn_reason", RespawnReason);
                Out("respawn_point", RespawnPoint);
                Out("respawn_pos", HasDeath ? P(RespawnPos) : "-");
                Out("permadeath_guard", HookSummary());
                Out("downed_rules", (_guarded ? "on" : "off") + " (" + _guardWhy + ")");
                Out("downed_protected_save", string.IsNullOrEmpty(_guardSlot) ? "-" : _guardSlot + (_guardGameIdKnown ? " id=" + _guardGameId : ""));
                Out("downed_count", Downs);
                Out("downed_trigger", LastTrigger);
                Out("downed_cause", string.IsNullOrEmpty(_downCause) ? "-" : _downCause);
                Out("downed_pos", _down ? P(_downPos) : "-");
                Out("death_attempts_stopped", DeathAttempts);
                Out("save_deletes_stopped", DeletesBlocked);
                Out("revived_count", Revived);
                Out("revives_sent", RevivesSent);
                Out("revive_hold", _holdSince >= 0f ? F(Time.realtimeSinceStartup - _holdSince, "0.0") + " s on " + _holdTarget : "-");
                Out("revive_nearest_downed", _nearId == Nobody ? "-" : _nearId + " at " + F(_nearDist, "0.0") + " m");
                Out("revive_last_result", LastHelperResult);
                Out("downed_last_note", LastNote);
                Out("downed_notice", string.IsNullOrEmpty(_notice) ? "-" : _notice);
                Out("gear_left_behind", HasDeath ? $"dropped={LastDropped} kept={LastKept}" + (LastKeptNames.Length > 0 ? " (" + LastKeptNames.Trim() + ")" : "") : "-");
                Out("afflictions_cleared_last", LastCured);
                Out("last_sleep_place", _sleepValid ? $"{_sleepSave} {_sleepScene} {P(_sleepPos)}" : "-");
                Out("scene_entry_place", _entryValid ? $"{_entryScene} {P(_entryPos)}" : "-");
            }
            catch (Exception e) { Out("downed_state", "? " + e.Message); }
            if (!WorldSync.InGame()) return;
            void Try(string k, Func<object> f) { try { Out(k, f()); } catch (Exception e) { Out(k, "? " + e.Message); } }
            Try("downed_lock", () =>
            {
                var pm = GameManager.GetPlayerManagerComponent(); var c = GameManager.GetConditionComponent();
                return $"mode={pm.GetControlMode()} freeze={pm.m_FreezeMovement} never_die={c.m_NeverDie} invulnerable={c.m_Invulnerable} ghost={pm.m_Ghost} pinned_back={_pinBacks} panels_closed={_panelsClosed}";
            });
            Try("downed_fade_alpha", () => F(CameraFade.CurrentAlpha, "0.00"));
            Try("afflicted", () => GameManager.GetConditionComponent().HasNonRiskAffliction(false));
            Try("bleeding", () => GameManager.GetBloodLossComponent().HasBloodLoss());
            Try("sprained_ankle", () => GameManager.GetSprainedAnkleComponent().HasSprainedAnkle());
        }

        // =====================================================================================
        // every frame
        // =====================================================================================
        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            bool inGame = false;
            try { inGame = WorldSync.InGame(); } catch { }
            try { TickRealDeath(inGame, now); } catch (Exception e) { Oops("real death", e); }
            try { RefreshGuard(inGame, now); } catch (Exception e) { Oops("guard", e); }
            try { TrackPlaces(inGame, now); } catch (Exception e) { Oops("places", e); }
            try
            {
                if (_down) TickDowned(inGame, now);
                else if (_pendingCause != null)
                {
                    string cause = _pendingCause; _pendingCause = null;
                    if (inGame && (_guarded || NowMs - _lastGuardedMs < StickyGuardMs)) GoDown(cause, now);
                    else CoopMod.Log($"The game's death was stopped ({cause}), but there is no game to go down in right now: nothing more done");
                }
                else if (_guarded) Watch(now);
            }
            catch (Exception e) { Oops("local player", e); }
            try { DriveSwitches(); } catch (Exception e) { Oops("switches", e); }
            try { ClearLeftoverSwitches(inGame); } catch (Exception e) { Oops("leftover switches", e); }
            try { TickHelper(inGame, now); } catch (Exception e) { Oops("helper", e); }
            if (_unfadeCheckAt > 0f && now >= _unfadeCheckAt)
            {
                // the game's fade-in after getting up did nothing: the screen is still as dark as we left it
                _unfadeCheckAt = -1f;
                try
                {
                    float a = CameraFade.CurrentAlpha;
                    if (!_down && !CameraFade.IsFading && a > 0.05f && a <= FadeTarget + 0.01f && SleepSync.LocalSleep() == 0 && !WorldSync.Loading())
                    {
                        CameraFade.CurrentAlpha = 0f;
                        CoopMod.Log("Downed and revive: the game's fade-in left the screen dark; cleared by hand");
                    }
                }
                catch { }
            }
            if (_checkRespawnAt > 0f && now >= _checkRespawnAt)
            {
                _checkRespawnAt = -1f;
                try { CoopMod.Log($"After coming to you stand {Vector3.Distance(GameManager.GetPlayerTransform().position, RespawnPos):0.0} m from the chosen place ({RespawnPoint})"); } catch { }
            }
        }

        /// <summary>After the game's own updates of this frame: whatever they did to condition, a downed player stays at the floor.</summary>
        public void LateUpdate()
        {
            if (!_down) return;
            try { var c = GameManager.GetConditionComponent(); if (c != null) HoldCondition(c); } catch { }
        }

        // =====================================================================================
        // is this a world where nobody dies for good
        // =====================================================================================
        private bool ComputeGuard(bool inGame, out string why, out bool coopSlot)
        {
            coopSlot = false;
            if (_realDeath) { why = "game over: nobody was left standing, the game's own death runs"; return false; }
            if (!SafePref()) { why = "switched off (YereYigilma)"; return false; }
            if (!inGame) { why = "not in a game"; return false; }
            bool story = false; try { story = GameManager.IsStoryMode(); } catch { }
            if (story) { why = "story mode"; return false; }
            if (SafeShared()) { why = _net.Mode == NetMode.Host ? "hosting" : "client in the co-op world"; coopSlot = _net.Mode != NetMode.Host; return true; }
            long ms = NowMs;
            if (ms >= _nextCoopLookMs)
            {
                _nextCoopLookMs = ms + 1000;
                try { _coopLoaded = SaveMerge.IsCoopSlot(SaveGameSystem.GetCurrentSaveName()); } catch { _coopLoaded = false; }
            }
            if (_coopLoaded) { why = "the co-op save is loaded (not connected)"; coopSlot = true; return true; }
            why = "single player";
            return false;
        }

        private void RefreshGuard(bool inGame, float now)
        {
            bool g = ComputeGuard(inGame, out string why, out bool coop);
            if (g)
            {
                _lastGuardedMs = NowMs; _guardIsCoopSlot = coop;
                if (now >= _nextSlotLook)
                {
                    _nextSlotLook = now + 1f;
                    try { var n = SaveGameSystem.GetCurrentSaveName(); if (!string.IsNullOrEmpty(n)) _guardSlot = n; } catch { }
                    try { _guardGameId = SaveGameSystem.m_CurrentGameId; _guardGameIdKnown = true; } catch { _guardGameIdKnown = false; }
                }
            }
            else if (inGame && !_down)
            {
                // still playing, but not a shared world any more (hosting ended): this save is the player's own business again
                _guardSlot = null; _guardGameIdKnown = false;
            }
            if (g != _guarded) CoopMod.Log($"Downed and revive is {(g ? "ON" : "off")} here: {why}");
            _guarded = g; _guardWhy = why; GuardCached = g;
        }

        /// <summary>
        /// For the hooks. A loading screen or a moment without a player object must not open a gap for the game's own
        /// death: what was a guarded world seconds ago still is, unless hosting ended or the setting was switched off.
        /// </summary>
        private bool HookGuard()
        {
            if (_realDeath) return false;
            bool inGame = false; try { inGame = WorldSync.InGame(); } catch { }
            if (ComputeGuard(inGame, out _, out _)) { _lastGuardedMs = NowMs; return true; }
            if (_down || _pendingCause != null) return true;
            return !inGame && NowMs - _lastGuardedMs < StickyGuardMs && SafePref() && (_guardIsCoopSlot || _net.Mode == NetMode.Host);
        }

        /// <summary>
        /// A hook saw the game start (or continue) the local player's death. True: this world is guarded, the game's call must not run.
        /// underWay: this is a later step, so the first one (Condition.PlayerDeath) was not stopped and the game's death
        /// is really running; only then is the save put under protection from deletion (see BlockDelete).
        /// </summary>
        internal bool DeathAttempt(string where, bool underWay)
        {
            if (!HookGuard()) return false;
            DeathAttempts++;
            if (underWay) _deathSeenMs = NowMs;
            // the game's own floor first: for the rest of this frame nothing may bring the player back to zero
            try { var c = GameManager.GetConditionComponent(); if (c != null && c.m_CurrentHP < Floor(c)) c.m_CurrentHP = Floor(c); } catch { }
            if (!_down && _pendingCause == null) { _pendingCause = where; LastTrigger = where; }
            try { if (_said.Add("hook " + where)) CoopMod.Log($"The game's death was stopped at {where}: this is a shared world"); } catch { }
            return true;
        }

        /// <summary>
        /// A hook saw the game delete a save. True: it is the shared world's save and the game's own death is behind it, do not delete.
        /// Only then: with the death stopped at its first step (the normal case) the game never gets this far, and its
        /// everyday handling of save slots, and the player's own Delete in the Load screen, pass untouched.
        /// </summary>
        internal bool BlockDelete(string name, uint gameId, bool byId, string where)
        {
            if (_realDeath) return false; // the game is over: its own death may delete the save, as in single player
            bool match = byId ? _guardGameIdKnown && !string.IsNullOrEmpty(_guardSlot) && gameId == _guardGameId
                              : !string.IsNullOrEmpty(name) && name == _guardSlot;
            if (!match) return false;
            bool death = NowMs - _deathSeenMs < DeathContextMs;
            if (!death)
            {
                // no hook saw the death, but the game says the player is dead: same thing
                bool guarded = false; try { guarded = HookGuard(); } catch { guarded = GuardCached; }
                if (guarded)
                {
                    try { var pm = GameManager.GetPlayerManagerComponent(); if (pm != null && pm.PlayerIsDead()) death = true; } catch { }
                    try { var c = GameManager.GetConditionComponent(); if (c != null && c.IsConsideredDead()) death = true; } catch { }
                    if (death) _deathSeenMs = NowMs;
                }
            }
            if (!death) return false;
            // decided: from here on nothing may change the answer
            DeletesBlocked++;
            try
            {
                CoopMod.Log($"Kept the save {_guardSlot}: the game tried to delete it after a death ({where})");
                if (!_saidKept && !WorldSync.InGame())
                {
                    _saidKept = true;
                    CoopMod.Popup(L.T("ORTAK DÜNYA", "SHARED WORLD"), L.T("Bu kayıt bir ölümden sonra silinmek istendi ve korundu. Gerçekten silmek istiyorsan bir dakika sonra tekrar dene.", "The game wanted to delete this save after a death; it was kept. If you really want to delete it, try again in a minute."));
                }
            }
            catch { }
            return true;
        }

        /// <summary>No hook fired, yet the player is at zero (or the game's own death got through): take over from here.</summary>
        private void Watch(float now)
        {
            var cond = GameManager.GetConditionComponent(); var pm = GameManager.GetPlayerManagerComponent();
            if (cond == null || pm == null) return;
            bool dead = false; try { dead = pm.PlayerIsDead(); } catch { }
            if (dead)
            {
                _deathSeenMs = NowMs;
                if (_said.Add("vanilla death")) CoopMod.Log("The game's own death got through in a shared world: taking the player back from it");
                try { pm.SetControlMode(PlayerControlMode.Normal); } catch (Exception e) { Oops("undo dead state", e); }
                try { cond.m_CurrentHP = Floor(cond); } catch { }
                try { var p = InterfaceManager.GetPanel<Panel_LifeAfterDeath>(); if (p != null && p.IsEnabled()) p.Enable(false); } catch { }
                _pendingCause = "the game's own death (no hook stopped it)"; LastTrigger = "watch: dead state";
                return;
            }
            float hp = cond.m_CurrentHP;
            // (without the death hook the game's never-die switch keeps the player alive here, see DriveSwitches;
            //  where exactly it stops the fall is the game's business, so "as good as zero" has to do)
            if (hp > 0.0001f && !(!_hookDeath && hp <= cond.m_MaxHP * 0.011f)) { _zeroSince = -1f; return; }
            // with the hook in place the game's own death call comes within the frame and is the one that counts
            if (_zeroSince < 0f) _zeroSince = now;
            if (_hookDeath && now - _zeroSince < 0.5f) return;
            _zeroSince = -1f;
            cond.m_CurrentHP = Floor(cond);
            _pendingCause = "condition at zero"; LastTrigger = "watch: condition at zero";
        }

        // =====================================================================================
        // places to come back to
        // =====================================================================================
        private void TrackPlaces(bool inGame, float now)
        {
            if (!inGame) { _trackScene = null; _wasSleeping = false; return; }
            string scene = CoopMod.CurrentScene();
            var t = GameManager.GetPlayerTransform(); var pm = GameManager.GetPlayerManagerComponent();
            if (t == null || pm == null || string.IsNullOrEmpty(scene)) return;
            if (scene != _trackScene) { _trackScene = scene; _entryPending = true; _entryValid = false; }
            if (_entryPending && !WorldSync.Loading())
            {
                bool placed = true;
                try { if (pm.IsTeleportPending() || pm.IsStickPlayerToGroundPending()) placed = false; } catch { }
                if (placed)
                {
                    // where the game put the player when this region came up: the door they used, or where the save had them
                    _entryPending = false; _entryValid = true; _entryScene = scene; _entryPos = t.position; _entryRot = t.rotation;
                    CoopMod.Log($"Entered {scene} at {P(_entryPos)}");
                }
            }
            bool sleeping = SleepSync.LocalSleep() == 1;
            if (sleeping && !_wasSleeping && !_down)
            {
                string save = null; try { save = SaveGameSystem.GetCurrentSaveName(); } catch { }
                if (!string.IsNullOrEmpty(save))
                {
                    _sleepSave = save; _sleepScene = scene; _sleepPos = t.position; _sleepValid = true;
                    try { _rememberSleepPlace?.Invoke(string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2:0.###}|{3:0.###}|{4:0.###}", save, scene, _sleepPos.x, _sleepPos.y, _sleepPos.z)); } catch { }
                    CoopMod.Log($"Slept in {scene} at {P(_sleepPos)}: the place to come to after a co-op death in this region");
                }
            }
            _wasSleeping = sleeping;
            if (!_down && now >= _nextGround)
            {
                _nextGround = now + 0.25f;
                try { var cc = PlayerManager.GetPlayerController(); if (cc != null && cc.isGrounded) { _lastGround = t.position; _lastGroundScene = scene; } } catch { }
            }
        }

        private void ReadSleepPlace(string text)
        {
            try
            {
                var p = (text ?? "").Split('|');
                if (p.Length != 5) return;
                _sleepSave = p[0]; _sleepScene = p[1];
                _sleepPos = new Vector3(float.Parse(p[2], CultureInfo.InvariantCulture), float.Parse(p[3], CultureInfo.InvariantCulture), float.Parse(p[4], CultureInfo.InvariantCulture));
                _sleepValid = _sleepSave.Length > 0 && _sleepScene.Length > 0;
            }
            catch { _sleepValid = false; }
        }

        private void PickRespawn(string scene, Vector3 deathPos, Quaternion rot, out Vector3 pos, out Quaternion outRot, out string point)
        {
            outRot = rot;
            string save = null; try { save = SaveGameSystem.GetCurrentSaveName(); } catch { }
            if (_sleepValid && _sleepScene == scene && _sleepSave == save) { pos = _sleepPos; point = "last_sleep"; return; }
            if (_entryValid && _entryScene == scene) { pos = _entryPos; outRot = _entryRot; point = "scene_entry"; return; }
            pos = deathPos; point = "death_position";
        }

        // =====================================================================================
        // going down
        // =====================================================================================
        private bool SomeoneCanCome()
        {
            if (!_net.IsConnected || !SafeShared()) return false;
            foreach (var r in _players.Snapshot())
                if (r.HasState && r.Age <= 60f && r.Downed == 0) return true; // in the world (a loading screen counts) and on their feet
            return false;
        }

        private void GoDown(string cause, float now)
        {
            var t = GameManager.GetPlayerTransform(); var pm = GameManager.GetPlayerManagerComponent(); var cond = GameManager.GetConditionComponent();
            if (t == null || pm == null || cond == null) { CoopMod.Log("Going down: no player object right now, nothing done"); return; }
            _down = true; DownFast = true; Downs++;
            _downAt = now; _deadline = now + SafeMinutes() * 60f; _downScene = CoopMod.CurrentScene() ?? ""; _downPos = t.position; _downCause = cause;
            _giveUpSince = -1f; _giveUpNow = false; _reviveBy = Nobody; _settle = true; _settleUntil = now + 3f; _unsharedSince = -1f; _nobodySince = -1f;
            _nextNotice = 0f; _nextPin = 0f; _pinBacks = 0; _panelsClosed = 0; _zeroSince = -1f; _fadeAsked = false; _unfadeCheckAt = -1f;
            try { cond.m_CurrentHP = Floor(cond); } catch (Exception e) { Oops("floor", e); }
            _needsKnown = false;
            try
            {
                _calories = GameManager.GetHungerComponent().m_CurrentReserveCalories; _thirst = GameManager.GetThirstComponent().m_CurrentThirst;
                _fatigue = GameManager.GetFatigueComponent().m_CurrentFatigue; _freezing = GameManager.GetFreezingComponent().m_CurrentFreezing;
                _needsKnown = true;
            }
            catch (Exception e) { Oops("needs", e); }
            // asleep when it happened: end the sleep the game's own way, so its fast clock stops
            try { if (SleepSync.LocalSleep() != 0) CoopMod.Instance.Sleep?.WakeUp("went down"); } catch (Exception e) { Oops("wake", e); }
            // in an animal's jaws when it happened: the game's own way out of a struggle
            try
            {
                var ps = GameManager.GetPlayerStruggleComponent();
                if (ps != null && ps.InStruggle())
                {
                    try { ps.MakePartnerFlee(); } catch { }
                    ps.BreakStruggle();
                    CoopMod.Log("Went down in a struggle: the struggle was ended");
                }
            }
            catch (Exception e) { Oops("struggle", e); }

            bool help = SomeoneCanCome();
            CoopMod.Log($"You went down ({cause}) in {_downScene} at {P(_downPos)}: " + (help ? $"a friend has {SafeMinutes()} min to lift you" : "nobody else is in the world"));
            if (!help && !InLiveWorld()) { Die("nobody_could_come", now); return; }
            _nobodySince = help ? -1f : now;
            if (!help) CoopMod.Log($"Nobody is left standing: the game ends in {GameOverSeconds():0} s unless someone gets up");
            try { HoldLock(pm, cond); } catch (Exception e) { Oops("lock", e); }
            try { ClosePanels(); } catch (Exception e) { Oops("panels", e); }
            // what the player is told from now on, every few seconds, with the time counting down (written to the log once)
            CoopMod.Log("On screen: " + DownedLine(_deadline - now));
        }

        private static string DownedLine(float left) =>
            L.T($"Yere yığıldın! Arkadaşın yanına gelip seni kaldırabilir. Kalan süre: {Clock(left)}. Vazgeçmek için {KeyName} tuşunu {GiveUpHoldSeconds:0} saniye basılı tut.",
                $"You are down! A friend can come and revive you. Time left: {Clock(left)}. To give up, hold {KeyName} for {GiveUpHoldSeconds:0} seconds.");

        // =====================================================================================
        // on the ground
        // =====================================================================================
        private void TickDowned(bool inGame, float now)
        {
            if (!inGame) { Abandon("the game was left while down"); return; }
            var t = GameManager.GetPlayerTransform(); var pm = GameManager.GetPlayerManagerComponent(); var cond = GameManager.GetConditionComponent();
            if (t == null || pm == null || cond == null) return;

            // the ways out first, before anything that could go wrong
            if (_reviveBy != Nobody) { byte by = _reviveBy; _reviveBy = Nobody; GetUp(by, now, pm, cond, t); return; }
            // no longer in a shared world (connection lost, hosting ended): nobody can come any more, so it ends here.
            // (a moment's patience, so a hiccup in "is this the shared world" cannot cost anybody their gear)
            if (SafeShared()) _unsharedSince = -1f;
            else
            {
                if (_unsharedSince < 0f) _unsharedSince = now;
                if (now - _unsharedSince >= (_net.IsConnected ? 3f : 1f)) { Die("disconnected", now); return; }
            }
            if (_giveUpNow) { _giveUpNow = false; Die("gave_up", now); return; }
            if (KeyDown())
            {
                if (_giveUpSince < 0f) { _giveUpSince = now; _nextNotice = 0f; }
                if (now - _giveUpSince >= GiveUpHoldSeconds) { Die("gave_up", now); return; }
            }
            else if (_giveUpSince >= 0f) { _giveUpSince = -1f; _nextNotice = 0f; }
            if (now >= _deadline) { Die("bled_out", now); return; }
            if (InLiveWorld() && !SomeoneCanCome())
            {
                if (_nobodySince < 0f) { _nobodySince = now; _nextNotice = 0f; CoopMod.Log($"Nobody is left standing: the game ends in {GameOverSeconds():0} s unless someone gets up"); }
                if (now - _nobodySince >= GameOverSeconds()) { RealDeath("nobody_standing", now); return; }
            }
            else if (_nobodySince >= 0f) { _nobodySince = -1f; _nextNotice = 0f; CoopMod.Log("Someone is on their feet again: the countdown to the end stops"); }

            // then the holds, each on its own
            try { HoldCondition(cond); } catch (Exception e) { Oops("hold condition", e); }
            try { HoldNeeds(); } catch (Exception e) { Oops("hold needs", e); }
            try { HoldLock(pm, cond); } catch (Exception e) { Oops("lock", e); }
            try { HoldPlace(pm, t, now); } catch (Exception e) { Oops("hold place", e); }
            try { HoldFade(now); } catch (Exception e) { Oops("fade", e); }
            try { if (InterfaceManager.IsOverlayActiveImmediate()) ClosePanels(); } catch (Exception e) { Oops("panels", e); }
            try { PauseSync.Touch(); } catch { } // lying there is not "idle": the world must keep running for the one on the ground

            if (now >= _nextNotice)
            {
                float left = _deadline - now;
                if (_giveUpSince >= 0f)
                {
                    _nextNotice = now + 1f;
                    _notice = L.T("Vazgeçiliyor… ", "Giving up… ") + Mathf.CeilToInt(GiveUpHoldSeconds - (now - _giveUpSince));
                    Line(_notice, 0.9f);
                }
                else if (_nobodySince >= 0f)
                {
                    _nextNotice = now + 1f;
                    int sec = Mathf.Max(0, Mathf.CeilToInt(GameOverSeconds() - (now - _nobodySince)));
                    _notice = L.T($"Ayakta kimse kalmadı! {sec} saniye sonra oyun biter.", $"Nobody is left standing! The game ends in {sec} seconds.");
                    Line(_notice, 0.9f);
                }
                else
                {
                    _nextNotice = now + 4f;
                    _notice = DownedLine(left);
                    Line(_notice, 3.8f); // a little shorter than the gap to the next one: lines must not queue up behind each other
                }
            }
        }

        private static void HoldCondition(Condition cond)
        {
            float floor = Floor(cond);
            if (Mathf.Abs(cond.m_CurrentHP - floor) > 0.01f) cond.m_CurrentHP = floor;
        }

        /// <summary>Hunger, thirst, tiredness and cold stand still while down: the timer is the only clock.</summary>
        private void HoldNeeds()
        {
            if (!_needsKnown) return;
            var h = GameManager.GetHungerComponent(); if (h != null && h.m_CurrentReserveCalories != _calories) h.m_CurrentReserveCalories = _calories;
            var th = GameManager.GetThirstComponent(); if (th != null && th.m_CurrentThirst != _thirst) th.m_CurrentThirst = _thirst;
            var f = GameManager.GetFatigueComponent(); if (f != null && f.m_CurrentFatigue != _fatigue) f.m_CurrentFatigue = _fatigue;
            var fr = GameManager.GetFreezingComponent(); if (fr != null && fr.m_CurrentFreezing != _freezing) fr.m_CurrentFreezing = _freezing;
        }

        /// <summary>
        /// Cannot move, cannot act: the game's own "locked" player state and its freeze-movement switch.
        /// Both are remembered and put back by Release.
        /// </summary>
        private void HoldLock(PlayerManager pm, Condition cond)
        {
            try
            {
                // (in a vehicle, a snow shelter, on a rope: left as it is, there is no walking off from there anyway.
                //  "Dead" can only be a leftover of the game's own death: a downed player is not dead)
                var mode = pm.GetControlMode();
                if (mode == PlayerControlMode.Normal || mode == PlayerControlMode.Dead) { pm.SetControlMode(PlayerControlMode.Locked); _lockedByUs = true; }
            }
            catch (Exception e) { Oops("locked state", e); }
            try
            {
                if (!_heldMove) { _prevFreeze = pm.m_FreezeMovement; _heldMove = true; }
                if (!pm.m_FreezeMovement) pm.m_FreezeMovement = true;
            }
            catch (Exception e) { Oops("freeze movement", e); }
        }

        /// <summary>Put back what HoldLock changed, the game's switches, and the screen.</summary>
        private void Release(PlayerManager pm, Condition cond)
        {
            try
            {
                var mode = pm != null ? pm.GetControlMode() : PlayerControlMode.Normal;
                if (pm != null && ((_lockedByUs && mode == PlayerControlMode.Locked) || (mode == PlayerControlMode.Dead && (_guarded || _lockedByUs))))
                {
                    pm.SetControlMode(PlayerControlMode.Normal);
                    // whatever the game later "goes back to" must not be our lock
                    Compat.UnlockReturnMode(pm);
                }
            }
            catch (Exception e) { Oops("unlock", e); }
            _lockedByUs = false;
            try { if (pm != null && _heldMove) pm.m_FreezeMovement = _prevFreeze; } catch (Exception e) { Oops("unfreeze", e); }
            _heldMove = false;
            try { DriveSwitches(); } catch (Exception e) { Oops("switches", e); }
            _notice = null;
            if (_fadeHeld)
            {
                _fadeHeld = false; _fadeAsked = false;
                try { CameraFade.FadeIn(1.5f, 0f, null); _unfadeCheckAt = Time.realtimeSinceStartup + 2.5f; }
                catch (Exception e) { Oops("fade in", e); try { CameraFade.CurrentAlpha = 0f; } catch { } }
            }
        }

        internal int LeftoversCleared;
        /// <summary>
        /// A save written while a player was down by an older version (or the game itself) can bring back "never die" and
        /// "invulnerable" with the player standing: nothing could hurt them then (not a wolf, not a friend's shot). In a
        /// shared world, a standing player whose switches are not ours gets them switched off again.
        /// </summary>
        private void ClearLeftoverSwitches(bool inGame)
        {
            if (!inGame || _down || !_guarded || !WorldSync.Settled()) return;
            var cond = GameManager.GetConditionComponent();
            if (cond == null) return;
            bool nd = !_ownNeverDie && cond.m_NeverDie && _hookDeath, inv = !_ownInvulnerable && cond.m_Invulnerable;
            if (!nd && !inv) return;
            if (nd) cond.m_NeverDie = false;
            if (inv) cond.m_Invulnerable = false;
            LeftoversCleared++;
            CoopMod.Log($"Downed and revive: the loaded player still had {(nd ? "'never die' " : "")}{(inv ? "'invulnerable' " : "")}switched on from an earlier downed state: switched off");
        }

        /// <summary>
        /// The game's own switches, as the floor under a downed player: never die and invulnerable (Condition), unseen
        /// by animals (PlayerManager). The game writes all three into its saves, so each is only used while the hook
        /// that keeps it out of the save is in place, and each is put back the moment it is no longer wanted.
        /// Without the death hook, never-die is also what keeps the player alive in a shared world at all.
        /// </summary>
        private void DriveSwitches()
        {
            Condition cond = null; PlayerManager pm = null;
            try { cond = GameManager.GetConditionComponent(); pm = GameManager.GetPlayerManagerComponent(); } catch { }
            if (cond != null)
            {
                bool wantNeverDie = _hookCondSave && (_down || (!_hookDeath && _guarded));
                if (wantNeverDie) { if (!_ownNeverDie) { _origNeverDie = cond.m_NeverDie; _ownNeverDie = true; } if (!cond.m_NeverDie) cond.m_NeverDie = true; }
                else if (_ownNeverDie) { cond.m_NeverDie = _origNeverDie; _ownNeverDie = false; }
                bool wantInvulnerable = _hookCondSave && _down;
                if (wantInvulnerable) { if (!_ownInvulnerable) { _origInvulnerable = cond.m_Invulnerable; _ownInvulnerable = true; } if (!cond.m_Invulnerable) cond.m_Invulnerable = true; }
                else if (_ownInvulnerable) { cond.m_Invulnerable = _origInvulnerable; _ownInvulnerable = false; }
            }
            if (pm != null)
            {
                bool wantGhost = _hookPlayerSave && _down;
                if (wantGhost) { if (!_ownGhost) { _origGhost = pm.m_Ghost; _origCheats = pm.m_CheatsUsed; _ownGhost = true; } if (!pm.m_Ghost) pm.m_Ghost = true; }
                else if (_ownGhost)
                {
                    pm.m_Ghost = _origGhost; _ownGhost = false;
                    // should the game have taken our switch for a cheat, that mark does not belong in the player's save
                    if (!_origCheats && pm.m_CheatsUsed) { pm.m_CheatsUsed = false; CoopMod.Log("Downed and revive: cleared a 'cheats used' mark the game set while the player was down"); }
                }
            }
        }

        /// <summary>Stay where you fell. Still falling when it happened: wait for the ground, or go back to the last ground stood on.</summary>
        private void HoldPlace(PlayerManager pm, Transform t, float now)
        {
            string scene = CoopMod.CurrentScene() ?? "";
            if (scene != _downScene)
            {
                CoopMod.Log($"Down, but the region changed ({_downScene} -> {scene}): lying here now");
                _downScene = scene; _downPos = t.position; _settle = false;
                return;
            }
            if (_settle)
            {
                bool grounded = true;
                try { var cc = PlayerManager.GetPlayerController(); if (cc != null) grounded = cc.isGrounded; } catch { }
                if (grounded) { _settle = false; _downPos = t.position; return; }
                if (now < _settleUntil) { _downPos = t.position; return; }
                _settle = false;
                if (_lastGroundScene == scene && (_lastGround - t.position).sqrMagnitude > 1f)
                {
                    CoopMod.Log($"Went down in the air: lying on the last ground stood on, {P(_lastGround)}");
                    _downPos = _lastGround;
                    pm.TeleportPlayer(_downPos, t.rotation);
                }
                else _downPos = t.position;
                return;
            }
            if ((t.position - _downPos).sqrMagnitude > 0.25f && now >= _nextPin)
            {
                // the lock should make this impossible; if the player moved anyway, back they go
                _nextPin = now + 0.5f; _pinBacks++;
                if (_said.Add("pin")) CoopMod.Log("A downed player moved although locked: put back (the lock does not hold fully in this game version)");
                pm.TeleportPlayer(_downPos, t.rotation);
            }
        }

        /// <summary>Dark screen, kept just short of "fully faded out", where the game would hide its HUD and our line with it.</summary>
        private void HoldFade(float now)
        {
            float target = FadeTarget;
            try { float th = CameraFade.FADE_OUT_THRESHOLD; if (th > 0.5f && th <= 1f) target = Mathf.Min(target, th - 0.05f); } catch { }
            _fadeHeld = true;
            float a = CameraFade.CurrentAlpha;
            if (a > target) { CameraFade.CurrentAlpha = target; return; }
            if (a >= target - 0.05f || CameraFade.IsFading) return;
            // the game's own fade first; if that does not take, the value is set by hand (as the sleep screen does)
            if (!_fadeAsked) { _fadeAsked = true; _fadeAskedAt = now; CameraFade.FadeTo(target, 1f, 0f, null); }
            else if (now - _fadeAskedAt > 1.5f) CameraFade.CurrentAlpha = Mathf.MoveTowards(a, target, Time.unscaledDeltaTime * 1.5f);
        }

        /// <summary>Inventory, radial menu, first aid, clothing, crafting, journal, map, rest, a container: none of them while down. The Esc menu stays.</summary>
        private void ClosePanels()
        {
            int n = 0;
            try { var p = InterfaceManager.GetPanel<Panel_Inventory>(); if (p != null && p.IsEnabled()) { p.Enable(false); n++; } } catch { }
            try { var p = InterfaceManager.GetPanel<Panel_ActionsRadial>(); if (p != null && p.IsEnabled()) { p.Enable(false); n++; } } catch { }
            try { var p = InterfaceManager.GetPanel<Panel_FirstAid>(); if (p != null && p.IsEnabled()) { p.Enable(false); n++; } } catch { }
            try { var p = InterfaceManager.GetPanel<Panel_Clothing>(); if (p != null && p.IsEnabled()) { p.Enable(false); n++; } } catch { }
            try { var p = InterfaceManager.GetPanel<Panel_Crafting>(); if (p != null && p.IsEnabled()) { p.Enable(false); n++; } } catch { }
            try { var p = InterfaceManager.GetPanel<Panel_Log>(); if (p != null && p.IsEnabled()) { p.Enable(false); n++; } } catch { }
            try { var p = InterfaceManager.GetPanel<Panel_Map>(); if (p != null && p.IsEnabled()) { p.Enable(false); n++; } } catch { }
            try { var p = InterfaceManager.GetPanel<Panel_Rest>(); if (p != null && p.IsEnabled()) { p.Enable(false); n++; } } catch { }
            try { var p = InterfaceManager.GetPanel<Panel_Container>(); if (p != null && p.IsEnabled()) { p.Enable(false); n++; } } catch { }
            if (n > 0) { _panelsClosed += n; if (_said.Add("panels closed")) CoopMod.Log("A screen was open (or opened) while down: closed"); }
        }

        /// <summary>Left to the main menu while down: nothing of the game left to hold; the save the game wrote has the player at the floor.</summary>
        private void Abandon(string why)
        {
            CoopMod.Log($"No longer down: {why}");
            _down = false; DownFast = false; _pendingCause = null;
            PlayerManager pm = null; Condition cond = null;
            try { pm = GameManager.GetPlayerManagerComponent(); cond = GameManager.GetConditionComponent(); } catch { }
            Release(pm, cond);
        }

        // =====================================================================================
        // getting up, or not
        // =====================================================================================
        private void GetUp(byte helper, float now, PlayerManager pm, Condition cond, Transform t)
        {
            _down = false; DownFast = false; _pendingCause = null;
            Release(pm, cond);
            try { cond.m_CurrentHP = Share(cond, RevivePercent); } catch (Exception e) { Oops("revive condition", e); }
            Revived++;
            CoopMod.Log($"Player {helper} lifted you after {now - _downAt:0} s on the ground: up with {RevivePercent:0} % condition, afflictions and gear as they were");
            CoopMod.Hud(L.T($"{Name(helper)} seni kaldırdı", $"{Name(helper)} revived you"));
            SendNote(2, helper, CoopMod.CurrentScene() ?? "", t.position);
        }

        /// <summary>The co-op way to die: the gear stays where you fell, you come to at a safe place in this region. No death screen, the save stays.</summary>
        private void Die(string reason, float now)
        {
            var t = GameManager.GetPlayerTransform(); var pm = GameManager.GetPlayerManagerComponent(); var cond = GameManager.GetConditionComponent();
            bool connected = _net.IsConnected && SafeShared();
            _down = false; DownFast = false; _pendingCause = null; _giveUpSince = -1f;
            Release(pm, cond);
            if (t == null || pm == null || cond == null) { CoopMod.Log($"Co-op death ({reason}): no player object, nothing more done"); return; }
            string scene = CoopMod.CurrentScene() ?? _downScene;
            Vector3 feet = t.position, spot = feet; Quaternion rot = t.rotation;
            // died in the air (a fall that never ends, a kill zone under the map): the gear belongs on the last ground stood on
            try
            {
                var cc = PlayerManager.GetPlayerController();
                if (cc != null && !cc.isGrounded && _lastGroundScene == scene && (_lastGround - feet).sqrMagnitude > 1f) spot = _lastGround;
            }
            catch { }

            int dropped = 0, kept = 0; string keptNames = "";
            // A client that has lost its host would drop the gear into its own copy of the world only, and the next join
            // replaces that copy with the host's: the gear would be gone for good. So it stays with the player then.
            bool worldKeepsIt = connected || _net.Mode == NetMode.Host;
            if (worldKeepsIt)
            {
                try { DropEverything(pm, feet, spot, out dropped, out kept, out keptNames); } catch (Exception e) { CoopMod.Log("Leaving the gear behind failed: " + e.Message); }
            }
            else CoopMod.Log("Co-op death without a connection to the host: the gear stays with the player (dropped here it would be lost at the next join)");

            PickRespawn(scene, spot, rot, out var pos, out var outRot, out string point);
            // in a vehicle, a snow shelter, a seat, on a rope: the game has the player fastened there, and only its own
            // way out is safe. So no move: the player comes to right there.
            try
            {
                var mode = pm.GetControlMode();
                if (mode != PlayerControlMode.Normal && point != "death_position")
                {
                    CoopMod.Log($"Co-op death while the player is {mode}: not moved to {point}, comes to in place");
                    point = "death_position"; pos = spot;
                }
            }
            catch { }
            if (point != "death_position")
            {
                try { pm.TeleportPlayer(pos + new Vector3(0f, 0.05f, 0f), outRot); }
                catch (Exception e) { CoopMod.Log("Moving to the safe place failed (" + e.Message + "): you stay where you fell"); point = "death_position"; pos = spot; }
            }
            try { cond.m_CurrentHP = Share(cond, RespawnPercent); } catch (Exception e) { Oops("respawn condition", e); }
            int cured = 0;
            try { cured = CureAll(); } catch (Exception e) { Oops("afflictions", e); }
            try { var h = GameManager.GetHungerComponent(); h.m_CurrentReserveCalories = 0.5f * h.m_MaxReserveCalories; } catch (Exception e) { Oops("hunger", e); }
            try { var th = GameManager.GetThirstComponent(); th.m_CurrentThirst = 0.5f * th.m_MaxThirst; } catch (Exception e) { Oops("thirst", e); }
            try { var f = GameManager.GetFatigueComponent(); f.m_CurrentFatigue = 0.5f * f.m_MaxFatigue; } catch (Exception e) { Oops("fatigue", e); }
            // frozen stiff is how many get here: no colder than half, or the next minute would be the same death again
            try { var fr = GameManager.GetFreezingComponent(); if (fr.m_CurrentFreezing > 0.5f * fr.m_MaxFreezing) fr.m_CurrentFreezing = 0.5f * fr.m_MaxFreezing; } catch (Exception e) { Oops("cold", e); }

            Deaths++; HasDeath = true; LastDeathPos = spot; LastDeathScene = scene; RespawnReason = reason; RespawnPoint = point; RespawnPos = pos;
            LastDropped = dropped; LastKept = kept; LastKeptNames = keptNames; LastCured = cured; _checkRespawnAt = now + 1.5f;
            CoopMod.Log($"Co-op death ({reason}) in {scene} at {P(spot)}: {dropped} item(s) left there, {kept} kept{(keptNames.Length > 0 ? " (" + keptNames.Trim() + ")" : "")}; "
                + $"back at {point} {P(pos)} with {RespawnPercent:0} % condition, {cured} affliction(s) cleared, hunger/thirst/tiredness at half");

            string why = reason switch
            {
                "bled_out" => L.T("Süre doldu.", "Time ran out."),
                "gave_up" => L.T("Vazgeçtin.", "You gave up."),
                "disconnected" => L.T("Bağlantı koptu.", "The connection was lost."),
                _ => L.T("Yardıma gelebilecek kimse yoktu.", "Nobody could come to help."),
            };
            string where = point switch { "last_sleep" => L.T("Son uyuduğun yerde", "You came to where you last slept."), "scene_entry" => L.T("Bu bölgeye girdiğin yerde", "You came to where you entered this region."), _ => L.T("Düştüğün yerde", "You came to where you fell.") };
            string gear = !worldKeepsIt ? L.T("Sunucuya bağlı olmadığın için eşyaların sende kaldı.", "You are not connected to a server, so you kept your gear.")
                : dropped > 0 ? L.T("Eşyaların düştüğün yerde duruyor.", "Your gear lies where you fell.") : L.T("Yanında bırakacak eşya yoktu.", "You had no gear to leave behind.");
            try { HUDMessage.AddMessage(L.T($"{why} {where} kendine geldin. {gear}", $"{why} {where} {gear}"), 9f, true, true); } catch { }
            CoopMod.Log($"{why} {where} kendine geldin. {gear}");
            SendNote(3, ReasonCode(reason), scene, spot);
        }

        /// <summary>A shared world with a live connection (hosting, or a client talking to its host).</summary>
        private bool InLiveWorld() => SafeShared() && (_net.Mode == NetMode.Host || _net.IsConnected);

        /// <summary>Nobody standing: with friends in the world they get half a minute to give up and get back on their feet; alone it is short.</summary>
        private float GameOverSeconds()
        {
            int others = 0;
            try { foreach (var r in _players.Snapshot()) if (r.HasState && r.Age <= 60f) others++; } catch { }
            return others > 0 ? 30f : 5f;
        }

        /// <summary>
        /// Nobody is left standing: the game ends the way it ends in single player. The guard is lifted, the game's own
        /// death runs (death screen, and its save is deleted: the host's world, a client's copy of it), and the others hear of it.
        /// </summary>
        private void RealDeath(string reason, float now)
        {
            var t = GameManager.GetPlayerTransform(); var pm = GameManager.GetPlayerManagerComponent(); var cond = GameManager.GetConditionComponent();
            string scene = CoopMod.CurrentScene() ?? _downScene;
            Vector3 pos = t != null ? t.position : _downPos;
            _realDeath = true; _realDeathAt = now; _realDeathPushed = false; RealDeaths++;
            _down = false; DownFast = false; _pendingCause = null; _giveUpSince = -1f; _nobodySince = -1f;
            GuardCached = false; _guarded = false;
            Release(pm, cond);
            try { DriveSwitches(); } catch (Exception e) { Oops("switches", e); }
            CoopMod.Log($"Game over ({reason}) in {scene} at {P(pos)}: nobody was left standing, the game's own death runs");
            SendNote(4, ReasonCode(reason), scene, pos);
            try { if (cond != null) cond.m_CurrentHP = 0f; } catch (Exception e) { Oops("real death condition", e); }
            try { HUDMessage.AddMessage(L.T("Ayakta kimse kalmadı. Oyun bitti.", "Nobody was left standing. The game is over."), 6f, true, true); } catch { }
        }

        /// <summary>The game's death should follow on its own (condition at zero); if it has not after two seconds, call it.</summary>
        private void TickRealDeath(bool inGame, float now)
        {
            if (!_realDeath) return;
            if (!inGame)
            {
                if (now - _realDeathAt > 3f) { _realDeath = false; CoopMod.Log("Game over: back at the menu, the co-op guard works again"); }
                return;
            }
            if (_realDeathPushed || now - _realDeathAt < 2f) return;
            _realDeathPushed = true;
            bool dead = false; try { dead = GameManager.GetPlayerManagerComponent().PlayerIsDead(); } catch { }
            if (dead) return;
            try
            {
                var c = GameManager.GetConditionComponent();
                c.m_NeverDie = false; c.m_Invulnerable = false; c.m_CurrentHP = 0f;
                c.PlayerDeath(null);
                CoopMod.Log("Game over: the game's death was called directly");
            }
            catch (Exception e) { CoopMod.Log("Game over: the game's death could not be called: " + e.Message); }
        }

        private static byte ReasonCode(string r) => r == "bled_out" ? (byte)1 : r == "gave_up" ? (byte)2 : r == "disconnected" ? (byte)4 : (byte)3;
        private static string ReasonName(byte c) => c == 1 ? "bled_out" : c == 2 ? "gave_up" : c == 4 ? "disconnected" : "nobody_could_come";

        // =====================================================================================
        // the gear stays where you fell
        // =====================================================================================
        /// <summary>
        /// Everything carried except worn clothing goes on the ground around the spot, through the game's own drop
        /// (the call the inventory screen's Drop ends in), and each item is announced once, where it finally lies,
        /// through the ground-item sync: the other game shows it and can pick it up like anything else dropped.
        /// Water is carried as a measured supply, not as an object, and stays with the player.
        /// </summary>
        private void DropEverything(PlayerManager pm, Vector3 feet, Vector3 spot, out int dropped, out int kept, out string keptNames)
        {
            dropped = 0; kept = 0; keptNames = "";
            var inv = GameManager.GetInventoryComponent();
            if (inv == null) return;
            try { pm.UnequipItemInHandsSkipAnimation(); } catch (Exception e) { Oops("empty hands", e); }
            var list = new List<GearItem>();
            var items = inv.m_Items;
            for (int i = 0; items != null && i < items.Count; i++)
            {
                try { var g = items[i].m_GearItem; if (g != null) list.Add(g); } catch { }
            }
            GearItem waterA = null, waterB = null;
            try { waterA = inv.m_WaterSupplyPotable; waterB = inv.m_WaterSupplyNotPotable; } catch { }
            int n = 0;
            foreach (var g in list)
            {
                string name = "?";
                try
                {
                    name = ItemSync.PrefabName(g.gameObject);
                    if (Same(g, waterA) || Same(g, waterB)) { kept++; keptNames += name + " "; continue; }
                    if (pm.IsWearingClothingItem(g)) { kept++; continue; }
                    int units = Mathf.Max(1, ItemSync.UnitsOf(g));
                    GearItem d;
                    // our own announcement follows below, once the item lies where it will stay
                    ItemSync.Applying = true;
                    try { d = g.Drop(units, false, true, true); } finally { ItemSync.Applying = false; }
                    if (d == null) { kept++; keptNames += name + " "; continue; }
                    try { Place(d, feet, spot, n); } catch (Exception e) { Oops("placing dropped gear", e); }
                    n++; dropped++;
                    ItemSync.Instance?.OnLocalDrop(d);
                }
                catch (Exception e)
                {
                    kept++; keptNames += name + " ";
                    if (_said.Add("drop " + name)) CoopMod.Log($"Could not leave {name} behind ({e.Message}): it stays with the player");
                }
            }
        }

        private bool _placeBroken;
        /// <summary>
        /// The game drops at the feet; spread the pile into rings around the spot (8, then 12, 16 ... per ring).
        /// Only where a straight line from the spot is free (not through a wall or into furniture) and there is
        /// ground; otherwise the item stays where the game put it.
        /// </summary>
        private void Place(GearItem d, Vector3 feet, Vector3 spot, int index)
        {
            bool elsewhere = (spot - feet).sqrMagnitude > 0.01f; // the spot is not where the game dropped it (died in the air)
            var cur = d.transform.position;
            // the height above the ground the game gave this item when it laid it at the feet
            float lift = elsewhere ? 0.05f : Mathf.Clamp(cur.y - feet.y, 0f, 0.4f);
            if (elsewhere) d.transform.position = spot + Vector3.up * lift;
            if (_placeBroken) return;
            int ring = 0, slot = index, cap = 8;
            while (slot >= cap) { slot -= cap; ring++; cap += 4; }
            float r = 0.45f + 0.25f * ring, a = (slot + 0.5f * (ring & 1)) / cap * Mathf.PI * 2f;
            var off = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            try
            {
                bool free = false;
                for (int k = 0; k < 3 && !free; k++)
                {
                    free = Clear(spot + Vector3.up * 0.25f, off) && Clear(spot + Vector3.up * 0.7f, off);
                    if (!free) off *= 0.5f;
                }
                if (!free) return;
                var p = spot + off;
                // the highest solid thing within reach of the floor we stand on: the floor itself, a rug, a bed
                var hits = Physics.RaycastAll(new Vector3(p.x, spot.y + 1.0f, p.z), Vector3.down, 1.6f, ~0, QueryTriggerInteraction.Ignore);
                float y = float.NaN;
                for (int i = 0; hits != null && i < hits.Length; i++)
                {
                    var h = hits[i];
                    if (IsLooseItem(h.collider)) continue;
                    float hy = h.point.y;
                    if (Mathf.Abs(hy - spot.y) > 0.6f) continue;
                    if (float.IsNaN(y) || hy > y) y = hy;
                }
                if (float.IsNaN(y)) return;
                d.transform.position = new Vector3(p.x, y + lift, p.z);
            }
            catch (Exception e)
            {
                _placeBroken = true;
                CoopMod.Log("Spreading dropped gear: the game's ray test is not usable (" + e.Message + "): items stay in a pile");
            }
        }

        private static bool Clear(Vector3 from, Vector3 off)
        {
            var hits = Physics.RaycastAll(from, off.normalized, off.magnitude + 0.15f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; hits != null && i < hits.Length; i++)
            {
                if (IsLooseItem(hits[i].collider)) continue; // other loose items are not walls
                return false;
            }
            return true;
        }

        /// <summary>Is this collider part of an item (every item object of the game is named GEAR_...).</summary>
        private static bool IsLooseItem(Collider c)
        {
            try
            {
                var tr = c != null ? c.transform : null;
                for (int i = 0; i < 4 && tr != null; i++)
                {
                    string n = tr.name;
                    if (n != null && n.StartsWith("GEAR_", StringComparison.Ordinal)) return true;
                    tr = tr.parent;
                }
            }
            catch { }
            return false;
        }

        // =====================================================================================
        // afflictions cleared (the co-op death only; a revive keeps them)
        // =====================================================================================
        private int CureAll()
        {
            int n = 0;
            void Do(string what, Func<bool> has, Action cure)
            {
                try { if (has()) { cure(); n++; } }
                catch (Exception e) { if (_said.Add("cure " + what)) CoopMod.Log($"Could not clear {what}: {e.Message}"); }
            }
            Do("blood loss", () => GameManager.GetBloodLossComponent().HasBloodLoss(), () => GameManager.GetBloodLossComponent().Cure());
            Do("infection", () => GameManager.GetInfectionComponent().HasInfection(), () => GameManager.GetInfectionComponent().Cure());
            Do("infection risk", () => GameManager.GetInfectionRiskComponent().HasInfectionRisk(), () => GameManager.GetInfectionRiskComponent().Cure());
            Do("sprained ankle", () => GameManager.GetSprainedAnkleComponent().HasSprainedAnkle(), () => GameManager.GetSprainedAnkleComponent().Cure());
            Do("sprained wrist", () => GameManager.GetSprainedWristComponent().HasSprainedWrist(), () => GameManager.GetSprainedWristComponent().Cure());
            Do("sprain pain", () => GameManager.GetSprainPainComponent().HasSprainPain(), () => GameManager.GetSprainPainComponent().Cure());
            Do("broken rib", () => GameManager.GetBrokenRibComponent().HasBrokenRib(), () => GameManager.GetBrokenRibComponent().Cure());
            Do("headache", () => GameManager.GetHeadacheComponent().HasHeadache(), () => GameManager.GetHeadacheComponent().Cure());
            Do("burns", () => GameManager.GetBurnsComponent().HasBurns(), () => GameManager.GetBurnsComponent().BurnsEnd(true));
            Do("electric burns", () => GameManager.GetBurnsElectricComponent().HasBurnsElectric(), () => GameManager.GetBurnsElectricComponent().BurnsElectricEnd(true));
            Do("dysentery", () => GameManager.GetDysenteryComponent().HasDysentery(), () => GameManager.GetDysenteryComponent().DysenteryEnd(true));
            Do("food poisoning", () => GameManager.GetFoodPoisoningComponent().HasFoodPoisoning(), () => GameManager.GetFoodPoisoningComponent().FoodPoisoningEnd(true));
            Do("hypothermia", () => GameManager.GetHypothermiaComponent().HasHypothermia(), () => GameManager.GetHypothermiaComponent().HypothermiaEnd(true));
            Do("frostbite", () => GameManager.GetFrostbiteComponent().HasFrostbite(), () => GameManager.GetFrostbiteComponent().FrostbiteEnd());
            Do("cabin fever", () => GameManager.GetCabinFeverComponent().HasCabinFever(), () => GameManager.GetCabinFeverComponent().CabinFeverEnd());
            Do("cabin fever risk", () => GameManager.GetCabinFeverComponent().HasCabinFeverRisk(), () => GameManager.GetCabinFeverComponent().ClearCabinFeverRisk());
            Do("parasites", () => GameManager.GetIntestinalParasitesComponent().HasIntestinalParasites(), () => GameManager.GetIntestinalParasitesComponent().IntestinalParasitesEnd(true));
            Do("lacerations", () => GameManager.GetSevereLacerations().HasAffliction(), () => GameManager.GetSevereLacerations().Cure(AfflictionOptions.None));
            Do("broken body", () => GameManager.GetBrokenBody().HasAffliction, () => GameManager.GetBrokenBody().Cure(AfflictionOptions.None));
            Do("scurvy", () => GameManager.GetScurvyComponent().HasAffliction(), () => GameManager.GetScurvyComponent().Cure());
            Do("chemical poisoning", () => GameManager.GetChemicalPoisoningComponent().HasAffliction(), () => GameManager.GetChemicalPoisoningComponent().CureAll());
            return n;
        }

        // =====================================================================================
        // helping a friend up
        // =====================================================================================
        private void TickHelper(bool inGame, float now)
        {
            if (!inGame || !_net.IsConnected || !SafePref() || !SafeShared()) { _holdSince = -1f; _nearId = Nobody; _testHoldFor = 0f; return; }
            if (!_players.AnyDowned() && _holdSince < 0f) { _nearId = Nobody; _testHoldFor = 0f; return; }
            var t = GameManager.GetPlayerTransform(); string scene = CoopMod.CurrentScene();
            if (t == null) return;
            byte best = Nobody; float bestD = float.MaxValue; float left = 0f;
            foreach (var r in _players.Snapshot())
            {
                if (!r.HasState || r.Downed == 0 || r.Age > 5f || r.Scene != scene) continue;
                float d = Vector3.Distance(t.position, r.Pos);
                if (d < bestD) { bestD = d; best = r.Id; left = Mathf.Max(0f, r.DownedLeft - r.Age); }
            }
            _nearId = best; _nearDist = bestD;
            if (best == Nobody || _down)
            {
                if (_holdSince >= 0f) { LastHelperResult = _down ? "went down while lifting" : $"player {_holdTarget} is no longer down or no longer here"; _holdSince = -1f; }
                _testHoldFor = 0f;
                return;
            }
            bool inRange = bestD <= ReviveRange;
            bool test = _testHoldFor > 0f;
            bool key = inRange && now >= _helperCooldown && (KeyDown() || test);
            if (key)
            {
                if (_holdSince < 0f || _holdTarget != best)
                {
                    _holdSince = now; _holdTarget = best; _helperNextLine = 0f;
                    CoopMod.Log($"Lifting player {best} ({bestD:0.0} m away): holding");
                }
                float held = now - _holdSince;
                // a test hold lasts exactly as long as asked, whatever the frame rate
                if (test && held >= _testHoldFor) { held = _testHoldFor; _testHoldFor = 0f; }
                if (held >= ReviveHoldSeconds)
                {
                    _holdSince = -1f; _helperCooldown = now + 3f; _testHoldFor = 0f;
                    SendRevive(best);
                    return;
                }
                if (now >= _helperNextLine) { _helperNextLine = now + 1f; Line(L.T($"{Name(best)} kaldırılıyor… ", $"Reviving {Name(best)}… ") + Mathf.CeilToInt(ReviveHoldSeconds - held), 0.9f); }
                return;
            }
            _testHoldFor = 0f;
            if (_holdSince >= 0f)
            {
                float held = now - _holdSince;
                LastHelperResult = inRange ? $"let go after {held:0.0} s of the {ReviveHoldSeconds:0} s needed" : $"moved out of reach after {held:0.0} s ({bestD:0.0} m)";
                CoopMod.Log($"Lifting player {_holdTarget}: {LastHelperResult}");
                _holdSince = -1f; _helperNextLine = 0f;
            }
            if (now < _helperNextLine || now < _helperCooldown) return;
            if (inRange) { _helperNextLine = now + 5f; Line(L.T($"{Name(best)} yerde: kaldırmak için {KeyName} basılı tut ({Clock(left)} kaldı)", $"{Name(best)} is down: hold {KeyName} to revive them ({Clock(left)} left)"), 4.8f); }
            else if (bestD <= NoticeRange) { _helperNextLine = now + 8f; Line(L.T($"{Name(best)} yerde: {Clock(left)} kaldı ({bestD:0} m)", $"{Name(best)} is down: {Clock(left)} left ({bestD:0} m)"), 4.5f); }
        }

        private void SendRevive(byte target)
        {
            RevivesSent++;
            LastHelperResult = $"lifted player {target}: sent";
            CoopMod.Log($"Held {KeyName} for {ReviveHoldSeconds:0} s next to player {target}: lifting them");
            var w = _net.Begin(Msg.Revive); w.Put(target); w.Put(_net.LocalId);
            if (_net.Mode == NetMode.Host) _net.SendTo(target, w, DeliveryMethod.ReliableOrdered);
            else _net.Send(w, DeliveryMethod.ReliableOrdered);
        }

        private void SendNote(byte what, byte other, string scene, Vector3 pos)
        {
            try
            {
                if (!_net.IsConnected) return;
                var w = _net.Begin(Msg.DownedNote); w.Put(_net.LocalId); w.Put(what); w.Put(other); w.Put(scene ?? ""); w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
                _net.Send(w, DeliveryMethod.ReliableOrdered);
            }
            catch (Exception e) { Oops("telling the others", e); }
        }

        // =====================================================================================
        // messages
        // =====================================================================================
        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (msg != Msg.Revive && msg != Msg.DownedNote) return;
            try
            {
                if (msg == Msg.Revive)
                {
                    byte target = r.GetByte(), helper = r.GetByte();
                    if (_net.Mode == NetMode.Host)
                    {
                        helper = from; // a client speaks only for itself
                        if (target != _net.LocalId)
                        {
                            var w = _net.Begin(Msg.Revive); w.Put(target); w.Put(helper);
                            _net.SendTo(target, w, DeliveryMethod.ReliableOrdered);
                            CoopMod.Log($"Player {helper} lifts player {target}: passed on");
                            return;
                        }
                    }
                    else if (target != _net.LocalId) return;
                    ReviveArrived(helper);
                    return;
                }
                byte id = r.GetByte(), what = r.GetByte(), other = r.GetByte(); string scene = r.GetString();
                var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                if (_net.Mode == NetMode.Host)
                {
                    id = from;
                    var w = _net.Begin(Msg.DownedNote); w.Put(id); w.Put(what); w.Put(other); w.Put(scene); w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
                    _net.Send(w, DeliveryMethod.ReliableOrdered, from);
                }
                if (id == _net.LocalId) return;
                if (what == 2)
                {
                    LastNote = $"player {id} was lifted by player {other}";
                    CoopMod.Log($"Player {id} was lifted by player {other} in {scene} at {P(pos)}: up with {RevivePercent:0} % condition");
                    if (other == _net.LocalId) LastHelperResult = $"player {id} got up";
                    if (WorldSync.InGame()) Line(other == _net.LocalId ? L.T($"{Name(id)} ayağa kalktı: onu sen kaldırdın", $"{Name(id)} is up again: you revived them") : L.T($"{Name(id)} ayağa kalktı", $"{Name(id)} is up again"), 5f);
                }
                else if (what == 4)
                {
                    LastNote = $"player {id} died for good: nobody was left standing";
                    CoopMod.Log($"Player {id} died for good in {scene} at {P(pos)}: nobody was left standing");
                    if (id == Protocol.HostId && _net.Mode == NetMode.Client)
                    {
                        // the host's world is over; whoever is still on the ground here goes with it
                        if (_down) { RealDeath("host_game_over", Time.realtimeSinceStartup); return; }
                        CoopMod.Popup(L.T("ORTAK DÜNYA", "SHARED WORLD"), L.T("Sunucu öldü: ortak dünyada ayakta kimse kalmadı, oyun bitti.", "The host died: nobody was left standing in the shared world, the game is over."));
                    }
                    else if (WorldSync.InGame()) Line(L.T($"{Name(id)} öldü.", $"{Name(id)} died."), 6f);
                }
                else if (what == 3)
                {
                    LastNote = $"player {id} died the co-op way ({ReasonName(other)})";
                    CoopMod.Log($"Player {id} died the co-op way ({ReasonName(other)}): their gear lies in {scene} at {P(pos)}, they came to at a safe place");
                    if (WorldSync.InGame()) Line(L.T($"{Name(id)} dayanamadı: güvenli bir yerde kendine geldi. Eşyaları düştüğü yerde duruyor.", $"{Name(id)} did not make it: they came to at a safe place. Their gear lies where they fell."), 7f);
                }
            }
            catch (Exception e) { CoopMod.Log($"Downed message {msg}: {e.Message}"); }
        }

        private void ReviveArrived(byte helper)
        {
            if (!_down) { CoopMod.Log($"Player {helper} tried to lift us, but we are not down: ignored"); return; }
            // the helper's game measured the distance; a helper this game sees far away or elsewhere is a stale message
            try
            {
                if (_players.TryGetState(helper, out var s, out var pos))
                {
                    var t = GameManager.GetPlayerTransform();
                    float d = t != null ? Vector3.Distance(t.position, pos) : 0f;
                    if (s != CoopMod.CurrentScene() || d > ReviveRange + 3f)
                    {
                        CoopMod.Log($"Player {helper} tried to lift us from {(s != CoopMod.CurrentScene() ? "another region" : d.ToString("0.0") + " m away")}: ignored");
                        return;
                    }
                }
            }
            catch { }
            _reviveBy = helper;
        }

        /// <summary>The "down" flag of another player changed (from their state packets).</summary>
        internal void RemoteDownedChanged(byte id, byte downed, string scene, Vector3 pos)
        {
            try
            {
                if (downed == 0) { CoopMod.Log($"Player {id} is no longer down"); return; }
                CoopMod.Log($"Player {id} went down in {scene} at {P(pos)}");
                if (!WorldSync.InGame()) return;
                string where = "";
                var t = GameManager.GetPlayerTransform();
                if (scene != CoopMod.CurrentScene()) where = L.T(" (başka bir bölgede)", " (in another region)");
                else if (t != null) where = L.T($" ({Vector3.Distance(t.position, pos):0} m ötede)", $" ({Vector3.Distance(t.position, pos):0} m away)");
                Line(L.T($"{Name(id)} yere yığıldı{where}", $"{Name(id)} is down{where}"), 6f);
                _helperNextLine = Time.realtimeSinceStartup + 5f;
            }
            catch { }
        }

        // =====================================================================================
        // the switches we flip never reach a save
        // =====================================================================================
        internal void BeforeConditionSave(Condition c)
        {
            if (c == null || (!_ownNeverDie && !_ownInvulnerable)) return;
            if (_ownNeverDie) c.m_NeverDie = _origNeverDie;
            if (_ownInvulnerable) c.m_Invulnerable = _origInvulnerable;
            _swappedCond = true;
        }
        internal void AfterConditionSave(Condition c)
        {
            if (!_swappedCond) return;
            _swappedCond = false;
            if (c == null) return;
            if (_ownNeverDie) c.m_NeverDie = true;
            if (_ownInvulnerable) c.m_Invulnerable = true;
        }
        internal void BeforePlayerSave(PlayerManager pm)
        {
            if (!_ownGhost || pm == null) return;
            pm.m_Ghost = _origGhost; _swappedPlayer = true;
        }
        internal void AfterPlayerSave(PlayerManager pm)
        {
            if (!_swappedPlayer) return;
            _swappedPlayer = false;
            if (_ownGhost && pm != null) pm.m_Ghost = true;
        }

        // =====================================================================================
        // the harness
        // =====================================================================================
        /// <summary>Is this other player on the ground, as this game sees it (false also when unknown).</summary>
        internal bool RemoteDowned(byte id)
        {
            foreach (var r in _players.Snapshot()) if (r.Id == id) return r.HasState && r.Downed != 0;
            return false;
        }

        /// <summary>Clear every affliction the co-op death clears (harness: tidy up after a test).</summary>
        internal int TestCureAll() => CureAll();

        /// <summary>The downed player's give-up, without the key. Null = done, else why not.</summary>
        internal string GiveUp()
        {
            if (!_down) return "not down";
            _giveUpNow = true;
            return null;
        }

        /// <summary>Set the time a downed local player has left. Null = done, else why not.</summary>
        internal string SetSecondsLeft(float seconds)
        {
            if (!_down) return "not down";
            _deadline = Time.realtimeSinceStartup + Mathf.Max(0f, seconds);
            _nextNotice = 0f;
            return null;
        }

        /// <summary>As if the key were held for this long, starting now. Null = started, else why it cannot.</summary>
        internal string TestHold(float seconds, out byte target, out float dist)
        {
            target = _nearId; dist = _nearDist;
            if (_down) return "this player is down itself";
            if (!_net.IsConnected || !SafeShared()) return "not connected in a shared world";
            if (_nearId == Nobody) return "no downed player in this region";
            if (_nearDist > ReviveRange) return $"the nearest downed player ({_nearId}) is {_nearDist:0.0} m away (within {ReviveRange:0.0} m needed)";
            _helperCooldown = 0f; _holdSince = Time.realtimeSinceStartup; _holdTarget = _nearId; _helperNextLine = 0f;
            _testHoldFor = Mathf.Max(0.1f, seconds);
            LastHelperResult = "holding";
            CoopMod.Log($"Lifting player {_nearId} ({_nearDist:0.0} m away): holding for {_testHoldFor:0.0} s (test)");
            return null;
        }
    }

    // Applied by hand (DownedSync.ApplyPatches), each on its own. Nothing thrown in here may reach the game: on any
    // trouble a death hook answers what the last frame knew about this world, a delete hook lets the game go on.
    internal static class DownedPatches
    {
        private static bool _logged;
        private static void Oops(string where, Exception e) { if (!_logged) { _logged = true; CoopMod.Log($"Downed and revive hook {where}: {e.Message}"); } }

        private static bool Stop(string where, bool underWay)
        {
            try { var d = DownedSync.Instance; return d != null && d.DeathAttempt(where, underWay); }
            catch (Exception e) { Oops(where, e); return DownedSync.GuardCached; }
        }

        internal static bool PlayerDeathPrefix() => !Stop("Condition.PlayerDeath", false);
        internal static bool HandlePlayerDeathPrefix() => !Stop("GameManager.HandlePlayerDeath", true);
        internal static bool EnableDeathViewPrefix() => !Stop("Panel_Log.EnableDeathView", true);
        internal static bool ShowLifeAfterDeathPrefix() => !Stop("Panel_LifeAfterDeath.ShowLifeAfterDeath", true);
        internal static bool PerformRespawnPrefix(ref bool __result)
        {
            if (!Stop("LifeAfterDeathManager.PerformRespawn", true)) return true;
            __result = false;
            return false;
        }

        private static bool Keep(string name, uint id, bool byId, string where)
        {
            try { var d = DownedSync.Instance; return d != null && d.BlockDelete(name, id, byId, where); }
            catch (Exception e) { Oops(where, e); return false; }
        }

        internal static bool DeleteByNamePrefix(string __0) => !Keep(__0, 0, false, "SaveGameSystem.DeleteSaveFiles");
        internal static bool DeleteByIdPrefix(uint __0) => !Keep(null, __0, true, "delete by game id");
        internal static bool DeleteSlotByNamePrefix(string __0, ref bool __result)
        {
            if (!Keep(__0, 0, false, "SaveGameSlots.DeleteSlot")) return true;
            __result = false;
            return false;
        }
        internal static bool DeleteSlotByDataPrefix(SlotData __0)
        {
            string name = null;
            try { name = __0 != null ? __0.m_InternalName : null; } catch { }
            return !Keep(name, 0, false, "SaveGameSlots.DeleteSlot(slot)");
        }
        internal static bool DeleteSlotInfoPrefix(SaveSlotInfo __0)
        {
            string name = null;
            try { name = __0 != null ? __0.m_SaveSlotName : null; } catch { }
            return !Keep(name, 0, false, "SaveGameSlotHelper.DeleteSaveSlotInfo");
        }

        /// <summary>Runs before every change of the player's condition: while down, nothing takes any away.</summary>
        internal static bool AddHealthPrefix(float __0) => !(DownedSync.DownFast && __0 < 0f);

        internal static void ConditionSavePrefix(Condition __instance) { try { DownedSync.Instance?.BeforeConditionSave(__instance); } catch (Exception e) { Oops("Condition.Serialize", e); } }
        internal static void ConditionSavePostfix(Condition __instance) { try { DownedSync.Instance?.AfterConditionSave(__instance); } catch (Exception e) { Oops("Condition.Serialize", e); } }
        internal static void PlayerSavePrefix(PlayerManager __instance) { try { DownedSync.Instance?.BeforePlayerSave(__instance); } catch (Exception e) { Oops("PlayerManager.Serialize", e); } }
        internal static void PlayerSavePostfix(PlayerManager __instance) { try { DownedSync.Instance?.AfterPlayerSave(__instance); } catch (Exception e) { Oops("PlayerManager.Serialize", e); } }
    }
}
