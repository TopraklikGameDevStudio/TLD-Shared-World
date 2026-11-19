using System;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// A movie that cannot finish stopping (seen after the start-up notices were stepped through by the test harness):
    /// InterfaceManager.RestorePanelsAfterSubtitle throws every frame, MoviePlayer stays "Stopping" and its camera keeps
    /// drawing the last picture over the world. The prefix skips that restore when its list is missing or holds a
    /// destroyed panel; the watchdog ends a movie that has been stopping for over two seconds.
    /// </summary>
    [HarmonyPatch(typeof(InterfaceManager), nameof(InterfaceManager.RestorePanelsAfterSubtitle))]
    internal static class MovieGuardRestorePatch
    {
        internal static int Skipped;
        private static bool Prefix()
        {
            try
            {
                var l = InterfaceManager.m_SubtitleHiddenPanels;
                bool bad = l == null;
                for (int i = 0; !bad && i < l.Count; i++) if (l[i] == null) bad = true;
                if (!bad) return true;
                Skipped++;
                try { l?.Clear(); } catch { }
                try { InterfaceManager.m_SubtitleUiCount = 0; } catch { }
                if (Skipped == 1) CoopMod.Log("Movie guard: the panels hidden for a movie's subtitles were gone; skipped putting them back (the game would fail here every frame)");
                return false;
            }
            catch { return true; }
        }
    }

    internal static class MovieGuard
    {
        private static float _stoppingSince = -1f;
        internal static int Forced;
        internal static string Last = "-";

        internal static void Update()
        {
            try
            {
                var st = MoviePlayer.m_State;
                float now = Time.realtimeSinceStartup;
                if (st != MoviePlayer.State.Stopping) { _stoppingSince = -1f; return; }
                if (_stoppingSince < 0f) { _stoppingSince = now; return; }
                if (now - _stoppingSince < 2f) return;
                _stoppingSince = -1f;
                Forced++;
                try { var cam = MoviePlayer.m_Camera; if (cam != null) { cam.enabled = false; UnityEngine.Object.Destroy(cam.gameObject); } } catch { }
                try { MoviePlayer.m_Camera = null; } catch { }
                try { MoviePlayer.m_State = MoviePlayer.State.Stopped; } catch { }
                Last = $"forced a stuck movie to stop at {DateTime.Now:HH:mm:ss}";
                CoopMod.Log("Movie guard: a movie was stuck stopping for 2 s; ended it and removed its camera");
            }
            catch { }
        }

        internal static string StateLine()
        {
            string s = "?", cam = "?";
            try { s = MoviePlayer.m_State.ToString(); } catch { }
            try { var c = MoviePlayer.m_Camera; cam = c == null ? "none" : (c.enabled && c.gameObject.activeInHierarchy ? "drawing" : "off"); } catch { }
            return $"state={s} camera={cam} restore_skipped={MovieGuardRestorePatch.Skipped} forced={Forced} last={Last}";
        }
    }
}
