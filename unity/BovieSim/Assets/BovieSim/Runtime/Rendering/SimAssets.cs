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

    /// <summary>A modelled instrument part in the sim's frame (tip at the origin, +Y along the handle, mm) with one material per submesh; unset = procedural.</summary>
    [System.Serializable]
    public class ToolModel { public Mesh mesh; public Material[] materials; public bool IsSet { get { return mesh != null && materials != null && materials.Length > 0; } } }

    public sealed class SimAssets : ScriptableObject
    {
        public const string ResourcePath = "BovieSim/SimAssets";
        public const int TissueBuckets = 20;

        [Tooltip("HDRP Lit materials per surface bucket: 0-8 tissue classes (skin, dermis, fat, lipoma, fibrous, muscle, artery, vein, viscera), 9-17 the same coagulated, 18 desiccated, 19 carbonized")]
        public Material[] tissue = new Material[TissueBuckets];
        public Material blood;
        [Tooltip("translucent pink-white connective strands drawn out by traction or blunt dissection")] public Material strand;
        [Tooltip("Loose blue drape (CC0 fabric or procedural weave), reinforcement pad and incise film around the fenestration")] public Material drape, drapePad, drapeFilm;
        [Tooltip("Cotton towels squaring the window on top of the drape")] public Material towel;
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
        [Tooltip("Modelled instruments from Assets/BovieSim/Models (pencil.fbx, frazier.fbx, skin_hook.fbx, blade/needle/ball.fbx); unset parts stay procedural")]
        public ToolModel pencilModel = new ToolModel(), pencilCutModel = new ToolModel(), pencilCoagModel = new ToolModel(), bladeModel = new ToolModel(), needleModel = new ToolModel(), ballModel = new ToolModel(), frazierModel = new ToolModel(), skinHookModel = new ToolModel();
        [Tooltip("UI Toolkit runtime theme (Generated/BovieTheme.tss)")] public UnityEngine.UIElements.ThemeStyleSheet uiTheme;
        [Tooltip("Set Up Project version that produced these assets; older registries trigger a re-run")] public int setupVersion;
        /// <summary>bump when Set Up Project must be re-run after an update</summary>
        public const int CurrentSetupVersion = 9;

        public static SimAssets Load()
        {
            var a = Resources.Load<SimAssets>(ResourcePath);
            if (a == null) Debug.LogError("[Bovie Sim] SimAssets not found. Run Bovie Sim > Set Up Project in the editor first.");
            return a;
        }
    }
}
