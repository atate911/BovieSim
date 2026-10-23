using System;
using UnityEngine;

namespace BovieSim
{
    /// <summary>GPU copies of the voxel volume plus the ping-pong pairs used by the kernels.</summary>
    public sealed class GpuVolume : IDisposable
    {
        public readonly Volume cpu;
        public readonly ComputeBuffer cells, occupancy, water, omega, heat;
        public readonly ComputeBuffer temp, tempNext, potential, potentialNext;
        /// <summary>per surface column: h, age, surfaceY, hNext</summary>
        public readonly ComputeBuffer blood;

        public GpuVolume(Volume v)
        {
            cpu = v;
            int n = v.count;
            cells = new ComputeBuffer(n, 4); cells.SetData(v.cell);
            occupancy = new ComputeBuffer(n, 4); occupancy.SetData(v.occupancy);
            water = new ComputeBuffer(n, 4); water.SetData(v.water);
            omega = new ComputeBuffer(n, 4); omega.SetData(v.omega);
            heat = new ComputeBuffer(n, 4);
            temp = new ComputeBuffer(n, 4); temp.SetData(v.temp);
            tempNext = new ComputeBuffer(n, 4); tempNext.SetData(v.temp);
            potential = new ComputeBuffer(n, 4);
            potentialNext = new ComputeBuffer(n, 4);
            blood = new ComputeBuffer(v.nx * v.ny, 16);
            blood.SetData(new Vector4[v.nx * v.ny]);
        }

        public void SetVolumeInfo(ComputeShader cs)
        {
            cs.SetInts("_Dims", cpu.nx, cpu.ny, cpu.nz, cpu.airLayers);
            cs.SetVector("_HInfo", new Vector4(cpu.h, cpu.h * 1e-3f, 0, 0));
        }

        public void Dispose()
        {
            foreach (var b in new[] { cells, occupancy, water, omega, heat, temp, tempNext, potential, potentialNext, blood }) if (b != null) b.Release();
        }
    }
}
