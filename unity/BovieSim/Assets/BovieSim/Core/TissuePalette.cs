using UnityEngine;

namespace BovieSim
{
    /// <summary>
    /// Reference tissue colours (sRGB albedo, i.e. the colour under neutral white light), chosen to match the
    /// intraoperative appearance a surgeon learns: pale pink-white cut dermis, golden lobulated fat with white
    /// septa, pearly fascia, beefy red muscle, bright arterial and dark venous blood, and the thermal states
    /// (blanched white-tan, rendered fat, cooked grey-brown muscle, brown coagulum, dry tan, black char).
    /// One source for the 3D textures (Editor/TissueTextures.cs), the section view legend (SimUI) and, by hand,
    /// TissueColor/CoagColor in Sim.compute: keep those in step when changing a value here.
    /// </summary>
    public static class TissuePalette
    {
        public static readonly Color SkinLight = new Color(0.87f, 0.69f, 0.60f);
        public static readonly Color SkinMedium = new Color(0.68f, 0.48f, 0.36f);
        public static readonly Color SkinDark = new Color(0.36f, 0.24f, 0.18f);
        public static readonly Color Dermis = new Color(0.93f, 0.84f, 0.80f);
        public static readonly Color Fat = new Color(0.96f, 0.79f, 0.33f);
        public static readonly Color Septum = new Color(0.94f, 0.91f, 0.84f);
        public static readonly Color Fascia = new Color(0.90f, 0.89f, 0.86f);
        public static readonly Color Muscle = new Color(0.56f, 0.13f, 0.11f);
        public static readonly Color Artery = new Color(0.66f, 0.08f, 0.07f);
        public static readonly Color Vein = new Color(0.33f, 0.17f, 0.30f);
        public static readonly Color Nerve = new Color(0.94f, 0.91f, 0.78f);
        public static readonly Color Lipoma = new Color(0.98f, 0.86f, 0.48f);
        public static readonly Color Capsule = new Color(0.93f, 0.88f, 0.74f);
        public static readonly Color Areolar = new Color(0.93f, 0.82f, 0.80f);
        public static readonly Color Viscera = new Color(0.62f, 0.30f, 0.30f);
        public static readonly Color Serosa = new Color(0.90f, 0.87f, 0.85f);
        public static readonly Color Strand = new Color(0.93f, 0.80f, 0.78f);
        public static readonly Color BloodFresh = new Color(0.55f, 0.03f, 0.03f);
        public static readonly Color BloodPooled = new Color(0.32f, 0.02f, 0.02f);

        // thermal states
        public static readonly Color CoagSkin = new Color(0.80f, 0.70f, 0.58f);
        public static readonly Color CoagDermis = new Color(0.86f, 0.81f, 0.74f);
        public static readonly Color CoagFat = new Color(0.86f, 0.70f, 0.42f);
        public static readonly Color CoagLipoma = new Color(0.88f, 0.74f, 0.48f);
        public static readonly Color CoagFibrous = new Color(0.84f, 0.80f, 0.70f);
        public static readonly Color CoagMuscle = new Color(0.66f, 0.52f, 0.46f);
        public static readonly Color CoagBlood = new Color(0.36f, 0.18f, 0.12f);
        public static readonly Color CoagVein = new Color(0.30f, 0.17f, 0.16f);
        public static readonly Color CoagViscera = new Color(0.66f, 0.52f, 0.46f);
        public static readonly Color Clot = new Color(0.22f, 0.04f, 0.04f);
        public static readonly Color Coagulum = new Color(0.08f, 0.04f, 0.03f);
        public static readonly Color Desiccated = new Color(0.62f, 0.48f, 0.32f);
        public static readonly Color Char = new Color(0.04f, 0.035f, 0.03f);

        /// <summary>Epidermis colour for a scenario skin tone (0 light, 0.5 medium, 1 dark).</summary>
        public static Color Skin(float tone)
        {
            tone = Mathf.Clamp01(tone);
            return tone < 0.5f ? Color.Lerp(SkinLight, SkinMedium, tone * 2) : Color.Lerp(SkinMedium, SkinDark, tone * 2 - 1);
        }

        /// <summary>Multiplicative stain of the skin prep: tinted chlorhexidine-alcohol (orange), povidone-iodine (brown), or none.</summary>
        public static Color Prep(string prep)
        {
            switch ((prep ?? "").ToLowerInvariant())
            {
                case "betadine": case "povidone-iodine": case "iodine": return new Color(0.72f, 0.45f, 0.22f);
                case "none": case "": return Color.white;
                default: return new Color(1.00f, 0.90f, 0.80f); // chlorhexidine
            }
        }

        /// <summary>The skin tone and prep the textures are baked with; the runtime corrects to the scenario's.</summary>
        public const float BakedTone = 0.3f;
        public const string BakedPrep = "chlorhexidine";
    }
}
