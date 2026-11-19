using System;
using Il2Cpp;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>Stage 1 world sync: shared clock and shared weather (host is the source of truth).</summary>
    internal sealed class WorldSync
    {
        private readonly Network _net;
        private float _nextTime, _nextWeather;
        private WeatherStage _lastSentStage = WeatherStage.Undefined;
        private float _lastFullWeatherSend;
        private bool _wasFast;

        // client side
        private WeatherStage _hostStage = WeatherStage.Undefined;
        private float _lastWeatherApply;
        private string _lastWeatherText = "";
        private float _lastHostHours = -1f; private int _backwards;

        public WorldSync(Network net)
        {
            _net = net;
            _net.OnMessage += OnMessage;
        }

        public static bool InGame()
        {
            try { return !GameManager.IsMainMenuActive() && GameManager.GetUniStorm() != null && GameManager.GetPlayerTransform() != null; }
            catch { return false; }
        }

        /// <summary>A loading screen is up, a scene is still being loaded, or the game is still restoring a save.</summary>
        public static bool Loading()
        {
            try { if (GameManager.SaveIsBlockedDueToRestoreGame()) return true; } catch { }
            try { var p = InterfaceManager.GetPanel<Panel_Loading>(); if (p != null && (p.IsEnabled() || p.IsLoading())) return true; } catch { }
            try { var d = GameManager.m_AsyncSceneLoadsInProgress; if (d != null && d.Count > 0) return true; } catch { }
            return false;
        }

        private static float _settledSince = -1f, _settledSeen = -100f;
        /// <summary>
        /// In a game and past its loading: no loading under way and the world has been up for a few seconds, so the
        /// game's own fade-in is over too. Pausing the game or asking it to save before that froze it for good
        /// (0.7.0 test: a host that was paused 0.6 s after its save had loaded never finished the load).
        /// </summary>
        public static bool Settled()
        {
            float now = Time.realtimeSinceStartup;
            bool ok = InGame() && !Loading();
            // looked at every frame by the pause rule; a gap means nobody looked, so start counting again
            if (!ok) _settledSince = -1f;
            else if (_settledSince < 0f || now - _settledSeen > 1.5f) _settledSince = now;
            _settledSeen = now;
            return ok && now - _settledSince >= 4f;
        }

        public void Update()
        {
            if (_net.Mode != NetMode.Host || _net.Remotes.Count == 0 || !InGame()) return;
            float now = Time.realtimeSinceStartup;

            // while everybody sleeps the host clock runs fast: the clients keep step with it, so they hear it more often
            bool fast = false;
            try { fast = CoopMod.Instance.Sleep != null && CoopMod.Instance.Sleep.HostAccelerating; } catch { }
            if (fast != _wasFast) { _wasFast = fast; _nextTime = 0f; } // the moment it starts or stops, say so at once
            if (now >= _nextTime)
            {
                _nextTime = now + (fast ? 0.1f : 1f);
                var uni = GameManager.GetUniStorm();
                var tod = GameManager.GetTimeOfDayComponent();
                float hours = uni.GetElapsedHours(), norm = tod != null ? tod.GetNormalizedTime() : uni.m_NormalizedTime;
                int day = uni.GetDayNumber();
                try { WorkTime.Instance?.SharedForSending(ref hours, ref norm, ref day); } catch { }
                var w = _net.Begin(Msg.WorldTime);
                w.Put(hours);
                w.Put(norm);
                w.Put(day);
                _net.Send(w, DeliveryMethod.Sequenced);
            }

            if (now >= _nextWeather)
            {
                _nextWeather = now + 2f;
                var uni = GameManager.GetUniStorm();
                var stage = uni.GetWeatherStage();
                bool changed = stage != _lastSentStage;
                if (changed || now - _lastFullWeatherSend > 30f)
                {
                    var wt = GameManager.GetWeatherTransitionComponent();
                    string text = wt != null ? wt.Serialize() : "";
                    var w = _net.Begin(Msg.Weather);
                    w.Put((int)stage);
                    w.Put(text ?? "");
                    _net.Send(w, DeliveryMethod.ReliableOrdered);
                    _lastSentStage = stage;
                    _lastFullWeatherSend = now;
                }
            }
        }

        /// <summary>A new client should get the full picture right away.</summary>
        public void ForceSendSoon()
        {
            _nextTime = 0; _nextWeather = 0; _lastSentStage = WeatherStage.Undefined;
        }

        private void OnMessage(byte from, NetDataReader r, Msg msg)
        {
            if (_net.Mode != NetMode.Client) return;
            if (msg == Msg.WorldTime) ApplyTime(r.GetFloat(), r.GetFloat(), r.GetInt());
            else if (msg == Msg.Weather) ApplyWeather((WeatherStage)r.GetInt(), r.GetString());
        }

        private void ApplyTime(float hostHours, float hostNorm, int hostDay)
        {
            if (!InGame() || Loading()) return; // while a scene loads this game's clock is not the real one yet
            try
            {
                // while the host loads a scene its clock briefly shows the saved time: time never really runs backwards
                try { WorkTime.Instance?.HostClock(hostHours); } catch { }
                if (hostHours < _lastHostHours - 0.02f && ++_backwards < 5) return;
                _backwards = 0; _lastHostHours = hostHours;
                var uni = GameManager.GetUniStorm();
                float signed = uni.GetElapsedHours() - hostHours;
                float diff = Mathf.Abs(signed);
                // asleep together with the host: our clock is steered towards the host's, a jump would cut the sleep short
                try { var sl = CoopMod.Instance.Sleep; if (sl != null && sl.HostClock(hostHours, diff)) return; } catch { }
                if (diff < 0.05f) return; // within 3 in-game minutes, leave it alone
                // ahead because this player is waiting out a job's time: not to be set back (see WorkTime)
                try { if (signed > 0f && WorkTime.Instance != null && WorkTime.Instance.HoldsAhead()) return; } catch { }
                uni.SetElapsedHours(hostHours);
                try { WorkTime.Instance?.ClockSetBySync(); } catch { }
                if (uni.GetDayNumber() != hostDay) uni.SetDayNumber(hostDay);
                var tod = GameManager.GetTimeOfDayComponent();
                if (tod != null) tod.SetNormalizedTime(hostNorm, true);
                else uni.SetNormalizedTime(hostNorm, true);
                if (diff > 0.5f) CoopMod.Log($"Clock synced to host (this game was {diff:0.00} h {(signed > 0 ? "ahead" : "behind")})");
            }
            catch (Exception e) { CoopMod.Log("Time sync failed: " + e.Message); }
        }

        private void ApplyWeather(WeatherStage stage, string text)
        {
            if (!InGame()) return;
            try
            {
                var uni = GameManager.GetUniStorm();
                var wt = GameManager.GetWeatherTransitionComponent();
                bool stageDiffers = uni.GetWeatherStage() != stage;
                bool stale = Time.realtimeSinceStartup - _lastWeatherApply > 60f;
                if (wt != null && !string.IsNullOrEmpty(text) && (stageDiffers || stage != _hostStage || stale) && text != _lastWeatherText)
                {
                    wt.Deserialize(text);
                    _lastWeatherText = text;
                    _lastWeatherApply = Time.realtimeSinceStartup;
                }
                if (uni.GetWeatherStage() != stage)
                    uni.SetWeatherStage(stage, 20f, false);
                if (stage != _hostStage) CoopMod.Log($"Weather -> {stage}");
                _hostStage = stage;
            }
            catch (Exception e) { CoopMod.Log("Weather sync failed: " + e.Message); }
        }
    }
}
