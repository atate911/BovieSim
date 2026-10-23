using System;

namespace BovieSim
{
    public enum BluntOutcome { Parts, Tears, Stretched, Resists }

    /// <summary>
    /// The rules of steel against tissue, as the MechCut kernel applies them (kept here for the tests and the docs).
    /// Sharp: a blade divides whatever it passes through, leaving fractional occupancy at its face. Blunt: spreading
    /// parts fat (so the plane around a capsule opens), tears small vessels, stretches larger vessels and nerves, and
    /// is resisted by septa, fasciae, skin, muscle, the capsule and the lipoma itself.
    /// </summary>
    public static class Dissection
    {
        public static BluntOutcome Blunt(Tissue ty, float radiusMm = 0)
        {
            if (ty == Tissue.Fat) return BluntOutcome.Parts;
            if (ty == Tissue.Artery || ty == Tissue.Vein) return radiusMm <= 0.3f ? BluntOutcome.Tears : BluntOutcome.Stretched;
            if (ty == Tissue.Nerve) return BluntOutcome.Stretched;
            return BluntOutcome.Resists;
        }

        /// <summary>Fat next to a vessel over 0.3 mm stays as its cuff: the vessel is tented in the spread rather than bared.</summary>
        public static bool FatKeepsCuff(float neighbourVesselRadiusMm) { return neighbourVesselRadiusMm > 0.3f; }

        /// <summary>Occupancy kept by a voxel at signed distance d (mm) from a blade face, voxel edge h.</summary>
        public static float SharpKeep(float d, float h) { return Math.Min(1f, Math.Max(0f, 0.5f + d / h)); }
    }
}
