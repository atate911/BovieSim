using UnityEngine;
using UnityEngine.Rendering;

namespace BovieSim
{
    /// <summary>
    /// Draws the tissue strands reported by the mechanics lattice (yielded links through areolar tissue, fat or
    /// capsule) as thin translucent ribbons between their deformed endpoints: the stringy connective tissue that is
    /// drawn out by traction or blunt dissection and snaps when overstretched. The mesh is rebuilt only when the
    /// strand set changes (a few hundred links at most).
    /// </summary>
    public sealed class StrandRenderer : System.IDisposable
    {
        public const int MaxStrands = 4096;
        readonly GameObject go; readonly MeshFilter filter; readonly MeshRenderer renderer;
        readonly Vector4[] a = new Vector4[MaxStrands], b = new Vector4[MaxStrands];
        Mesh mesh; int lastCount = -1; float lastHash;
        public int count;

        public StrandRenderer(Transform fieldRoot, Material material)
        {
            go = new GameObject("Tissue strands");
            go.transform.SetParent(fieldRoot, false);
            filter = go.AddComponent<MeshFilter>();
            renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.enabled = false;
        }

        /// <summary>Refreshes the ribbons from the lattice; cheap when nothing changed.</summary>
        public void Update(Lattice lattice)
        {
            count = lattice.StrandData(a, b);
            if (count == 0) { if (lastCount != 0) { renderer.enabled = false; lastCount = 0; } return; }
            float hash = 0;
            for (int n = 0; n < count; n++) hash += a[n].x * 0.37f + a[n].y * 0.11f + b[n].z * 0.23f + a[n].w;
            if (count == lastCount && Mathf.Abs(hash - lastHash) < 1e-4f) return;
            lastCount = count; lastHash = hash;
            var f = new MeshFactory();
            for (int n = 0; n < count; n++)
            {
                // thinness w (0 fresh .. 1 about to snap) narrows the ribbon
                float r = Mathf.Lerp(0.18f, 0.06f, a[n].w);
                MeshFactory.Strand(f, new Vector3(a[n].x, a[n].y, a[n].z), new Vector3(b[n].x, b[n].y, b[n].z), r);
            }
            if (mesh != null) Object.Destroy(mesh);
            mesh = f.Build("Strands");
            filter.sharedMesh = mesh;
            renderer.enabled = true;
        }

        public void Dispose() { if (mesh != null) Object.Destroy(mesh); if (go != null) Object.Destroy(go); }
    }
}
