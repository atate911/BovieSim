using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BovieSim
{
    /// <summary>
    /// Orchestrates the electrothermal, bleeding, dissection and probe kernels each frame (port of src/physics/pipeline.ts):
    /// pick, then (active) seed + red/black SOR + heating + generator, then bioheat substeps over the thermal box,
    /// coaptation, blunt dissection and tear carving, blood surface/source/drops/flow/commit, constraint probe, slice.
    /// Results come back with AsyncGPUReadback one frame later.
    /// </summary>
    public sealed class PhysicsPipeline : IDisposable
    {
        /// <summary>section image size in pixels: 640 x 400 at scale 1 (DetailSettings.sliceScale), drawn with bilinear filtering</summary>
        public readonly int sliceW, sliceH;
        const int StateWords = 96;
        const int MaxDrops = 16;
        public const int MaxTears = 16;

        readonly ComputeShader cs;
        readonly GpuVolume vol;
        readonly ComputeBuffer props, state, drops, tears, dummyF4, dummyU4, rimHist;
        ComputeBuffer segs, probes, latDisp;
        int segCount;
        public readonly RenderTexture sliceTex;
        readonly uint[] zeros = new uint[StateWords];

        readonly int kSeed, kRed, kBlack, kHeating, kGenerator, kBioheat, kSurfaceHeight, kBloodSource, kBloodDrops, kBloodFlow, kBloodCommit, kCoapt, kDissect, kTearCarve, kProbe, kPick, kSlice, kRim;
        readonly int[] allKernels;

        public readonly SimReadout readout = new SimReadout();
        public float simTime;
        public float sigmaScale = 1f;
        public int relaxSweeps = 40;
        public readonly int subdomain;
        public readonly float dtMax;
        public float totalVapVoxels, activeSeconds;
        /// <summary>cumulative counters mirrored from the web client's `totals`</summary>
        public int totalDissectedVoxels, totalTornVoxels; public float totalGauzeSoakUl;

        // slice plane
        public Vector3 sliceOrigin; public Vector3 sliceU = Vector3.right; public float sliceWidthMm = 32, sliceDepthMm = 20; public int sliceMode; public bool sliceEnabled = true;
        /// <summary>section overlays: 1 isotherms (45/60/100 C), 2 irreversible-damage outline (Omega = 1)</summary>
        public int sliceFlags = 3;
        /// <summary>depth-peel plane of the 3D view (world y, mm), drawn as a dashed line on the section; 1e9 = none</summary>
        public float peelY = 1e9f;

        /// <summary>skin-edge thermal rim (mm): high percentile of the distance from damaged skin to the nearest incision wall</summary>
        public float rimMm; public int rimSamples;
        bool rimInFlight; int rimEvery; readonly uint[] zeros16 = new uint[16];
        /// <summary>frames between requesting the state readback and receiving it (latency of the HUD numbers and GPU pick)</summary>
        public int stateAgeFrames; int stateRequestFrame;

        Vector3 rayO, rayD = Vector3.down;
        readonly int[] tbMin = { int.MaxValue, int.MaxValue, int.MaxValue }, tbMax = { -1, -1, -1 };
        readonly int[] wbMin = { int.MaxValue, int.MaxValue, int.MaxValue }, wbMax = { -1, -1, -1 };
        float hotUntil;
        bool lastStepActive; readonly int[] activeMin = new int[3], activeMax = new int[3];

        /// <summary>The field-solve region around the electrode in the last step, if it was active (voxels, max exclusive).</summary>
        public bool ActiveBox(out int[] min, out int[] max) { min = activeMin; max = activeMax; return lastStepActive; }

        /// <summary>The region still cooling after activation (voxels, max exclusive), if any.</summary>
        public bool ThermalBox(out int[] min, out int[] max)
        {
            min = new[] { Math.Max(0, tbMin[0]), Math.Max(0, tbMin[1]), Math.Max(0, tbMin[2]) };
            max = new[] { tbMax[0], tbMax[1], tbMax[2] };
            return simTime < hotUntil && tbMax[0] > tbMin[0];
        }
        int lastSubsteps = 2;
        int probeEvery;
        bool stateInFlight, probeInFlight;
        Vector3 tool2A, tool2B, tool2Vel; float tool2Radius, tool2Pressure, tool2Absorb = 1; int tool2Mode, tool2LastMode;
        float dissectThreshold = TissueTable.DissectSpreadCohesion, dissectRadius = 0.9f;
        readonly Vector4[] dropData = new Vector4[MaxDrops];
        readonly Vector4[] tearData = new Vector4[MaxTears * 2];
        public readonly List<Vector4> pendingDrops = new List<Vector4>();
        /// <summary>torn lattice links waiting to carve the tissue they ran through (Lattice.TakeTears)</summary>
        public readonly List<TearRecord> pendingTears = new List<TearRecord>();

        /// <summary>latest probe results; Fresh is cleared by TakeProbe</summary>
        byte[] probeFlags; float[] probeOmega, probeDry; bool probeFresh;

        /// <param name="maxSubdomain">largest field-solve region (voxels per side); the region aims for ~14 mm</param>
        /// <param name="sliceScale">section image scale: 1 = 640 x 400, 2 = 1280 x 800</param>
        public PhysicsPipeline(ComputeShader shader, GpuVolume volume, int maxSubdomain = 64, float sliceScale = 2f)
        {
            cs = shader; vol = volume;
            sliceW = Math.Max(1, (int)Math.Round(640 * sliceScale / 8)) * 8; sliceH = Math.Max(1, (int)Math.Round(400 * sliceScale / 8)) * 8;
            float h = vol.cpu.h;
            subdomain = Math.Max(24, Math.Min(maxSubdomain, (int)Math.Round(14 / h)));
            subdomain = (subdomain + 3) / 4 * 4;
            float aMax = 0; for (int t = 1; t < TissueTable.Count; t++) aMax = Math.Max(aMax, TissueTable.Diffusivity(TissueTable.Props[t]));
            dtMax = Math.Min(0.8f * (h * 1e-3f) * (h * 1e-3f) / (6 * aMax), 0.004f);

            var packed = TissueTable.Pack();   // 12 floats per tissue = 3 float4
            props = new ComputeBuffer(packed.Length / 4, 16); props.SetData(packed);
            state = new ComputeBuffer(StateWords, 4); state.SetData(zeros);
            drops = new ComputeBuffer(MaxDrops, 16);
            tears = new ComputeBuffer(MaxTears * 2, 16);
            dummyF4 = new ComputeBuffer(2, 16);
            dummyU4 = new ComputeBuffer(1, 16);
            rimHist = new ComputeBuffer(16, 4);
            latDisp = new ComputeBuffer(1, 16);
            sliceTex = new RenderTexture(sliceW, sliceH, 0, RenderTextureFormat.ARGB32) { enableRandomWrite = true, filterMode = FilterMode.Bilinear, name = "BovieSlice" };
            sliceTex.Create();

            kSeed = cs.FindKernel("SeedPotential"); kRed = cs.FindKernel("RelaxRed"); kBlack = cs.FindKernel("RelaxBlack");
            kHeating = cs.FindKernel("Heating"); kGenerator = cs.FindKernel("Generator"); kBioheat = cs.FindKernel("Bioheat");
            kSurfaceHeight = cs.FindKernel("SurfaceHeight"); kBloodSource = cs.FindKernel("BloodSource"); kBloodDrops = cs.FindKernel("BloodDrops");
            kBloodFlow = cs.FindKernel("BloodFlow"); kBloodCommit = cs.FindKernel("BloodCommit"); kCoapt = cs.FindKernel("CoaptMark");
            kDissect = cs.FindKernel("Dissect"); kTearCarve = cs.FindKernel("TearCarve");
            kProbe = cs.FindKernel("ConstraintProbe"); kPick = cs.FindKernel("Pick"); kSlice = cs.FindKernel("Slice"); kRim = cs.FindKernel("RimMeasure");
            allKernels = new[] { kSeed, kRed, kBlack, kHeating, kGenerator, kBioheat, kSurfaceHeight, kBloodSource, kBloodDrops, kBloodFlow, kBloodCommit, kCoapt, kDissect, kTearCarve, kProbe, kPick, kSlice, kRim };

            vol.SetVolumeInfo(cs);
            cs.SetInts("_LatDims", 2, 2, 2, 0);
            foreach (var k in allKernels)
            {
                cs.SetBuffer(k, "_Props", props); cs.SetBuffer(k, "_State", state);
                cs.SetBuffer(k, "_Cells", vol.cells); cs.SetBuffer(k, "_Occ", vol.occupancy); cs.SetBuffer(k, "_Water", vol.water);
                cs.SetBuffer(k, "_Omega", vol.omega); cs.SetBuffer(k, "_Heat", vol.heat); cs.SetBuffer(k, "_Blood", vol.blood);
                cs.SetBuffer(k, "_Drops", drops); cs.SetBuffer(k, "_Tears", tears); cs.SetBuffer(k, "_Segs", dummyF4); cs.SetBuffer(k, "_Probes", dummyU4);
                cs.SetBuffer(k, "_Src", vol.temp); cs.SetBuffer(k, "_Dst", vol.tempNext);
                cs.SetBuffer(k, "_LatDisp", latDisp);
                cs.SetTexture(k, "_SliceTex", sliceTex);
                cs.SetBuffer(k, "_RimHist", rimHist);
            }
            cs.SetInt("_TearCount", 0);
            cs.SetVector("_Tool3", Vector4.zero);
            cs.SetVector("_Dis", new Vector4(dissectThreshold, dissectRadius, 1, 0));
            // tissue top heights for every column once (blood mesh and droplet landings need them everywhere)
            SetBox("_WBox0", 0, 0, 0, 1); SetBox("_WBox1", vol.cpu.nx, vol.cpu.ny, vol.cpu.nz, 0);
            cs.Dispatch(kSurfaceHeight, Ceil(vol.cpu.nx, 8), Ceil(vol.cpu.ny, 8), 1);
        }

        static int Ceil(int a, int b) { return (a + b - 1) / b; }
        void SetBox(string name, int a, int b, int c, int d) { cs.SetInts(name, a, b, c, d); }

        public void SetPickRay(Vector3 originMm, Vector3 dir) { rayO = originMm; rayD = dir; }

        /// <summary>
        /// Secondary instrument: forceps or hemostat jaws (a to b, mode 1 grasping), suction tip (a, mode 2), gauze pad
        /// (a, radius, mode 3), hemostat spreading (tips a, b, mode 4) or pushing (previous to current tip midpoint, mode 5).
        /// </summary>
        public void SetSecondaryTool(Vector3 a, Vector3 b, float radius, int mode) { tool2A = a; tool2B = b; tool2Radius = radius; tool2Mode = mode; }
        /// <summary>Gauze motion (velocity mm/s, pressure 0..1, absorb 1 or 0 when saturated); in modes 4/5 the velocity slot carries the jaws' entry point.</summary>
        public void SetSecondaryMotion(Vector3 vel, float pressure, float absorb = 1f) { tool2Vel = vel; tool2Pressure = pressure; tool2Absorb = absorb; }
        /// <summary>Blunt dissection parameters: spreading jaws separate planes up to the spread cohesion threshold, pushing tips up to the lower push threshold.</summary>
        public void SetDissect(bool spread, float jawRadius)
        {
            dissectThreshold = spread ? TissueTable.DissectSpreadCohesion : TissueTable.DissectPushCohesion;
            dissectRadius = jawRadius + 0.35f;
        }

        public void MarkWound(int[] min, int[] max)
        {
            for (int a = 0; a < 3; a++) { wbMin[a] = Math.Min(wbMin[a], min[a]); wbMax[a] = Math.Max(wbMax[a], max[a]); }
        }

        void MarkWoundAround(Vector3 a, Vector3 b, float marginMm)
        {
            var v = vol.cpu;
            float li, lj, lk, hi, hj, hk;
            v.WorldToVoxel(Math.Min(a.x, b.x) - marginMm, Math.Min(a.y, b.y) - marginMm, Math.Min(a.z, b.z) - marginMm, out li, out lj, out lk);
            v.WorldToVoxel(Math.Max(a.x, b.x) + marginMm, Math.Max(a.y, b.y) + marginMm, Math.Max(a.z, b.z) + marginMm, out hi, out hj, out hk);
            MarkWound(new[] { Mathf.FloorToInt(li) - 1, Mathf.FloorToInt(lj) - 1, Mathf.FloorToInt(lk) - 1 }, new[] { Mathf.CeilToInt(hi) + 2, Mathf.CeilToInt(hj) + 2, Mathf.CeilToInt(hk) + 2 });
        }

        public void SetConstraintSegments(Vector4[] segData)
        {
            if (segs != null) segs.Release(); if (probes != null) probes.Release();
            segCount = segData.Length / 2;
            segs = new ComputeBuffer(Math.Max(2, segData.Length), 16); segs.SetData(segData);
            probes = new ComputeBuffer(Math.Max(1, segCount), 16);
            probeFlags = new byte[segCount]; probeOmega = new float[segCount]; probeDry = new float[segCount];
            foreach (var k in allKernels) { cs.SetBuffer(k, "_Segs", segs); cs.SetBuffer(k, "_Probes", probes); }
            cs.SetInt("_SegCount", segCount);
        }

        /// <summary>Shares the lattice displacement buffer (also bound by the surface extractor).</summary>
        public void SetLatticeDisplacement(ComputeBuffer disp, int nx, int ny, int nz, Vector3 origin, float spacing)
        {
            foreach (var k in allKernels) cs.SetBuffer(k, "_LatDisp", disp);
        }

        /// <summary>Latest constraint probe: flags (bit 0 severed, bit 2 char), max Omega and dryness per constraint.</summary>
        public bool TakeProbe(out byte[] flags, out float[] omegaMax, out float[] dry)
        {
            flags = probeFlags; omegaMax = probeOmega; dry = probeDry;
            if (!probeFresh) return false;
            probeFresh = false; return true;
        }

        void WoundBox(out int[] o, out int[] s)
        {
            var v = vol.cpu;
            if (wbMax[0] <= wbMin[0]) { o = new[] { 0, 0, 0 }; s = new[] { 0, 0, 0 }; return; }
            o = new[] { Math.Max(0, wbMin[0]), Math.Max(0, wbMin[1]), Math.Max(0, wbMin[2]) };
            s = new[] { Math.Min(v.nx, wbMax[0]) - o[0], Math.Min(v.ny, wbMax[1]) - o[1], Math.Min(v.nz, wbMax[2]) - o[2] };
        }

        /// <summary>Runs one frame of physics. Returns true when tissue state may have changed (surface needs re-extraction).</summary>
        public bool Step(ElectrodeTool tool, FT10 gen, float frameDt)
        {
            var v = vol.cpu;
            float dtFrame = Math.Min(frameDt, 0.05f);
            bool active = gen.activation != Activation.None && !gen.remFault && tool.visible && readout.hasHit;
            lastStepActive = active;
            if (!active) { readout.powerW = 0; readout.iRms = 0; readout.contactVoxels = 0; }
            int S = subdomain;
            int[] box0 = { 0, 0, 0 };
            if (active)
            {
                float vi, vj, vk; v.WorldToVoxel(tool.tip.x, tool.tip.y, tool.tip.z, out vi, out vj, out vk);
                box0 = new[] { (int)Math.Round(vi) - S / 2, (int)Math.Round(vj) - S / 2, (int)Math.Round(vk) - S / 2 };
                var hi = new[] { box0[0] + S, box0[1] + S, box0[2] + S };
                for (int a = 0; a < 3; a++) { tbMin[a] = Math.Min(tbMin[a], box0[a]); tbMax[a] = Math.Max(tbMax[a], hi[a]); }
                MarkWound(box0, hi);
                for (int a = 0; a < 3; a++) { activeMin[a] = box0[a]; activeMax[a] = hi[a]; }
                hotUntil = simTime + 10;
                activeSeconds += dtFrame;
            }
            bool hot = simTime < hotUntil && tbMax[0] > tbMin[0];
            int[] tb0 = { 0, 0, 0 }, tb1 = { 0, 0, 0 };
            if (hot)
            {
                tb0 = new[] { Math.Max(0, tbMin[0]), Math.Max(0, tbMin[1]), Math.Max(0, tbMin[2]) };
                tb1 = new[] { Math.Min(v.nx, tbMax[0]) - tb0[0], Math.Min(v.ny, tbMax[1]) - tb0[1], Math.Min(v.nz, tbMax[2]) - tb0[2] };
            }
            else { for (int a = 0; a < 3; a++) { tbMin[a] = int.MaxValue; tbMax[a] = -1; } }

            // blunt dissection and tears open tissue outside the electrode's box: widen the wound box around them
            bool dissecting = tool2Mode == 4 || tool2Mode == 5;
            if (dissecting) { MarkWoundAround(tool2A, tool2B, dissectRadius + 1); MarkWoundAround(tool2Vel, tool2B, dissectRadius + 1); }
            int nt = Math.Min(pendingTears.Count, MaxTears);
            for (int n = 0; n < nt; n++) { var t = pendingTears[n]; MarkWoundAround(t.a, t.b, t.radius + 1); }

            int substeps = (int)Math.Ceiling(dtFrame / dtMax);
            if (substeps % 2 == 1) substeps++;
            float dt = dtFrame / substeps;
            lastSubsteps = substeps;

            // ---- parameters
            var mode = gen.ActiveMode;
            float reach = mode != null ? Math.Max(0.3f * v.h, mode.ArcReachMm) : 0;
            SetBox("_Box0", box0[0], box0[1], box0[2], S);
            SetBox("_TBox0", tb0[0], tb0[1], tb0[2], 0);
            SetBox("_TBox1", tb1[0], tb1[1], tb1[2], 0);
            cs.SetVector("_Tip", new Vector4(tool.tip.x, tool.tip.y, tool.tip.z, reach));
            cs.SetVector("_Axis", new Vector4(tool.axis.x, tool.axis.y, tool.axis.z, tool.Length));
            cs.SetVector("_WDir", new Vector4(tool.wdir.x, tool.wdir.y, tool.wdir.z, tool.HalfWidth));
            cs.SetVector("_NDir", new Vector4(tool.ndir.x, tool.ndir.y, tool.ndir.z, tool.HalfThickness));
            cs.SetVector("_Gen", mode != null ? new Vector4(gen.ActiveWatts, mode.zLo, mode.zHi, mode.VMaxRms) : new Vector4(0, 100, 1000, 0));
            cs.SetVector("_Gen2", new Vector4(mode != null ? mode.vapFactor : 0, active ? 1 : 0, (int)tool.type, sigmaScale));
            cs.SetVector("_NF", new Vector4(mode != null ? mode.nearField : 0, 0, 0, 0));
            cs.SetVector("_DT", new Vector4(dt, simTime, 15, mode != null ? mode.contactMm : 0));
            cs.SetVector("_Ray", rayO);
            cs.SetVector("_RayDir", new Vector4(rayD.x, rayD.y, rayD.z, 600));
            cs.SetVector("_SliceO", new Vector4(sliceOrigin.x, sliceOrigin.y, sliceOrigin.z, sliceWidthMm));
            cs.SetVector("_SliceU", new Vector4(sliceU.x, sliceU.y, sliceU.z, sliceW));
            cs.SetVector("_SliceV", new Vector4(0, -1, 0, sliceH));
            cs.SetVector("_SliceParams", new Vector4(sliceDepthMm, sliceMode, sliceFlags, peelY));
            int[] wo, ws; WoundBox(out wo, out ws);
            SetBox("_WBox0", wo[0], wo[1], wo[2], substeps);
            SetBox("_WBox1", ws[0], ws[1], ws[2], 0);
            cs.SetVector("_Tool2", new Vector4(tool2A.x, tool2A.y, tool2A.z, tool2Radius));
            cs.SetVector("_Tool2B", new Vector4(tool2B.x, tool2B.y, tool2B.z, tool2Mode));
            cs.SetVector("_Tool3", new Vector4(tool2Vel.x, tool2Vel.y, tool2Vel.z, tool2Pressure));
            cs.SetVector("_Dis", new Vector4(dissectThreshold, dissectRadius, tool2Absorb, 0));
            int nd = Math.Min(pendingDrops.Count, MaxDrops);
            for (int n = 0; n < nd; n++) dropData[n] = pendingDrops[n];
            pendingDrops.Clear();
            if (nd > 0) drops.SetData(dropData, 0, 0, nd);
            cs.SetInt("_DropCount", nd);
            for (int n = 0; n < nt; n++)
            {
                var t = pendingTears[n];
                tearData[n * 2] = new Vector4(t.a.x, t.a.y, t.a.z, t.radius);
                tearData[n * 2 + 1] = new Vector4(t.b.x, t.b.y, t.b.z, (int)t.tissue);
            }
            pendingTears.RemoveRange(0, nt);
            if (nt > 0) tears.SetData(tearData, 0, 0, nt * 2);
            cs.SetInt("_TearCount", nt);

            // per-frame accumulators (cumulative counters 20, 23-25, 72-73 are kept)
            ResetState();

            // ---- dispatch
            Bind(kPick, vol.potential, vol.potentialNext);
            cs.Dispatch(kPick, 1, 1, 1);
            int wg = Ceil(S, 4);
            if (active)
            {
                Bind(kSeed, vol.temp, vol.potential); cs.Dispatch(kSeed, wg, wg, wg);
                Bind(kRed, vol.temp, vol.potential); Bind(kBlack, vol.temp, vol.potential);
                for (int it = 0; it < relaxSweeps; it++) { cs.Dispatch(kRed, wg, wg, wg); cs.Dispatch(kBlack, wg, wg, wg); }
                Bind(kHeating, vol.potential, vol.potentialNext); cs.Dispatch(kHeating, wg, wg, wg);
                cs.Dispatch(kGenerator, 1, 1, 1);
            }
            if (hot)
            {
                int gx = Ceil(tb1[0], 4), gy = Ceil(tb1[1], 4), gz = Ceil(tb1[2], 4);
                for (int s = 0; s < substeps; s++)
                {
                    if ((s & 1) == 0) Bind(kBioheat, vol.temp, vol.tempNext); else Bind(kBioheat, vol.tempNext, vol.temp);
                    cs.Dispatch(kBioheat, gx, gy, gz);
                }
            }
            if (tool2Mode == 1 || tool2LastMode == 1) cs.Dispatch(kCoapt, 3, 3, 3);
            tool2LastMode = tool2Mode;
            if (dissecting)
            {
                // the kernel derives the same box origin from the three points; dispatch covers its extent (x, z, y) -> (i, j, k)
                float r = dissectRadius, h = v.h;
                var a = tool2A; var b = tool2B; var e = tool2Vel;
                int ex = Mathf.CeilToInt((Mathf.Max(a.x, b.x, e.x) - Mathf.Min(a.x, b.x, e.x) + 2 * r) / h) + 2;
                int ey = Mathf.CeilToInt((Mathf.Max(a.y, b.y, e.y) - Mathf.Min(a.y, b.y, e.y) + 2 * r) / h) + 2;
                int ez = Mathf.CeilToInt((Mathf.Max(a.z, b.z, e.z) - Mathf.Min(a.z, b.z, e.z) + 2 * r) / h) + 2;
                cs.Dispatch(kDissect, Ceil(ex, 4), Ceil(ez, 4), Ceil(ey, 4));
            }
            if (nt > 0) cs.Dispatch(kTearCarve, 1, 1, 1);
            if (ws[0] > 0 && ws[1] > 0 && ws[2] > 0)
            {
                foreach (var k in new[] { kSurfaceHeight, kBloodSource, kBloodDrops, kBloodFlow, kBloodCommit }) Bind(k, vol.temp, vol.tempNext);
                cs.Dispatch(kSurfaceHeight, Ceil(ws[0], 8), Ceil(ws[1], 8), 1);
                cs.Dispatch(kBloodSource, Ceil(ws[0], 4), Ceil(ws[1], 4), Ceil(ws[2], 4));
                if (nd > 0) cs.Dispatch(kBloodDrops, 1, 1, 1);
                int fx = Ceil(ws[0] + 8, 8), fy = Ceil(ws[1] + 8, 8);
                for (int it = 0; it < 2; it++) { cs.Dispatch(kBloodFlow, fx, fy, 1); cs.Dispatch(kBloodCommit, fx, fy, 1); }
            }
            if (segCount > 0 && (hot || dissecting || nt > 0 || probeEvery++ % 10 == 0) && !probeInFlight)
            {
                cs.Dispatch(kProbe, Ceil(segCount, 64), 1, 1);
                probeInFlight = true;
                AsyncGPUReadback.Request(probes, OnProbe);
            }
            if (sliceEnabled)
            {
                if (sliceMode >= 4) Bind(kSlice, vol.potential, vol.potentialNext); else Bind(kSlice, vol.temp, vol.tempNext);
                cs.Dispatch(kSlice, Ceil(sliceW, 8), Ceil(sliceH, 8), 1);
            }
            if (ws[0] > 0 && ws[1] > 0 && ws[2] > 0 && !rimInFlight && rimEvery++ % 15 == 0)
            {
                rimHist.SetData(zeros16);
                cs.Dispatch(kRim, Ceil(ws[0], 4), Ceil(ws[1], 4), Ceil(ws[2], 4));
                rimInFlight = true;
                AsyncGPUReadback.Request(rimHist, OnRim);
            }
            if (!stateInFlight) { stateInFlight = true; stateRequestFrame = Time.frameCount; AsyncGPUReadback.Request(state, OnState); }
            simTime += dtFrame;
            return hot || dissecting || nt > 0;
        }

        void Bind(int k, ComputeBuffer src, ComputeBuffer dst) { cs.SetBuffer(k, "_Src", src); cs.SetBuffer(k, "_Dst", dst); }

        void ResetState()
        {
            // contiguous ranges keep the uploads few: [0..4), [16..20), [21..23), [26..29), [64..72)
            state.SetData(zeros, 0, 0, 4); state.SetData(zeros, 0, 16, 4); state.SetData(zeros, 0, 21, 2); state.SetData(zeros, 0, 26, 3); state.SetData(zeros, 0, 64, 8);
        }

        void OnRim(AsyncGPUReadbackRequest req)
        {
            rimInFlight = false;
            if (req.hasError) return;
            var h = req.GetData<uint>();
            // bin 15 = no incision wall within reach (a surface burn, not an edge rim)
            long total = 0; for (int b = 1; b < 15; b++) total += h[b];
            rimSamples = (int)total;
            if (total < 4) { rimMm = 0; return; }
            long target = (long)Math.Ceiling(total * 0.95), cum = 0;
            for (int b = 1; b < 15; b++) { cum += h[b]; if (cum >= target) { rimMm = (b - 0.5f) * vol.cpu.h; return; } }
        }

        /// <summary>Reads back the blood buffer (two float4 per column; the tissue top height is [n * 2].z) for the CPU surface picker.</summary>
        public void RequestSurfaceHeights(Action<Unity.Collections.NativeArray<Vector4>> onData)
        {
            AsyncGPUReadback.Request(vol.blood, req => { if (!req.hasError) onData(req.GetData<Vector4>()); });
        }

        void OnState(AsyncGPUReadbackRequest req)
        {
            stateInFlight = false;
            stateAgeFrames = Time.frameCount - stateRequestFrame;
            if (req.hasError) return;
            var u = req.GetData<uint>();
            var r = readout;
            Func<int, float> F = i => BitConverter.ToSingle(BitConverter.GetBytes(u[i]), 0);
            r.contactVoxels = (int)u[1]; r.vapVoxels = u[2] / 1000f;
            r.zOhm = F(4); r.powerW = F(5); r.vRms = (float)Math.Sqrt(Math.Max(F(6), 0)); r.iRms = F(7);
            r.hasHit = F(11) > 0.5f; r.hit = new Vector3(F(8), F(9), F(10));
            r.hitCell = u[12]; r.hitIdx = (int)u[13];
            r.maxTempC = u[16] / 100f; r.carbonVoxels = (int)u[17]; r.newCoagVoxels = (int)u[18];
            r.muscleHeatVoxels = (int)u[19]; r.bloodLostMl = u[20] / 1000f; r.exposedArterial = (int)u[21]; r.exposedVenous = (int)u[22];
            r.sealedVoxels = (int)u[23]; r.charPlugVoxels = (int)u[24]; r.rebleeds = (int)u[25];
            r.poolMl = u[26] / 2e6f; r.poolAtTipMm = u[27] / 1000f;
            r.bleedCount = (int)Math.Min(u[28], 8u);
            for (int n = 0; n < r.bleedCount; n++) r.bleedPoints[n] = new Vector4(F(32 + n * 4), F(33 + n * 4), F(34 + n * 4), F(35 + n * 4));
            r.spatterCount = (int)Math.Min(u[64], 4u);
            for (int n = 0; n < r.spatterCount; n++) r.spatterPoints[n] = new Vector4(F(76 + n * 4), F(77 + n * 4), F(78 + n * 4), F(79 + n * 4));
            // the flow/commit pair runs twice per frame: pooled and clotted volumes are summed twice
            r.boilColumns = (int)(u[65] / 2); r.oozeVoxels = (int)u[66]; r.clotMl = u[67] / 2e6f;
            r.dissectedVoxels = (int)u[68]; r.tornVoxels = (int)u[69]; r.gauzeSoakUl = u[70] / 1000f; r.visceraHeatVoxels = (int)u[71];
            r.visceraVapVoxels = u[72] / 1000f; r.oozeMl = u[73] / 1000f;
            totalVapVoxels += r.vapVoxels;
            totalDissectedVoxels += r.dissectedVoxels; totalTornVoxels += r.tornVoxels; totalGauzeSoakUl += r.gauzeSoakUl;
        }

        void OnProbe(AsyncGPUReadbackRequest req)
        {
            probeInFlight = false;
            if (req.hasError || probeFlags == null) return;
            var d = req.GetData<uint>();
            int n = Math.Min(segCount, d.Length / 4);
            for (int i = 0; i < n; i++)
            {
                probeFlags[i] = (byte)(d[i * 4] & 7u);
                probeOmega[i] = BitConverter.ToSingle(BitConverter.GetBytes(d[i * 4 + 1]), 0);
                probeDry[i] = BitConverter.ToSingle(BitConverter.GetBytes(d[i * 4 + 2]), 0);
            }
            probeFresh = true;
        }

        public void Dispose()
        {
            foreach (var b in new[] { props, state, drops, tears, dummyF4, dummyU4, segs, probes, latDisp, rimHist }) if (b != null) b.Release();
            if (sliceTex != null) sliceTex.Release();
        }
    }
}
