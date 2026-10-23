using System;
using UnityEngine;

namespace BovieSim.EditorTools
{
    /// <summary>
    /// Bakes tileable tissue texture sets (albedo, tangent-space normal, HDRP mask map) from the deterministic
    /// noise in Core/Noise.cs. One tile covers <see cref="TileMm"/> of tissue and is applied with triplanar world
    /// mapping, so cut faces at any angle get detail of the right physical size. Colours are chosen to match what
    /// a surgeon sees under an OR light: pale pink dermis with punctate bleeding, yellow lobulated fat with white
    /// septa, beefy red striated muscle, pearly white fascia, and the blanched / browned / charred states.
    /// </summary>
    public static class TissueTextures
    {
        public const float TileMm = 20f;
        public static readonly string[] ClassNames = { "Skin", "Dermis", "Fat", "Lipoma", "Fibrous", "Muscle", "Artery", "Vein", "Viscera" };
        /// <summary>bucket of the desiccated and carbonized states (after the normal and coagulated class blocks)</summary>
        public static int DesiccatedBucket { get { return ClassNames.Length * 2; } }
        public static int CarbonizedBucket { get { return ClassNames.Length * 2 + 1; } }

        public sealed class Set
        {
            public int size;
            public Color[] albedo, normal, mask;
            /// <summary>suggested material scalars</summary>
            public float normalScale = 1f, coat;
        }

