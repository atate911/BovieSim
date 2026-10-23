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
        Matrix4x4 xf = Matrix4x4.identity;

        public MeshFactory Transform(Matrix4x4 m) { xf = m; return this; }

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
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(idx, 0);
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

        /// <summary>
        /// #15 scalpel on a #3 handle: tip at the origin, +Y along the handle, blade face normal +Z, cutting edge in the XY
        /// plane. The small curved #15 blade is a lofted plate whose belly bulges to 3.4 mm, with a pointed tip.
        /// </summary>
        public static Mesh Scalpel()
        {
            var f = new MeshFactory();
            f.Box(new Vector3(0, 0.2f, 0), new Vector3(0.25f, 0.2f, 0.12f));
            f.Loft(0.3f, 9.5f, 12, y => new Vector2(Mathf.Lerp(0.3f, 1.7f, Mathf.Sin(Mathf.Clamp01((y - 0.3f) / 9.2f) * Mathf.PI * 0.8f)), 0.15f), 6f, 20);
            // neck and the flat #3 handle (125 mm, 8 x 3 mm) with grip ridges
            f.Box(new Vector3(0, 11f, 0), new Vector3(1.4f, 2.5f, 0.6f));
            f.Loft(13.5f, 53.5f, 8, y => new Vector2(Mathf.Lerp(2.4f, 4f, Mathf.InverseLerp(13.5f, 53.5f, y)), 1.3f), 4f, 20);
            f.Loft(53.5f, 137.5f, 4, y => new Vector2(4f, 1.5f), 4f, 20);
            for (float y = 100; y <= 128; y += 4f) f.Box(new Vector3(0, y, 1.5f), new Vector3(3.6f, 0.5f, 0.15f));
            return f.Build("Scalpel");
        }

        /// <summary>
        /// One Metzenbaum blade with its shank and ring handle: +Y from the tip (origin) to the pivot at jawLength, blade
        /// normal +Z. The two blades are posed converging at the pivot, like the forceps arms.
        /// </summary>
        public static Mesh ScissorsBlade(float jawLength = 20f, float shankLength = 95f)
        {
            var f = new MeshFactory();
            f.Loft(0f, jawLength, 16, y => new Vector2(Mathf.Lerp(0.3f, 1.2f, Mathf.InverseLerp(0, jawLength, y)), Mathf.Lerp(0.25f, 0.45f, Mathf.InverseLerp(0, jawLength, y))), 3f, 16);
            // pivot boss: a short disc about the blade normal
            f.Transform(Matrix4x4.TRS(new Vector3(0, jawLength, 0), Quaternion.Euler(90, 0, 0), Vector3.one));
            f.Cylinder(2.2f, 2.2f, -0.9f, 0.9f, 16);
            f.Transform(Matrix4x4.identity);
            f.Loft(jawLength + 1.5f, jawLength + shankLength * 0.9f, 6, y => new Vector2(1.7f, 1.0f), 4f, 16);
            var ring = new List<Vector3>();
            float cy = jawLength + shankLength * 0.9f + 7f;
            for (int i = 0; i <= 24; i++) { float a = i / 24f * Mathf.PI * 2; ring.Add(new Vector3(Mathf.Cos(a) * 7f, cy + Mathf.Sin(a) * 7f, 0)); }
            f.Tube(ring, 1.3f, 10);
            return f.Build("ScissorsBlade");
        }

        /// <summary>Drape frame around the bed aperture with a sloped lip down to the skin.</summary>
        public static Mesh Drapes(float halfX, float halfZ, float margin = 2.5f)
        {
            var f = new MeshFactory();
            float ax = halfX - margin, az = halfZ - margin, R = 400, y = 0.6f, lip = 3f;
            System.Action<float, float, float, float> q = (x0, z0, x1, z1) => f.Quad(new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0), Vector3.up);
            q(-R, -R, R, -az - lip); q(-R, az + lip, R, R); q(-R, -az - lip, -ax - lip, az + lip); q(ax + lip, -az - lip, R, az + lip);
            System.Action<Vector3, Vector3, Vector3, Vector3> slope = (a, b, c, d) => f.Quad(a, b, c, d, Vector3.up);
            slope(new Vector3(ax + lip, y, -az - lip), new Vector3(-ax - lip, y, -az - lip), new Vector3(-ax, 0.15f, -az), new Vector3(ax, 0.15f, -az));
            slope(new Vector3(-ax - lip, y, az + lip), new Vector3(ax + lip, y, az + lip), new Vector3(ax, 0.15f, az), new Vector3(-ax, 0.15f, az));
            slope(new Vector3(-ax - lip, y, -az - lip), new Vector3(-ax - lip, y, az + lip), new Vector3(-ax, 0.15f, az), new Vector3(-ax, 0.15f, -az));
            slope(new Vector3(ax + lip, y, az + lip), new Vector3(ax + lip, y, -az - lip), new Vector3(ax, 0.15f, -az), new Vector3(ax, 0.15f, az));
            return f.Build("Drapes");
        }
    }
}
