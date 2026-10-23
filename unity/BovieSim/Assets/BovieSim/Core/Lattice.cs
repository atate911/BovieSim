using System;
using System.Collections.Generic;
using UnityEngine;

namespace BovieSim
{
    /// <summary>XPBD link. kind 0 ordinary, 1 lipoma capsule link (host to cluster), 2 internal lipoma.</summary>
    public sealed class Constraint
    {
        public int a, b; public float rest, rest0, compliance, lambda; public bool alive; public int kind;
    }

    public struct Attachment { public int node; public Vector3 target; public float strength; }

    /// <summary>
    /// XPBD mechanics lattice in material space (mm). Port of src/mechanics/lattice.ts.
    /// `pos` is the deformed position; rendering samples displacement = pos - mat.
    /// </summary>
    public sealed class Lattice
    {
        public readonly int nx, ny, nz, count;
        public readonly float spacing;
        public readonly Vector3 origin;
        public readonly float[] mat, pos, prev, vel, invMass;
        public readonly byte[] layer, cluster;
        public readonly List<Constraint> constraints = new List<Constraint>();
        public readonly List<Attachment> attachments = new List<Attachment>();
        /// <summary>displacement, float4 per node, uploaded for rendering</summary>
        public readonly Vector4[] disp;
        public int substeps = 8;
        public float damping = 0.12f;
        public float gravityOnFree = 0.004f;
        public bool lipomaFree;
        public int capsuleTotal, capsuleAlive;
        float twitchAmp, twitchPhase, twitchUntil = -1; Vector3 twitchCentre;

        static float Compliance(int t)
        {
            switch ((Tissue)t)
            {
                case Tissue.Epidermis: return 1.0e-6f;
                case Tissue.Dermis: return 2.0e-6f;
                case Tissue.Fat: return 4.0e-5f;
                case Tissue.Septum: return 5.0e-6f;
                case Tissue.SuperficialFascia: return 8.0e-7f;
                case Tissue.DeepFascia: return 5.0e-7f;
                case Tissue.Muscle: return 1.0e-5f;
                case Tissue.Artery: return 5.0e-6f;
                case Tissue.Vein: return 8.0e-6f;
                case Tissue.Nerve: return 3.0e-6f;
                case Tissue.Lipoma: return 3.0e-5f;
                case Tissue.LipomaCapsule: return 4.0e-6f;
                default: return 1e-5f;
            }
        }

