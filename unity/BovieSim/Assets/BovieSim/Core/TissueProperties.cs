using System;

namespace BovieSim
{
    /// <summary>Tissue ids. Must match src/core/tissueProperties.ts and every shader.</summary>
    public enum Tissue : byte
    {
        Air = 0, Epidermis = 1, Dermis = 2, Fat = 3, Septum = 4, SuperficialFascia = 5, DeepFascia = 6,
        Muscle = 7, Artery = 8, Vein = 9, Nerve = 10, Lipoma = 11, LipomaCapsule = 12, Blood = 13,
        /// <summary>loose areolar connective tissue: the wispy, stringy plane between fat lobules, over fasciae and around capsules</summary>
        Areolar = 14,
        /// <summary>visceral parenchyma (bowel wall / solid organ): soft, friable, very vascular</summary>
        Viscera = 15,
        /// <summary>serosa / organ capsule: thin glistening collagen membrane</summary>
        Serosa = 16,
    }

    /// <summary>
    /// Cell state (3 bits). For tissue: Normal, Coagulated, Desiccated, Carbonized. For air (type 0): Normal = the
    /// original air above the skin, Vaporized = air made by cutting, vaporising or tearing (an open wound surface),
    /// Dissected = air made by blunt dissection (a separated plane: intact vessels crossing it do not bleed).
    /// </summary>
    public enum TissueState : byte { Normal = 0, Coagulated = 1, Desiccated = 2, Carbonized = 3, Vaporized = 4, Dissected = 5 }

    [Serializable]
    public struct TissueProps
    {
        public string name;
        /// <summary>electrical conductivity S/m at ~450 kHz</summary>
        public float sigma;
        /// <summary>thermal conductivity W/(m K)</summary>
        public float k;
        /// <summary>density kg/m3</summary>
        public float rho;
        /// <summary>specific heat J/(kg K)</summary>
        public float c;
        /// <summary>blood perfusion ml/(min kg)</summary>
        public float perfusion;
        /// <summary>water mass fraction</summary>
        public float water;
        /// <summary>Arrhenius activation energy J/mol</summary>
        public double Ea;
        /// <summary>Arrhenius ln(A), A in 1/s</summary>
        public double lnA;
        // ---- mechanics (XPBD lattice) ----
        /// <summary>XPBD compliance of a lattice link through this tissue (lower = stiffer)</summary>
        public float compliance;
        /// <summary>strain stiffening knee: compliance falls by 1 + (strain / kneeStrain)^2 (collagen J-curve)</summary>
        public float kneeStrain;
        /// <summary>strain beyond which the tissue yields: links lengthen permanently and become strands</summary>
        public float yieldStrain;
        /// <summary>strain at which a link through healthy tissue tears</summary>
        public float tearStrain;
        /// <summary>0..1 adhesion to neighbours: planes with cohesion below the instrument threshold separate under blunt dissection</summary>
        public float cohesion;
        /// <summary>fractional rest-length contraction once fully coagulated</summary>
        public float coagShrink;
        /// <summary>stiffness multiplier once coagulated (&gt; 1 firmer, &lt; 1 softer: rendered fat)</summary>
        public float coagStiffen;
        /// <summary>capillary ooze from a fresh raw surface, microlitres per second per mm2 of exposed surface</summary>
        public float oozeRate;

        public TissueProps(string name, float sigma, float k, float rho, float c, float perfusion, float water, double Ea, double lnA,
            float compliance, float kneeStrain, float yieldStrain, float tearStrain, float cohesion, float coagShrink, float coagStiffen, float oozeRate)
        {
            this.name = name; this.sigma = sigma; this.k = k; this.rho = rho; this.c = c; this.perfusion = perfusion; this.water = water; this.Ea = Ea; this.lnA = lnA;
            this.compliance = compliance; this.kneeStrain = kneeStrain; this.yieldStrain = yieldStrain; this.tearStrain = tearStrain; this.cohesion = cohesion;
            this.coagShrink = coagShrink; this.coagStiffen = coagStiffen; this.oozeRate = oozeRate;
        }
    }

