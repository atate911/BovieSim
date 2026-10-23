using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BovieSim
{
    /// <summary>
    /// Extracts the tissue surface on the GPU (Surface.compute) straight into Mesh vertex buffers, one brick
    /// (32 x 32 x 32 cells) at a time, and draws it with ordinary MeshRenderers: one submesh per material bucket
    /// (tissue class x thermal state), each a stock HDRP Lit material, so shadows, SSS and post apply unchanged.
    /// Only bricks marked dirty are rebuilt (the active electrode region every frame, the hot region and moving
    /// tissue as needed), nearest the instrument first, up to a per-frame cap. Each brick has two meshes: the new
    /// one is written while the old one is shown, then swapped when its bucket counts come back (async readback).
    /// Brick meshes start empty and grow to fit. Also builds the blood-pool surface mesh.
    /// </summary>
    public sealed class SurfaceRenderer : IDisposable
    {
        public const int Buckets = SimAssets.TissueBuckets;
        const int CountStride = Buckets + 1;
        public const int BrickCells = 32;
        const int Stride = 56;

        sealed class Slot
        {
            public Mesh mesh; public GraphicsBuffer vb; public GameObject go; public MeshRenderer mr, overlay;
            public int capacity; public int verts;
        }

        sealed class Brick
        {
            public int[] o = new int[3], n = new int[3];
            public Vector3 centreMm;
            public readonly Slot[] slots = new Slot[2];
            public int writeSlot, displaySlot = -1;
            public bool pending, dirty;
            public int requestFrame;
        }

        struct Pending { public Brick brick; public int slot; public int countBase; }

        readonly ComputeShader cs;
        readonly GpuVolume vol;
        readonly Transform root;
        readonly Material[] materials; readonly Material bloodMaterial;
        Material[] overlayMaterials; bool overlayOn;
        readonly Brick[] bricks; readonly int bx, by, bz;
        readonly List<Brick> queue = new List<Brick>();
        public int maxBricksPerFrame = 48;
        readonly int countCapacity;
        readonly ComputeBuffer bucketCount, bucketCursor, bucketOffset, occSmooth;
        readonly GraphicsBuffer dummyVb;
        ComputeBuffer latDisp;
        readonly int kSmooth, kCount, kScan, kEmit, kBlood;
        readonly uint[] zeros;
        static int[] identity = new int[0];
        public int lastVertexCount;
        /// <summary>frames from starting a brick extraction to showing it (latest)</summary>
        public int lastAgeFrames;
        public bool capped;
        readonly Mesh bloodMesh; readonly GraphicsBuffer bloodVb; public readonly GameObject bloodGo;
        readonly Bounds brickBoundsPad;

        public SurfaceRenderer(ComputeShader shader, GpuVolume volume, Transform fieldRoot, Material[] bucketMaterials, Material bloodMat)
        {
            cs = shader; vol = volume; root = fieldRoot; materials = bucketMaterials; bloodMaterial = bloodMat;
            var v = vol.cpu;
            countCapacity = 256;
            bucketCount = new ComputeBuffer(CountStride * countCapacity, 4); bucketCursor = new ComputeBuffer(CountStride * countCapacity, 4); bucketOffset = new ComputeBuffer(CountStride * countCapacity, 4);
            zeros = new uint[CountStride * countCapacity];
            occSmooth = new ComputeBuffer(v.count, 4);
            dummyVb = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 16, 4);
            latDisp = new ComputeBuffer(8, 16);
            kSmooth = cs.FindKernel("SmoothOcc"); kCount = cs.FindKernel("CountTris"); kScan = cs.FindKernel("ScanBuckets"); kEmit = cs.FindKernel("EmitTris"); kBlood = cs.FindKernel("BloodMesh");

            // bricks over the cell grid (cells i..i+1 make a marching cube, so nx-1 cubes per axis)
            bx = (v.nx - 1 + BrickCells - 1) / BrickCells; by = (v.ny - 1 + BrickCells - 1) / BrickCells; bz = (v.nz - 1 + BrickCells - 1) / BrickCells;
            bricks = new Brick[bx * by * bz];
            for (int k = 0; k < bz; k++)
                for (int j = 0; j < by; j++)
                    for (int i = 0; i < bx; i++)
                    {
                        var b = new Brick();
                        b.o[0] = i * BrickCells; b.o[1] = j * BrickCells; b.o[2] = k * BrickCells;
                        b.n[0] = Math.Min(BrickCells, v.nx - 1 - b.o[0]); b.n[1] = Math.Min(BrickCells, v.ny - 1 - b.o[1]); b.n[2] = Math.Min(BrickCells, v.nz - 1 - b.o[2]);
                        // centre in field mm (i -> x, j -> z, k -> y; see Volume.VoxelToWorld)
                        float fi = b.o[0] + b.n[0] * 0.5f, fj = b.o[1] + b.n[1] * 0.5f, fk = b.o[2] + b.n[2] * 0.5f;
                        b.centreMm = new Vector3((fi + 0.5f - v.nx * 0.5f) * v.h, (fk + 0.5f - (v.nz - v.airLayers)) * v.h, (fj + 0.5f - v.ny * 0.5f) * v.h);
                        bricks[i + bx * (j + by * k)] = b;
                    }
            // brick bounds: the brick plus headroom for lattice deformation (mm)
            float half = BrickCells * v.h * 0.5f + 20f;
            brickBoundsPad = new Bounds(Vector3.zero, Vector3.one * half * 2);

            // blood pool surface: one vertex per surface column
            int nx = v.nx, ny = v.ny;
            bloodMesh = new Mesh { name = "BloodPool", indexFormat = IndexFormat.UInt32 };
            bloodMesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            bloodMesh.SetVertexBufferParams(nx * ny, Layout());
            var tri = new int[(nx - 1) * (ny - 1) * 6];
            int t = 0;
            for (int j = 0; j < ny - 1; j++)
                for (int i = 0; i < nx - 1; i++)
                {
                    int a = i + nx * j, b = a + 1, c = a + nx, d = c + 1;
                    // +Y facing in Unity: cross(b-a, c-a) must point up; (a, c, b) with i->x, j->z
                    tri[t++] = a; tri[t++] = c; tri[t++] = b;
                    tri[t++] = b; tri[t++] = c; tri[t++] = d;
                }
            bloodMesh.SetIndexBufferParams(tri.Length, IndexFormat.UInt32);
            bloodMesh.SetIndexBufferData(tri, 0, 0, tri.Length, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            bloodMesh.subMeshCount = 1;
            bloodMesh.SetSubMesh(0, new SubMeshDescriptor(0, tri.Length, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            bloodMesh.bounds = new Bounds(new Vector3(0, -v.SizeDepth / 2 + 5, 0), new Vector3(v.SizeX + 20, v.SizeDepth + 30, v.SizeZ + 20));
            bloodVb = bloodMesh.GetVertexBuffer(0);
            bloodGo = new GameObject("Blood Pool");
            bloodGo.transform.SetParent(fieldRoot, false);
            bloodGo.AddComponent<MeshFilter>().sharedMesh = bloodMesh;
            var bmr = bloodGo.AddComponent<MeshRenderer>();
            bmr.sharedMaterial = bloodMaterial;
            bmr.shadowCastingMode = ShadowCastingMode.Off;

            vol.SetVolumeInfo(cs);
            cs.SetFloat("_BloodShowMm", 0.015f);
            cs.SetInts("_LatDims", 2, 2, 2, 0);
            cs.SetVector("_LatOrigin", new Vector4(0, 0, 0, 1));
            foreach (var k in new[] { kSmooth, kCount, kEmit }) cs.SetBuffer(k, "_OccS", occSmooth);
            foreach (var k in new[] { kSmooth, kCount, kScan, kEmit, kBlood })
            {
                cs.SetBuffer(k, "_Occ", vol.occupancy); cs.SetBuffer(k, "_Cells", vol.cells); cs.SetBuffer(k, "_BloodCols", vol.blood);
                cs.SetBuffer(k, "_Temp", vol.temp); cs.SetBuffer(k, "_OmegaR", vol.omega);
                cs.SetBuffer(k, "_BucketCount", bucketCount); cs.SetBuffer(k, "_BucketCursor", bucketCursor); cs.SetBuffer(k, "_BucketOffset", bucketOffset);
                cs.SetBuffer(k, "_LatDisp", latDisp);
            }
            MarkAll();
        }

        static VertexAttributeDescriptor[] Layout()
        {
            return new[]
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2),
            };
        }

        /// <summary>Attaches the mechanics lattice: displacement is applied to every extracted vertex.</summary>
        public void SetLattice(int nx, int ny, int nz, Vector3 origin, float spacing, int nodeCount)
        {
            if (latDisp != null) latDisp.Release();
            latDisp = new ComputeBuffer(nodeCount, 16);
            cs.SetInts("_LatDims", nx, ny, nz, 1);
            cs.SetVector("_LatOrigin", new Vector4(origin.x, origin.y, origin.z, spacing));
            foreach (var k in new[] { kSmooth, kCount, kScan, kEmit, kBlood }) cs.SetBuffer(k, "_LatDisp", latDisp);
        }

        public ComputeBuffer LatticeBuffer { get { return latDisp; } }
        public void UploadLattice(Vector4[] disp) { latDisp.SetData(disp); }

        // ------------------------------------------------------------------ dirty marking
        public void MarkAll() { foreach (var b in bricks) Mark(b); }

        void Mark(Brick b) { if (!b.dirty) { b.dirty = true; queue.Add(b); } }

        /// <summary>Marks every brick touching a voxel box (min inclusive, max exclusive), with the smoothing apron.</summary>
        public void MarkVoxelBox(int[] min, int[] max)
        {
            const int apron = 3;
            int i0 = Math.Max(0, (min[0] - apron) / BrickCells), i1 = Math.Min(bx - 1, (max[0] + apron) / BrickCells);
            int j0 = Math.Max(0, (min[1] - apron) / BrickCells), j1 = Math.Min(by - 1, (max[1] + apron) / BrickCells);
            int k0 = Math.Max(0, (min[2] - apron) / BrickCells), k1 = Math.Min(bz - 1, (max[2] + apron) / BrickCells);
            for (int k = k0; k <= k1; k++) for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) Mark(bricks[i + bx * (j + by * k)]);
        }

        /// <summary>Marks bricks within a radius (mm) of a material-space point, e.g. a lattice node that moved.</summary>
        public void MarkSphereMm(Vector3 p, float r)
        {
            var v = vol.cpu;
            float a, b, c, d, e, f;
            v.WorldToVoxel(p.x - r, p.y - r, p.z - r, out a, out b, out c);
            v.WorldToVoxel(p.x + r, p.y + r, p.z + r, out d, out e, out f);
            MarkVoxelBox(new[] { (int)Math.Floor(Math.Min(a, d)), (int)Math.Floor(Math.Min(b, e)), (int)Math.Floor(Math.Min(c, f)) },
                         new[] { (int)Math.Ceiling(Math.Max(a, d)), (int)Math.Ceiling(Math.Max(b, e)), (int)Math.Ceiling(Math.Max(c, f)) });
        }

        public int QueuedBricks { get { return queue.Count; } }

        // ------------------------------------------------------------------ extraction
        /// <summary>Rebuilds up to maxBricksPerFrame dirty bricks, nearest the focus point (mm) first.</summary>
        public void Update(Vector3 focusMm)
        {
            if (queue.Count == 0) return;
            queue.Sort((p, q) => (p.centreMm - focusMm).sqrMagnitude.CompareTo((q.centreMm - focusMm).sqrMagnitude));
            var batch = new List<Pending>();
            int limit = Math.Min(maxBricksPerFrame, countCapacity);
            for (int qi = 0; qi < queue.Count && batch.Count < limit; qi++)
            {
                var b = queue[qi];
                if (b.pending) continue;
                batch.Add(new Pending { brick = b, slot = b.writeSlot, countBase = batch.Count * CountStride });
            }
            if (batch.Count == 0) return;
            bucketCount.SetData(zeros, 0, 0, batch.Count * CountStride);
            foreach (var p in batch)
            {
                var b = p.brick;
                b.dirty = false; queue.Remove(b);
                b.pending = true; b.requestFrame = Time.frameCount;
                var s = b.slots[p.slot];
                int cap = s != null ? s.capacity : 0;
                cs.SetInts("_Brick0", b.o[0], b.o[1], b.o[2], p.countBase);
                cs.SetInts("_BrickN", b.n[0], b.n[1], b.n[2], 0);
                cs.SetInt("_Capacity", cap);
                var vb = s != null ? s.vb : dummyVb;
                cs.SetBuffer(kEmit, "_Verts", vb); cs.SetBuffer(kCount, "_Verts", vb);
                cs.Dispatch(kSmooth, (b.n[0] + 4 + 3) / 4, (b.n[1] + 4 + 3) / 4, (b.n[2] + 4 + 3) / 4);
                cs.Dispatch(kCount, (b.n[0] + 3) / 4, (b.n[1] + 3) / 4, (b.n[2] + 3) / 4);
                cs.Dispatch(kScan, 1, 1, 1);
                if (cap > 0) cs.Dispatch(kEmit, (b.n[0] + 3) / 4, (b.n[1] + 3) / 4, (b.n[2] + 3) / 4);
            }
            var list = batch;
            AsyncGPUReadback.Request(bucketCount, list.Count * CountStride * 4, 0, req => OnCounts(list, req));
        }

        void OnCounts(List<Pending> list, AsyncGPUReadbackRequest req)
        {
            if (req.hasError)
            {
                foreach (var p in list) { p.brick.pending = false; Mark(p.brick); }
                return;
            }
            var counts = req.GetData<uint>();
            bool anyCapped = false;
            foreach (var p in list)
            {
                var b = p.brick;
                b.pending = false;
                if (b.slots[0] != null && b.slots[0].mesh == null) continue; // disposed
                int raw = (int)counts[p.countBase + Buckets];
                var s = b.slots[p.slot];
                int cap = s != null ? s.capacity : 0;
                if (raw > cap)
                {
                    // too small (or not yet allocated): grow both meshes of this brick and rebuild it
                    int newCap = (int)Math.Min(4800000, raw * 1.3 + 3000) / 3 * 3;
                    EnsureSlot(b, 0, newCap); EnsureSlot(b, 1, newCap);
                    if (raw > 4800000) anyCapped = true;
                    Mark(b);
                    continue;
                }
                lastAgeFrames = Time.frameCount - b.requestFrame;
                if (raw == 0)
                {
                    foreach (var sl in b.slots) if (sl != null) { sl.mr.enabled = false; sl.overlay.enabled = false; sl.verts = 0; }
                    b.displaySlot = -1;
                    continue;
                }
                int offset = 0;
                for (int k = 0; k < Buckets; k++)
                {
                    int n = (int)counts[p.countBase + k];
                    s.mesh.SetSubMesh(k, new SubMeshDescriptor(offset, n, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                    offset += n;
                }
                s.verts = offset;
                s.mr.enabled = true; s.overlay.enabled = overlayOn;
                if (b.displaySlot >= 0 && b.displaySlot != p.slot) { var o = b.slots[b.displaySlot]; o.mr.enabled = false; o.overlay.enabled = false; }
                b.displaySlot = p.slot;
                b.writeSlot = 1 - p.slot;
            }
            capped = anyCapped;
            int total = 0; foreach (var b in bricks) if (b.displaySlot >= 0) total += b.slots[b.displaySlot].verts;
            lastVertexCount = total;
        }

        void EnsureSlot(Brick b, int si, int capacity)
        {
            var s = b.slots[si];
            if (s != null && s.capacity >= capacity) return;
            if (s == null)
            {
                s = new Slot();
                s.go = new GameObject("Tissue brick " + b.o[0] / BrickCells + "," + b.o[1] / BrickCells + "," + b.o[2] / BrickCells + (si == 0 ? " a" : " b"));
                s.go.transform.SetParent(root, false);
                s.go.AddComponent<MeshFilter>();
                s.mr = s.go.AddComponent<MeshRenderer>();
                s.mr.sharedMaterials = materials;
                s.mr.shadowCastingMode = ShadowCastingMode.On; s.mr.receiveShadows = true;
                s.mr.enabled = false;
                var ov = new GameObject("Teaching overlay");
                ov.transform.SetParent(s.go.transform, false);
                ov.AddComponent<MeshFilter>();
                s.overlay = ov.AddComponent<MeshRenderer>();
                s.overlay.shadowCastingMode = ShadowCastingMode.Off; s.overlay.receiveShadows = false;
                if (overlayMaterials != null) s.overlay.sharedMaterials = overlayMaterials;
                s.overlay.enabled = false;
                b.slots[si] = s;
            }
            else
            {
                if (s.vb != null) s.vb.Dispose();
                if (s.mesh != null) UnityEngine.Object.Destroy(s.mesh);
                s.mr.enabled = false; s.overlay.enabled = false;
                if (b.displaySlot == si) b.displaySlot = -1;
            }
            if (identity.Length < capacity) { identity = new int[capacity]; for (int i = 0; i < capacity; i++) identity[i] = i; }
            var m = new Mesh { name = "TissueBrick", indexFormat = IndexFormat.UInt32 };
            m.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            m.SetVertexBufferParams(capacity, Layout());
            m.SetIndexBufferParams(capacity, IndexFormat.UInt32);
            m.SetIndexBufferData(identity, 0, 0, capacity, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            m.subMeshCount = Buckets;
            for (int k = 0; k < Buckets; k++) m.SetSubMesh(k, new SubMeshDescriptor(0, 0, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            m.bounds = new Bounds(b.centreMm, brickBoundsPad.size);
            s.mesh = m; s.vb = m.GetVertexBuffer(0); s.capacity = capacity; s.verts = 0;
            s.go.GetComponent<MeshFilter>().sharedMesh = m;
            s.overlay.GetComponent<MeshFilter>().sharedMesh = m;
        }

        /// <summary>Teaching view: the same meshes drawn again with an overlay material (temperature or damage colouring).</summary>
        public void SetOverlay(Material overlay, bool on)
        {
            overlayOn = on && overlay != null;
            if (overlay != null && (overlayMaterials == null || overlayMaterials[0] != overlay))
            {
                overlayMaterials = new Material[Buckets];
                for (int k = 0; k < Buckets; k++) overlayMaterials[k] = overlay;
                foreach (var b in bricks) foreach (var s in b.slots) if (s != null) s.overlay.sharedMaterials = overlayMaterials;
            }
            foreach (var b in bricks) if (b.displaySlot >= 0) b.slots[b.displaySlot].overlay.enabled = overlayOn;
        }

        public void UpdateBlood()
        {
            cs.SetBuffer(kBlood, "_Verts", bloodVb);
            cs.Dispatch(kBlood, (vol.cpu.nx + 7) / 8, (vol.cpu.ny + 7) / 8, 1);
        }

        public void Dispose()
        {
            foreach (var b in new[] { bucketCount, bucketCursor, bucketOffset, latDisp, occSmooth }) if (b != null) b.Release();
            if (dummyVb != null) dummyVb.Dispose();
            foreach (var br in bricks)
                foreach (var s in br.slots)
                    if (s != null) { if (s.vb != null) s.vb.Dispose(); if (s.mesh != null) UnityEngine.Object.Destroy(s.mesh); s.mesh = null; }
            if (bloodVb != null) bloodVb.Dispose();
            if (bloodMesh != null) UnityEngine.Object.Destroy(bloodMesh);
        }
    }
}
