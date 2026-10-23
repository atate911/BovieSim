using System;

namespace BovieSim
{
    public enum ModeId { Pure, Blend, Low, Valleylab, Fulgurate, Spray }
    public enum Activation { None, Cut, Coag }

    public sealed class ModeSpec
    {
        public ModeId id; public string label; public bool isCut;
        public int maxWatts;
        /// <summary>~ constant-power band, ohms</summary>
        public float zLo, zHi;
        /// <summary>~ peak open-circuit voltage, V; crest factor Vpeak/Vrms; duty cycle</summary>
        public float vPeak, crest, duty;
        /// <summary>fraction of boiling energy that removes tissue</summary>
        public float vapFactor;
        /// <summary>~ arc reach scale: mm of gap bridged per kV of peak voltage</summary>
        public float arcMmPerKv;
        /// <summary>~ thickness of tissue electrically coupled to the electrode surface, mm</summary>
        public float contactMm;
        /// <summary>~ fraction of delivered power dissipated in the unresolved near-field layer</summary>
        public float nearField;
        public float toneHz;
        public float ArcReachMm { get { return vPeak / 1000f * arcMmPerKv; } }
        public float VMaxRms { get { return vPeak / crest; } }
    }

    public struct GeneratorOutput { public float power, vRms, zOhm; }

    /// <summary>Valleylab FT10 model (monopolar). Port of src/core/generator/ft10.ts; approximations marked there.</summary>
    public sealed class FT10
    {
        public static readonly ModeSpec[] Modes =
        {
            new ModeSpec { id = ModeId.Pure, label = "Pure", isCut = true, maxWatts = 300, zLo = 100, zHi = 2000, vPeak = 1600, crest = 1.4f, duty = 1.0f, vapFactor = 1.0f, arcMmPerKv = 0.08f, contactMm = 0.1f, nearField = 0.7f, toneHz = 500 },
            new ModeSpec { id = ModeId.Blend, label = "Blend", isCut = true, maxWatts = 200, zLo = 100, zHi = 1500, vPeak = 2200, crest = 2.8f, duty = 0.5f, vapFactor = 0.6f, arcMmPerKv = 0.15f, contactMm = 0.15f, nearField = 0.5f, toneHz = 500 },
            new ModeSpec { id = ModeId.Low, label = "Low", isCut = true, maxWatts = 200, zLo = 100, zHi = 600, vPeak = 1200, crest = 1.4f, duty = 1.0f, vapFactor = 1.0f, arcMmPerKv = 0.05f, contactMm = 0.1f, nearField = 0.7f, toneHz = 500 },
            new ModeSpec { id = ModeId.Valleylab, label = "Valleylab", isCut = true, maxWatts = 300, zLo = 100, zHi = 2000, vPeak = 2000, crest = 2.4f, duty = 0.6f, vapFactor = 0.7f, arcMmPerKv = 0.15f, contactMm = 0.15f, nearField = 0.5f, toneHz = 500 },
            new ModeSpec { id = ModeId.Fulgurate, label = "Fulgurate", isCut = false, maxWatts = 120, zLo = 200, zHi = 3000, vPeak = 4500, crest = 6.0f, duty = 0.06f, vapFactor = 0.12f, arcMmPerKv = 0.45f, contactMm = 0.3f, nearField = 0.4f, toneHz = 700 },
            new ModeSpec { id = ModeId.Spray, label = "Spray", isCut = false, maxWatts = 120, zLo = 300, zHi = 4000, vPeak = 6000, crest = 8.0f, duty = 0.04f, vapFactor = 0.08f, arcMmPerKv = 0.6f, contactMm = 0.3f, nearField = 0.4f, toneHz = 700 },
        };

        public static ModeSpec Mode(ModeId id) { return Modes[(int)id]; }

        public static ModeId Parse(string s, ModeId fallback)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "pure": return ModeId.Pure;
                case "blend": return ModeId.Blend;
                case "low": return ModeId.Low;
                case "valleylab": return ModeId.Valleylab;
                case "fulgurate": return ModeId.Fulgurate;
                case "spray": return ModeId.Spray;
                default: return fallback;
            }
        }

        /// <summary>Delivered power and RMS voltage into load Z (mirrored in Sim.compute Generator kernel).</summary>
        public static GeneratorOutput Output(ModeSpec mode, float settingW, float zOhm)
        {
            float setting = Math.Min(settingW, mode.maxWatts);
            if (!(zOhm > 0) || float.IsInfinity(zOhm)) return new GeneratorOutput { power = 0, vRms = mode.VMaxRms, zOhm = float.PositiveInfinity };
            float p;
            if (zOhm < mode.zLo) p = setting * (zOhm / mode.zLo);
            else if (zOhm <= mode.zHi) p = setting;
            else p = setting * (mode.zHi / zOhm);
            float vMax = mode.VMaxRms;
            float v2 = p * zOhm;
            if (v2 > vMax * vMax) { v2 = vMax * vMax; p = v2 / zOhm; }
            return new GeneratorOutput { power = p, vRms = (float)Math.Sqrt(v2), zOhm = zOhm };
        }

        public ModeId cutMode = ModeId.Pure, coagMode = ModeId.Fulgurate;
        public int cutWatts = 30, coagWatts = 30;
        public Activation activation = Activation.None;
        /// <summary>REM return-electrode fault: refuses to activate</summary>
        public bool remFault;

        public ModeSpec ActiveMode { get { return activation == Activation.Cut ? Mode(cutMode) : activation == Activation.Coag ? Mode(coagMode) : null; } }
        public int ActiveWatts { get { return activation == Activation.Cut ? cutWatts : activation == Activation.Coag ? coagWatts : 0; } }

        public void Adjust(bool cut, int delta)
        {
            if (cut) cutWatts = Math.Max(1, Math.Min(Mode(cutMode).maxWatts, cutWatts + delta));
            else coagWatts = Math.Max(1, Math.Min(Mode(coagMode).maxWatts, coagWatts + delta));
        }

        public void CycleMode(bool cut)
        {
            if (cut) cutMode = cutMode == ModeId.Valleylab ? ModeId.Pure : (ModeId)((int)cutMode + 1);
            else coagMode = coagMode == ModeId.Fulgurate ? ModeId.Spray : ModeId.Fulgurate;
            Adjust(cut, 0);
        }

        public string SettingsKey { get { return cutMode + "|" + cutWatts + "|" + coagMode + "|" + coagWatts; } }
    }
}
