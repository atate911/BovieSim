using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BovieSim
{
    /// <summary>Procedural instrument and drape meshes in millimetres, local +Y along the handle (port of src/render/meshes.ts).</summary>
    public sealed class MeshFactory
    {
        readonly List<Vector3> v = new List<Vector3>();
        readonly List<Vector3> n = new List<Vector3>();
        readonly List<Vector2> uv = new List<Vector2>();
        readonly List<int> idx = new List<int>();
        readonly List<int> subStarts = new List<int>();
        Matrix4x4 xf = Matrix4x4.identity;

        public MeshFactory Transform(Matrix4x4 m) { xf = m; return this; }

        /// <summary>Starts a new submesh (one material slot) at the current triangle count.</summary>
        public MeshFactory BeginSubmesh() { subStarts.Add(idx.Count); return this; }

        /// <summary>Triangle with a normal per vertex, wound so that it faces `outward`.</summary>
        void TriSmooth(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc, Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0) Tri(a, c, b, na, nc, nb); else Tri(a, b, c, na, nb, nc);
        }

        /// <summary>Adds a triangle facing `outward` (Unity front faces have cross(b-a, c-a) toward the viewer).</summary>
        void TriFacing(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0) { var t = b; b = c; c = t; }
            Tri(a, b, c);
        }

        void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3? na = null, Vector3? nb = null, Vector3? nc = null)
        {
            a = xf.MultiplyPoint3x4(a); b = xf.MultiplyPoint3x4(b); c = xf.MultiplyPoint3x4(c);
            var fn = Vector3.Cross(b - a, c - a).normalized;
            int i0 = v.Count;
            v.Add(a); v.Add(b); v.Add(c);
            n.Add(na.HasValue ? xf.MultiplyVector(na.Value).normalized : fn);
            n.Add(nb.HasValue ? xf.MultiplyVector(nb.Value).normalized : fn);
            n.Add(nc.HasValue ? xf.MultiplyVector(nc.Value).normalized : fn);
            uv.Add(new Vector2(a.x, a.z) * 0.05f); uv.Add(new Vector2(b.x, b.z) * 0.05f); uv.Add(new Vector2(c.x, c.z) * 0.05f);
            idx.Add(i0); idx.Add(i0 + 1); idx.Add(i0 + 2);
        }
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward) { TriFacing(a, b, c, outward); TriFacing(a, c, d, outward); }

        /// <summary>Axis-aligned box centred at c with half extents e.</summary>
        public MeshFactory Box(Vector3 c, Vector3 e)
        {
            System.Func<float, float, float, Vector3> P = (sx, sy, sz) => new Vector3(c.x + sx * e.x, c.y + sy * e.y, c.z + sz * e.z);
            Quad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), Vector3.forward);
            Quad(P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), Vector3.back);
            Quad(P(1, -1, 1), P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), Vector3.right);
            Quad(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), Vector3.left);
            Quad(P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), P(-1, 1, -1), Vector3.up);
            Quad(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), Vector3.down);
            return this;
        }

        /// <summary>Cylinder along +Y from y0 to y1 with radii r0 (bottom) and r1 (top), smooth normals.</summary>
        public MeshFactory Cylinder(float r0, float r1, float y0, float y1, int seg = 24, bool caps = true)
        {
            float slope = (r0 - r1) / Mathf.Max(y1 - y0, 1e-6f);
            for (int s = 0; s < seg; s++)
            {
                float a0 = s / (float)seg * Mathf.PI * 2, a1 = (s + 1) / (float)seg * Mathf.PI * 2;
                float c0 = Mathf.Cos(a0), s0 = Mathf.Sin(a0), c1 = Mathf.Cos(a1), s1 = Mathf.Sin(a1);
                var p00 = new Vector3(c0 * r0, y0, s0 * r0); var p10 = new Vector3(c1 * r0, y0, s1 * r0);
                var p01 = new Vector3(c0 * r1, y1, s0 * r1); var p11 = new Vector3(c1 * r1, y1, s1 * r1);
                var n0 = new Vector3(c0, slope, s0).normalized; var n1 = new Vector3(c1, slope, s1).normalized;
                // Unity front faces: cross(b-a, c-a) points outward
                Tri(p00, p11, p10, n0, n1, n1);
                Tri(p00, p01, p11, n0, n0, n1);
                if (caps)
                {
                    if (r1 > 0) Tri(new Vector3(0, y1, 0), p11, p01, Vector3.up, Vector3.up, Vector3.up);
                    if (r0 > 0) Tri(new Vector3(0, y0, 0), p00, p10, Vector3.down, Vector3.down, Vector3.down);
                }
            }
            return this;
        }

        /// <summary>
        /// Lofted rounded-rectangle (superellipse) section along +Y. halfSize(y) gives the half width along X and Z;
        /// exponent 2 is an ellipse, larger values approach a rectangle with rounded corners.
        /// </summary>
        public MeshFactory Loft(float y0, float y1, int rings, System.Func<float, Vector2> halfSize, float exponent, int seg = 32, bool capStart = true, bool capEnd = true)
        {
            var ring = new Vector3[rings + 1, seg]; var nrm = new Vector3[rings + 1, seg];
            for (int r = 0; r <= rings; r++)
            {
                float y = Mathf.Lerp(y0, y1, r / (float)rings);
                var hs = halfSize(y);
                var hsN = halfSize(Mathf.Min(y1, y + 0.5f)); var hsP = halfSize(Mathf.Max(y0, y - 0.5f));
                float slopeX = (hsP.x - hsN.x), slopeZ = (hsP.y - hsN.y);
                for (int k = 0; k < seg; k++)
                {
                    float th = k / (float)seg * Mathf.PI * 2, c = Mathf.Cos(th), sn = Mathf.Sin(th);
                    float e = 2f / exponent;
                    float px = hs.x * Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), e), pz = hs.y * Mathf.Sign(sn) * Mathf.Pow(Mathf.Abs(sn), e);
                    ring[r, k] = new Vector3(px, y, pz);
                    float gx = Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), 2 - e) / Mathf.Max(hs.x, 1e-3f), gz = Mathf.Sign(sn) * Mathf.Pow(Mathf.Abs(sn), 2 - e) / Mathf.Max(hs.y, 1e-3f);
                    var g = new Vector2(gx, gz).normalized;
                    nrm[r, k] = new Vector3(g.x, g.x * slopeX + g.y * slopeZ, g.y).normalized;
                }
            }
            for (int r = 0; r < rings; r++)
                for (int k = 0; k < seg; k++)
                {
                    int k1 = (k + 1) % seg;
                    Vector3 a = ring[r, k], b = ring[r, k1], c = ring[r + 1, k1], d = ring[r + 1, k];
                    // outward: cross(b-a, c-a) must point away from the axis
                    Tri(a, c, b, nrm[r, k], nrm[r + 1, k1], nrm[r, k1]);
                    Tri(a, d, c, nrm[r, k], nrm[r + 1, k], nrm[r + 1, k1]);
                }
            if (capStart) { var cen = new Vector3(0, y0, 0); for (int k = 0; k < seg; k++) TriFacing(cen, ring[0, k], ring[0, (k + 1) % seg], Vector3.down); }
            if (capEnd) { var cen = new Vector3(0, y1, 0); for (int k = 0; k < seg; k++) TriFacing(cen, ring[rings, k], ring[rings, (k + 1) % seg], Vector3.up); }
            return this;
        }

        /// <summary>Round tube of radius r along a polyline (parallel-transport frames), optional cone at the end.</summary>
        public MeshFactory Tube(IList<Vector3> path, float r, int seg = 16, bool pointedEnd = false)
        {
            int m = path.Count;
            var t = new Vector3[m]; for (int i = 0; i < m; i++) t[i] = (path[Mathf.Min(m - 1, i + 1)] - path[Mathf.Max(0, i - 1)]).normalized;
            var nrmV = Vector3.Cross(t[0], Mathf.Abs(t[0].y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var rings = new Vector3[m, seg]; var norms = new Vector3[m, seg];
            for (int i = 0; i < m; i++)
            {
                if (i > 0) { var axis = Vector3.Cross(t[i - 1], t[i]); if (axis.sqrMagnitude > 1e-10f) nrmV = Quaternion.AngleAxis(Vector3.SignedAngle(t[i - 1], t[i], axis), axis) * nrmV; }
                var bin = Vector3.Cross(t[i], nrmV).normalized;
                float rr = pointedEnd && i == m - 1 ? 0.02f : r;
                for (int k = 0; k < seg; k++)
                {
                    float th = k / (float)seg * Mathf.PI * 2;
                    var dir = nrmV * Mathf.Cos(th) + bin * Mathf.Sin(th);
                    rings[i, k] = path[i] + dir * rr; norms[i, k] = dir;
                }
            }
            for (int i = 0; i < m - 1; i++)
                for (int k = 0; k < seg; k++)
                {
                    int k1 = (k + 1) % seg;
                    Vector3 a = rings[i, k], b = rings[i, k1], c = rings[i + 1, k1], d = rings[i + 1, k];
                    var outward = norms[i, k] + norms[i, k1];
                    if (Vector3.Dot(Vector3.Cross(c - a, b - a), outward) >= 0) { Tri(a, c, b, norms[i, k], norms[i + 1, k1], norms[i, k1]); Tri(a, d, c, norms[i, k], norms[i + 1, k], norms[i + 1, k1]); }
                    else { Tri(a, b, c, norms[i, k], norms[i, k1], norms[i + 1, k1]); Tri(a, c, d, norms[i, k], norms[i + 1, k1], norms[i + 1, k]); }
                }
            return this;
        }

        /// <summary>Points along a circular arc from `start` heading `dir`, turning by `degrees` about `axis`.</summary>
        static void Arc(List<Vector3> pts, Vector3 start, Vector3 dir, Vector3 axis, float radius, float degrees, int steps)
        {
            var toCentre = Vector3.Cross(axis, dir).normalized;
            var centre = start + toCentre * radius;
            for (int i = 1; i <= steps; i++)
            {
                var q = Quaternion.AngleAxis(degrees * i / steps, axis);
                pts.Add(centre + q * (start - centre));
            }
        }

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name };
            if (v.Count > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv);
            if (subStarts.Count == 0) m.SetTriangles(idx, 0);
            else
            {
                m.subMeshCount = subStarts.Count;
                for (int s = 0; s < subStarts.Count; s++)
                {
                    int start = subStarts[s], end = s + 1 < subStarts.Count ? subStarts[s + 1] : idx.Count;
                    m.SetTriangles(idx.GetRange(start, end - start), s);
                }
            }
            m.RecalculateTangents(); m.RecalculateBounds();
            return m;
        }

        // ---- instruments (millimetres, local +Y from the working tip toward the hand) -------------------

        /// <summary>
        /// Bovie pencil body (Valleylab-style hand switching pencil): tapered nose with the electrode collet,
        /// rounded-rectangle grip about 12 x 10 mm, ~15 cm long, and the cord leaving the back. The switches sit on
        /// the +X face (up in the hand frame): yellow CUT nearest the tip, blue COAG behind it.
        /// </summary>
        public static Mesh PencilBody()
        {
            var f = new MeshFactory();
            f.Cylinder(1.25f, 1.25f, 12, 16, 16);                         // collet holding the electrode shaft
            f.Loft(15f, 170f, 40, y =>
            {
                float nose = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(15, 42, y));
                float tail = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(150, 170, y));
                float wx = Mathf.Lerp(1.9f, 6.1f, nose) - 0.6f * tail, wz = Mathf.Lerp(1.9f, 5.2f, nose) - 0.6f * tail;
                // a faint finger waist just behind the switches
                float waist = 0.25f * Mathf.Exp(-Mathf.Pow((y - 92f) / 10f, 2));
                return new Vector2(wx - waist, wz - waist);
            }, 3.2f, 40);
            // strain relief and cord, drooping away from the hand
            f.Cylinder(2.4f, 1.9f, 170, 182, 16);
            var cord = new List<Vector3> { new Vector3(0, 182, 0), new Vector3(0, 205, 0) };
            Arc(cord, new Vector3(0, 205, 0), Vector3.up, Vector3.forward, 90f, 35f, 10);
            f.Tube(cord, 1.7f, 12);
            return f.Build("PencilBody");
        }

        /// <summary>Rocker switch pad on the +X face of the pencil, between y0 and y1 (mm from the tip).</summary>
        public static Mesh PencilButton(float y0, float y1)
        {
            var f = new MeshFactory();
            float mid = (y0 + y1) / 2, half = (y1 - y0) / 2;
            f.Transform(Matrix4x4.TRS(new Vector3(5.9f, 0, 0), Quaternion.identity, Vector3.one));
            f.Loft(y0, y1, 12, y =>
            {
                float u = (y - mid) / half; float round = Mathf.Sqrt(Mathf.Max(0.05f, 1 - u * u * u * u));
                return new Vector2(1.1f * round, 3.4f * round);
            }, 4f, 24);
            return f.Build("PencilButton");
        }

        /// <summary>Blade electrode: insulated shaft into the collet, stainless blade ~2.4 x 0.5 mm, tip at the origin.</summary>
        public static Mesh Blade()
        {
            var f = new MeshFactory().Cylinder(1.18f, 1.18f, 5, 15, 16);
            f.Loft(0f, 5.2f, 8, y => new Vector2(Mathf.Lerp(0.7f, 1.2f, Mathf.Clamp01(y / 1.2f)), 0.25f), 5f, 20);
            return f.Build("Blade");
        }
        public static Mesh Needle() { return new MeshFactory().Cylinder(1.18f, 1.18f, 5, 15, 16).Cylinder(0.05f, 0.22f, 0, 10, 12).Build("Needle"); }
        public static Mesh Ball() { var f = new MeshFactory().Cylinder(1.18f, 1.18f, 5, 15, 16).Cylinder(0.4f, 0.4f, 2, 5, 10); f.Sphere(new Vector3(0, 1.5f, 0), 1.5f, 14); return f.Build("Ball"); }

        public MeshFactory Sphere(Vector3 c, float r, int seg)
        {
            for (int i = 0; i < seg; i++)
                for (int j = 0; j < seg; j++)
                {
                    float t0 = Mathf.PI * i / seg, t1 = Mathf.PI * (i + 1) / seg, p0 = 2 * Mathf.PI * j / seg, p1 = 2 * Mathf.PI * (j + 1) / seg;
                    System.Func<float, float, Vector3> S = (t, p) => new Vector3(Mathf.Sin(t) * Mathf.Cos(p), Mathf.Cos(t), Mathf.Sin(t) * Mathf.Sin(p));
                    Vector3 a = S(t0, p0), b = S(t0, p1), cc = S(t1, p1), d = S(t1, p0);
                    TriFacing(c + a * r, c + b * r, c + cc * r, a + b + cc);
                    TriFacing(c + a * r, c + cc * r, c + d * r, a + cc + d);
                }
            return this;
        }

        /// <summary>
        /// One arm of Adson tissue forceps. Flat blade, thin across the closing direction (Z): narrow 1.2 mm tip,
        /// widening to a 7 mm serrated grip, joined to the other arm at the back. Teeth (1 on one arm, 2 on the
        /// other: "1 x 2") point toward the opposite arm; <paramref name="towardPositiveZ"/> says which way that is.
        /// </summary>
        public static Mesh ForcepsArm(float length = 95f, bool towardPositiveZ = true, int teeth = 1)
        {
            var f = new MeshFactory();
            f.Loft(0f, length, 48, y =>
            {
                float w = Mathf.Lerp(0.6f, 3.5f, Mathf.SmoothStep(0, 1, Mathf.InverseLerp(6, 34, y)));
                w = Mathf.Lerp(w, 2.1f, Mathf.SmoothStep(0, 1, Mathf.InverseLerp(64, length, y)));
                float t = Mathf.Lerp(0.45f, 0.8f, Mathf.InverseLerp(0, 30, y));
                return new Vector2(w, t);
            }, 6f, 28);
            float side = towardPositiveZ ? 1f : -1f;
            // teeth at the tip, pointing at the other jaw
            for (int i = 0; i < teeth; i++)
            {
                float x = teeth == 1 ? 0 : (i == 0 ? -0.35f : 0.35f);
                f.Box(new Vector3(x, 0.45f, side * 0.62f), new Vector3(0.16f, 0.32f, 0.2f));
            }
            // transverse grip serrations on the outer face
            for (float y = 38; y <= 60; y += 1.3f) f.Box(new Vector3(0, y, -side * 0.85f), new Vector3(3.2f, 0.22f, 0.1f));
            return f.Build("ForcepsArm");
        }

        /// <summary>
        /// Frazier suction: 10 Fr (3.3 mm) cannula, straight for 70 mm then angled ~30 degrees into the handle,
        /// which carries the thumb vent (a raised ring: cover it to suck) and the tubing barb.
        /// </summary>
        public static Mesh Frazier()
        {
            var f = new MeshFactory();
            var path = new List<Vector3> { Vector3.zero, new Vector3(0, 70, 0) };
            Arc(path, new Vector3(0, 70, 0), Vector3.up, Vector3.forward, 12f, 30f, 8);
            var last = path[path.Count - 1]; var dir = Quaternion.AngleAxis(30f, Vector3.forward) * Vector3.up;
            path.Add(last + dir * 8f);
            f.Tube(path, 1.65f, 16);
            // handle along the angled direction
            var hStart = last + dir * 8f;
            var rot = Quaternion.FromToRotation(Vector3.up, dir);
            f.Transform(Matrix4x4.TRS(hStart, rot, Vector3.one));
            f.Loft(0f, 45f, 16, y => new Vector2(Mathf.Lerp(2.3f, 3.6f, Mathf.InverseLerp(0, 10, y)), Mathf.Lerp(2.3f, 4.8f, Mathf.InverseLerp(0, 10, y))), 3f, 28);
            f.Cylinder(2.4f, 1.8f, 45f, 58f, 16);                        // tubing barb
            // thumb vent: a raised ring on top of the handle
            var ring = new List<Vector3>();
            for (int i = 0; i <= 24; i++) { float a = i / 24f * Mathf.PI * 2; ring.Add(new Vector3(3.7f, 22f + Mathf.Cos(a) * 2.2f, Mathf.Sin(a) * 2.2f)); }
            f.Tube(ring, 0.45f, 8);
            return f.Build("Frazier");
        }

        /// <summary>Single-prong skin hook: fine curved wire hook on a 2 mm shaft and a flat ridged handle; the hook's bend is at the origin.</summary>
        public static Mesh SkinHook()
        {
            var f = new MeshFactory();
            // straight wire down to 1.6 mm, then a 200-degree curl whose lowest point is the origin (the anchor)
            var wire = new List<Vector3> { new Vector3(0, 9, 0), new Vector3(0, 1.6f, 0) };
            Arc(wire, new Vector3(0, 1.6f, 0), Vector3.down, Vector3.forward, 1.6f, 200f, 14);
            f.Tube(wire, 0.32f, 10, true);
            f.Cylinder(1.0f, 0.45f, 9f, 16f, 12, false).Cylinder(1.0f, 1.0f, 16f, 60f, 12);
            f.Loft(60f, 140f, 12, y => new Vector2(Mathf.Lerp(1.6f, 4.2f, Mathf.InverseLerp(60, 72, y)), 1.6f), 4f, 24);
            for (float y = 78; y <= 110; y += 2.4f) f.Box(new Vector3(0, y, 1.62f), new Vector3(3.6f, 0.35f, 0.12f));
            return f.Build("SkinHook");
        }

        // ---- fenestrated drape (port of src/render/meshes.ts: same noise, same folds) ----------------------

        /// <summary>incise film margin adhered to the skin (mm from the aperture edge)</summary>
        public const float DrapeFilmMm = 2.5f;
        /// <summary>flat reinforced zone around the fenestration (mm from the aperture edge), under the towels</summary>
        public const float DrapePadMm = 36f;
        /// <summary>towels: folded edge this far outside the skin edge, folded width (outward), extra length past each corner, folded thickness</summary>
        public const float TowelGapMm = 2.5f, TowelWMm = 55f, TowelLExtraMm = 45f, TowelTMm = 2.4f;

        static float Smooth01(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3 - 2 * t); }

        /// <summary>Signed distance outward from the rounded-rectangle aperture edge (negative inside).</summary>
        public static float ApertureDist(float x, float z, float ax, float az, float rc)
        {
            float qx = Mathf.Abs(x) - (ax - rc), qz = Mathf.Abs(z) - (az - rc);
            float ox = Mathf.Max(qx, 0), oz = Mathf.Max(qz, 0);
            return Mathf.Sqrt(ox * ox + oz * oz) + Mathf.Min(Mathf.Max(qx, qz), 0) - rc;
        }

        static float Ridge(float t, float period) { float q = Mathf.Abs(Mathf.Repeat(t / period, 1f) - 0.5f) * period; return Mathf.Exp(-(q / 2.2f) * (q / 2.2f)); }

        /// <summary>
        /// Drape height above the skin plane (mm). The incise film lies on the skin behind a rolled hem, the
        /// reinforcement pad is thicker and flat, and beyond it the loose drape carries broad swells, pleats
        /// radiating from the fenestration where it was pulled taut, the sharp creases left by its fan-folded
        /// packaging, and fine wrinkles; past the table edge it hangs down.
        /// </summary>
        public static float DrapeHeight(float x, float z, float ax, float az, float rc) { return DrapeHeight(x, z, ax, az, rc, 0); }

        /// <summary>As above with the scenario seed: the folds, waves, bunching and creases differ per attempt (same noise and layout as the browser build).</summary>
        public static float DrapeHeight(float x, float z, float ax, float az, float rc, int seed)
        {
            float d = ApertureDist(x, z, ax, az, rc);
            int sd = seed * 17;
            float y = 0.12f + 0.35f * Smooth01(0, DrapeFilmMm, d) + 0.55f * Smooth01(DrapeFilmMm, DrapePadMm, d);
            y += 1.1f * Mathf.Exp(-((d - 1.2f) / 0.9f) * ((d - 1.2f) / 0.9f));   // rolled hem at the aperture edge
            float loose = Smooth01(DrapePadMm - 6, 70, d);
            float far = Smooth01(70, 240, d);
            // domain warp so swells and waves are irregular rather than a stationary noise pattern
            float wx = x + 30 * ((float)Noise.Fbm2(x / 95.0, z / 95.0, 2, sd + 3) - 0.5f), wz = z + 30 * ((float)Noise.Fbm2(x / 95.0 + 7, z / 95.0, 2, sd + 4) - 0.5f);
            float swell = ((float)Noise.Fbm2(wx / 70.0, wz / 70.0, 3, sd + 5) - 0.5f) * 2;
            // waves: the sheet lies over the body's contour, so broad parallel waves run across it, bending with the warp
            float wa = 0.5f + 0.8f * ((float)Noise.Fbm2(x / 300.0, z / 300.0, 1, sd + 6) - 0.5f);
            float wave = Mathf.Sin((wx * Mathf.Cos(wa) + wz * Mathf.Sin(wa)) / 26 + 2.5f * (float)Noise.Fbm2(x / 60.0, z / 60.0, 2, sd + 7));
            float ang = Mathf.Atan2(z, x);
            float pleat = Mathf.Sin(ang * 9 + ((float)Noise.Fbm2(x / 40.0, z / 40.0, 2, sd + 11) - 0.5f) * 6) * 0.5f + 0.5f;
            float wrinkle = ((float)Noise.Fbm2(wx / 14.0, wz / 14.0, 2, sd + 17) - 0.5f) * 2;
            // local heaps where the fabric bunched when it was laid
            float bunch = Mathf.Max(0, (float)Noise.Fbm2(x / 48.0, z / 48.0, 2, sd + 29) - 0.56f) * 22;
            y += loose * (swell * (2.5f + 6 * far) + wave * (0.8f + 2.6f * far) + pleat * pleat * (1.0f + 2.5f * far) + wrinkle * 0.5f + bunch * (0.3f + far));
            // package creases: a slightly skewed grid of sharp ridges (120 x 170 mm fan folds) plus a few random creases
            float ca = 0.14f + (seed % 7) * 0.05f;
            float u1 = x * Mathf.Cos(ca) + z * Mathf.Sin(ca), v1 = -x * Mathf.Sin(ca) + z * Mathf.Cos(ca);
            float creaseGrid = Ridge(u1 + 37 + seed * 13, 120) + Ridge(v1 + 61 + seed * 7, 170);
            float cr = Mathf.Abs((float)Noise.Fbm2(wx / 55.0, wz / 55.0, 2, sd + 23) - 0.5f) / 0.012f;
            float creaseRand = Mathf.Exp(-cr * cr);
            y += loose * (creaseGrid * 1.8f + creaseRand * 1.2f);
            y -= Mathf.Max(0, d - 260) * 0.5f;
            return y;
        }

        static Vector3 DrapeNormal(float x, float z, float ax, float az, float rc, int seed)
        {
            const float e = 0.5f;
            float dx = (DrapeHeight(x + e, z, ax, az, rc, seed) - DrapeHeight(x - e, z, ax, az, rc, seed)) / (2 * e);
            float dz = (DrapeHeight(x, z + e, ax, az, rc, seed) - DrapeHeight(x, z - e, ax, az, rc, seed)) / (2 * e);
            return new Vector3(-dx, 1, -dz).normalized;
        }

        /// <summary>Point on the rounded rectangle offset by d from the aperture, at perimeter sample k of n (same topology on every ring).</summary>
        static Vector2 RingPoint(int k, int n, float d, float ax, float az, float rc)
        {
            float a = ax + d, b = az + d, r = rc + d;
            int quarter = n / 4, nEdge = Mathf.RoundToInt(n / 12f), nArc = quarter - nEdge;
            int seg = k / quarter, t = k - seg * quarter;
            if (t < nEdge)
            {
                float u = t / (float)nEdge;
                switch (seg)
                {
                    case 0: return new Vector2(a, -(b - r) + u * 2 * (b - r));
                    case 1: return new Vector2((a - r) - u * 2 * (a - r), b);
                    case 2: return new Vector2(-a, (b - r) - u * 2 * (b - r));
                    default: return new Vector2(-(a - r) + u * 2 * (a - r), -b);
                }
            }
            float v = (t - nEdge) / (float)nArc;
            float angle = seg * Mathf.PI / 2 + v * Mathf.PI / 2;
            float cx = seg == 0 || seg == 3 ? a - r : -(a - r), cz = seg == 0 || seg == 1 ? b - r : -(b - r);
            return new Vector2(cx + Mathf.Cos(angle) * r, cz + Mathf.Sin(angle) * r);
        }

        /// <summary>
        /// Fenestrated drape around the bed aperture, three submeshes for three materials: 0 incise film,
        /// 1 absorbent reinforcement pad, 2 the loose blue drape with folds.
        /// </summary>
        public static Mesh Drapes(float halfX, float halfZ, int seed = 0, float margin = 2.5f)
        {
            float ax = halfX - margin, az = halfZ - margin, rc = 6f;
            float[] rings = { 0, 0.5f, 1.0f, 1.5f, 2.0f, DrapeFilmMm, 3.5f, 5, 7, 10, 14, 18, 22, 26, 30, DrapePadMm, 40, 44, 48, 52, 56, 60, 65, 70, 76, 82, 88, 95, 102, 110, 118, 127, 137, 148, 160, 175, 190, 210, 235, 260, 290, 330, 400 };
            const int n = 256;
            var f = new MeshFactory();
            System.Func<int, float, Vector3> P = (k, d) => { var q = RingPoint(k % n, n, d, ax, az, rc); return new Vector3(q.x, DrapeHeight(q.x, q.y, ax, az, rc, seed), q.y); };
            System.Func<int, float, Vector3> N = (k, d) => { var q = RingPoint(k % n, n, d, ax, az, rc); return DrapeNormal(q.x, q.y, ax, az, rc, seed); };
            int part = -1;
            for (int r = 0; r < rings.Length - 1; r++)
            {
                float d0 = rings[r], d1 = rings[r + 1];
                int want = d1 <= DrapeFilmMm ? 0 : d1 <= DrapePadMm ? 1 : 2;
                while (part < want) { f.BeginSubmesh(); part++; }
                for (int k = 0; k < n; k++)
                {
                    Vector3 a = P(k, d0), b = P(k + 1, d0), c = P(k + 1, d1), d = P(k, d1);
                    Vector3 na = N(k, d0), nb = N(k + 1, d0), nc = N(k + 1, d1), nd = N(k, d1);
                    f.TriSmooth(a, c, b, na, nc, nb, Vector3.up);
                    f.TriSmooth(a, d, c, na, nd, nc, Vector3.up);
                }
            }
            return f.Build("Drapes");
        }

        // ---- towels and towel clips (port of src/render/meshes.ts) --------------------------------------------------

        struct TowelFrame { public float ox, oz, ux, uz, vx, vz, half, len; public bool onTop; }

        /// <summary>Towel k runs along side k of the window (0: +z, 1: +x, 2: -z, 3: -x); u along the side, v outward from its folded edge; slightly misaligned per towel and seed.</summary>
        static TowelFrame Towel(int k, float ax, float az, int seed)
        {
            float wx = ax + TowelGapMm, wz = az + TowelGapMm;
            System.Func<int, float> r = n => (float)Noise.Fbm2(k * 3.7 + n, seed * 1.3 + n * 0.37, 1, 211 + n) - 0.5f;
            float rot = r(1) * 0.09f;
            float bx, bz, bux, buz, half;
            switch (k)
            {
                case 0: bx = 0; bz = wz; bux = 1; buz = 0; half = wx; break;
                case 1: bx = wx; bz = 0; bux = 0; buz = -1; half = wz; break;
                case 2: bx = 0; bz = -wz; bux = -1; buz = 0; half = wx; break;
                default: bx = -wx; bz = 0; bux = 0; buz = 1; half = wz; break;
            }
            float c = Mathf.Cos(rot), sn = Mathf.Sin(rot);
            var f = new TowelFrame();
            f.ux = bux * c - buz * sn; f.uz = bux * sn + buz * c;
            f.vx = -f.uz; f.vz = f.ux;
            float shift = r(2) * 24;
            f.ox = bx + f.ux * shift + r(3) * 3 * f.vx; f.oz = bz + f.uz * shift + r(3) * 3 * f.vz;
            f.half = half; f.len = half + TowelLExtraMm; f.onTop = k % 2 == 0;
            return f;
        }

        static bool InTowel(int k, float ax, float az, int seed, float x, float z)
        {
            var f = Towel(k, ax, az, seed);
            float dx = x - f.ox, dz = z - f.oz, u = dx * f.ux + dz * f.uz, v = dx * f.vx + dz * f.vz;
            return u > -f.len && u < f.len && v > 0 && v < TowelWMm;
        }

        /// <summary>Top-surface height of towel k above the skin plane at local (u, v): its base (the drape, or the towel it overlaps), the folded hem and ripples.</summary>
        static float TowelHeight(int k, float ax, float az, int seed, float u, float v)
        {
            var f = Towel(k, ax, az, seed);
            float x = f.ox + f.ux * u + f.vx * v, z = f.oz + f.uz * u + f.vz * v;
            float b = DrapeHeight(x, z, ax, az, 6f, seed) + 0.15f;
            if (f.onTop && (InTowel(1, ax, az, seed, x, z) || InTowel(3, ax, az, seed, x, z))) b += TowelTMm;
            float hem = v < TowelTMm ? Mathf.Sqrt(Mathf.Max(0, 1 - ((TowelTMm - v) / TowelTMm) * ((TowelTMm - v) / TowelTMm))) : 1;
            int sd = seed * 17 + k * 101;
            float loose = Smooth01(8, 45, v);
            float ripple = Mathf.Sin(u / 17 + 2.2f * (float)Noise.Fbm2(u / 40.0, v / 40.0, 2, sd + 1)) * (0.35f + 0.5f * loose) * Smooth01(3, 20, v)
                + ((float)Noise.Fbm2(u / 22.0, v / 22.0, 3, sd + 2) - 0.5f) * 2 * (0.3f + 0.9f * loose)
                + ((float)Noise.Fbm2(u / 7.0, v / 7.0, 2, sd + 3) - 0.5f) * 0.35f;
            return b + TowelTMm * hem + Mathf.Max(ripple, -TowelTMm * 0.6f * hem);
        }

        /// <summary>One of the four folded towels squaring the window on top of the drape (field mm; the two along z lie over the two along x at the corners).</summary>
        public static Mesh Towel(int k, float halfX, float halfZ, int seed, float margin = 2.5f)
        {
            float ax = halfX - margin, az = halfZ - margin;
            var f = Towel(k, ax, az, seed);
            float[] vs = { 0, 0.5f, 1.0f, 1.6f, 2.4f, 3.5f, 5, 7, 10, 13, 16, 20, 24, 28, 33, 38, 44, 50, 55 };
            const int nu = 56;
            var mf = new MeshFactory();
            System.Func<int, int, Vector3> P = (iu, iv) => { float u = -f.len + 2 * f.len * iu / nu, v = vs[iv]; return new Vector3(f.ox + f.ux * u + f.vx * v, TowelHeight(k, ax, az, seed, u, v), f.oz + f.uz * u + f.vz * v); };
            System.Func<int, int, Vector3> N = (iu, iv) =>
            {
                float u = -f.len + 2 * f.len * iu / nu, v = vs[iv], e = 0.4f;
                float du = (TowelHeight(k, ax, az, seed, u + e, v) - TowelHeight(k, ax, az, seed, u - e, v)) / (2 * e);
                float dv = (TowelHeight(k, ax, az, seed, u, v + e) - TowelHeight(k, ax, az, seed, u, Mathf.Max(0, v - e))) / (2 * e);
                return new Vector3(-du * f.ux - dv * f.vx, 1, -du * f.uz - dv * f.vz).normalized;
            };
            for (int iu = 0; iu < nu; iu++)
                for (int iv = 0; iv < vs.Length - 1; iv++)
                {
                    Vector3 a = P(iu, iv), b = P(iu + 1, iv), c = P(iu + 1, iv + 1), d = P(iu, iv + 1);
                    mf.TriSmooth(a, c, b, N(iu, iv), N(iu + 1, iv + 1), N(iu + 1, iv), Vector3.up);
                    mf.TriSmooth(a, d, c, N(iu, iv), N(iu, iv + 1), N(iu + 1, iv + 1), Vector3.up);
                }
            // skirts at the two ends and the back edge, down to the base
            System.Action<Vector3, Vector3, Vector3> skirt = (p0, p1, outward) =>
            {
                var q0 = new Vector3(p0.x, p0.y - TowelTMm - 1.2f, p0.z); var q1 = new Vector3(p1.x, p1.y - TowelTMm - 1.2f, p1.z);
                mf.TriSmooth(p0, p1, q1, outward, outward, outward, outward); mf.TriSmooth(p0, q1, q0, outward, outward, outward, outward);
            };
            for (int iv = 0; iv < vs.Length - 1; iv++) { skirt(P(0, iv), P(0, iv + 1), new Vector3(-f.ux, 0, -f.uz)); skirt(P(nu, iv + 1), P(nu, iv), new Vector3(f.ux, 0, f.uz)); }
            for (int iu = 0; iu < nu; iu++) skirt(P(iu + 1, vs.Length - 1), P(iu, vs.Length - 1), new Vector3(f.vx, 0, f.vz));
            return mf.Build("Towel" + k);
        }

        /// <summary>Backhaus towel clip in its own frame: jaw tips at the origin, +Y toward the ring handles, lying in the X-Y plane with +Z up (mm).</summary>
        public static Mesh TowelClip()
        {
            var f = new MeshFactory();
            foreach (float side in new[] { -1f, 1f })
            {
                float zo = side * 0.9f;
                var path = new List<Vector3>();
                for (int i = 0; i <= 8; i++) { float t = i / 8f, a = t * Mathf.PI * 0.55f; path.Add(new Vector3(side * (1 - Mathf.Cos(a)) * 9, Mathf.Sin(a) * 9 * 1.6f, zo * t)); }
                path.Add(new Vector3(side * 9, 24, zo)); path.Add(new Vector3(-side * 7, 62, zo)); path.Add(new Vector3(-side * 9, 70, zo));
                f.Tube(path, 1.5f, 12);
                var ring = new List<Vector3>();
                for (int i = 0; i <= 28; i++) { float a = i / 28f * Mathf.PI * 2; ring.Add(new Vector3(-side * (9 + 1.5f) + Mathf.Cos(a) * 10.5f, 80.5f + Mathf.Sin(a) * 10.5f, zo)); }
                f.Tube(ring, 1.6f, 10);
            }
            f.Box(new Vector3(0, 34, 0), new Vector3(4.2f, 5, 1.6f));
            return f.Build("TowelClip");
        }

        /// <summary>Pose of towel clip i (0-3) at the window corners: jaw tips on the corner, handles out along the diagonal; `dir` is the clip's +Y in field space (horizontal).</summary>
        public static void TowelClipPose(int i, float halfX, float halfZ, int seed, out Vector3 pos, out Vector3 dir, float margin = 2.5f)
        {
            float ax = halfX - margin, az = halfZ - margin, wx = ax + TowelGapMm, wz = az + TowelGapMm;
            float sx = i == 0 || i == 1 ? 1 : -1, sz = i == 0 || i == 3 ? 1 : -1;
            float jitter = (float)Noise.Fbm2(i * 2.3, seed * 0.7, 1, 307 + i) - 0.5f;
            float ang = Mathf.Atan2(sz, sx) + jitter * 0.5f;
            float tipX = sx * wx + Mathf.Cos(ang) * 4, tipZ = sz * wz + Mathf.Sin(ang) * 4;
            float h = TowelHeight(0, ax, az, seed, 0, 20) - DrapeHeight(0, 0, ax, az, 6f, seed);
            pos = new Vector3(tipX, DrapeHeight(tipX, tipZ, ax, az, 6f, seed) + 2 * TowelTMm + h * 0.3f + 1.2f, tipZ);
            dir = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
        }

        /// <summary>Operating-light head: a flat dome (metres, +Y toward the field) whose face is emissive, so instruments and wet tissue reflect it.</summary>
        public static Mesh LampDome(float radius = 0.3f, float depth = 0.08f)
        {
            return new MeshFactory().Cylinder(radius * 0.8f, radius, -depth, 0, 48).Build("LampDome");
        }
    }
}
