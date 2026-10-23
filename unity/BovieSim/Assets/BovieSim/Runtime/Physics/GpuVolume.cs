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
        /// <summary>per surface column, 2 float4: (h, age, surfaceY, hNext), (clot, temp, stain, topK)</summary>
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
            blood = new ComputeBuffer(v.nx * v.ny * 2, 16);
            var init = new Vector4[v.nx * v.ny * 2];
            for (int c = 0; c < v.nx * v.ny; c++) init[c * 2 + 1] = new Vector4(0, 37, 0, -1);
            blood.SetData(init);
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
