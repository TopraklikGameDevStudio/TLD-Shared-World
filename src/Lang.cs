using System;
using Il2Cpp;
using UnityEngine;

namespace TLDCoop
{
    /// <summary>
    /// The mod's own texts in Turkish or English. "auto" (the default) follows the game's language: Turkish when the
    /// game is in Turkish, English for every other language. The Dil / Language setting can force either one.
    /// </summary>
    internal static class L
    {
        internal const string FeedbackMail = "destek@topraklikgamedevstudio.com";
        internal const string ProjectPage = "https://github.com/TopraklikGameDevStudio/TLD-Shared-World";

        internal static string Setting = "auto";
        private static bool _en;
        private static float _checkedAt = -10f;
        internal static string GameLanguage = "?";

        /// <summary>True when the mod should speak English.</summary>
        internal static bool En
        {
            get
            {
                float now = Time.realtimeSinceStartup;
                if (now - _checkedAt < 1f) return _en;
                _checkedAt = now;
                string s = (Setting ?? "auto").Trim().ToLowerInvariant();
                if (s == "en" || s == "english" || s == "ingilizce") return _en = true;
                if (s == "tr" || s == "turkish" || s == "türkçe" || s == "turkce") return _en = false;
                try { GameLanguage = Localization.Language ?? "?"; } catch { GameLanguage = "?"; }
                string g = GameLanguage.ToLowerInvariant();
                return _en = !(g.StartsWith("turk") || g.StartsWith("türk") || g == "tr");
            }
        }

        /// <summary>The Turkish or the English text, whichever the player reads.</summary>
        internal static string T(string tr, string en) => En ? en : tr;

        internal static void ForceRecheck() => _checkedAt = -10f;

        private const char Sep = '\u001f';
        /// <summary>A text sent to another player in both languages, so each reads their own.</summary>
        internal static string Both(string tr, string en) => tr + Sep + en;
        /// <summary>The reader's half of a text made with Both (or the text itself if it has one language).</summary>
        internal static string Pick(string wire)
        {
            if (wire == null) return "";
            int i = wire.IndexOf(Sep);
            return i < 0 ? wire : (En ? wire.Substring(i + 1) : wire.Substring(0, i));
        }
    }
}
