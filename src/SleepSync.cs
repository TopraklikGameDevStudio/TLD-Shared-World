using System;
using HarmonyLib;
using Il2Cpp;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Shared sleep: in a shared world time only speeds up while every player in it is sleeping or passing time.
    ///
    /// How the game does it: Rest (bed, bedroll) and PassTime both speed the clock up by making the day shorter,
    /// TimeOfDay.SetDayLengthScale(smaller value), and put the old value back when they end. Both count their own
    /// progress in game time, so a sleep whose clock is kept at normal speed simply takes longer.
    /// That one call is where the speed is decided, so that is where the rule lives:
    ///   - somebody else is awake  -> the local sleep keeps the normal day length (no speed-up on any game)
    ///   - everybody sleeps        -> the host runs the speed its own game picked; clients run the host's speed and
    ///                                steer towards the host's clock instead of being jumped to it
    /// The host decides who "everybody" is and tells the clients; a client never speeds up without fresh word from it.
    /// Alone, switched off, or without the hook, nothing here touches the game.
    /// </summary>
    internal sealed class SleepSync
    {
        internal const byte Nobody = 255;
        internal const string WakeKeyName = "F8";
        private const float StaleAfter = 5f;    // no news from a player for this long: we cannot call them asleep
        private const float GoneAfter = 60f;    // ... and for this long: they are not in the world (main menu, still joining)
        private const float HostWordFor = 3f;   // a client believes "everyone sleeps" only this long without hearing it again
        private const float ChaseSeconds = 0.5f;    // how quickly a sleeping client closes the gap to the host's fast clock
        private const float CatchUpSeconds = 0.1f;  // ... and the last bit once the host has stopped

        /// <summary>Test harness only: 0 = follow the setting, 1 = on, 2 = off.</summary>
        internal static int TestOverride;

        private readonly Network _net;
        private readonly RemotePlayers _players;
        private readonly Func<bool> _shared, _pref;
        private bool _hooked, _loggedError, _loggedHook, _loggedRate, _loggedNormal, _loggedPopup;
        internal int HookCalls;                 // times the game itself changed the day length (seen by the hook)

        // ---- this game's own sleep ----
        private bool _holding;                  // the local player sleeps in a shared world: we decide how fast
        private bool _leave;                    // ... but this one cannot be held (see Begin): hands off
        private byte _kind;                     // 1 sleeping, 2 passing time
        private float _normal = 1f;             // day length scale at normal speed
        private float _wanted = -1f;            // the scale the game itself picked for this sleep (smaller = faster); -1 not seen yet
        private float _lastSet = -1f;           // what we last told the game
        private bool _own;                      // our own SetDayLengthScale call is running
        private float _idleScale = -1f;         // scale last seen while awake
        private float _rateK;                   // game hours per real second x scale (speed = _rateK / scale)
        private float _left, _beganAt;
        private bool _allSeen, _first;          // what the sleeper was last told; first look of this sleep
        private float _nextSteer;

        // ---- host: the decision ----
        private bool _all; private byte _blocker = Nobody; private float _nextSay, _saidFactor = -1f;

        // ---- client: what the host said ----
        private bool _hostOn; private float _hostFactor = 1f, _hostSaidAt = -100f; private byte _hostBlocker = Nobody;
        private float _chaseFactor = 1f;        // the host's speed while it last ran fast (still needed to catch up after its "stop")
        private float _hostClock, _hostClockAt;
        private float _catchUpTo = float.NaN, _catchUpSince;

        // ---- the line on screen ----
        private string _noticeText; private float _nextNotice;
        private bool _popupTouched, _popupWasOn; private string _popupOldText;
        internal bool NoticeOnHud, NoticeOnPopup;

        // ---- measured clock speed (state dump) ----
        private float _mT, _mH, _clockSpeed;
        private float _idleK;                   // measured while awake: game hours per real second x scale
        private readonly float[] _kSamples = new float[5]; private int _kCount;

        public SleepSync(Network net, RemotePlayers players, Func<bool> worldShared, Func<bool> pref)
        {
            _net = net; _players = players; _shared = worldShared; _pref = pref;
            _net.OnMessage += OnMessage;
            _net.OnStopped += () => { _all = false; _blocker = Nobody; _hostOn = false; _hostSaidAt = -100f; _catchUpTo = float.NaN; _saidFactor = -1f; };
        }

        /// <summary>The one hook. Without it shared sleep stays off and sleeping works as it did before.</summary>
        internal void ApplyPatches(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(TimeOfDay), nameof(TimeOfDay.SetDayLengthScale), new[] { typeof(float) }),
                    prefix: new HarmonyMethod(typeof(SleepPatches), nameof(SleepPatches.SetDayLengthScalePrefix)));
                _hooked = true;
                CoopMod.Log($"Shared sleep: hook in place, setting {(SafePref() ? "on" : "off")}");
            }
            catch (Exception e)
            {
                _hooked = false;
                CoopMod.Log("Shared sleep: could not hook TimeOfDay.SetDayLengthScale (" + e.Message + "): sleeping works as before");
            }
        }

        private bool SafePref() { try { return _pref(); } catch { return true; } }
        private bool SafeShared() { try { return _shared(); } catch { return false; } }
        private bool Wanted => _hooked && (TestOverride == 1 || (TestOverride == 0 && SafePref()));
        private static bool Sane(float v) => v > 0.000001f && v < 1000f && !float.IsNaN(v);
        private static bool Close(float a, float b) => Mathf.Abs(a - b) <= 0.0005f * Mathf.Max(Mathf.Abs(a), Mathf.Abs(b)) + 0.0000001f;
        private bool Faster(float scale) => scale < _normal * 0.999f;

        // ---------------- what the local player is doing ----------------
        /// <summary>0 awake, 1 sleeping (bed, bedroll or any other rest), 2 passing time.</summary>
        internal static byte LocalSleep()
        {
            try { var r = GameManager.GetRestComponent(); if (r != null && r.IsSleeping()) return 1; } catch { }
            try { var p = GameManager.GetPassTime(); if (p != null && p.IsPassingTime()) return 2; } catch { }
            return 0;
        }

        /// <summary>Game hours the local sleep or pass-time still has to run (0 when awake).</summary>
        internal static float HoursLeft()
        {
            try
            {
                var r = GameManager.GetRestComponent();
                if (r != null && r.IsSleeping()) return Mathf.Max(0f, (r.m_SleepDurationSeconds - r.m_NumSecondsSleeping) / 3600f);
            }
            catch { }
            try { var p = GameManager.GetPassTime(); if (p != null && p.IsPassingTime()) return Mathf.Max(0f, p.GetNumHoursRemaining()); } catch { }
            return 0f;
        }

        // ---------------- views for WorldSync, the state dump and the harness ----------------
        /// <summary>Host: every player in the world sleeps and the host's own clock runs fast for all of them.</summary>
        internal bool HostAccelerating => _net.Mode == NetMode.Host && _all;

        /// <summary>Client: asleep and keeping step with the host's fast clock; the clock is steered, not jumped.</summary>
        internal bool Following { get; private set; }

        /// <summary>What this game currently believes.</summary>
        internal bool EveryoneSleeping
        {
            get
            {
                if (_net.Mode == NetMode.Host && _net.Remotes.Count > 0 && Wanted) return _all;
                if (_net.Mode == NetMode.Client && _net.IsConnected && Wanted) return ClientAll(Time.realtimeSinceStartup);
                return WorldSync.InGame() && LocalSleep() != 0; // alone: everybody is just us
            }
        }

        /// <summary>How much faster than normal this game's clock is set to run right now (1 = normal).</summary>
        internal float TimeAccel()
        {
            try
            {
                if (!WorldSync.InGame()) return 1f;
                float cur = GameManager.GetTimeOfDayComponent().GetDayLengthScale();
                float normal = _holding ? _normal : Sane(_idleScale) ? _idleScale : 1f;
                if (!Sane(cur)) return 1f;
                if (!_holding && LocalSleep() == 0) return 1f; // awake: whatever the game does now is its normal
                return normal / cur;
            }
            catch { return 1f; }
        }

        /// <summary>WorldSync asks before it jumps a client's clock to the host's. True: leave the clock alone, we steer it.</summary>
        internal bool HostClock(float hostHours, float diff)
        {
            if (_net.Mode != NetMode.Client) return false;
            float now = Time.realtimeSinceStartup;
            if (_hostOn) { _hostClock = hostHours; _hostClockAt = now; }
            // (the word from the host can arrive in the same frame as its clock, before we had a chance to act on it)
            bool steering = _holding && !_leave && ((ClientAll(now) && _hostFactor > 0f) || !float.IsNaN(_catchUpTo));
            // hours off: something else is wrong, let the jump happen
            return steering && diff < 4f;
        }

        internal void WriteState(System.Text.StringBuilder sb)
        {
            void Line(string k, object v) => sb.Append(k).Append(": ").Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            string F(float v, string fmt) => v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                Line("sleep_sync", !_hooked ? "off (hook missing)" : !Wanted ? "off (setting)" : _holding ? (_leave ? "hands off (this sleep cannot be held)" : "holding") : "ready");
                Line("sleep_hook_calls", HookCalls);
                byte awake = _net.Mode == NetMode.Host ? _blocker : _hostBlocker;
                Line("sleep_blocker", awake == Nobody ? "-" : awake.ToString());
                Line("sleep_following_host", Following);
                Line("sleep_host_factor", _net.Mode == NetMode.Client ? F(_hostFactor, "0.0") : _saidFactor < 0f ? "-" : F(_saidFactor, "0.0"));
                if (WorldSync.InGame())
                {
                    float cur = GameManager.GetTimeOfDayComponent().GetDayLengthScale();
                    Line("day_length_scale", F(cur, "0.000000"));
                    Line("day_length_scale_normal", F(_holding ? _normal : _idleScale, "0.000000"));
                    Line("day_length_scale_game_wants", _wanted > 0f ? F(_wanted, "0.000000") : "-");
                    Line("clock_hours_per_second", F(_clockSpeed, "0.00000"));
                    Line("clock_hours_per_second_normal", F(NormalSpeed(cur), "0.00000"));
                    float kN = NormalSpeed(cur);
                    Line("time_accel_measured", kN > 0f ? F(_clockSpeed / kN, "0.0") : "?");
                }
                UiDump((k, v) => Line(k, v));
                Line("sleep_notice", string.IsNullOrEmpty(_noticeText) ? "-" : _noticeText);
                Line("sleep_notice_shown", $"hud={NoticeOnHud} popup={NoticeOnPopup}");
            }
            catch (Exception e) { Line("sleep_sync", "? " + e.Message); }
        }

        /// <summary>Game hours per real second at normal speed, as the game itself computes it.</summary>
        private float NormalSpeed(float cur)
        {
            try
            {
                float normal = _holding ? _normal : Sane(_idleScale) ? _idleScale : cur;
                float k = GameManager.GetTimeOfDayComponent().GetTODHours(1f) * cur;
                return Sane(k) && Sane(normal) ? k / normal : 0f;
            }
            catch { return 0f; }
        }

        private void MeasureClock(float now, bool inGame, byte local)
        {
            if (!inGame) { _mT = 0f; _clockSpeed = 0f; return; }
            float h;
            try { h = GameManager.GetUniStorm().GetElapsedHours(); } catch { return; }
            if (_mT <= 0f) { _mT = now; _mH = h; return; }
            if (now - _mT < 1f) return;
            _clockSpeed = (h - _mH) / (now - _mT); _mT = now; _mH = h;
            // while awake and running: what the clock really does, in case the game gives no usable figure at the next sleep
            if (_holding || local != 0 || !Sane(_idleScale) || !(_clockSpeed > 0.00001f && _clockSpeed < 1f)) return;
            _kSamples[_kCount++ % _kSamples.Length] = _clockSpeed * _idleScale;
            if (_kCount < _kSamples.Length) return;
            // the middle one of the last five: a second in which the clock was jumped to the host's, or in which
            // the speed changed half-way (crafting), does not count
            var sorted = (float[])_kSamples.Clone();
            Array.Sort(sorted);
            _idleK = sorted[sorted.Length / 2];
        }

        // ---------------- every frame ----------------
        public void Update()
        {
            try { Tick(); }
            catch (Exception e) { if (!_loggedError) { _loggedError = true; CoopMod.Log("Shared sleep: " + e); } }
        }

        /// <summary>
        /// End of the frame: a sleep that began during this frame (a click in the rest screen) is taken in hand before
        /// the next frame's clock update, so its speed-up does not run for a frame first.
        /// </summary>
        public void LateUpdate()
        {
            if (_holding || !_hooked) return;
            try { if (!_net.IsConnected || !WorldSync.InGame() || LocalSleep() == 0) return; } catch { return; }
            Update();
        }

        private void Tick()
        {
            float now = Time.realtimeSinceStartup;
            bool inGame = WorldSync.InGame();
            byte local = inGame ? LocalSleep() : (byte)0;
            TimeOfDay tod = null;
            if (inGame) { try { tod = GameManager.GetTimeOfDayComponent(); } catch { } }
            MeasureClock(now, inGame, local);

            bool on = Wanted && _net.IsConnected && SafeShared();
            // nobody else connected: exactly the unmodified game
            bool active = on && tod != null && (_net.Mode == NetMode.Client || _net.Remotes.Count > 0);

            if (_holding && (!active || local == 0)) Release(tod, local, now);
            if (!_holding && active && local != 0) Begin(tod, local, now);
            if (!_holding && local == 0 && tod != null) { try { _idleScale = tod.GetDayLengthScale(); } catch { } }

            if (_net.Mode == NetMode.Host) HostDecide(now, on && _net.Remotes.Count > 0, inGame, local);
            if (!_holding) { Following = false; return; }

            float cur = tod.GetDayLengthScale();
            Observe(cur);
            _left = HoursLeft();
            if (_leave) { Following = false; return; }

            bool host = _net.Mode == NetMode.Host;
            bool all = host ? _all : ClientAll(now);
            byte blocker = host ? _blocker : _hostBlocker;
            float target = _normal;
            if (host) { Following = false; if (all) target = _wanted > 0f ? _wanted : cur; }
            else target = ClientTarget(now, all, cur);
            Apply(tod, cur, target);

            // tell the sleeper what is going on, once per change
            if (_first)
            {
                _first = false; _allSeen = all;
                if (all && !host && _hostFactor <= 0f) CoopMod.Log("You sleep; the host leaves sleep to each game: time speeds up as the game normally does");
                else if (all) { CoopMod.Log("You sleep and so does everyone else: time speeds up"); Hud(L.T("Herkes uyuyor: zaman hızlanıyor", "Everyone is asleep: time speeds up")); }
                else if (blocker == Nobody || blocker == _net.LocalId) CoopMod.Log("You sleep, not everyone does yet: time stays normal");
                else CoopMod.Log($"You sleep, {Who(blocker)} awake: time stays normal");
            }
            else if (all != _allSeen)
            {
                _allSeen = all;
                if (all && !host && _hostFactor <= 0f) CoopMod.Log("The host leaves sleep to each game: time speeds up as the game normally does");
                else if (all) { CoopMod.Log("Everyone sleeps: time speeds up"); Hud(L.T("Herkes uyuyor: zaman hızlanıyor", "Everyone is asleep: time speeds up")); }
                else
                {
                    CoopMod.Log($"{Cap(Who(blocker))} awake: time back to normal");
                    Hud(blocker != Nobody && blocker != _net.LocalId ? L.T($"{Name(blocker)} uyanık: zaman normale döndü", $"{Name(blocker)} is awake: time is back to normal") : L.T("Zaman normale döndü", "Time is back to normal"));
                    _nextNotice = now + 4f;
                }
            }
            Notice(now, !all && !Following, blocker);
        }

        /// <summary>The local player went to sleep (or started passing time) in a shared world.</summary>
        private void Begin(TimeOfDay tod, byte local, float now)
        {
            _holding = true; _leave = false; _kind = local; _wanted = -1f; _lastSet = -1f; _first = true; _allSeen = false;
            _catchUpTo = float.NaN; _beganAt = now; _nextSteer = 0f; Following = false;
            float cur = tod.GetDayLengthScale();
            // normal speed is what the game itself will put back when this sleep ends
            float before = -1f;
            try { before = local == 1 ? GameManager.GetRestComponent().m_DayLengthScaleBeforeSleeping : GameManager.GetPassTime().m_DayLengthScaleOriginal; } catch { }
            _normal = Sane(before) ? before : Sane(_idleScale) ? _idleScale : 1f;
            if (!_loggedNormal && Sane(before) && Sane(_idleScale) && !Close(before, _idleScale))
            {
                _loggedNormal = true;
                CoopMod.Log($"Shared sleep: the game will go back to day length {before:0.####} after this sleep, while awake it was {_idleScale:0.####}; using the game's value");
            }
            if (Faster(cur)) _wanted = cur;
            _rateK = 0f;
            try { _rateK = tod.GetTODHours(1f) * cur; } catch { }
            bool usable = _rateK > 0.0000001f && _rateK < 10f;
            if (usable && _idleK > 0f && (_rateK > 2f * _idleK || _rateK < 0.5f * _idleK))
            {
                // only worth a note: the game's own figure is the one used
                if (!_loggedRate) { _loggedRate = true; CoopMod.Log($"Shared sleep: the game says its clock speed is {_rateK:0.######}, the clock we watched ran at {_idleK:0.######}"); }
            }
            else if (!usable)
            {
                // nothing usable from the game: what we measured, else a day of 2 real hours, the game's standard
                if (!_loggedRate) { _loggedRate = true; CoopMod.Log($"Shared sleep: the game gave no usable clock speed ({_rateK}); using {(_idleK > 0f ? "the measured one" : "the standard 2-hour day")}"); }
                _rateK = _idleK > 0f ? _idleK : _normal * 24f / 7200f;
            }
            _left = HoursLeft();
            string kind = local == 1 ? "sleep" : "pass time";
            if (local == 2)
            {
                // Pass time must count game time for a held one to make sense. If this build counts real seconds instead,
                // holding the clock would end it after seconds with no time passed: leave such a pass-time alone.
                try
                {
                    var p = GameManager.GetPassTime();
                    double ms = p.m_PassTimeDurationMilliseconds, gameMs = _left * 3600000.0, realMs = _left * p.m_PassTimeRealtimeSecondsPerHour * 1000.0;
                    if (_left > 0.01f && Math.Abs(ms - gameMs) > 0.5 * gameMs && Math.Abs(ms - realMs) < 0.25 * realMs)
                    {
                        _leave = true;
                        CoopMod.Log($"Shared sleep: this game measures pass time in real seconds ({ms:0} ms for {_left:0.00} h): it cannot be held, it runs as before");
                    }
                }
                catch { }
            }
            CoopMod.Log($"Shared sleep: you {kind} for {_left:0.00} h (day length now {cur:0.######}, normal {_normal:0.######}"
                + (_wanted > 0f ? $", the game asks for {_normal / _wanted:0}x speed)" : ", no speed-up asked yet)"));
        }

        /// <summary>The game changed the day length behind our back: remember what it wants.</summary>
        private void Observe(float cur)
        {
            if (_lastSet > 0f && Close(cur, _lastSet)) return;
            if (!Faster(cur)) return;
            if (_wanted > 0f && Close(cur, _wanted)) return;
            // our hook should have seen this call; if it did not, holding the speed every frame still works
            if (_lastSet > 0f && !_loggedHook) { _loggedHook = true; CoopMod.Log("Shared sleep: the game changed its day length without passing the hook; holding it every frame instead"); }
            _wanted = cur;
        }

        private void Apply(TimeOfDay tod, float cur, float target)
        {
            if (!Sane(target)) return;
            if (!Close(cur, target))
            {
                _own = true;
                try { tod.SetDayLengthScale(target); } finally { _own = false; }
            }
            _lastSet = target;
        }

        /// <summary>The local sleep is over, or this is no longer a shared world.</summary>
        private void Release(TimeOfDay tod, byte local, float now)
        {
            _holding = false; Following = false; _catchUpTo = float.NaN;
            bool wasFast = _allSeen; _allSeen = false; _first = false;
            ClearNotice();
            if (tod == null) return; // left the world while asleep: nothing of the game left to set
            float cur;
            try { cur = tod.GetDayLengthScale(); } catch { return; }
            if (local != 0)
            {
                // still asleep but on our own now (the others left, the connection dropped, or the setting was switched off):
                // from here on exactly what the game would do alone
                if (!_leave && _wanted > 0f && !Close(cur, _wanted)) Apply(tod, cur, _wanted);
                CoopMod.Log("Nobody to wait for any more: your sleep speeds time up as the game normally does");
                return;
            }
            // the game puts normal speed back itself when a sleep ends; never leave a speed-up of ours behind
            if (!_leave && _lastSet > 0f && Close(cur, _lastSet) && Faster(cur))
            {
                Apply(tod, cur, _normal);
                CoopMod.Log("Shared sleep: the sleep ended while time was still fast; set back to normal");
            }
            string what = _kind == 2 ? "pass time" : "sleep";
            string how = _left > 0.05f ? $"Your {what} ended early with {_left:0.00} h left" : $"Your {what} is over";
            CoopMod.Log(how + (wasFast ? ": time back to normal" : "") + $" (it took {now - _beganAt:0} s)");
        }

        // ---------------- host: who is "everybody" ----------------
        private void HostDecide(float now, bool on, bool inGame, byte local)
        {
            if (!on)
            {
                // switched off or nobody connected while clients may still believe "everyone sleeps": tell them once
                if (_all) { _all = false; _blocker = Nobody; Say(now, false, Nobody, 1f, inGame); }
                _blocker = Nobody;
                // Shared sleep is off on this host. A client that has it on would wait for a word that never comes:
                // tell sleeping clients to run their own speed, which is how it worked before.
                if (!Wanted && _net.Remotes.Count > 0 && now >= _nextSay)
                {
                    bool someone = false;
                    foreach (var r in _players.Snapshot()) if (r.HasState && r.Sleep != 0 && r.Age <= StaleAfter) someone = true;
                    if (someone) Say(now, true, Nobody, 0f, inGame);
                }
                return;
            }
            bool all = Decide(local, inGame, out byte blocker, out bool anySleeper);
            // the speed the clients should run: the host's own, as its game picked it (0 = the host has no clock running)
            float factor = 1f;
            if (all) factor = !inGame ? 0f : _holding && _wanted > 0f ? _normal / _wanted : 1f;
            bool changed = all != _all || blocker != _blocker || (all && (_saidFactor < 0f || Mathf.Abs(factor - _saidFactor) > 0.01f * Mathf.Max(1f, factor)));
            if (all != _all && !_holding)
                CoopMod.Log(all ? "Everyone in the world sleeps: time speeds up for them" : $"{Cap(Who(blocker))} awake: time back to normal for the sleepers");
            _all = all; _blocker = blocker;
            if (changed || (anySleeper && now >= _nextSay)) Say(now, all, blocker, factor, inGame);
        }

        private bool Decide(byte local, bool inGame, out byte blocker, out bool anySleeper)
        {
            blocker = Nobody; anySleeper = false; bool all = true;
            if (inGame)
            {
                if (local != 0) anySleeper = true; else { all = false; blocker = _net.LocalId; }
            }
            foreach (var r in _players.Snapshot())
            {
                if (!r.HasState || r.Age > GoneAfter) continue; // not in the world: main menu, still joining
                if (r.Sleep != 0) anySleeper = true;
                // away in a menu but not asleep is awake; so is somebody we have not heard from for a few seconds (loading a region)
                if (r.Sleep == 0 || r.Age > StaleAfter) { all = false; if (blocker == Nobody) blocker = r.Id; }
            }
            return all && anySleeper;
        }

        private void Say(float now, bool all, byte blocker, float factor, bool inGame)
        {
            _nextSay = now + 1f; _saidFactor = all ? factor : -1f;
            float hours = 0f;
            if (inGame) { try { hours = GameManager.GetUniStorm().GetElapsedHours(); } catch { } }
            var w = _net.Begin(Msg.TimeAccel);
            w.Put(all); w.Put(factor); w.Put(hours); w.Put(blocker);
            // always the same ordered channel: an old "everyone sleeps" must never arrive after the "stop"
            _net.Send(w, DeliveryMethod.ReliableOrdered);
        }

        // ---------------- client: follow the host ----------------
        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (msg != Msg.TimeAccel || _net.Mode != NetMode.Client) return;
            bool on = r.GetBool(); float factor = r.GetFloat(), hours = r.GetFloat(); byte blocker = r.GetByte();
            float now = Time.realtimeSinceStartup;
            if (on) { _catchUpTo = float.NaN; _chaseFactor = factor; }
            else if (_hostOn && Following && _hostFactor > 0f)
            {
                // the host stopped at this clock value: we run on until our own clock is there too, then stop
                _catchUpTo = hours; _catchUpSince = now;
            }
            if (on || _hostOn) { _hostClock = hours; _hostClockAt = now; }
            if (on != _hostOn) _nextSteer = 0f; // a change of plan is acted on at once
            _hostOn = on; _hostFactor = factor; _hostBlocker = blocker; _hostSaidAt = now;
        }

        private bool ClientAll(float now) => _hostOn && now - _hostSaidAt < HostWordFor;

        /// <summary>The day length a sleeping client should run right now.</summary>
        private float ClientTarget(float now, bool all, float cur)
        {
            bool catchUp = !all && !float.IsNaN(_catchUpTo);
            if (!all && !catchUp) { Following = false; return _normal; }
            if (all && _hostFactor <= 0f)
            {
                // the host is not in the world itself, so there is no clock to follow: our own speed, as the game picked it
                Following = false;
                return _wanted > 0f ? _wanted : cur;
            }
            float mine;
            try { mine = GameManager.GetUniStorm().GetElapsedHours(); } catch { Following = false; return _normal; }
            float kN = _rateK / _normal;                        // game hours per real second at normal speed
            float hostSpeed = kN * Mathf.Max(1f, all ? _hostFactor : _chaseFactor);
            float want;
            if (all)
            {
                // where the host's clock should be by now; without fresh news we stop guessing and wait at the last known value
                float age = now - _hostClockAt;
                float aim = _hostClock + hostSpeed * Mathf.Min(age, 0.4f);
                want = (age < 0.4f ? hostSpeed : 0f) + (aim - mine) / ChaseSeconds;
            }
            else
            {
                float gap = _catchUpTo - mine;
                // there (to within a few game seconds), or it takes too long: done
                if (gap <= kN * 0.25f || now - _catchUpSince > 5f) { _catchUpTo = float.NaN; Following = false; return _normal; }
                want = gap / CatchUpSeconds;
            }
            Following = true;
            // while the host runs fast a few changes a second are enough, and kinder to the game's own timers than one every frame
            if (all && now < _nextSteer && _lastSet > 0f && Faster(_lastSet)) return _lastSet;
            _nextSteer = now + 0.2f;
            want = Mathf.Clamp(want, kN, hostSpeed * 3f);
            return _rateK / want;
        }

        // ---------------- the hook ----------------
        /// <summary>Runs before every TimeOfDay.SetDayLengthScale call, the game's and our own.</summary>
        internal void BeforeSetDayLengthScale(ref float scale)
        {
            if (_own) return;
            HookCalls++;
            if (!_holding || _leave) return;        // not our business: awake, alone, or switched off
            if (!Faster(scale)) return;             // back to normal (the sleep is ending) is always fine
            if (_lastSet > 0f && Close(scale, _lastSet)) return; // the game only repeats what is already set
            // the game asks for a speed-up during a sleep we hold: remember it, allow only what the rule allows now
            _wanted = scale;
            if (_lastSet > 0f) scale = _lastSet;
        }

        // ---------------- getting up ----------------
        /// <summary>The player's wake key (or the harness): end the sleep the game's own way. Null = done, else why not.</summary>
        internal string WakeUp(string why)
        {
            try
            {
                var rest = GameManager.GetRestComponent();
                if (rest != null && rest.IsSleeping())
                {
                    CoopMod.Log($"Getting up ({why}) with {HoursLeft():0.00} h of sleep left");
                    rest.EndSleeping(true); // the game's own "sleep was interrupted" path
                    return null;
                }
                var pt = GameManager.GetPassTime();
                if (pt != null && pt.IsPassingTime())
                {
                    CoopMod.Log($"Stopping pass time ({why}) with {HoursLeft():0.00} h left");
                    // the rest screen's own Cancel button first, so the screen tidies itself up
                    try { var panel = InterfaceManager.GetPanel<Panel_Rest>(); if (panel != null && panel.IsEnabled()) panel.OnCancel(); } catch { }
                    if (pt.IsPassingTime()) pt.End();
                    return null;
                }
                return "not sleeping or passing time";
            }
            catch (Exception e) { return "the game refused: " + e.Message; }
        }

        /// <summary>F8: only while we are the ones keeping the sleeper waiting (alone, the game has no such key).</summary>
        internal void WakeKey()
        {
            if (!_holding) return;
            string no = WakeUp(WakeKeyName + " pressed");
            if (no != null) CoopMod.Log("Wake key: " + no);
        }

        // ---------------- the line on screen ----------------
        private static void Hud(string s)
        {
            try { HUDMessage.AddMessage(s, 5f, false, true); } catch { }
        }

        private string Name(byte id) => _net.Remotes.TryGetValue(id, out var info) && !string.IsNullOrEmpty(info.Name) ? info.Name : L.T($"Oyuncu {id}", $"Player {id}");
        private string Who(byte id) => id == Nobody ? "somebody is" : id == _net.LocalId ? "you are" : $"player {id} is";
        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>Another player went to sleep: whoever is still up should know that the sleeper is waiting for them.</summary>
        internal void RemoteSleepChanged(byte id, byte sleep)
        {
            try
            {
                if (sleep == 0 || !Wanted || !_net.IsConnected || !WorldSync.InGame() || LocalSleep() != 0) return;
                Hud(L.T($"{Name(id)} {(sleep == 2 ? "zaman geçiriyor" : "uyuyor")}. Sen de uyursan zaman hızlanır.", $"{Name(id)} is {(sleep == 2 ? "passing time" : "asleep")}. If you sleep too, time speeds up."));
            }
            catch { }
        }

        /// <summary>Test harness only: ways to make the waiting sleeper's screen readable (0 = as built).</summary>
        internal static int TestUiMode;
        internal static float TestUiAlpha = 0.8f;

        private static void UiDump(Action<string, string> line)
        {
            try { line("ui_fade", $"alpha={CameraFade.CurrentAlpha:0.00} fading={CameraFade.IsFading} out={CameraFade.IsFadedOut} out_threshold={CameraFade.FADE_OUT_THRESHOLD:0.000}"); } catch (Exception e) { line("ui_fade", "? " + e.Message); }
            try { var tod = GameManager.GetTimeOfDayComponent(); line("ui_timelapse", $"active={tod.IsTimeLapseActive()} counter={tod.m_IsTimeLapseActiveCounter}"); } catch (Exception e) { line("ui_timelapse", "? " + e.Message); }
            try
            {
                var hud = InterfaceManager.GetPanel<Panel_HUD>();
                line("ui_hud", $"enabled={hud.IsEnabled()} faded_for_sleep={hud.m_WasFadedDueToSleep}");
                var pop = hud.m_AccelTimePopup;
                string G(GameObject g) { try { return g == null ? "null" : $"{g.activeSelf}/{g.activeInHierarchy}"; } catch { return "?"; } }
                var lab = pop.m_Label_Message;
                line("ui_popup", $"active={pop.IsActive()} go={G(pop.gameObject)} resting={G(pop.m_RestingObject)} action={G(pop.m_ActionObject)} back={G(pop.m_BackButtonObject)} suppressed={pop.IsPassTimeUISuppressed()} msg_on={pop.IsLabelMessageActive()}");
                line("ui_popup_label", lab == null ? "null" : $"go={G(lab.gameObject)} alpha={lab.alpha:0.00} final={lab.finalAlpha:0.00} text='{lab.text}'");
                try { var el = pop.m_ElementToFade; line("ui_popup_fade_element", el == null ? "null" : $"alpha={el.alpha:0.00} final={el.finalAlpha:0.00} go={G(el.gameObject)}"); } catch { }
            }
            catch (Exception e) { line("ui_popup", "? " + e.Message); }
        }

        /// <summary>Every frame while the local sleeper is kept waiting (test modes only).</summary>
        private void TestUiFrame()
        {
            if (TestUiMode == 0) return;
            try
            {
                if ((TestUiMode & 1) != 0)
                {
                    var hud = InterfaceManager.GetPanel<Panel_HUD>();
                    var pop = hud != null ? hud.m_AccelTimePopup : null;
                    if (pop != null)
                    {
                        if (!pop.IsActive()) pop.SetActive(true, false);
                        try { if (pop.m_RestingObject != null && !pop.m_RestingObject.activeSelf) pop.m_RestingObject.SetActive(true); } catch { }
                        pop.EnableLabelMessage(true);
                        var lab = pop.m_Label_Message;
                        if (lab != null && !string.IsNullOrEmpty(_noticeText)) { lab.text = _noticeText; lab.alpha = 1f; }
                    }
                }
                if ((TestUiMode & 2) != 0) { if (CameraFade.CurrentAlpha > TestUiAlpha) CameraFade.CurrentAlpha = TestUiAlpha; }
            }
            catch (Exception e) { if (!_loggedPopup) { _loggedPopup = true; CoopMod.Log("Sleep screen test: " + e.Message); } }
        }

        /// <summary>
        /// The game hides its whole HUD, and with it our line, while the screen is fully faded out. A sleeper who is
        /// kept waiting would stare at a silent black screen (seen in the 0.7.2 test). So the fade is held a hair
        /// light: the screen stays black (the world is not drawn while asleep), but the HUD line can be read.
        /// </summary>
        private void KeepHudReadable()
        {
            if ((TestUiMode & 4) != 0) return; // test: as it was before
            try
            {
                // The HUD is drawn under the fade, so it is as dim as the fade is dark (0.77 was barely readable); the
                // world itself is not drawn during a sleep, so a light fade still shows a black screen (0.7.4 test).
                const float cap = 0.3f;
                if (CameraFade.CurrentAlpha > cap) CameraFade.CurrentAlpha = cap;
            }
            catch (Exception e) { if (!_loggedFade) { _loggedFade = true; CoopMod.Log("Shared sleep: could not keep the sleep screen readable: " + e.Message); } }
        }
        private bool _loggedFade;

        private void Notice(float now, bool waiting, byte blocker)
        {
            if (waiting) { KeepHudReadable(); TestUiFrame(); }
            if (!waiting) { ClearNotice(); return; }
            string who = blocker != Nobody && blocker != _net.LocalId ? Name(blocker) + L.T(" uyanık. ", " is awake. ") : "";
            string text = who + L.T($"Herkes uyuyunca zaman hızlanacak. Kalkmak için {WakeKeyName}'e bas.", $"Time speeds up once everyone sleeps. Press {WakeKeyName} to get up.");
            bool changed = text != _noticeText;
            if (!changed && now < _nextNotice) return;
            _noticeText = text; _nextNotice = now + 8.5f; // the line stays 9 s: no gap between two showings
            // the game's own message line (may be hidden behind the sleep screen) ...
            try { HUDMessage.AddMessage(text, 9f, false, true); NoticeOnHud = true; } catch { NoticeOnHud = false; }
            // ... and the label of the "time is passing" box, which the sleep screen itself shows
            if (changed) ShowOnPopup(text);
        }

        private void ShowOnPopup(string text)
        {
            try
            {
                var hud = InterfaceManager.GetPanel<Panel_HUD>();
                var pop = hud != null ? hud.m_AccelTimePopup : null;
                if (pop == null) return;
                var label = pop.m_Label_Message;
                if (!_popupTouched)
                {
                    _popupTouched = true;
                    try { _popupWasOn = pop.IsLabelMessageActive(); } catch { _popupWasOn = false; }
                    try { _popupOldText = label != null ? label.text : null; } catch { _popupOldText = null; }
                }
                pop.SetLabelMessageLocIdAndDuration(text, 3600f, 0.5f, 0f); // not a translation key: shown as it is
                if (label != null) label.text = text;
                pop.EnableLabelMessage(true);
                NoticeOnPopup = true;
            }
            catch (Exception e)
            {
                NoticeOnPopup = false;
                if (!_loggedPopup) { _loggedPopup = true; CoopMod.Log("Shared sleep: could not write on the sleep screen: " + e.Message); }
            }
        }

        private void ClearNotice()
        {
            _noticeText = null; _nextNotice = 0f; NoticeOnHud = false; NoticeOnPopup = false;
            if (!_popupTouched) return;
            _popupTouched = false;
            try
            {
                var hud = InterfaceManager.GetPanel<Panel_HUD>();
                var pop = hud != null ? hud.m_AccelTimePopup : null;
                if (pop == null) return;
                var label = pop.m_Label_Message;
                if (label != null) label.text = _popupOldText ?? "";
                pop.EnableLabelMessage(_popupWasOn);
            }
            catch { }
        }
    }

    // Applied by hand (SleepSync.ApplyPatches): if the game ever changes this method only shared sleep is lost.
    internal static class SleepPatches
    {
        private static bool _logged;
        internal static void SetDayLengthScalePrefix(ref float scale)
        {
            try { CoopMod.Instance?.Sleep?.BeforeSetDayLengthScale(ref scale); }
            catch (Exception e) { if (!_logged) { _logged = true; CoopMod.Log("Shared sleep hook: " + e.Message); } }
        }
    }
}
