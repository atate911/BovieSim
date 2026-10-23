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

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name };
            if (v.Count > 65000) m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetTriangles(idx, 0);
            m.RecalculateTangents(); m.RecalculateBounds();
            return m;
        }

        // ---- instruments -------------------------------------------------------------------------

        public static Mesh PencilBody()
        {
            return new MeshFactory().Cylinder(3.2f, 5.2f, 22, 60, 28).Cylinder(5.2f, 5.2f, 60, 165, 28).Cylinder(5.2f, 4.6f, 165, 172, 28).Cylinder(1.8f, 3.2f, 14, 22, 20).Build("PencilBody");
        }
        public static Mesh PencilButton(float y) { return new MeshFactory().Box(new Vector3(4.6f, y, 0), new Vector3(1.6f, 9, 3.2f)).Build("PencilButton"); }
        /// <summary>Blade electrode: shaft 2.4 mm from y=5..15, blade 2.4 x 0.5 mm, tip at the origin.</summary>
        public static Mesh Blade()
        {
            var f = new MeshFactory().Cylinder(1.2f, 1.2f, 5, 15, 16).Box(new Vector3(0, 2.5f, 0), new Vector3(1.2f, 2.5f, 0.25f));
            f.TriFacing(new Vector3(-1.2f, 0, 0.25f), new Vector3(0, -1.2f, 0), new Vector3(1.2f, 0, 0.25f), new Vector3(0, -0.2f, 1));
            f.TriFacing(new Vector3(1.2f, 0, -0.25f), new Vector3(0, -1.2f, 0), new Vector3(-1.2f, 0, -0.25f), new Vector3(0, -0.2f, -1));
            return f.Build("Blade");
        }
        public static Mesh Needle() { return new MeshFactory().Cylinder(1.2f, 1.2f, 5, 15, 16).Cylinder(0.05f, 0.2f, 0, 10, 10).Build("Needle"); }
        public static Mesh Ball() { var f = new MeshFactory().Cylinder(1.2f, 1.2f, 5, 15, 16).Cylinder(0.4f, 0.4f, 2, 5, 10); f.Sphere(new Vector3(0, 1.5f, 0), 1.5f, 12); return f.Build("Ball"); }

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

        public static Mesh ForcepsArm(float length = 95f)
        {
            var f = new MeshFactory().Box(new Vector3(0, 4, 0), new Vector3(0.6f, 4, 0.4f));
            for (int i = 0; i < 6; i++) f.Box(new Vector3(0, 0.8f + i * 1.1f, 0.45f), new Vector3(0.6f, 0.25f, 0.12f));
            f.Box(new Vector3(0, 8 + (length - 8) * 0.5f, 0), new Vector3(1.8f, (length - 8) * 0.5f, 0.7f));
            for (int i = 0; i < 10; i++) f.Box(new Vector3(0, length * 0.45f + i * 3, -0.9f), new Vector3(2.4f, 0.6f, 0.25f));
            return f.Build("ForcepsArm");
        }

        public static Mesh Frazier()
        {
            var f = new MeshFactory().Cylinder(1.5f, 1.5f, 0, 40, 16);
            var bend = Matrix4x4.TRS(new Vector3(0, 40, 0), Quaternion.Euler(0, 0, -30), Vector3.one);
            f.Transform(bend).Cylinder(1.5f, 1.5f, 0, 80, 16).Cylinder(4.5f, 4.5f, 60, 70, 16).Box(new Vector3(0, 66, 4.6f), new Vector3(2, 3, 0.6f));
            return f.Build("Frazier");
        }

        public static Mesh SkinHook()
        {
            var f = new MeshFactory().Cylinder(1.2f, 1.2f, 4, 110, 12).Cylinder(0.45f, 0.45f, 0, 4.5f, 8);
            f.Transform(Matrix4x4.Rotate(Quaternion.Euler(110, 0, 0))).Cylinder(0.4f, 0.3f, 0, 3.2f, 8);
            return f.Build("SkinHook");
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
