using System;
using System.Collections.Generic;
using UnityEngine;

namespace BovieSim
{
    /// <summary>
    /// Steam (0), smoke (1), arc filaments (2) and blood droplets (3). CPU-simulated (port of particles.ts) and
    /// drawn as camera-facing quads by BovieSim/Particles (a hand-written HDRP ForwardOnly shader) via
    /// Graphics.RenderPrimitives. Positions are field millimetres.
    /// </summary>
    public sealed class Particles : IDisposable
    {
        public const int Max = 4096;
        struct P { public Vector3 p, v, d; public float age, life, size0, size1, alpha, vol; public int kind; }
        // GPU record: pos.xyz, size | color.rgb, alpha | dir.xyz, kind  (matches Particles.shader)
        struct Gpu { public Vector4 a, b, c; }

        readonly List<P> parts = new List<P>(Max);
        readonly Gpu[] data = new Gpu[Max];
        readonly ComputeBuffer buffer;
        readonly Material alphaMat, addMat;
        readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock(), mpbAdd = new MaterialPropertyBlock();
        int countAlpha, countAdd;
        uint rng = 12345;
        /// <summary>droplets that landed this frame: xyz (mm), w = microlitres</summary>
        public readonly List<Vector4> landings = new List<Vector4>();

        public Particles(Material alphaMaterial, Material additiveMaterial)
        {
            alphaMat = alphaMaterial; addMat = additiveMaterial;
            buffer = new ComputeBuffer(Max, 48);
        }

        float Rnd() { rng = rng * 1664525u + 1013904223u; return rng / 4294967296f; }

        public void Emit(int kind, Vector3 at, float rate, float dt)
        {
            float n = rate * dt; int count = (int)n; if (Rnd() < n - count) count++;
            for (int i = 0; i < count && parts.Count < Max - 64; i++)
            {
                float a = Rnd() * Mathf.PI * 2, sp = kind == 0 ? 14 + Rnd() * 12 : 6 + Rnd() * 8;
                parts.Add(new P
                {
                    p = at + new Vector3((Rnd() - 0.5f) * 1.2f, 0.3f, (Rnd() - 0.5f) * 1.2f),
                    v = new Vector3(Mathf.Cos(a) * sp * 0.35f, sp, Mathf.Sin(a) * sp * 0.35f),
                    life = kind == 0 ? 0.9f + Rnd() * 0.6f : 2.5f + Rnd() * 2f,
                    size0 = kind == 0 ? 0.4f : 0.5f, size1 = kind == 0 ? 1.8f + Rnd() * 1.5f : 2.5f + Rnd() * 2.5f,
                    kind = kind, d = Vector3.up, alpha = kind == 0 ? 0.14f : 0.12f,
                });
            }
        }

        public void Arc(Vector3 a, Vector3 b)
        {
            if (parts.Count >= Max - 1) return;
            var d = b - a;
            parts.Add(new P { p = a, life = 0.04f + Rnd() * 0.05f, size0 = d.magnitude, size1 = d.magnitude, kind = 2, d = d, alpha = 0.6f + Rnd() * 0.4f });
        }

        /// <summary>Droplets from an exposed vessel end; mlPerS > 0 arterial jet, &lt; 0 venous welling. Volume is conserved through landings.</summary>
        public void Bleed(Vector3 at, float mlPerS, float dt)
        {
            bool arterial = mlPerS > 0;
            float volUl = Mathf.Abs(mlPerS) * dt * 1000, perDrop = arterial ? 0.4f : 0.8f;
            float n = volUl / perDrop; int count = (int)n; if (Rnd() < n - count) count++;
            for (int i = 0; i < count && parts.Count < Max - 64; i++)
            {
                float a = Rnd() * Mathf.PI * 2, el = arterial ? 0.5f + Rnd() * 0.9f : 0.9f + Rnd() * 0.5f;
                float sp = arterial ? 120 + Rnd() * 280 : 15 + Rnd() * 30;
                parts.Add(new P
                {
                    p = at + new Vector3(0, 0.2f, 0),
                    v = new Vector3(Mathf.Cos(a) * Mathf.Cos(el) * sp, Mathf.Sin(el) * sp, Mathf.Sin(a) * Mathf.Cos(el) * sp),
                    life = 3f, size0 = arterial ? 0.35f : 0.5f, size1 = arterial ? 0.35f : 0.5f, kind = 3, d = Vector3.up, alpha = 0.95f, vol = perDrop,
                });
            }
        }