        static float F(double u, double v, int period, int oct, int seed) { return (float)Noise.Fbm2P(u, v, period, oct, seed); }
        static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3 - 2 * t); }

        /// <summary>bucket 0..19: 0-8 normal classes, 9-17 coagulated, 18 desiccated, 19 carbonized</summary>
        public static Set Bake(int bucket, int size, float skinTone)
        {
            int n = size * size;
            var col = new Color[n]; var height = new float[n]; var smooth = new float[n]; var ao = new float[n];
            int nc = ClassNames.Length;
            int cls = bucket < 2 * nc ? bucket % nc : -1;
            bool coag = bucket >= nc && bucket < 2 * nc;
            // the state textures are keyed 16 (desiccated) / 17 (carbonized) inside BakeClass
            int classKey = bucket < 2 * nc ? cls : bucket == DesiccatedBucket ? 16 : 17;
            var set = new Set { size = size };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int i = x + y * size;
                    double u = (double)x / size, v = (double)y / size;
                    Color c; float h, s;
                    BakeClass(classKey, u, v, skinTone, out c, out h, out s);
                    if (coag) Coagulate(cls, u, v, ref c, ref h, ref s);
                    col[i] = c; height[i] = h; smooth[i] = s;
                }
            // ambient occlusion from local height concavity (cheap cavity term)
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float hc = height[x + y * size], sum = 0;
                    for (int d = 1; d <= 4; d++)
                    {
                        sum += height[(x + d) % size + y * size] + height[(x - d + size) % size + y * size]
                             + height[x + ((y + d) % size) * size] + height[x + ((y - d + size) % size) * size];
                    }
                    float cav = sum / 16f - hc;
                    ao[x + y * size] = Mathf.Clamp01(1f - cav * 6f);
                }
            float strength = classKey == 17 ? 3.5f : classKey == 16 ? 2.5f : 2.0f;
            set.albedo = col;
            set.normal = HeightToNormal(height, size, strength);
            set.mask = new Color[n];
            for (int i = 0; i < n; i++) set.mask[i] = new Color(0, ao[i], 1, smooth[i]);
            set.coat = bucket >= 2 * nc ? 0 : coag ? 0.15f : cls == 0 ? 0.25f : 0.8f;
            return set;
        }

        static void BakeClass(int k, double u, double v, float tone, out Color c, out float h, out float s)
        {
            switch (k)
            {
                case 0: // epidermis: pores, fine creases, mottled melanin/erythema, chlorhexidine tint
                {
                    var pore = Noise.Voronoi2P(u, v, 48, 11);
                    float pores = 1 - Smooth(0.05f, 0.16f, (float)pore.f1);
                    float crease = Mathf.Abs(F(u, v * 0.35, 6, 4, 21) - 0.5f) * 2; crease = 1 - Smooth(0.0f, 0.06f, crease);
                    float crease2 = Mathf.Abs(F(u * 0.4, v, 9, 3, 23) - 0.5f) * 2; crease2 = 1 - Smooth(0.0f, 0.05f, crease2);
                    float mott = F(u, v, 4, 4, 31);
                    c = TissuePalette.Skin(tone);
                    c = Color.Lerp(c, c * new Color(1.05f, 0.86f, 0.84f), (mott - 0.4f) * 0.8f);
                    c *= 1 - pores * 0.12f - (crease + crease2 * 0.6f) * 0.08f;
                    c *= TissuePalette.Prep(TissuePalette.BakedPrep); // the runtime corrects to the scenario prep and tone
                    h = -pores * 0.35f - crease * 0.5f - crease2 * 0.3f + F(u, v, 24, 3, 37) * 0.25f;
                    s = 0.42f + mott * 0.1f - pores * 0.1f;
                    break;
                }
                case 1: // dermis (cut edge): pale pink-white collagen with punctate dermal bleeding
                {
                    var pt = Noise.Voronoi2P(u, v, 26, 41);
                    float dot = 1 - Smooth(0.04f, 0.11f, (float)pt.f1);
                    bool bleeds = pt.id % 3 == 0;
                    float fib = F(u * 3, v * 0.6, 10, 4, 43);
                    c = Color.Lerp(TissuePalette.Dermis * new Color(0.93f, 0.85f, 0.84f), TissuePalette.Dermis, fib);
                    if (bleeds) c = Color.Lerp(c, TissuePalette.BloodFresh, dot * 0.9f);
                    h = fib * 0.3f + (bleeds ? -dot * 0.15f : 0);
                    s = 0.58f + (bleeds ? dot * 0.3f : 0);
                    break;
                }
                case 2: // subcutaneous fat: 2-3 mm lobules, white fibrous septa carrying small vessels
                case 3: // lipoma: larger, paler, more uniform lobules
                {
                    bool lip = k == 3;
                    var lob = Noise.Voronoi2P(u, v, lip ? 5 : 8, lip ? 53 : 51);
                    float edge = (float)(lob.f2 - lob.f1);
                    float septum = 1 - Smooth(0.02f, lip ? 0.05f : 0.08f, edge);
                    float tint = (lob.id % 17) / 17f;
                    var fat = lip ? TissuePalette.Lipoma : TissuePalette.Fat;
                    c = Color.Lerp(fat, fat * new Color(1.02f, 0.94f, 0.85f), tint);
                    c *= 0.92f + 0.1f * F(u, v, 32, 3, 57);
                    c = Color.Lerp(c, TissuePalette.Septum, septum * 0.75f);
                    // fine vessels running in the septa
                    float vessel = septum * Smooth(0.62f, 0.75f, F(u, v, 12, 3, 59));
                    if (!lip) c = Color.Lerp(c, TissuePalette.Artery, vessel * 0.8f);
                    float bulge = Mathf.Sqrt(Mathf.Clamp01(1 - (float)lob.f1 * 1.1f));
                    h = bulge * 0.8f - septum * 0.4f;
                    s = 0.74f + bulge * 0.12f;
                    break;
                }
                case 4: // fascia / septum / capsule / nerve: pearly white, parallel collagen striations
                {
                    float str = F(u * 0.5, v * 8, 4, 4, 61);
                    float sheen = F(u, v, 6, 3, 63);
                    c = Color.Lerp(TissuePalette.Fascia * 0.91f, TissuePalette.Fascia * 1.03f, str);
                    c = Color.Lerp(c, new Color(0.85f, 0.74f, 0.70f), Smooth(0.6f, 0.8f, sheen) * 0.3f);
                    h = str * 0.5f;
                    s = 0.72f + str * 0.12f;
                    break;
                }
                case 5: // skeletal muscle: beefy red fibres, paler perimysium between fascicles
                {
                    var fasc = Noise.Voronoi2P(u * 0.25, v, 14, 71);
                    float peri = 1 - Smooth(0.02f, 0.07f, (float)(fasc.f2 - fasc.f1));
                    float fib = F(u * 0.15, v * 6, 12, 3, 73);
                    c = Color.Lerp(TissuePalette.Muscle * new Color(0.75f, 0.55f, 0.55f), TissuePalette.Muscle * 1.07f, fib);
                    c *= 0.9f + 0.15f * ((fasc.id % 11) / 11f);
                    c = Color.Lerp(c, new Color(0.78f, 0.62f, 0.58f), peri * 0.5f);
                    h = fib * 0.4f - peri * 0.4f;
                    s = 0.62f + fib * 0.1f;
                    break;
                }
                case 6: // artery / extravasated blood: glossy red
                {
                    float f = F(u, v, 6, 4, 81);
                    c = Color.Lerp(TissuePalette.Artery * 0.6f, TissuePalette.Artery * 0.95f, f);
                    h = f * 0.2f; s = 0.86f;
                    break;
                }
                case 7: // vein: dark blue-purple through a thin wall
                {
                    float f = F(u, v, 6, 4, 83);
                    c = Color.Lerp(TissuePalette.Vein * 0.62f, TissuePalette.Vein, f);
                    h = f * 0.2f; s = 0.84f;
                    break;
                }
                case 8: // viscera (bowel wall / solid organ): dusky red, smooth, wet, fine vascular arcades under a glistening serosa
                {
                    float f = F(u, v, 8, 4, 85);
                    float arc = Smooth(0.66f, 0.78f, F(u * 2, v, 10, 3, 87));
                    c = Color.Lerp(TissuePalette.Viscera * 0.9f, TissuePalette.Viscera * 1.08f, f);
                    c = Color.Lerp(c, TissuePalette.Artery, arc * 0.45f);
                    c = Color.Lerp(c, TissuePalette.Serosa, Smooth(0.7f, 0.9f, F(u, v, 5, 2, 89)) * 0.2f);
                    h = f * 0.3f + arc * 0.15f; s = 0.88f;
                    break;
                }
                case 16: // desiccated: dry tan-brown, cracked, matt
                {
                    var cr = Noise.Voronoi2P(u, v, 10, 91);
                    float crack = 1 - Smooth(0.01f, 0.04f, (float)(cr.f2 - cr.f1));
                    float f = F(u, v, 8, 4, 93);
                    c = Color.Lerp(TissuePalette.Desiccated * 0.92f, TissuePalette.Desiccated * 1.18f, f);
                    c = Color.Lerp(c, new Color(0.22f, 0.14f, 0.09f), crack * 0.8f);
                    h = f * 0.4f - crack * 0.8f; s = 0.18f + f * 0.08f;
                    break;
                }
                default: // 17 carbonized eschar: black, crusted, faint sheen of tar
                {
                    var cr = Noise.Voronoi2P(u, v, 16, 101);
                    float f = F(u, v, 10, 5, 103);
                    float crust = Mathf.Sqrt(Mathf.Clamp01(1 - (float)cr.f1 * 1.2f));
                    c = Color.Lerp(TissuePalette.Char * 0.6f, TissuePalette.Char * 2.6f, f * 0.7f + crust * 0.3f);
                    float ember = Smooth(0.78f, 0.9f, F(u, v, 5, 2, 107));
                    c = Color.Lerp(c, new Color(0.22f, 0.12f, 0.06f), ember * 0.3f);
                    h = crust * 0.9f + f * 0.4f; s = 0.30f + f * 0.15f;
                    break;
                }
            }
        }

        /// <summary>Thermal coagulation: proteins denature, the tissue blanches (grey-white to tan), loses its gloss and scatters less.</summary>
        static void Coagulate(int cls, double u, double v, ref Color c, ref float h, ref float s)
        {
            Color target;
            switch (cls)
            {
                case 0: target = TissuePalette.CoagSkin; break;   // blanched epidermis, faintly tan
                case 1: target = TissuePalette.CoagDermis; break;   // white coagulated dermis
                case 2: target = TissuePalette.CoagFat; break;   // rendered fat: darker, translucent-yellow, oily
                case 3: target = TissuePalette.CoagLipoma; break;
                case 4: target = TissuePalette.CoagFibrous; break;
                case 5: target = TissuePalette.CoagMuscle; break;   // "cooked" grey-brown muscle
                case 6: target = TissuePalette.CoagBlood; break;   // coagulated blood: brown
                case 8: target = TissuePalette.CoagViscera; break; // cooked viscus: grey-brown
                default: target = TissuePalette.CoagVein; break;
            }
            float mott = F(u, v, 7, 4, 111);
            c = Color.Lerp(c, target * (0.92f + 0.16f * mott), 0.8f);
            // browning at the hottest spots
            c = Color.Lerp(c, new Color(0.45f, 0.30f, 0.18f), Smooth(0.65f, 0.85f, mott) * 0.35f);
            s = cls == 2 || cls == 3 ? s * 0.85f : s * 0.45f;
            h = h * 0.6f + mott * 0.3f;
        }

        static Color[] HeightToNormal(float[] h, int size, float strength)
        {
            var o = new Color[h.Length];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = h[(x + 1) % size + y * size] - h[(x - 1 + size) % size + y * size];
                    float dy = h[x + ((y + 1) % size) * size] - h[x + ((y - 1 + size) % size) * size];
                    var nrm = new Vector3(-dx * strength, -dy * strength, 1).normalized;
                    o[x + y * size] = new Color(nrm.x * 0.5f + 0.5f, nrm.y * 0.5f + 0.5f, nrm.z * 0.5f + 0.5f, 1);
                }
            return o;
        }

        /// <summary>Procedural plain-weave drape fabric, used when the ambientCG download is unavailable.</summary>
        public static Set Fabric(int size)
        {
            int n = size * size; var col = new Color[n]; var hgt = new float[n]; var mask = new Color[n];
            var baseC = new Color(0.10f, 0.24f, 0.40f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double u = (double)x / size, v = (double)y / size;
                    float wx = Mathf.Sin((float)(u * 64 * Math.PI * 2)), wy = Mathf.Sin((float)(v * 64 * Math.PI * 2));
                    bool over = ((int)(u * 64) + (int)(v * 64)) % 2 == 0;
                    float hh = over ? Mathf.Abs(wy) : Mathf.Abs(wx);
                    float f = F(u, v, 16, 3, 121);
                    int i = x + y * size;
                    col[i] = baseC * (0.85f + 0.2f * hh + 0.1f * f);
                    hgt[i] = hh * 0.6f + f * 0.2f;
                    mask[i] = new Color(0, 0.8f + 0.2f * hh, 1, 0.12f);
                }
            return new Set { size = size, albedo = col, normal = HeightToNormal(hgt, size, 1.5f), mask = mask };
        }
    }
}
