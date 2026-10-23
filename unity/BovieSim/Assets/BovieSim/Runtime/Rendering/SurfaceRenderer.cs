using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace BovieSim
{
    /// <summary>
    /// Extracts the tissue surface on the GPU (Surface.compute) straight into Mesh vertex buffers and draws it
    /// with ordinary MeshRenderers: one submesh per material bucket (tissue class x thermal state), each with a
    /// stock HDRP Lit material, so HDRP shadows, SSS, motion vectors and post all apply unchanged.
    /// Bucket counts come back by AsyncGPUReadback; two meshes alternate so a mesh is never drawn while it is
    /// being rewritten. Also builds the blood-pool surface mesh from the blood height field.
    /// </summary>
    public sealed class SurfaceRenderer : IDisposable
    {
        public const int Buckets = SimAssets.TissueBuckets;

        sealed class Slot
        {
            public Mesh mesh; public GraphicsBuffer vb; public GameObject go; public MeshRenderer mr; public bool pending; public int requestFrame;
        }

        readonly ComputeShader cs;
        readonly GpuVolume vol;
        readonly Slot[] slots = new Slot[2];
        int writeSlot, displaySlot = -1;
        readonly ComputeBuffer bucketCount, bucketCursor, bucketOffset, args, occSmooth;
        ComputeBuffer latDisp;
        readonly int kSmooth, kCount, kScan, kEmit, kBlood;
        readonly uint[] zeros = new uint[Buckets];
        public readonly int capacity;
        public int lastVertexCount;
        /// <summary>frames from starting an extraction to showing it</summary>
        public int lastAgeFrames;
        public bool capped;
        readonly Mesh bloodMesh; readonly GraphicsBuffer bloodVb; public readonly GameObject bloodGo;
        public bool dirty = true;

        public SurfaceRenderer(ComputeShader shader, GpuVolume volume, Transform fieldRoot, Material[] bucketMaterials, Material bloodMaterial)
        {
            cs = shader; vol = volume;
            var v = vol.cpu;
            capacity = Mathf.Clamp(v.nx * v.ny * 24, 600000, 4800000) / 3 * 3;
            bucketCount = new ComputeBuffer(Buckets, 4); bucketCursor = new ComputeBuffer(Buckets, 4); bucketOffset = new ComputeBuffer(Buckets, 4);
            args = new ComputeBuffer(Buckets, 20);
            latDisp = new ComputeBuffer(8, 16);
            occSmooth = new ComputeBuffer(v.count, 4);
            kSmooth = cs.FindKernel("SmoothOcc"); kCount = cs.FindKernel("CountTris"); kScan = cs.FindKernel("ScanBuckets"); kEmit = cs.FindKernel("EmitTris"); kBlood = cs.FindKernel("BloodMesh");

            // bounds in field mm: the bed plus headroom for deformation and pooled blood
            var bounds = new Bounds(new Vector3(0, -v.SizeDepth / 2 + 5, 0), new Vector3(v.SizeX + 20, v.SizeDepth + 30, v.SizeZ + 20));
            var identity = new int[capacity];
            for (int i = 0; i < capacity; i++) identity[i] = i;
            for (int s = 0; s < 2; s++)
            {
                var m = new Mesh { name = "TissueSurface" + s, indexFormat = IndexFormat.UInt32 };
                m.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                m.SetVertexBufferParams(capacity,
                    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                    new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                    new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
                m.SetIndexBufferParams(capacity, IndexFormat.UInt32);
                m.SetIndexBufferData(identity, 0, 0, capacity, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
                m.subMeshCount = Buckets;
                for (int b = 0; b < Buckets; b++) m.SetSubMesh(b, new SubMeshDescriptor(0, 0, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                m.bounds = bounds;
                var go = new GameObject("Tissue Surface " + s);
                go.transform.SetParent(fieldRoot, false);
                go.AddComponent<MeshFilter>().sharedMesh = m;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = bucketMaterials;
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.receiveShadows = true;
                mr.enabled = false;
                slots[s] = new Slot { mesh = m, vb = m.GetVertexBuffer(0), go = go, mr = mr };
            }

            // blood pool surface: one vertex per surface column
            int nx = v.nx, ny = v.ny;
            bloodMesh = new Mesh { name = "BloodPool", indexFormat = IndexFormat.UInt32 };
            bloodMesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            bloodMesh.SetVertexBufferParams(nx * ny,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
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
            bloodMesh.bounds = bounds;
            bloodVb = bloodMesh.GetVertexBuffer(0);
            bloodGo = new GameObject("Blood Pool");
            bloodGo.transform.SetParent(fieldRoot, false);
            bloodGo.AddComponent<MeshFilter>().sharedMesh = bloodMesh;
            var bmr = bloodGo.AddComponent<MeshRenderer>();
            bmr.sharedMaterial = bloodMaterial;
            bmr.shadowCastingMode = ShadowCastingMode.Off;

            vol.SetVolumeInfo(cs);
            cs.SetInt("_Capacity", capacity);
            cs.SetFloat("_BloodShowMm", 0.015f);
            cs.SetInts("_LatDims", 2, 2, 2, 0);
            cs.SetVector("_LatOrigin", new Vector4(0, 0, 0, 1));
            foreach (var k in new[] { kSmooth, kCount, kEmit }) cs.SetBuffer(k, "_OccS", occSmooth);
            foreach (var k in new[] { kSmooth, kCount, kScan, kEmit, kBlood })
            {
                cs.SetBuffer(k, "_Occ", vol.occupancy); cs.SetBuffer(k, "_Cells", vol.cells); cs.SetBuffer(k, "_BloodCols", vol.blood);
                cs.SetBuffer(k, "_BucketCount", bucketCount); cs.SetBuffer(k, "_BucketCursor", bucketCursor); cs.SetBuffer(k, "_BucketOffset", bucketOffset);
                cs.SetBuffer(k, "_Args", args); cs.SetBuffer(k, "_LatDisp", latDisp);
            }
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

        /// <summary>Re-extracts into the hidden mesh when the previous extraction has been shown. Returns false when skipped.</summary>
        public bool Extract()
        {
            var s = slots[writeSlot];
            if (s.pending) return false;
            var v = vol.cpu;
            bucketCount.SetData(zeros);
            cs.SetBuffer(kEmit, "_Verts", s.vb);
            cs.SetBuffer(kCount, "_Verts", s.vb);
            int gx = (v.nx + 2) / 4, gy = (v.ny + 2) / 4, gz = (v.nz + 2) / 4;
            cs.Dispatch(kSmooth, (v.nx + 3) / 4, (v.ny + 3) / 4, (v.nz + 3) / 4);
            cs.Dispatch(kCount, gx, gy, gz);
            cs.Dispatch(kScan, 1, 1, 1);
            cs.Dispatch(kEmit, gx, gy, gz);
            s.pending = true; s.requestFrame = Time.frameCount;
            int slot = writeSlot;
            AsyncGPUReadback.Request(bucketCount, req => OnCounts(slot, req));
            dirty = false;
            return true;
        }

        void OnCounts(int slot, AsyncGPUReadbackRequest req)
        {
            var s = slots[slot];
            s.pending = false;
            if (req.hasError || s.mesh == null) return;
            lastAgeFrames = Time.frameCount - s.requestFrame;
            var counts = req.GetData<uint>();
            int offset = 0;
            for (int b = 0; b < Buckets; b++)
            {
                int n = (int)counts[b];
                s.mesh.SetSubMesh(b, new SubMeshDescriptor(offset, n, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                offset += n;
            }
            lastVertexCount = offset;
            capped = offset >= capacity - 3;
            s.mr.enabled = true;
            if (displaySlot >= 0 && displaySlot != slot) slots[displaySlot].mr.enabled = false;
            displaySlot = slot;
            writeSlot = 1 - slot;
        }

        public void UpdateBlood()
        {
            cs.SetBuffer(kBlood, "_Verts", bloodVb);
            cs.Dispatch(kBlood, (vol.cpu.nx + 7) / 8, (vol.cpu.ny + 7) / 8, 1);
        }

        public void Dispose()
        {
            foreach (var b in new[] { bucketCount, bucketCursor, bucketOffset, args, latDisp, occSmooth }) if (b != null) b.Release();
            foreach (var s in slots) if (s != null) { if (s.vb != null) s.vb.Dispose(); if (s.mesh != null) UnityEngine.Object.Destroy(s.mesh); }
            if (bloodVb != null) bloodVb.Dispose();
            if (bloodMesh != null) UnityEngine.Object.Destroy(bloodMesh);
        }
    }
}