    /// <summary>
    /// Physical property table, identical to the web client (docs/physics.md is the provenance).
    /// </summary>
    public static class TissueTable
    {
        public const double R = 8.314;
        public const float BodyC = 37f;
        public const float LatentHeatWater = 2.26e6f;
        public const int Count = 17;
        /// <summary>floats per tissue in the GPU table: sigma, k, rho, c, w_b, water, Ea/R, lnA, cohesion, oozeRate, tearStrain, 0</summary>
        public const int PropsStride = 12;

        /// <summary>Blunt dissection thresholds: a spreading instrument separates planes with cohesion up to the threshold.</summary>
        public const float DissectSpreadCohesion = 0.55f, DissectPushCohesion = 0.35f;
        /// <summary>Thermal-state mechanics: desiccated and charred tissue is stiffer and brittle.</summary>
        public const float DryTearFactor = 0.65f, DryStiffen = 0.5f, CharTearFactor = 0.25f, CoagTearFactor = 0.8f;

        static double LnAFor(double Ea, double t1sC) { return Ea / (R * (t1sC + 273.15)); }
        const double HenriquesEa = 6.28e5;
        static readonly double HenriquesLnA = Math.Log(3.1e98);

        public static readonly TissueProps[] Props = Build();

        static TissueProps[] Build()
        {
            var t = new TissueProps[Count];
            double colEa = 3.06e5, colLnA = LnAFor(3.06e5, 65), fatEa = 3.0e5, fatLnA = LnAFor(3.0e5, 65);
            double musEa = 2.6e5, musLnA = Math.Log(2.94e39);
            double bloodEa = 5.0e5, bloodLnA = LnAFor(5.0e5, 68);
            t[0] = new TissueProps("air", 0f, 0.026f, 1.2f, 1005f, 0f, 0f, 1e9, 0, 1e-5f, 9f, 9f, 9f, 0f, 0f, 1f, 0f);
            t[1] = new TissueProps("epidermis", 0.05f, 0.24f, 1190f, 3600f, 0f, 0.5f, HenriquesEa, HenriquesLnA, 1.0e-6f, 0.30f, 0.5f, 0.9f, 1.0f, 0.12f, 2.5f, 0f);
            t[2] = new TissueProps("dermis", 0.3f, 0.37f, 1109f, 3391f, 106f, 0.65f, HenriquesEa, HenriquesLnA, 2.0e-6f, 0.35f, 0.5f, 1.0f, 1.0f, 0.15f, 2.5f, 0.08f);
            t[3] = new TissueProps("subcutaneous fat", 0.04f, 0.21f, 911f, 2348f, 33f, 0.18f, fatEa, fatLnA, 4.0e-5f, 0.50f, 0.25f, 0.6f, 0.3f, 0.04f, 0.6f, 0.01f);
            t[4] = new TissueProps("fibrous septum", 0.3f, 0.4f, 1100f, 3300f, 20f, 0.6f, colEa, colLnA, 5.0e-6f, 0.15f, 0.3f, 0.8f, 0.5f, 0.15f, 2f, 0.02f);
            t[5] = new TissueProps("superficial fascia", 0.3f, 0.4f, 1100f, 3300f, 15f, 0.6f, colEa, colLnA, 8.0e-7f, 0.05f, 0.15f, 0.3f, 0.9f, 0.18f, 2f, 0.02f);
            t[6] = new TissueProps("deep fascia", 0.3f, 0.4f, 1100f, 3300f, 10f, 0.6f, colEa, colLnA, 5.0e-7f, 0.05f, 0.15f, 0.25f, 0.9f, 0.18f, 2f, 0.02f);
            t[7] = new TissueProps("muscle", 0.55f, 0.49f, 1090f, 3421f, 37f, 0.75f, musEa, musLnA, 1.0e-5f, 0.25f, 0.35f, 0.7f, 0.7f, 0.10f, 2f, 0.15f);
            t[8] = new TissueProps("artery", 0.7f, 0.52f, 1050f, 3617f, 0f, 0.8f, colEa, colLnA, 5.0e-6f, 0.30f, 0.6f, 1.2f, 1.0f, 0.25f, 2f, 0f);
            t[9] = new TissueProps("vein", 0.7f, 0.52f, 1050f, 3617f, 0f, 0.8f, colEa, colLnA, 8.0e-6f, 0.35f, 0.6f, 1.2f, 1.0f, 0.25f, 2f, 0f);
            t[10] = new TissueProps("nerve", 0.2f, 0.49f, 1075f, 3613f, 20f, 0.7f, musEa, musLnA, 3.0e-6f, 0.20f, 0.4f, 0.9f, 1.0f, 0.10f, 1.5f, 0f);
            t[11] = new TissueProps("lipoma", 0.045f, 0.21f, 920f, 2348f, 15f, 0.2f, fatEa, fatLnA, 3.0e-5f, 0.50f, 0.25f, 0.5f, 0.6f, 0.04f, 0.6f, 0.01f);
            t[12] = new TissueProps("lipoma capsule", 0.3f, 0.4f, 1100f, 3300f, 10f, 0.6f, colEa, colLnA, 4.0e-6f, 0.15f, 0.3f, 0.7f, 0.2f, 0.15f, 2f, 0.01f);
            t[13] = new TissueProps("blood", 0.7f, 0.52f, 1050f, 3617f, 0f, 0.8f, bloodEa, bloodLnA, 1e-4f, 9f, 9f, 9f, 0f, 0f, 1f, 0f);
            t[14] = new TissueProps("areolar tissue", 0.35f, 0.45f, 1050f, 3500f, 25f, 0.72f, colEa, colLnA, 6.0e-5f, 0.80f, 0.4f, 1.2f, 0.1f, 0.10f, 1.5f, 0.02f);
            t[15] = new TissueProps("viscera", 0.55f, 0.52f, 1079f, 3540f, 800f, 0.76f, musEa, musLnA, 2.0e-5f, 0.30f, 0.3f, 0.4f, 0.8f, 0.08f, 1.5f, 0.15f);
            t[16] = new TissueProps("serosa", 0.3f, 0.4f, 1100f, 3300f, 15f, 0.6f, colEa, colLnA, 1.5e-6f, 0.08f, 0.2f, 0.6f, 0.9f, 0.15f, 2f, 0.02f);
            return t;
        }