        public Lattice(Volume volume, float spacingMm = 3f, float langerRad = 0f, float preAlong = 0.96f, float preAcross = 0.985f)
        {
            float sx = volume.SizeX, sy = volume.SizeDepth, sz = volume.SizeZ;
            spacing = spacingMm;
            nx = (int)Math.Round(sx / spacingMm) + 1; nz = (int)Math.Round(sz / spacingMm) + 1; ny = (int)Math.Round(sy / spacingMm) + 1;
            count = nx * ny * nz;
            origin = new Vector3(-sx / 2, -sy, -sz / 2);
            mat = new float[count * 3]; pos = new float[count * 3]; prev = new float[count * 3]; vel = new float[count * 3];
            invMass = new float[count]; layer = new byte[count]; cluster = new byte[count]; disp = new Vector4[count];
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        int n = Index(i, j, k);
                        float x = origin.x + i * spacingMm, y = origin.y + j * spacingMm, z = origin.z + k * spacingMm;
                        mat[n * 3] = pos[n * 3] = prev[n * 3] = x; mat[n * 3 + 1] = pos[n * 3 + 1] = prev[n * 3 + 1] = y; mat[n * 3 + 2] = pos[n * 3 + 2] = prev[n * 3 + 2] = z;
                        float vi, vj, vk; volume.WorldToVoxel(x, Math.Min(y, -0.2f), z, out vi, out vj, out vk);
                        int ci = Math.Min(Math.Max((int)Math.Round(vi), 0), volume.nx - 1), cj = Math.Min(Math.Max((int)Math.Round(vj), 0), volume.ny - 1);
                        int ck = Math.Min(Math.Max((int)Math.Round(vk), 0), volume.nz - 1);
                        while (ck > 0 && volume.occupancy[volume.Index(ci, cj, ck)] < 0.5f) ck--;
                        var ty = Cell.Type(volume.cell[volume.Index(ci, cj, ck)]);
                        layer[n] = (byte)ty;
                        cluster[n] = (byte)(ty == Tissue.Lipoma || ty == Tissue.LipomaCapsule ? 1 : 0);
                        bool edge = i == 0 || k == 0 || i == nx - 1 || k == nz - 1 || j == 0;
                        bool inAir = y > volume.SurfaceY(x, z) + 0.01f;
                        invMass[n] = edge || inAir ? 0f : 1f;
                    }
            float ca = (float)Math.Cos(langerRad), sa = (float)Math.Sin(langerRad);
            Action<int, int> add = (a, b) =>
            {
                int la = layer[a], lb = layer[b];
                float comp = Math.Max(Compliance(la), Compliance(lb));
                float dx = mat[b * 3] - mat[a * 3], dy = mat[b * 3 + 1] - mat[a * 3 + 1], dz = mat[b * 3 + 2] - mat[a * 3 + 2];
                float rest = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                bool skin = (la == (int)Tissue.Epidermis || la == (int)Tissue.Dermis) && (lb == (int)Tissue.Epidermis || lb == (int)Tissue.Dermis);
                if (skin && Math.Abs(dy) < 1e-3f)
                {
                    float along = Math.Abs((dx * ca + dz * sa) / rest);
                    rest *= preAlong * along + preAcross * (1 - along);
                }
                int ka = cluster[a], kb = cluster[b];
                int kind = ka != kb ? 1 : ka == 1 ? 2 : 0;
                bool alive = true;
                for (int t = 1; t <= 3; t++)
                {
                    float f = t / 4f;
                    float px = mat[a * 3] + dx * f, py = mat[a * 3 + 1] + dy * f, pz = mat[a * 3 + 2] + dz * f;
                    if (py > volume.SurfaceY(px, pz) + 0.01f) { alive = false; break; }
                }
                if (kind == 1 && alive) capsuleTotal++;
                constraints.Add(new Constraint { a = a, b = b, rest = rest, rest0 = rest, compliance = kind == 2 ? 2e-6f : comp, alive = alive, kind = kind });
            };
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        int n = Index(i, j, k);
                        if (i + 1 < nx) add(n, Index(i + 1, j, k));
                        if (j + 1 < ny) add(n, Index(i, j + 1, k));
                        if (k + 1 < nz) add(n, Index(i, j, k + 1));
                        if (i + 1 < nx && j + 1 < ny) { add(n, Index(i + 1, j + 1, k)); add(Index(i + 1, j, k), Index(i, j + 1, k)); }
                        if (i + 1 < nx && k + 1 < nz) { add(n, Index(i + 1, j, k + 1)); add(Index(i + 1, j, k), Index(i, j, k + 1)); }
                        if (j + 1 < ny && k + 1 < nz) { add(n, Index(i, j + 1, k + 1)); add(Index(i, j + 1, k), Index(i, j, k + 1)); }
                    }
            capsuleAlive = capsuleTotal;
        }

        public int Index(int i, int j, int k) { return i + nx * (j + ny * k); }
        public Vector3 Pos(int n) { return new Vector3(pos[n * 3], pos[n * 3 + 1], pos[n * 3 + 2]); }
        public Vector3 Mat(int n) { return new Vector3(mat[n * 3], mat[n * 3 + 1], mat[n * 3 + 2]); }

        /// <summary>Trilinear displacement at a material point.</summary>
        public Vector3 DisplacementAt(Vector3 p)
        {
            float fx = (p.x - origin.x) / spacing, fy = (p.y - origin.y) / spacing, fz = (p.z - origin.z) / spacing;
            int i0 = Math.Min(Math.Max((int)Math.Floor(fx), 0), nx - 2), j0 = Math.Min(Math.Max((int)Math.Floor(fy), 0), ny - 2), k0 = Math.Min(Math.Max((int)Math.Floor(fz), 0), nz - 2);
            float tx = Mathf.Clamp01(fx - i0), ty = Mathf.Clamp01(fy - j0), tz = Mathf.Clamp01(fz - k0);
            var o = Vector3.zero;
            for (int dk = 0; dk < 2; dk++)
                for (int dj = 0; dj < 2; dj++)
                    for (int di = 0; di < 2; di++)
                    {
                        int n = Index(i0 + di, j0 + dj, k0 + dk);
                        float w = (di == 1 ? tx : 1 - tx) * (dj == 1 ? ty : 1 - ty) * (dk == 1 ? tz : 1 - tz);
                        o.x += w * (pos[n * 3] - mat[n * 3]); o.y += w * (pos[n * 3 + 1] - mat[n * 3 + 1]); o.z += w * (pos[n * 3 + 2] - mat[n * 3 + 2]);
                    }
            return o;
        }

        public Vector3 WorldToMaterial(Vector3 pw)
        {
            var o = pw;
            for (int it = 0; it < 3; it++) o = pw - DisplacementAt(o);
            return o;
        }

        public int NearestNode(Vector3 pw, float radius = 4f)
        {
            int best = -1; float bd = radius * radius;
            for (int n = 0; n < count; n++)
            {
                if (invMass[n] == 0) continue;
                float dx = pos[n * 3] - pw.x, dy = pos[n * 3 + 1] - pw.y, dz = pos[n * 3 + 2] - pw.z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < bd) { bd = d2; best = n; }
            }
            return best;
        }

        /// <summary>Sever / shrink constraints from the GPU probe (per constraint: severed flag, max omega).</summary>
        public void ApplyProbe(byte[] severed, float[] omegaMax)
        {
            for (int c = 0; c < constraints.Count; c++)
            {
                var k = constraints[c];
                if (!k.alive) continue;
                if (severed[c] != 0) { k.alive = false; if (k.kind == 1) capsuleAlive--; continue; }
                float om = omegaMax[c];
                if (om > 0.5f)
                {
                    float sh = Mathf.Clamp01((om - 0.5f) / 1.5f);
                    k.rest = k.rest0 * (1 - 0.12f * sh * sh * (3 - 2 * sh));
                }
            }
            if (!lipomaFree && capsuleTotal > 0 && capsuleAlive < 0.62f * capsuleTotal) lipomaFree = true;
        }

        public void Stimulate(Vector3 centre, float amplitudeMm, float now)
        {
            twitchCentre = centre; twitchAmp = amplitudeMm; twitchPhase = now; twitchUntil = now + 0.5f;
        }

        public void Step(float dtSeconds, float now)
        {
            float dt = Math.Min(dtSeconds, 0.05f) / substeps;
            foreach (var c in constraints) c.lambda = 0;
            bool twitchActive = now < twitchUntil;
            float damp = 1 - damping;
            for (int s = 0; s < substeps; s++)
            {
                for (int n = 0; n < count; n++)
                {
                    if (invMass[n] == 0) continue;
                    int o = n * 3;
                    prev[o] = pos[o]; prev[o + 1] = pos[o + 1]; prev[o + 2] = pos[o + 2];
                    float gy = 0;
                    if (cluster[n] == 1 && lipomaFree) gy = -gravityOnFree * 1000;
                    if (twitchActive && layer[n] == (byte)Tissue.Muscle)
                    {
                        float dx = pos[o] - twitchCentre.x, dz = pos[o + 2] - twitchCentre.z;
                        float r2 = dx * dx + dz * dz;
                        if (r2 < 144)
                        {
                            float tau = now - twitchPhase;
                            gy += twitchAmp * (float)(Math.Exp(-tau * 6) * Math.Cos(tau * 2 * Math.PI * 6)) * 4000 * (1 - r2 / 144);
                        }
                    }
                    vel[o] *= damp; vel[o + 1] = vel[o + 1] * damp + gy * dt; vel[o + 2] *= damp;
                    pos[o] += vel[o] * dt; pos[o + 1] += vel[o + 1] * dt; pos[o + 2] += vel[o + 2] * dt;
                    float kb = cluster[n] == 1 && lipomaFree ? 0f : 0.004f;
                    pos[o] += (mat[o] - pos[o]) * kb; pos[o + 1] += (mat[o + 1] - pos[o + 1]) * kb; pos[o + 2] += (mat[o + 2] - pos[o + 2]) * kb;
                }
                foreach (var at in attachments)
                {
                    int o = at.node * 3;
                    pos[o] += (at.target.x - pos[o]) * at.strength; pos[o + 1] += (at.target.y - pos[o + 1]) * at.strength; pos[o + 2] += (at.target.z - pos[o + 2]) * at.strength;
                }
                float dt2 = dt * dt;
                for (int ci = 0; ci < constraints.Count; ci++)
                {
                    var c = constraints[ci];
                    if (!c.alive) continue;
                    int oa = c.a * 3, ob = c.b * 3;
                    float wa = invMass[c.a], wb = invMass[c.b];
                    if (wa + wb == 0) continue;
                    float dx = pos[ob] - pos[oa], dy = pos[ob + 1] - pos[oa + 1], dz = pos[ob + 2] - pos[oa + 2];
                    float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz); if (len < 1e-6f) continue;
                    float C = len - c.rest;
                    float alpha = c.compliance / dt2;
                    float dl = (-C - alpha * c.lambda) / (wa + wb + alpha);
                    c.lambda += dl;
                    float sx = dx / len * dl, sy = dy / len * dl, sz = dz / len * dl;
                    pos[oa] -= wa * sx; pos[oa + 1] -= wa * sy; pos[oa + 2] -= wa * sz;
                    pos[ob] += wb * sx; pos[ob + 1] += wb * sy; pos[ob + 2] += wb * sz;
                }
                for (int n = 0; n < count; n++)
                {
                    if (invMass[n] == 0) continue;
                    int o = n * 3;
                    vel[o] = (pos[o] - prev[o]) / dt; vel[o + 1] = (pos[o + 1] - prev[o + 1]) / dt; vel[o + 2] = (pos[o + 2] - prev[o + 2]) / dt;
                }
            }
            for (int n = 0; n < count; n++)
            {
                int o = n * 3;
                disp[n] = new Vector4(pos[o] - mat[o], pos[o + 1] - mat[o + 1], pos[o + 2] - mat[o + 2], 0);
            }
        }

        /// <summary>Constraint segments for the GPU probe: (a.xyz, throughTissue), (b.xyz, 0) per constraint.</summary>
        public Vector4[] SegmentData(Volume volume)
        {
            var o = new Vector4[constraints.Count * 2];
            for (int n = 0; n < constraints.Count; n++)
            {
                var c = constraints[n]; int a = c.a * 3, b = c.b * 3;
                float mx = (mat[a] + mat[b]) / 2, my = (mat[a + 1] + mat[b + 1]) / 2, mz = (mat[a + 2] + mat[b + 2]) / 2;
                float through = my < volume.SurfaceY(mx, mz) - 0.1f ? 1f : 0f;
                o[n * 2] = new Vector4(mat[a], mat[a + 1], mat[a + 2], through);
                o[n * 2 + 1] = new Vector4(mat[b], mat[b + 1], mat[b + 2], 0);
            }
            return o;
        }

        public float DistanceBetween(Vector3 a, Vector3 b)
        {
            int na = NearestNode(a, 10), nb = NearestNode(b, 10);
            if (na < 0 || nb < 0) return 0;
            return (Pos(na) - Pos(nb)).magnitude;
        }

        /// <summary>Mean rise (mm) of the lipoma cluster above its material position.</summary>
        public float ClusterRise()
        {
            float dy = 0; int cn = 0;
            for (int n = 0; n < count; n++) if (cluster[n] == 1) { dy += pos[n * 3 + 1] - mat[n * 3 + 1]; cn++; }
            return cn > 0 ? dy / cn : 0;
        }
    }
}
