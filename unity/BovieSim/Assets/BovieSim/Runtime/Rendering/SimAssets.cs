using UnityEngine;

namespace BovieSim
{
    /// <summary>
    /// Registry of generated assets (materials, textures). Created by Bovie Sim > Set Up Project and stored at
    /// Resources/BovieSim/SimAssets.asset so the runtime can load it without scene references.
    /// </summary>
    /// <summary>A pack instrument shown lying on the drape (visual only): mesh fitted flat with its centre at the origin, placed in field mm.</summary>
    [System.Serializable]
    public class DressingItem { public string name; public Mesh mesh; public Material material; public Vector3 positionMm; public float yawDeg; }

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
        [Tooltip("Pencil body: off-white moulded plastic, textured when a Plastic set is available")] public Material pencilBody;
        public Material pencilCutButton;
        public Material pencilCoagButton;
        public Material particlesAlpha;
        public Material particlesAdditive;
        [Tooltip("Teaching view overlay (BovieSim/TeachingOverlay)")] public Material teachingOverlay;
        [Tooltip("Adson forceps arms from an instrument pack in Assets/BovieSim/Models (tip at the origin, +Y toward the hinge, mm); null = procedural arms")] public Mesh forcepsArmA, forcepsArmB;
        [Tooltip("Instrument-pack steel with the pack's scratch maps in its own UVs")] public Material packSteel;
        [Tooltip("Instruments from the pack laid on the drape beside the field")] public DressingItem[] dressing = new DressingItem[0];
        [Tooltip("UI Toolkit runtime theme (Generated/BovieTheme.tss)")] public UnityEngine.UIElements.ThemeStyleSheet uiTheme;
        [Tooltip("Set Up Project version that produced these assets; older registries trigger a re-run")] public int setupVersion;
        /// <summary>bump when Set Up Project must be re-run after an update</summary>
        public const int CurrentSetupVersion = 6;

        public static SimAssets Load()
        {
            var a = Resources.Load<SimAssets>(ResourcePath);
            if (a == null) Debug.LogError("[Bovie Sim] SimAssets not found. Run Bovie Sim > Set Up Project in the editor first.");
            return a;
        }
    }
}
