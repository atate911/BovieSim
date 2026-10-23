using UnityEngine;

namespace BovieSim
{
    /// <summary>
    /// Same-frame cursor pick on the CPU. The GPU Pick kernel answers 2-4 frames late (async readback), which
    /// made the instrument trail the cursor. This ray-marches a height map of the tissue top (one height per
    /// voxel column, in field mm) instead. The heights come from the GPU SurfaceHeight kernel via async
    /// readback, so only freshly cut tissue is a few frames old; cursor motion itself has no added latency.
    /// The GPU pick still supplies the tissue type under the tip for the HUD and events.
    /// </summary>
    public sealed class SurfacePicker
    {
        readonly int nx, ny; readonly float h, halfX, halfZ;
        readonly float[] heights;
        float maxTop;
        bool inFlight; int lastRequest = -1000;
        public int heightsAgeFrames; int requestFrame;
        const float NoTissue = -1e3f;

        public SurfacePicker(Volume v)
        {
            nx = v.nx; ny = v.ny; h = v.h; halfX = v.SizeX / 2; halfZ = v.SizeZ / 2;
            heights = new float[nx * ny];
            maxTop = NoTissue;
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    float x = (i + 0.5f - nx * 0.5f) * h, z = (j + 0.5f - ny * 0.5f) * h;
                    float y = v.SurfaceY(x, z);
                    heights[i + nx * j] = y;
                    if (y > maxTop) maxTop = y;
                }
        }

        /// <summary>Requests fresh heights from the GPU: every frame while tissue is changing, otherwise twice a second.</summary>
        public void Refresh(PhysicsPipeline physics, bool changing)
        {
            if (inFlight) return;
            if (!changing && Time.frameCount - lastRequest < 30) return;
            inFlight = true; lastRequest = Time.frameCount; requestFrame = Time.frameCount;
            physics.RequestSurfaceHeights(data =>
            {
                inFlight = false;
                if (data.Length < heights.Length * 2) return;
                float top = NoTissue;
                for (int n = 0; n < heights.Length; n++) { float y = data[n * 2].z; heights[n] = y; if (y > top) top = y; }
                maxTop = top;
                heightsAgeFrames = Time.frameCount - requestFrame;
            });
        }

        float Column(int i, int j)
        {
            if (i < 0) i = 0; else if (i >= nx) i = nx - 1;
            if (j < 0) j = 0; else if (j >= ny) j = ny - 1;
            return heights[i + nx * j];
        }

        /// <summary>Tissue top height (mm) at a field position, bilinear between column centres.</summary>
        public float HeightAt(float x, float z)
        {
            float fi = x / h + nx * 0.5f - 0.5f, fj = z / h + ny * 0.5f - 0.5f;
            int i0 = Mathf.FloorToInt(fi), j0 = Mathf.FloorToInt(fj);
            float tx = fi - i0, tz = fj - j0;
            float a = Column(i0, j0), b = Column(i0 + 1, j0), c = Column(i0, j0 + 1), d = Column(i0 + 1, j0 + 1);
            // a missing column (no tissue) must not drag its neighbours down into a slope
            float lo = Mathf.Max(Mathf.Max(a, b), Mathf.Max(c, d)) - 50f;
            a = Mathf.Max(a, lo); b = Mathf.Max(b, lo); c = Mathf.Max(c, lo); d = Mathf.Max(d, lo);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
        }

        /// <summary>First intersection of a ray (field mm) with the tissue top.</summary>
        public bool Raycast(Vector3 origin, Vector3 dir, out Vector3 hit)
        {
            hit = Vector3.zero;
            if (maxTop <= NoTissue + 1) return false;
            dir.Normalize();
            // clip the ray to the bed's box (x, z extents; y from below the deepest cut to above the top)
            float t0 = 0f, t1 = 2000f;
            if (!Slab(origin.x, dir.x, -halfX, halfX, ref t0, ref t1)) return false;
            if (!Slab(origin.z, dir.z, -halfZ, halfZ, ref t0, ref t1)) return false;
            if (!Slab(origin.y, dir.y, -200f, maxTop + 0.5f, ref t0, ref t1)) return false;
            float step = h * 0.5f, prev = t0;
            for (float t = t0; t <= t1; t += step)
            {
                var p = origin + dir * t;
                if (p.y <= HeightAt(p.x, p.z))
                {
                    float lo = prev, hi = t;
                    for (int it = 0; it < 10; it++)
                    {
                        float m = (lo + hi) * 0.5f; var q = origin + dir * m;
                        if (q.y <= HeightAt(q.x, q.z)) hi = m; else lo = m;
                    }
                    hit = origin + dir * hi;
                    return true;
                }
                prev = t;
            }
            return false;
        }

        static bool Slab(float o, float d, float lo, float hi, ref float t0, ref float t1)
        {
            if (Mathf.Abs(d) < 1e-8f) return o >= lo && o <= hi;
            float a = (lo - o) / d, b = (hi - o) / d;
            if (a > b) { float tmp = a; a = b; b = tmp; }
            t0 = Mathf.Max(t0, a); t1 = Mathf.Min(t1, b);
            return t0 <= t1;
        }
    }
}
