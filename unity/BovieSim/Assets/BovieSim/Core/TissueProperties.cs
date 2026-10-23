using System;

namespace BovieSim
{
    /// <summary>Tissue ids. Must match src/core/tissueProperties.ts and every shader.</summary>
    public enum Tissue : byte
    {
        Air = 0, Epidermis = 1, Dermis = 2, Fat = 3, Septum = 4, SuperficialFascia = 5, DeepFascia = 6,
        Muscle = 7, Artery = 8, Vein = 9, Nerve = 10, Lipoma = 11, LipomaCapsule = 12, Blood = 13,
    }

    public enum TissueState : byte { Normal = 0, Coagulated = 1, Desiccated = 2, Carbonized = 3, Vaporized = 4 }

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

        public TissueProps(string name, float sigma, float k, float rho, float c, float perfusion, float water, double Ea, double lnA)
        { this.name = name; this.sigma = sigma; this.k = k; this.rho = rho; this.c = c; this.perfusion = perfusion; this.water = water; this.Ea = Ea; this.lnA = lnA; }
    }

    /// <summary>
    /// Physical property table, identical to the web client (docs/physics.md is the provenance).
    /// </summary>
    public static class TissueTable
    {
        public const double R = 8.314;
        public const float BodyC = 37f;
        public const float LatentHeatWater = 2.26e6f;
        public const int Count = 14;
        public const int PropsStride = 8;

        static double LnAFor(double Ea, double t1sC) { return Ea / (R * (t1sC + 273.15)); }
        const double HenriquesEa = 6.28e5;
        static readonly double HenriquesLnA = Math.Log(3.1e98);

        public static readonly TissueProps[] Props = Build();

        static TissueProps[] Build()
        {
            var t = new TissueProps[Count];
            double colEa = 3.06e5, colLnA = LnAFor(3.06e5, 65), fatEa = 3.0e5, fatLnA = LnAFor(3.0e5, 65);
            double musEa = 2.6e5, musLnA = Math.Log(2.94e39);
            t[0] = new TissueProps("air", 0f, 0.026f, 1.2f, 1005f, 0f, 0f, 1e9, 0);
            t[1] = new TissueProps("epidermis", 0.05f, 0.24f, 1190f, 3600f, 0f, 0.5f, HenriquesEa, HenriquesLnA);
            t[2] = new TissueProps("dermis", 0.3f, 0.37f, 1109f, 3391f, 106f, 0.65f, HenriquesEa, HenriquesLnA);
            t[3] = new TissueProps("subcutaneous fat", 0.04f, 0.21f, 911f, 2348f, 33f, 0.18f, fatEa, fatLnA);
            t[4] = new TissueProps("fibrous septum", 0.3f, 0.4f, 1100f, 3300f, 20f, 0.6f, colEa, colLnA);
            t[5] = new TissueProps("superficial fascia", 0.3f, 0.4f, 1100f, 3300f, 15f, 0.6f, colEa, colLnA);
            t[6] = new TissueProps("deep fascia", 0.3f, 0.4f, 1100f, 3300f, 10f, 0.6f, colEa, colLnA);
            t[7] = new TissueProps("muscle", 0.55f, 0.49f, 1090f, 3421f, 37f, 0.75f, musEa, musLnA);
            t[8] = new TissueProps("artery", 0.7f, 0.52f, 1050f, 3617f, 0f, 0.8f, colEa, colLnA);
            t[9] = new TissueProps("vein", 0.7f, 0.52f, 1050f, 3617f, 0f, 0.8f, colEa, colLnA);
            t[10] = new TissueProps("nerve", 0.2f, 0.49f, 1075f, 3613f, 20f, 0.7f, musEa, musLnA);
            t[11] = new TissueProps("lipoma", 0.045f, 0.21f, 920f, 2348f, 15f, 0.2f, fatEa, fatLnA);
            t[12] = new TissueProps("lipoma capsule", 0.3f, 0.4f, 1100f, 3300f, 10f, 0.6f, colEa, colLnA);
            t[13] = new TissueProps("blood", 0.7f, 0.52f, 1050f, 3617f, 0f, 0.8f, colEa, colLnA);
            return t;
        }

        public static string Name(int t) { return t >= 0 && t < Count ? Props[t].name : "?"; }
        public static readonly string[] StateNames = { "normal", "coagulated", "desiccated", "carbonized", "vaporized" };

        /// <summary>Perfusion rate w_b in (m3 blood / m3 tissue / s) times tissue density, as in the web client.</summary>
        public static float PerfusionRate(TissueProps p) { return (p.perfusion * 1e-6f / 60f) * p.rho; }
        public static float Diffusivity(TissueProps p) { return p.k / (p.rho * p.c); }

        /// <summary>Packs the table for the GPU: sigma, k, rho, c, w_b, water, Ea/R, lnA per tissue.</summary>
        public static float[] Pack()
        {
            var o = new float[Count * PropsStride];
            for (int i = 0; i < Count; i++)
            {
                var p = Props[i]; int b = i * PropsStride;
                o[b] = p.sigma; o[b + 1] = p.k; o[b + 2] = p.rho; o[b + 3] = p.c; o[b + 4] = PerfusionRate(p);
                o[b + 5] = p.water; o[b + 6] = (float)(p.Ea / R); o[b + 7] = (float)p.lnA;
            }
            return o;
        }
    }
}
