using System;

namespace BovieSim
{
    /// <summary>
    /// CPU copy of the voxel volume. Axes: i -> world X (mm), j -> world Z (mm), k -> world Y (up).
    /// World origin is the centre of the nominal skin surface; tissue lies at Y &lt;= 0.
    /// `cell` packs: bits 0-7 tissue type, 8-10 state, 11-15 exposure clock, 16-23 aux, 24-31 flags (see Cell).
    /// </summary>
    public sealed class Volume
    {
        public readonly int nx, ny, nz, airLayers;
        /// <summary>voxel edge, mm</summary>
        public readonly float h;
        public readonly int count;
        public readonly uint[] cell;
        public readonly float[] occupancy;
        public readonly float[] temp;
        public readonly float[] water;
        public readonly float[] omega;

        public Volume(int nx, int ny, int nz, float h, int airLayers)
        {
            this.nx = nx; this.ny = ny; this.nz = nz; this.h = h; this.airLayers = airLayers;
            count = nx * ny * nz;
            cell = new uint[count]; occupancy = new float[count]; temp = new float[count];
            water = new float[count]; omega = new float[count];
            for (int n = 0; n < count; n++) temp[n] = 37f;
        }

        public int Index(int i, int j, int k) { return i + nx * (j + ny * k); }
        public int SurfaceK { get { return nz - airLayers; } }
        /// <summary>bed size in mm: x, depth (y), z</summary>
        public float SizeX { get { return nx * h; } }
        public float SizeDepth { get { return (nz - airLayers) * h; } }
        public float SizeZ { get { return ny * h; } }

        public void VoxelToWorld(int i, int j, int k, out float x, out float y, out float z)
        {
            x = (i + 0.5f - nx * 0.5f) * h; y = (k + 0.5f - (nz - airLayers)) * h; z = (j + 0.5f - ny * 0.5f) * h;
        }
        public void WorldToVoxel(float x, float y, float z, out float vi, out float vj, out float vk)
        {
            vi = x / h + nx * 0.5f - 0.5f; vj = z / h + ny * 0.5f - 0.5f; vk = y / h + (nz - airLayers) - 0.5f;
        }

        /// <summary>Tissue top height (world Y, mm) of the column under world x, z, from the CPU occupancy.</summary>
        public float SurfaceY(float x, float z)
        {
            float vi, vj, vk; WorldToVoxel(x, 0, z, out vi, out vj, out vk);
            int i = (int)Math.Round(vi), j = (int)Math.Round(vj);
            if (i < 0 || j < 0 || i >= nx || j >= ny) return 0f;
            for (int k = nz - 1; k >= 0; k--)
            {
                if (occupancy[Index(i, j, k)] >= 0.5f) { float wx, wy, wz; VoxelToWorld(i, j, k, out wx, out wy, out wz); return wy + 0.5f * h; }
            }
            return -1e3f;
        }
    }

    public static class Cell
    {
        public const uint FlagDermalPlexus = 1u << 24;
        public const uint FlagFibreX = 1u << 25;
        public const uint FlagCoapted = 1u << 26;
        public const uint FlagSealed = 1u << 27;
        public const uint FlagCharPlug = 1u << 28;
        public const uint FlagBloodCover = 1u << 29;
        public const uint FlagRebled = 1u << 30;
        /// <summary>the blood covering this surface voxel has clotted into coagulum</summary>
        public const uint FlagClotCover = 1u << 31;
        public const uint StateMask = 0x700u, ClockMask = 0xf800u;
        public const int ClockMax = 31;

        public static uint Pack(Tissue type, TissueState state = TissueState.Normal, int aux = 0, uint flags = 0)
        {
            return ((uint)type & 0xff) | (((uint)state & 0x7) << 8) | (((uint)aux & 0xff) << 16) | flags;
        }
        public static Tissue Type(uint c) { return (Tissue)(c & 0xff); }
        public static TissueState State(uint c) { return (TissueState)((c >> 8) & 0x7); }
        public static int Clock(uint c) { return (int)((c >> 11) & 0x1f); }
        public static int Aux(uint c) { return (int)((c >> 16) & 0xff); }
        public static uint WithState(uint c, TissueState s) { return (c & ~StateMask) | (((uint)s & 0x7) << 8); }
        public static uint WithClock(uint c, int k) { k = k < 0 ? 0 : k > 31 ? 31 : k; return (c & ~ClockMask) | (((uint)k & 0x1f) << 11); }
        /// <summary>air made by cutting, vaporising or tearing: a raw wound surface</summary>
        public static readonly uint CutAir = Pack(Tissue.Air, TissueState.Vaporized);
        /// <summary>air made by blunt dissection: a separated plane</summary>
        public static readonly uint DissectedAir = Pack(Tissue.Air, TissueState.Dissected);
    }
}