        public void Update(float dt, bool suction, Vector3 suctionAt, Func<float, float, float> landingY)
        {
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                var p = parts[i];
                p.age += dt;
                bool remove = p.age >= p.life;
                if (!remove && p.kind == 3)
                {
                    p.v.y -= 9810 * dt; p.v.x *= 1 - 0.5f * dt; p.v.z *= 1 - 0.5f * dt;
                    p.p += p.v * dt;
                    if (p.v.y < 0 && p.p.y <= landingY(p.p.x, p.p.z)) { landings.Add(new Vector4(p.p.x, p.p.y, p.p.z, p.vol)); remove = true; }
                }
                else if (!remove && p.kind != 2)
                {
                    p.v.y += (p.kind == 0 ? 10 : 4) * dt; p.v.x *= 1 - 0.8f * dt; p.v.z *= 1 - 0.8f * dt; p.v.y *= 1 - 0.5f * dt;
                    p.v.x += (Rnd() - 0.5f) * 20 * dt + 3 * dt; p.v.z += (Rnd() - 0.5f) * 20 * dt;
                    if (suction)
                    {
                        var d = suctionAt - p.p; float d2 = d.sqrMagnitude;
                        if (d2 < 900) { float f = 400 * dt / (d2 + 20); p.v += d * f; if (d2 < 9) remove = true; }
                    }
                    p.p += p.v * dt;
                }
                if (remove) { parts[i] = parts[parts.Count - 1]; parts.RemoveAt(parts.Count - 1); }
                else parts[i] = p;
            }
        }

        public void Draw(Transform fieldRoot)
        {
            int o = 0; countAlpha = 0;
            for (int pass = 0; pass < 2; pass++)
                foreach (var p in parts)
                {
                    bool add = p.kind == 2;
                    if ((pass == 0) == add) continue;
                    float f = p.age / p.life;
                    float size = p.kind >= 2 ? p.size0 : p.size0 + (p.size1 - p.size0) * Mathf.Sqrt(f);
                    float alpha = p.kind >= 2 ? p.alpha : p.alpha * (f < 0.1f ? f / 0.1f : 1 - (f - 0.1f) / 0.9f);
                    Color col = p.kind == 0 ? new Color(0.92f, 0.92f, 0.94f) : p.kind == 1 ? new Color(0.30f, 0.27f, 0.24f) : p.kind == 3 ? new Color(0.55f, 0.04f, 0.03f) : new Color(0.65f, 0.78f, 1f);
                    data[o].a = new Vector4(p.p.x, p.p.y, p.p.z, size);
                    data[o].b = new Vector4(col.r, col.g, col.b, alpha);
                    data[o].c = new Vector4(p.d.x, p.d.y, p.d.z, p.kind);
                    o++;
                    if (pass == 0) countAlpha = o;
                }
            countAdd = o - countAlpha;
            if (o == 0 || alphaMat == null) return;
            buffer.SetData(data, 0, 0, o);
            var bounds = new Bounds(fieldRoot.position, Vector3.one * 2f);
            mpb.SetBuffer("_Particles", buffer);
            mpb.SetMatrix("_FieldToWorld", fieldRoot.localToWorldMatrix);
            if (countAlpha > 0)
            {
                mpb.SetInt("_Offset", 0);
                var rp = new RenderParams(alphaMat) { worldBounds = bounds, matProps = mpb };
                Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 6, countAlpha);
            }
            if (countAdd > 0 && addMat != null)
            {
                mpbAdd.SetBuffer("_Particles", buffer); mpbAdd.SetMatrix("_FieldToWorld", fieldRoot.localToWorldMatrix); mpbAdd.SetInt("_Offset", countAlpha);
                var rp = new RenderParams(addMat) { worldBounds = bounds, matProps = mpbAdd };
                Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 6, countAdd);
            }
        }

        public int Count { get { return parts.Count; } }
        public void Dispose() { if (buffer != null) buffer.Release(); }
    }
}
