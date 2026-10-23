using UnityEngine;

namespace BovieSim
{
    /// <summary>
    /// Registry of generated assets (materials, textures). Created by Bovie Sim > Set Up Project and stored at
    /// Resources/BovieSim/SimAssets.asset so the runtime can load it without scene references.
    /// </summary>
    public sealed class SimAssets : ScriptableObject
    {
        public const string ResourcePath = "BovieSim/SimAssets";
        public const int TissueBuckets = 18;

        [Tooltip("HDRP Lit materials per surface bucket: 0-7 tissue classes (skin, dermis, fat, lipoma, fibrous, muscle, artery, vein), 8-15 the same coagulated, 16 desiccated, 17 carbonized")]
        public Material[] tissue = new Material[TissueBuckets];
        public Material blood;
        [Tooltip("Loose blue drape (CC0 fabric or procedural weave), reinforcement pad and incise film around the fenestration")] public Material drape, drapePad, drapeFilm;
        [Tooltip("Emissive face of the operating-light heads, seen in reflections")] public Material lampDome;
        public Material steel;
        public Material pencilBody;
        public Material pencilCutButton;
        public Material pencilCoagButton;
        public Material particlesAlpha;
        public Material particlesAdditive;
        [Tooltip("Teaching view overlay (BovieSim/TeachingOverlay)")] public Material teachingOverlay;
        [Tooltip("UI Toolkit runtime theme (Generated/BovieTheme.tss)")] public UnityEngine.UIElements.ThemeStyleSheet uiTheme;
        [Tooltip("Set Up Project version that produced these assets; older registries trigger a re-run")] public int setupVersion;
        /// <summary>bump when Set Up Project must be re-run after an update</summary>
        public const int CurrentSetupVersion = 4;

        public static SimAssets Load()
        {
            var a = Resources.Load<SimAssets>(ResourcePath);
            if (a == null) Debug.LogError("[Bovie Sim] SimAssets not found. Run Bovie Sim > Set Up Project in the editor first.");
            return a;
        }
    }
}
