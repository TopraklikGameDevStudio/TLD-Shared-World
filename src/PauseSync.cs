using System;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// In a shared world one player cannot stop time for the others: their Esc menu opens (or their window goes to the
    /// background) but the world keeps going while anybody else is playing. When nobody is, every game pauses.
    /// </summary>
    internal static class PauseSync
    {
        private static Panel_PauseMenu _menu;
        private static bool _wasAway, _pausedByUs, _overriding;
        private static Panel_OptionsMenu _options;
        private static float _nobodySince = -1f;
        private static float _nextLook, _nextLookOptions;
        private static bool _menuOpen, _logged;

        private static bool _focused = true, _loggedFocus;
        private static float _lastInput, _nextInputCheck;
        private static int _lastMouseX, _lastMouseY;
        private const float IdleSeconds = 180f;

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

        /// <summary>We are not really playing: Esc menu, window in the background, or no input for 3 minutes.</summary>
        public static bool LocalAway => TestPresence == 1 ? false : TestPresence == 2 ? true
            : (_menuOpen || !_focused || Time.realtimeSinceStartup - _lastInput > IdleSeconds);

        /// <summary>Test harness only: 0 = normal, 1 = count as actively playing (and never pause), 2 = count as away.</summary>
        internal static int TestPresence;
        internal static bool ForcedActive => TestPresence == 1;
        /// <summary>Test harness only: counts as input, so the 3-minute idle rule starts over.</summary>
        internal static void Touch() { _lastInput = Time.realtimeSinceStartup; }

        private static void CheckInput()
        {
            float now = Time.realtimeSinceStartup;
            if (_lastInput == 0f) _lastInput = now;
            if (now < _nextInputCheck) return;
            _nextInputCheck = now + 0.5f;
            if (!_focused) return;
            try
            {
                if (GetCursorPos(out var p) && (p.X != _lastMouseX || p.Y != _lastMouseY)) { _lastMouseX = p.X; _lastMouseY = p.Y; _lastInput = now; return; }
                for (int vk = 0x01; vk <= 0xA5; vk++)
                    if ((GetAsyncKeyState(vk) & 0x8001) != 0) { _lastInput = now; return; }
            }
            catch { _lastInput = now; }
        }

        public static bool Shared
        {
            get
            {
                var m = CoopMod.Instance;
                return m != null && m.Net != null && m.Net.IsConnected && m.Items != null && m.Items.SharedWorld && WorldSync.InGame();
            }
        }

        /// <summary>Vanilla would pause this game right now: the Esc menu is open or the window is in the background.</summary>
        private static bool VanillaWantsPause => _menuOpen || !_focused;

        /// <summary>Somebody other than us is really playing (or the test harness says we count as playing).
        /// A friend who is waiting for the world counts too: the game has to run to write the save he is waiting for.</summary>
        internal static bool SomeoneElsePlays => ForcedActive || CoopMod.Instance.Players.AnyRemoteActive() || HostSendingWorld;
        private static bool HostSendingWorld { get { try { return CoopMod.Instance.Transfer.HostSending; } catch { return false; } } }

        /// <summary>True while we keep this game running although vanilla would pause it.</summary>
        public static bool KeepRunning => Shared && VanillaWantsPause && SomeoneElsePlays;

        private static bool IsPaused()
        {
            try { return GameManager.m_IsPaused && Time.timeScale == 0f; } catch { return false; }
        }

        private static void Unpause(string why)
        {
            try { GameManager.m_IsPaused = false; if (Time.timeScale == 0f) Time.timeScale = 1f; } catch { }
            _pausedByUs = false;
            CoopMod.Log("This game runs again: " + why);
        }

        // The rule, decided fresh every frame from what is true right now (no reliance on the game's own
        // "paused when focus lost" flag, which can stay set while the game is in fact running):
        //   focused and no menu            -> the game runs, as always
        //   menu open or in the background -> runs while someone else is playing; pauses once nobody has been for 6 s
        public static void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (_menu == null && now >= _nextLook)
            {
                _nextLook = now + 1f;
                try { _menu = InterfaceManager.GetPanel<Panel_PauseMenu>(); } catch { }
            }
            if (_options == null && now >= _nextLookOptions)
            {
                _nextLookOptions = now + 2f;
                try { _options = InterfaceManager.GetPanel<Panel_OptionsMenu>(); } catch { }
            }
            bool open = false;
            try { open = _menu != null && _menu.IsEnabled(); } catch { _menu = null; }
            // the options screen is part of the Esc menu
            try { if (!open && _options != null && _options.IsEnabled()) open = true; } catch { _options = null; }
            _menuOpen = open && WorldSync.InGame();
            _focused = Application.isFocused;
            CheckInput();

            bool shared = Shared;
            bool settled = WorldSync.Settled(); // looked at every frame, see there
            bool wasAway = _wasAway; _wasAway = LocalAway && shared;
            if (_wasAway != wasAway) CoopMod.Log(_wasAway ? "You are away (menu, background or idle)" : "You are back");

            if (!shared)
            {
                // we had kept this game running behind its menu / in the background for a friend who is now gone
                // (they left, or the connection dropped): pause it again right away, as the game itself would have
                if (_overriding)
                {
                    _overriding = false;
                    if (settled && VanillaWantsPause && !IsPaused())
                    {
                        try { GameManager.m_IsPaused = true; Time.timeScale = 0f; } catch { }
                        _pausedByUs = true;
                        CoopMod.Log("No longer in a shared world: this game pauses");
                    }
                }
                // and let go of our own pause as soon as vanilla would run
                if (_pausedByUs && !VanillaWantsPause) Unpause("no longer in a shared world");
                _nobodySince = -1f;
                return;
            }

            bool wantsPause = VanillaWantsPause;
            if (!wantsPause || SomeoneElsePlays)
            {
                _nobodySince = -1f;
                if (!wantsPause)
                {
                    _overriding = false;
                    if (_pausedByUs) Unpause("you are playing");
                    return;
                }
                _overriding = true;
                // someone else is playing: keep going behind the menu / in the background
                try
                {
                    if (GameManager.m_IsPaused) GameManager.m_IsPaused = false;
                    if (Time.timeScale == 0f) Time.timeScale = 1f;
                }
                catch { }
                if (_pausedByUs) { _pausedByUs = false; CoopMod.Log("This game runs again: someone is playing"); }
                if (_menuOpen && !_logged) { _logged = true; CoopMod.Log("Esc menu open: the shared world keeps running"); }
                if (!_focused && !_loggedFocus) { _loggedFocus = true; CoopMod.Log("Window in the background: the shared world keeps running"); }
                return;
            }

            // menu open or in the background, and nobody else is playing
            // ... but never in the middle of a load or its fade-in: a game frozen there never finishes loading
            if (!settled) { _nobodySince = -1f; return; }
            if (_nobodySince < 0f) _nobodySince = now;
            // not just switching windows; with no friend connected at all there is nobody to wait for
            float grace = 6f;
            try { if (CoopMod.Instance.Players.Count == 0) grace = 0.5f; } catch { }
            if (now - _nobodySince < grace) return;
            _overriding = false;
            if (!IsPaused())
            {
                try { GameManager.m_IsPaused = true; Time.timeScale = 0f; } catch { }
                if (!_pausedByUs) CoopMod.Log("Nobody is playing: this game pauses");
                _pausedByUs = true;
            }
        }
    }

    // alt-tab must not freeze the shared world (fires, flares, physics) either
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.PauseWhenFocusLost))]
    internal static class FocusPausePatch
    {
        private static bool Prefix(bool focusLost)
        {
            if (focusLost && PauseSync.Shared && PauseSync.SomeoneElsePlays) return false;
            return true;
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.UpdatePaused))]
    internal static class UpdatePausedPatch
    {
        private static bool Prefix(GameManager __instance)
        {
            if (!PauseSync.KeepRunning) return true;
            try { __instance.UpdateNotPaused(); } catch (Exception e) { CoopMod.Log("Keep running: " + e.Message); return true; }
            return false;
        }
    }
}
