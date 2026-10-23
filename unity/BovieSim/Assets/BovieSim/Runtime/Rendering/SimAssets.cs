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
        public Material drape;
        public Material steel;
        public Material pencilBody;
        public Material pencilCutButton;
        public Material pencilCoagButton;
        public Material particlesAlpha;
        public Material particlesAdditive;

        public static SimAssets Load()
        {
            var a = Resources.Load<SimAssets>(ResourcePath);
            if (a == null) Debug.LogError("[Bovie Sim] SimAssets not found. Run Bovie Sim > Set Up Project in the editor first.");
            return a;
        }
    }
}
