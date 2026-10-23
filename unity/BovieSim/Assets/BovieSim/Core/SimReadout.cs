using UnityEngine;

namespace BovieSim
{
    /// <summary>Per-frame readout of generator state, pick, and tissue/bleeding statistics (one frame latency).</summary>
    public sealed class SimReadout
    {
        public float zOhm = float.PositiveInfinity, powerW, vRms, iRms;
        public bool hasHit; public Vector3 hit; public uint hitCell; public int hitIdx;
        public float maxTempC = 37; public int carbonVoxels, newCoagVoxels, contactVoxels, muscleHeatVoxels;
        public float vapVoxels;
        public float bloodLostMl; public int exposedArterial, exposedVenous, sealedVoxels, charPlugVoxels, rebleeds;
        public float poolMl, poolAtTipMm;
        /// <summary>exposed bleeding voxels this frame: xyz mm, w = ml/s (negative = venous)</summary>
        public readonly Vector4[] bleedPoints = new Vector4[8];
        public int bleedCount;
        /// <summary>mechanical cut and blunt dissection this frame (voxels), and dermal plexus voxels oozing</summary>
        public int cutVoxels, bluntVoxels, bluntResist, vesselStretch, nerveStretch, vesselTorn, oozeVoxels;
    }
}
