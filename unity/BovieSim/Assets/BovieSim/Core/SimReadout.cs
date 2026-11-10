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
        /// <summary>boiling-blood spatter sources this frame: xyz mm, w = microlitres</summary>
        public readonly Vector4[] spatterPoints = new Vector4[4];
        public int spatterCount;
        /// <summary>blood columns boiling this frame; raw-surface voxels oozing; clotted blood in the field (ml)</summary>
        public int boilColumns, oozeVoxels; public float clotMl;
        /// <summary>cumulative capillary ooze (ml); voxels separated by blunt dissection / carved by tears this frame</summary>
        public float oozeMl; public int dissectedVoxels, tornVoxels;
        /// <summary>blood absorbed by the gauze this frame (microlitres)</summary>
        public float gauzeSoakUl;
        /// <summary>viscera voxels above 45 C this step; cumulative vaporised viscera (voxels)</summary>
        public int visceraHeatVoxels; public float visceraVapVoxels;
    }
}
