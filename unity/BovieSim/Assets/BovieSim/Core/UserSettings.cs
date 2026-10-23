using System;

namespace BovieSim
{
    /// <summary>
    /// Per-user preferences saved as JSON (Application.persistentDataPath/settings.json on the PC running the
    /// simulator). <see cref="version"/> is bumped when fields change meaning; <see cref="Migrate"/> brings older
    /// files forward, and unknown or missing fields fall back to the defaults below, so an update never breaks
    /// a saved file.
    /// </summary>
    [Serializable]
    public class UserSettings
    {
        public const int CurrentVersion = 1;
        public int version = CurrentVersion;
        /// <summary>DetailLevel name: Auto, Low, Medium, High, Ultra</summary>
        public string detail = "Auto";
        public bool depthOfField;
        /// <summary>0 SMAA, 1 TAA, 2 FXAA, 3 off</summary>
        public int antialiasing;
        public bool invertTiltY;
        /// <summary>pause and replay the last seconds on serious mistakes</summary>
        public bool teachingMoments = true;
        public bool showTiming;
        public bool tipGauge = true;
        /// <summary>last detail level the PC could not hold in budget (the app suggests the one below)</summary>
        public string overBudgetAt = "";

        public static UserSettings FromJson(string json)
        {
            UserSettings s = null;
            try { if (!string.IsNullOrEmpty(json)) s = UnityEngine.JsonUtility.FromJson<UserSettings>(json); }
            catch (Exception) { s = null; }
            if (s == null) s = new UserSettings();
            s.Migrate();
            return s;
        }

        public string ToJson() { return UnityEngine.JsonUtility.ToJson(this, true); }

        /// <summary>Upgrade an older file in place (add a case per version bump).</summary>
        public void Migrate()
        {
            if (version < 1) { detail = "Auto"; teachingMoments = true; tipGauge = true; }
            if (antialiasing < 0 || antialiasing > 3) antialiasing = 0;
            version = CurrentVersion;
        }
    }
}
