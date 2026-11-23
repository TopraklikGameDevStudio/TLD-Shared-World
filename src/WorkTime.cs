using System;
using Il2Cpp;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// Jobs take their time in a shared world. Alone, harvesting a carcass, breaking down a crate, crafting, reading,
    /// repairing... fast-forward the clock: ten minutes go by in a few seconds. With friends there is one clock, so
    /// one player's job must not move everybody's time (the host's used to), nor happen "for free" (a friend's used to:
    /// their clock was simply set back afterwards).
    ///
    /// How it works, the same for every job the game has, without touching any of them: the job runs as the game
    /// always runs it (its clock speeds up, hunger, cold and tiredness add up for the time it takes). When it is done,
    /// this player's clock is ahead of the shared one by the job's time. Then the player stays at the job (cannot
    /// walk away, the friends see them kneel) and this game's clock stands still until the shared clock has caught up.
    /// F8 ends the wait early; the job's time is then simply not counted for the world (the old way).
    ///
    /// The shared clock: on the host its own clock, except while the host itself is at a job (then it runs on at
    /// normal speed from where it was); on a client the host's clock as last heard, carried on at normal speed.
    /// Sleeping and passing time are not touched here: that is the shared sleep's business.
    /// </summary>
    internal sealed class WorkTime
    {
        internal static WorkTime Instance;
        private const float Tolerance = 0.05f;     // hours (3 game minutes) of difference that are left alone
        private const float GiveUpAhead = 8f;      // hours: more than this ahead is not a job, the old fix applies
        private const float CalmAfter = 1.5f;      // real seconds of normal speed before a job counts as finished

        private readonly Network _net;
        private readonly Func<bool> _on, _shared;

        private float _prevLocal = -1f, _prevAt;
        private bool _accel; private float _calmSince = -1f;
        private float _winStep, _winDt;
        private float _sharedHours = -1f;          // the world's clock as this game sees it
        private float _hostHours = -1f, _hostAt;   // client: last time the host spoke

        internal bool Busy;
        private float _savedScale = -1f, _maxScale = -1f;
        private bool _lockedByUs;
        private float _nextHud;
        private bool _allowSnap;                   // client: let the clock be set back once (F8, or too far ahead)
        private bool _waitForSync;                 // client: after F8, no new wait before the host's clock has been taken

        // ---- for the state dump ----
        internal float RatePerSecond;              // game hours per real second at normal speed
        internal int Jobs, Waited, Aborted;
        internal float LastAheadMin, LeftSeconds;
        internal string Last = "-";
        private readonly System.Collections.Generic.Queue<string> _trace = new();
        private float _traceUntil, _nextTrace;
        private void Trace(string line)
        {
            _trace.Enqueue($"{Time.realtimeSinceStartup:0.00} {line}");
            while (_trace.Count > 160) _trace.Dequeue();
        }

        public WorkTime(Network net, Func<bool> on, Func<bool> shared)
        {
            Instance = this; _net = net; _on = on; _shared = shared;
        }

        // ---------------- the job itself takes its time ----------------
        // Most jobs run a progress bar for a few real seconds while the game's clock races through their minutes.
        // In a shared world the bar is stretched so that the clock runs at its normal speed through the job: the job
        // takes as long as its minutes really last, nobody's clock gets ahead, nothing to wait out afterwards.
        // Jobs whose bar cannot be stretched here still use the wait above.
        internal int Stretched; internal float LastStretchFrom, LastStretchTo;
        internal static bool InBreakDown;

        internal static void ApplyPatches(HarmonyLib.Harmony h)
        {
            int ok = 0, all = 0;
            void P(Type t, string name, Type[] args, string prefix, string postfix = null)
            {
                all++;
                try
                {
                    var m = HarmonyLib.AccessTools.Method(t, name, args);
                    if (m == null) throw new MissingMethodException(name);
                    h.Patch(m, prefix: prefix != null ? new HarmonyLib.HarmonyMethod(typeof(WorkTimePatches), prefix) : null,
                               postfix: postfix != null ? new HarmonyLib.HarmonyMethod(typeof(WorkTimePatches), postfix) : null);
                    ok++;
                }
                catch (Exception e) { CoopMod.Log($"Job time: could not hook {t.Name}.{name} ({e.Message})"); }
            }
            var gp = typeof(Panel_GenericProgressBar);
            foreach (var m in gp.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (m.Name != nameof(Panel_GenericProgressBar.Launch)) continue;
                var ps = m.GetParameters();
                if (ps.Length < 3 || ps[1].ParameterType != typeof(float) || ps[2].ParameterType != typeof(float)) continue;
                all++;
                try { h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(WorkTimePatches), nameof(WorkTimePatches.LaunchPrefix))); ok++; }
                catch (Exception e) { CoopMod.Log($"Job time: could not hook a progress bar ({e.Message})"); }
            }
            P(typeof(Panel_BreakDown), nameof(Panel_BreakDown.OnBreakDown), Type.EmptyTypes, nameof(WorkTimePatches.BreakDownPrefix), nameof(WorkTimePatches.BreakDownPostfix));
            P(typeof(TimeOfDay), nameof(TimeOfDay.Accelerate), new[] { typeof(float), typeof(float), typeof(bool) }, nameof(WorkTimePatches.AcceleratePrefix));
            CoopMod.Log($"Job time: {ok} of {all} hooks in place (jobs take their real time in a shared world)");
        }

        /// <summary>Real seconds a job of these game minutes lasts at the clock's normal speed; 0 = leave it as the game made it.</summary>
        internal float StretchSeconds(float gameMinutes, float realSeconds)
        {
            try
            {
                if (!Active() || SleepSync.LocalSleep() != 0 || gameMinutes <= 0f) return 0f;
                float r0 = RatePerSecond > 0f ? RatePerSecond : 0f;
                if (r0 <= 0f) { try { float day = GameManager.GetTimeOfDayComponent().GetDayLengthSeconds(); if (day > 60f) r0 = 24f / day; } catch { } }
                if (r0 <= 0f) return 0f;
                float want = gameMinutes / 60f / r0;
                if (want <= realSeconds * 1.05f || want > 7200f) return 0f;
                return want;
            }
            catch { return 0f; }
        }

        internal void Note(string what, float from, float to)
        {
            Stretched++; LastStretchFrom = from; LastStretchTo = to;
            Last = $"{what}: {from:0.0} s stretched to {to:0} s (the job's real time)";
            CoopMod.Log("Job time: " + Last);
        }

        private bool Active()
        {
            try
            {
                if (!_on() || !WorldSync.InGame() || WorldSync.Loading()) return false;
                if (_net.Mode == NetMode.Host) return _net.Remotes.Count > 0;
                return _net.Mode == NetMode.Client && _net.IsConnected && _shared();
            }
            catch { return false; }
        }

        /// <summary>Client: the host's clock arrived (every second).</summary>
        internal void HostClock(float hours) { _hostHours = hours; _hostAt = Time.realtimeSinceStartup; }

        /// <summary>Client: may the time sync set this game's clock back to the host's now? Not while a job's time is being waited out.</summary>
        internal bool HoldsAhead()
        {
            if (_allowSnap) { _allowSnap = false; return false; }
            if (!Active()) return false;
            if (!Busy && !_accel && _prevLocal >= 0f && RatePerSecond > 0f)
            {
                try
                {
                    float local = GameManager.GetUniStorm().GetElapsedHours(), dt = Time.realtimeSinceStartup - _prevAt;
                    float sc0 = GameManager.GetTimeOfDayComponent().GetDayLengthScale();
                    if (_maxScale > 0f && sc0 < _maxScale * 0.5f) { _accel = true; _calmSince = -1f; Last = "a job is speeding up the clock (seen by the time sync)"; }
                    if (dt > 0f && local - _prevLocal > dt * RatePerSecond * 4f && local - _prevLocal > 0.00002f) { _accel = true; _calmSince = -1f; Last = "a job started speeding up the clock (seen by the time sync)"; }
                }
                catch { }
            }
            Trace($"time sync asks: hold={Busy || _accel} (busy={Busy} accel={_accel})");
            return Busy || _accel;
        }

        /// <summary>Client: the time sync has just set this game's clock: start measuring afresh (that jump was no job).</summary>
        internal void ClockSetBySync()
        {
            Trace("clock set by the time sync" + (Busy ? " (while busy!)" : ""));
            _prevLocal = -1f; _waitForSync = false;
            if (!Busy) _accel = false;
        }

        /// <summary>Host: the clock the friends are told (its own, unless the host is at a job).</summary>
        internal void SharedForSending(ref float hours, ref float norm, ref int day)
        {
            if (_net.Mode != NetMode.Host || !(Busy || _accel) || _sharedHours < 0f) return;
            float back = hours - _sharedHours;
            if (back <= 0f || back > GiveUpAhead) return;
            hours = _sharedHours;
            norm -= back / 24f;
            while (norm < 0f) { norm += 1f; day--; }
        }

        /// <summary>The friends see a player who is waiting out a job kneel at it.</summary>
        internal bool Kneeling => Busy;

        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (!Active() || SleepSync.LocalSleep() != 0)
            {
                if (Busy) EndBusy("not playing together any more, or sleeping");
                _accel = false; _prevLocal = -1f; _sharedHours = -1f;
                return;
            }
            UniStormWeatherSystem uni; TimeOfDay tod;
            try { uni = GameManager.GetUniStorm(); tod = GameManager.GetTimeOfDayComponent(); } catch { return; }
            if (uni == null || tod == null) return;
            float local = uni.GetElapsedHours();

            // normal speed: the slowest the clock has run while we were not holding it (a job's speed-up only ever makes
            // it faster, so the smallest rate seen is the normal one); the normal day-length scale is the largest seen
            float sc = 1f;
            try { sc = tod.GetDayLengthScale(); } catch { }
            if (!Busy)
            {
                if (sc > _maxScale && sc < 1000f) _maxScale = sc;
                try
                {
                    float day = tod.GetDayLengthSeconds();
                    if (day > 60f && day < 1e7f) { float cand = 24f / day; RatePerSecond = RatePerSecond > 0f ? Mathf.Min(RatePerSecond, cand) : cand; }
                }
                catch { }
            }
            bool scaleNormal = _maxScale <= 0f || sc >= _maxScale * 0.999f;
            float r0 = RatePerSecond > 0f ? RatePerSecond : 24f / 4320f;

            if (_prevLocal < 0f) { _prevLocal = local; _prevAt = now; _sharedHours = local; return; }
            float dt = now - _prevAt; if (dt <= 0f) return;
            float step = local - _prevLocal;

            // is the clock running fast right now (a job passing time)?
            // (the game's jobs make the day shorter: the scale is the sure sign; the measured speed catches anything else.
            // At 170 frames a second one frame of a 16x speed-up is only 0.0003 h, so no big fixed margin here)
            // the speed is judged over a quarter of a second: single frames are uneven (a hitch looks like a speed-up)
            _winStep += step; _winDt += dt;
            bool fastByRate = false;
            if (_winDt >= 0.25f) { fastByRate = _winStep > _winDt * r0 * 4f; _winStep = 0f; _winDt = 0f; }
            bool fastNow = !Busy && ((_maxScale > 0f && sc < _maxScale * 0.5f) || fastByRate);
            bool wasAccel = _accel;
            if (fastNow)
            {
                if (!_accel) Last = $"a job started speeding up the clock at {now:0.0} s";
                _accel = true; _calmSince = -1f;
            }
            else if (_accel)
            {
                if (_calmSince < 0f) _calmSince = now;
                if (now - _calmSince > CalmAfter) _accel = false;
            }

            // the world's clock
            if (_net.Mode == NetMode.Host)
            {
                if (!wasAccel && !_accel && !Busy) _sharedHours = local;   // (not in the frame the job ends: that would hide its time)
                else _sharedHours += dt * r0;
            }
            else if (_hostAt > 0f)
                _sharedHours = _hostHours + (now - _hostAt) * r0;

            float ahead = local - _sharedHours;
            if (_waitForSync && ahead <= Tolerance) _waitForSync = false;
            if (fastNow) _traceUntil = now + 15f;
            if (now < _traceUntil && now >= _nextTrace)
            {
                _nextTrace = now + 0.2f;
                Trace(FormattableString.Invariant($"local={local:0.0000} shared={_sharedHours:0.0000} ahead_min={ahead * 60f:0.0} step/dt={(step / dt) * 60f:0.00}min/s r0={r0 * 60f:0.000} sc={sc:0.0000} max={_maxScale:0.0000} accel={_accel} fast={fastNow} busy={Busy} calm={(_calmSince > 0 ? now - _calmSince : -1):0.0}"));
            }
            if (!Busy && !_accel && scaleNormal && !_waitForSync && ahead > Tolerance)
            {
                if (ahead < GiveUpAhead) StartBusy(tod, ahead, r0);
                else if (_net.Mode == NetMode.Client) { _allowSnap = true; Last = $"{ahead:0.0} h ahead of the host: not a job, the clock is set back"; }
            }
            if (Busy)
            {
                LeftSeconds = Mathf.Max(0f, ahead / r0);
                if (ahead <= 0.002f) { Waited++; EndBusy("the world's clock caught up"); }
                else
                {
                    KeepLocked();
                    if (now >= _nextHud)
                    {
                        _nextHud = now + 6f;
                        int s = Mathf.CeilToInt(LeftSeconds);
                        Hud(L.T($"İş sürüyor ({s / 60}:{s % 60:00} kaldı). F8: beklemeden bitir.", $"Still working ({s / 60}:{s % 60:00} left). F8: finish without waiting."));
                    }
                }
            }
            _prevLocal = uni.GetElapsedHours(); _prevAt = now;
        }

        private void StartBusy(TimeOfDay tod, float ahead, float r0)
        {
            Busy = true; Jobs++;
            LastAheadMin = ahead * 60f;
            LeftSeconds = ahead / r0;
            try { _savedScale = _maxScale > 0f ? _maxScale : tod.GetDayLengthScale(); tod.SetDayLengthScale(_savedScale * 100000f); } catch (Exception e) { _savedScale = -1f; CoopMod.Log("Job time: could not hold the clock: " + e.Message); }
            KeepLocked();
            _nextHud = 0f;
            Last = $"job of {LastAheadMin:0} game min done: waiting {LeftSeconds:0} s for the world's clock";
            CoopMod.Log("Job time: " + Last);
        }

        private void KeepLocked()
        {
            try
            {
                var pm = GameManager.GetPlayerManagerComponent();
                if (pm == null) return;
                if (CoopMod.Instance?.Downed?.LocalDowned == true) return;
                if (pm.GetControlMode() == PlayerControlMode.Normal) { pm.SetControlMode(PlayerControlMode.Locked); _lockedByUs = true; }
            }
            catch { }
        }

        private void EndBusy(string why)
        {
            Busy = false; LeftSeconds = 0f;
            try
            {
                var tod = GameManager.GetTimeOfDayComponent();
                if (tod != null && _savedScale > 0f) tod.SetDayLengthScale(_savedScale);
            }
            catch { }
            _savedScale = -1f;
            try
            {
                var pm = GameManager.GetPlayerManagerComponent();
                if (_lockedByUs && pm != null && pm.GetControlMode() == PlayerControlMode.Locked && CoopMod.Instance?.Downed?.LocalDowned != true)
                    pm.SetControlMode(PlayerControlMode.Normal);
            }
            catch { }
            _lockedByUs = false;
            Last = "wait over: " + why;
            CoopMod.Log("Job time: " + Last);
            if (why.StartsWith("the world")) Hud(L.T("İş bitti.", "Done."));
        }

        /// <summary>F8 (or the harness): stop waiting. The job's time then does not count for the world.</summary>
        internal bool Abort()
        {
            if (!Busy) return false;
            Aborted++;
            EndBusy("F8: not waited out");
            if (_net.Mode == NetMode.Client) { _allowSnap = true; _waitForSync = true; }
            else _sharedHours = GameManager.GetUniStorm().GetElapsedHours(); // the host's clock is the world's again (the friends jump with it)
            return true;
        }

        private static void Hud(string s) { try { HUDMessage.AddMessage(s, 5f, false, true); } catch { } }

        internal void WriteState(System.Text.StringBuilder sb)
        {
            bool on = false; try { on = _on(); } catch { }
            sb.Append("work_setting: ").Append(on ? "on" : "off").Append(" active=").Append(Active()).Append('\n');
            sb.Append("work_rate: ").Append(FormattableString.Invariant($"{RatePerSecond * 60f:0.000} game min per real s (day {(RatePerSecond > 0 ? 24f / RatePerSecond / 60f : 0):0.0} real min)")).Append('\n');
            float ahead = 0f; try { ahead = (GameManager.GetUniStorm().GetElapsedHours() - _sharedHours) * 60f; } catch { }
            sb.Append("work_now: ").Append(FormattableString.Invariant($"accel={_accel} busy={Busy} ahead_min={ahead:0.0} left_s={LeftSeconds:0}")).Append('\n');
            sb.Append("work_counts: ").Append($"jobs={Jobs} waited={Waited} aborted={Aborted} last_job_min={LastAheadMin:0}").Append('\n');
            sb.Append("work_last: ").Append(Last).Append('\n');
            foreach (var t in _trace) sb.Append("work_trace: ").Append(t).Append('\n');
        }
    }

    /// <summary>Applied by hand (WorkTime.ApplyPatches).</summary>
    internal static class WorkTimePatches
    {
        public static void LaunchPrefix(ref float __1, float __2)
        {
            try
            {
                var w = WorkTime.Instance; if (w == null) return;
                float s = w.StretchSeconds(__2, __1);
                if (s <= 0f) return;
                w.Note("progress bar", __1, s);
                __1 = s;
            }
            catch { }
        }

        public static void BreakDownPrefix() { WorkTime.InBreakDown = true; }
        public static void BreakDownPostfix() { WorkTime.InBreakDown = false; }

        /// <summary>Breaking things down sets its bar's length itself and then speeds the clock up: stretch both together.</summary>
        public static void AcceleratePrefix(ref float __0, float __1)
        {
            if (!WorkTime.InBreakDown) return;
            try
            {
                var w = WorkTime.Instance; if (w == null) return;
                float s = w.StretchSeconds(__1 * 60f, __0);
                if (s <= 0f) return;
                try { var p = InterfaceManager.GetPanel<Panel_BreakDown>(); if (p != null) p.m_SecondsToBreakDown = s; } catch { }
                w.Note("breaking down", __0, s);
                __0 = s;
            }
            catch { }
        }
    }
}
