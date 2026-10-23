// Shared definitions for the Bovie Sim compute kernels. Port of the WGSL VolumeInfo header.
// Axes: i -> X (mm), j -> Z (mm), k -> Y (up). Origin at the centre of the nominal skin surface.
#ifndef BOVIE_SIM_COMMON
#define BOVIE_SIM_COMMON

int4 _Dims;     // nx, ny, nz, airLayers
float4 _HInfo;  // x = voxel edge (mm), y = voxel edge (m)

#define NX (_Dims.x)
#define NY (_Dims.y)
#define NZ (_Dims.z)
#define AIR (_Dims.w)
#define HMM (_HInfo.x)
#define HM (_HInfo.y)

static const uint T_AIR = 0u, T_EPI = 1u, T_DERMIS = 2u, T_FAT = 3u, T_SEPTUM = 4u, T_SFASCIA = 5u, T_DFASCIA = 6u;
static const uint T_MUSCLE = 7u, T_ARTERY = 8u, T_VEIN = 9u, T_NERVE = 10u, T_LIPOMA = 11u, T_CAPSULE = 12u, T_BLOOD = 13u;
static const uint S_NORMAL = 0u, S_COAG = 1u, S_DESIC = 2u, S_CARBON = 3u, S_VAPOR = 4u;
static const uint FLAG_DERMAL_PLEXUS = 1u << 24;
static const uint FLAG_COAPTED = 1u << 26, FLAG_SEALED = 1u << 27, FLAG_CHARPLUG = 1u << 28, FLAG_BLOODCOVER = 1u << 29, FLAG_REBLED = 1u << 30;

static const int3 NB6[6] = { int3(1, 0, 0), int3(-1, 0, 0), int3(0, 1, 0), int3(0, -1, 0), int3(0, 0, 1), int3(0, 0, -1) };

uint CellType(uint c) { return c & 0xffu; }
uint CellState(uint c) { return (c >> 8) & 0xffu; }
uint CellAux(uint c) { return (c >> 16) & 0xffu; }
uint PackCellState(uint c, uint s) { return (c & 0xffff00ffu) | ((s & 0xffu) << 8); }

bool InVolume(int i, int j, int k) { return i >= 0 && j >= 0 && k >= 0 && i < NX && j < NY && k < NZ; }
uint Idx3(int i, int j, int k) { return (uint)i + (uint)NX * ((uint)j + (uint)NY * (uint)k); }

// voxel centre (fractional voxel coords) -> mm
float3 VoxelToWorld(float3 p)
{
    return float3((p.x + 0.5 - NX * 0.5) * HMM, (p.z + 0.5 - (NZ - AIR)) * HMM, (p.y + 0.5 - NY * 0.5) * HMM);
}
float3 WorldOf(int i, int j, int k) { return VoxelToWorld(float3(i, j, k)); }
// mm -> nearest voxel
int3 WorldToVoxelRound(float3 p)
{
    return int3((int)round(p.x / HMM + NX * 0.5 - 0.5), (int)round(p.z / HMM + NY * 0.5 - 0.5), (int)round(p.y / HMM + (NZ - AIR) - 0.5));
}

float Hash13(float3 p)
{
    float3 q = frac(p * float3(0.1031, 0.1030, 0.0973));
    q += dot(q, q.yzx + 33.33);
    return frac((q.x + q.y) * q.z);
}

// ---- mechanics lattice displacement (mm), sampled at a material position ----------------------
StructuredBuffer<float4> _LatDisp;
int4 _LatDims;      // nx, ny, nz, enabled
float4 _LatOrigin;  // xyz origin (mm), w = spacing (mm)

float3 LatticeDisp(float3 p)
{
    if (_LatDims.w == 0) return float3(0, 0, 0);
    float3 f = (p - _LatOrigin.xyz) / _LatOrigin.w;
    int i0 = clamp((int)floor(f.x), 0, _LatDims.x - 2);
    int j0 = clamp((int)floor(f.y), 0, _LatDims.y - 2);
    int k0 = clamp((int)floor(f.z), 0, _LatDims.z - 2);
    float3 t = saturate(f - float3(i0, j0, k0));
    float3 d = float3(0, 0, 0);
    [unroll] for (int dk = 0; dk < 2; dk++)
    [unroll] for (int dj = 0; dj < 2; dj++)
    [unroll] for (int di = 0; di < 2; di++)
    {
        uint n = (uint)(i0 + di) + (uint)_LatDims.x * ((uint)(j0 + dj) + (uint)_LatDims.y * (uint)(k0 + dk));
        float w = (di == 1 ? t.x : 1 - t.x) * (dj == 1 ? t.y : 1 - t.y) * (dk == 1 ? t.z : 1 - t.z);
        d += _LatDisp[n].xyz * w;
    }
    return d;
}

#endif