        public static string Name(int t) { return t >= 0 && t < Count ? Props[t].name : "?"; }
        public static readonly string[] StateNames = { "normal", "coagulated", "desiccated", "carbonized", "vaporized", "dissected" };

        public static bool IsVessel(Tissue t) { return t == Tissue.Artery || t == Tissue.Vein; }
        /// <summary>tissues a blunt dissector cannot divide: they bridge a separated plane until torn or cut</summary>
        public static bool IsBridging(Tissue t) { return t == Tissue.Artery || t == Tissue.Vein || t == Tissue.Nerve; }

        /// <summary>Perfusion rate w_b in (m3 blood / m3 tissue / s) times tissue density, as in the web client.</summary>
        public static float PerfusionRate(TissueProps p) { return (p.perfusion * 1e-6f / 60f) * p.rho; }
        public static float Diffusivity(TissueProps p) { return p.k / (p.rho * p.c); }

        /// <summary>Packs the table for the GPU: sigma, k, rho, c, w_b, water, Ea/R, lnA, cohesion, oozeRate, tearStrain, 0 per tissue.</summary>
        public static float[] Pack()
        {
            var o = new float[Count * PropsStride];
            for (int i = 0; i < Count; i++)
            {
                var p = Props[i]; int b = i * PropsStride;
                o[b] = p.sigma; o[b + 1] = p.k; o[b + 2] = p.rho; o[b + 3] = p.c; o[b + 4] = PerfusionRate(p);
                o[b + 5] = p.water; o[b + 6] = (float)(p.Ea / R); o[b + 7] = (float)p.lnA;
                o[b + 8] = p.cohesion; o[b + 9] = p.oozeRate; o[b + 10] = p.tearStrain; o[b + 11] = 0;
            }
            return o;
        }
    }
}
