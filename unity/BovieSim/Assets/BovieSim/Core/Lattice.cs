using System;
using System.Collections.Generic;
using UnityEngine;

namespace BovieSim
{
    /// <summary>
    /// XPBD link. kind 0 ordinary, 1 lipoma capsule link (host to cluster), 2 internal lipoma. Each link behaves like
    /// the tissue it runs through (see Lattice): nonlinear elasticity, yield, tearing and thermal state.
    /// </summary>
    public sealed class Constraint
    {
        public int a, b; public float rest, rest0, compliance, lambda; public bool alive; public int kind;
        /// <summary>governing tissue: the softer of the two end layers</summary>
        public Tissue tissue;
        /// <summary>|cos| between the link and the muscle fibre direction (muscle links only)</summary>
        public float along;
        /// <summary>permanent elongation factor from yield (&gt;= 1), thermal contraction (0..coagShrink)</summary>
        public float plastic = 1f, shrink;
        /// <summary>last probe: max Arrhenius damage, max dryness, any sample charred</summary>
        public float omega, dry; public bool charred;
        /// <summary>precomputed multipliers on tear strain and compliance</summary>
        public float tearMul = 1f, stiffMul = 1f;
        /// <summary>seconds spent above the tear strain; in-situ strain after settling</summary>
        public float over, baseStrain;
        /// <summary>the segment initially ran through tissue: its tear carves the volume</summary>
        public bool through;
    }

    public struct Attachment { public int node; public Vector3 target; public float strength; }

    /// <summary>A link that tore this frame: material endpoints, carve radius and governing tissue, for the GPU tear kernel.</summary>
    public struct TearRecord { public Vector3 a, b; public float radius; public Tissue tissue; }

    /// <summary>
    /// XPBD mechanics lattice in material space (mm). Port of src/mechanics/lattice.ts.
    /// `pos` is the deformed position; rendering samples displacement = pos - mat.
    /// Links stiffen with strain (per-tissue J-curve), yield into strands past the yield strain, tear past the
    /// tear strain (carving the voxel path), shrink and stiffen when coagulated, turn brittle when dried or
    /// charred; muscle is anisotropic and vessels recoil when cut.
    /// </summary>
    public sealed class Lattice
    {
        public const float TearDwell = 0.04f, YieldRate = 8f, VesselPretension = 0.92f, StrandCohesion = 0.5f;
        /// <summary>reference substep (60 fps, 8 substeps): per-substep damping, anchoring and attachment rates are defined at this step and rescaled to the actual one</summary>
        const float DtRef = 1f / 480f;

        public readonly int nx, ny, nz, count;
        public readonly float spacing;
        public readonly Vector3 origin;
        public readonly float[] mat, pos, prev, vel, invMass, anchor;
        public readonly byte[] layer, cluster, fibreX;
        public readonly List<Constraint> constraints = new List<Constraint>();
        public readonly List<Attachment> attachments = new List<Attachment>();
        public readonly List<TearRecord> tears = new List<TearRecord>();
        /// <summary>displacement, float4 per node, uploaded for rendering</summary>
        public readonly Vector4[] disp;
        public int substeps = 8;
        public float damping = 0.12f;
        public float gravityOnFree = 0.004f;
        public bool lipomaFree;
        public int capsuleTotal, capsuleAlive;
        public int tornLinks, yieldedLinks;
        int settling;
        float twitchAmp, twitchPhase, twitchUntil = -1; Vector3 twitchCentre;

        static TissueProps Prop(Tissue t) { return TissueTable.Props[(int)t]; }
        static float Smooth01(float x) { float t = Mathf.Clamp01(x); return t * t * (3 - 2 * t); }

        /// <summary>Effective XPBD compliance of a link through `tissue` at an elastic strain (&gt;= 0) and thermal multiplier: the J-curve.</summary>
        public static float EffectiveCompliance(Tissue tissue, float strain, float stiffMul = 1f)
        {
            var p = Prop(tissue);
            float sn = Math.Max(strain, 0) / p.kneeStrain;
            return p.compliance * stiffMul / (1 + sn * sn);
        }

        /// <summary>Thermal multipliers on tear strain and compliance, and the thermal shrink, for a link.</summary>
        public static void ThermalMultipliers(Tissue tissue, float omega, float dry, bool charred, float along, out float tearMul, out float stiffMul, out float shrink)
        {
            var p = Prop(tissue);
            bool coag = omega > 1;
            dry = Mathf.Clamp01(dry);
            shrink = p.coagShrink * Smooth01((omega - 0.5f) / 1.5f);
            tearMul = (1 - TissueTable.DryTearFactor * dry) * (coag ? TissueTable.CoagTearFactor : 1) * (charred ? TissueTable.CharTearFactor : 1);
            stiffMul = (1 - TissueTable.DryStiffen * dry) * (coag ? 1 / p.coagStiffen : 1);
            if (tissue == Tissue.Muscle) { tearMul *= 1 + 0.6f * along; stiffMul *= 1.4f - 0.8f * along; }
        }

        public Lattice(Volume volume, float spacingMm = 3f, float langerRad = 0f, float preAlong = 0.96f, float preAcross = 0.985f)
        {
            float sx = volume.SizeX, sy = volume.SizeDepth, sz = volume.SizeZ;
            spacing = spacingMm;
            nx = (int)Math.Round(sx / spacingMm) + 1; nz = (int)Math.Round(sz / spacingMm) + 1; ny = (int)Math.Round(sy / spacingMm) + 1;
            count = nx * ny * nz;
            origin = new Vector3(-sx / 2, -sy, -sz / 2);
            mat = new float[count * 3]; pos = new float[count * 3]; prev = new float[count * 3]; vel = new float[count * 3];
            invMass = new float[count]; anchor = new float[count]; layer = new byte[count]; cluster = new byte[count]; fibreX = new byte[count]; disp = new Vector4[count];
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
                        uint c = volume.cell[volume.Index(ci, cj, ck)];
                        var ty = Cell.Type(c);
                        layer[n] = (byte)ty;
                        fibreX[n] = (byte)((c & Cell.FlagFibreX) != 0 ? 1 : 0);
                        cluster[n] = (byte)(ty == Tissue.Lipoma || ty == Tissue.LipomaCapsule ? 1 : 0);
                        // anchoring to the body: 15 % at the skin, 100 % at the bottom of the bed
                        anchor[n] = 0.15f + 0.85f * Mathf.Clamp01(-y / sy);
                        bool edge = i == 0 || k == 0 || i == nx - 1 || k == nz - 1 || j == 0;
                        bool inAir = y > volume.SurfaceY(x, z) + 0.01f;
                        invMass[n] = edge || inAir ? 0f : 1f;
                    }
            float ca = (float)Math.Cos(langerRad), sa = (float)Math.Sin(langerRad);
            Action<int, int> add = (a, b) =>
            {
                var la = (Tissue)layer[a]; var lb = (Tissue)layer[b];
                // the softer layer governs the link
                var tissue = Prop(la).compliance >= Prop(lb).compliance ? la : lb;
                float dx = mat[b * 3] - mat[a * 3], dy = mat[b * 3 + 1] - mat[a * 3 + 1], dz = mat[b * 3 + 2] - mat[a * 3 + 2];
                float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                float rest = len;
                bool skin = (la == Tissue.Epidermis || la == Tissue.Dermis) && (lb == Tissue.Epidermis || lb == Tissue.Dermis);
                if (skin && Math.Abs(dy) < 1e-3f)
                {
                    float along = Math.Abs((dx * ca + dz * sa) / rest);
                    rest *= preAlong * along + preAcross * (1 - along);
                }
                // vessels are under tension in situ: a cut end recoils into the fat
                if (TissueTable.IsVessel(la) || TissueTable.IsVessel(lb)) rest *= VesselPretension;
                int ka = cluster[a], kb = cluster[b];
                int kind = ka != kb ? 1 : ka == 1 ? 2 : 0;
                bool alive = true;
                for (int t = 1; t <= 3; t++)
                {
                    float f = t / 4f;
                    float px = mat[a * 3] + dx * f, py = mat[a * 3 + 1] + dy * f, pz = mat[a * 3 + 2] + dz * f;
                    if (py > volume.SurfaceY(px, pz) + 0.01f) { alive = false; break; }
                }
                float my = (mat[a * 3 + 1] + mat[b * 3 + 1]) / 2;
                bool through = my < volume.SurfaceY((mat[a * 3] + mat[b * 3]) / 2, (mat[a * 3 + 2] + mat[b * 3 + 2]) / 2) - 0.1f;
                float alongF = 0;
                if (tissue == Tissue.Muscle)
                {
                    int fx = layer[a] == (byte)Tissue.Muscle ? fibreX[a] : fibreX[b];
                    alongF = len > 1e-6f ? Math.Abs(fx != 0 ? dx : dz) / len : 0;
                }
                if (kind == 1 && alive) capsuleTotal++;
                var cn = new Constraint { a = a, b = b, rest = rest, rest0 = rest, compliance = kind == 2 ? 2e-6f : Prop(tissue).compliance, alive = alive, kind = kind, tissue = tissue, along = alongF, through = through };
                UpdateThermal(cn);
                constraints.Add(cn);
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
            // settle into the pre-tensioned rest state before any yield or tearing can happen, and record it as the
            // in-situ reference: a bed lying there is not injured
            settling = 40;
            for (int s = 0; s < 40; s++) Step(1 / 60f, 0);
            foreach (var c in constraints)
            {
                if (!c.alive) continue;
                int oa = c.a * 3, ob = c.b * 3;
                float len = (float)Math.Sqrt(Sq(pos[ob] - pos[oa]) + Sq(pos[ob + 1] - pos[oa + 1]) + Sq(pos[ob + 2] - pos[oa + 2]));
                c.baseStrain = Math.Max(0, len / c.rest0 - 1);
            }
        }

        static float Sq(float x) { return x * x; }

        void UpdateThermal(Constraint c)
        {
            float tearMul, stiffMul, shrink;
            ThermalMultipliers(c.tissue, c.omega, c.dry, c.charred, c.along, out tearMul, out stiffMul, out shrink);
            c.tearMul = tearMul; c.stiffMul = stiffMul; c.shrink = shrink;
            c.rest = c.rest0 * (1 - c.shrink) * c.plastic;
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

        /// <summary>all free nodes within radius of a world point</summary>
        public List<int> NodesWithin(Vector3 pw, float radius)
        {
            var o = new List<int>(); float r2 = radius * radius;
            for (int n = 0; n < count; n++)
            {
                if (invMass[n] == 0) continue;
                float dx = pos[n * 3] - pw.x, dy = pos[n * 3 + 1] - pw.y, dz = pos[n * 3 + 2] - pw.z;
                if (dx * dx + dy * dy + dz * dz < r2) o.Add(n);
            }
            return o;
        }

        /// <summary>Sever / shrink / stiffen constraints from the GPU probe (per constraint: flags bit 0 severed, bit 2 char; max omega; max dryness).</summary>
        public void ApplyProbe(byte[] flags, float[] omegaMax, float[] dry = null)
        {
            for (int c = 0; c < constraints.Count; c++)
            {
                var k = constraints[c];
                if (!k.alive) continue;
                if ((flags[c] & 1) != 0) { Sever(k, false); continue; }
                float om = omegaMax[c];
                float dr = dry != null ? Mathf.Clamp01(dry[c]) : 0f;
                bool ch = (flags[c] & 4) != 0;
                if (om != k.omega || dr != k.dry || ch != k.charred) { k.omega = om; k.dry = dr; k.charred = ch; UpdateThermal(k); }
            }
            if (!lipomaFree && capsuleTotal > 0 && capsuleAlive < 0.62f * capsuleTotal) lipomaFree = true;
        }

        void Sever(Constraint c, bool mechanical)
        {
            c.alive = false;
            if (c.kind == 1) capsuleAlive--;
            if (mechanical)
            {
                tornLinks++;
                if (c.through) tears.Add(new TearRecord { a = Mat(c.a), b = Mat(c.b), radius = spacing * 0.3f, tissue = c.tissue });
            }
        }

        /// <summary>Tear records since the last call (material mm).</summary>
        public List<TearRecord> TakeTears() { var t = new List<TearRecord>(tears); tears.Clear(); return t; }

        public void Stimulate(Vector3 centre, float amplitudeMm, float now)
        {
            twitchCentre = centre; twitchAmp = amplitudeMm; twitchPhase = now; twitchUntil = now + 0.5f;
        }

        public void Step(float dtSeconds, float now)
        {
            float dt = Math.Min(dtSeconds, 0.05f) / substeps;
            bool twitchActive = now < twitchUntil;
            float rate = dt / DtRef;
            float damp = (float)Math.Pow(1 - damping, rate);
            Func<float, float> blend = k => 1f - (float)Math.Pow(1 - Math.Min(k, 0.999f), rate);
            float yieldK = Math.Min(1f, YieldRate * dt);
            for (int s = 0; s < substeps; s++)
            {
                // XPBD: the Lagrange multipliers belong to one time step; each substep is a new step (carrying them over
                // makes stiff links, fascia and the J-curve, pump energy into the bed until it tears itself apart at rest)
                foreach (var c in constraints) c.lambda = 0;
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
                    // spring back toward the material position: anchored to the body, firmly at depth, loosely at the skin
                    float kb = cluster[n] == 1 && lipomaFree ? 0f : blend(0.004f * anchor[n]);
                    pos[o] += (mat[o] - pos[o]) * kb; pos[o + 1] += (mat[o + 1] - pos[o + 1]) * kb; pos[o + 2] += (mat[o + 2] - pos[o + 2]) * kb;
                }
                foreach (var at in attachments)
                {
                    int o = at.node * 3; float k = blend(at.strength);
                    pos[o] += (at.target.x - pos[o]) * k; pos[o + 1] += (at.target.y - pos[o + 1]) * k; pos[o + 2] += (at.target.z - pos[o + 2]) * k;
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
                    var p = Prop(c.tissue);
                    // elastic strain from the yielded length, total elongation from the original length, both beyond the in-situ state
                    float strain = len / (c.rest0 * c.plastic) - 1 - c.baseStrain;
                    float total = len / c.rest0 - 1 - c.baseStrain;
                    // boundary links (one end pinned at the bed edge) stand for tissue beyond the bed: no yield, no tearing
                    if (total > 0 && settling == 0 && wa + wb > 1.5f)
                    {
                        if (total > p.tearStrain * c.tearMul)
                        {
                            c.over += dt;
                            if (c.over > TearDwell) { Sever(c, true); continue; }
                        }
                        else if (c.over > 0) c.over = Math.Max(0, c.over - dt);
                        float ys = p.yieldStrain;
                        if (strain > ys && ys < 5)
                        {
                            float target = len / (1 + ys + c.baseStrain) / c.rest0;
                            if (target > c.plastic)
                            {
                                float before = c.plastic;
                                c.plastic += (target - c.plastic) * yieldK;
                                if (before <= 1.02f && c.plastic > 1.02f) yieldedLinks++;
                                c.rest = c.rest0 * (1 - c.shrink) * c.plastic;
                            }
                        }
                    }
                    // strain stiffening (J-curve) and thermal state
                    float sn = Math.Max(strain, 0) / p.kneeStrain;
                    float comp = c.compliance * c.stiffMul / (1 + sn * sn);
                    float C = len - c.rest;
                    float alpha = comp / dt2;
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
            if (settling > 0) settling--;
        }

        /// <summary>True when the link is drawn as a strand: yielded, attached, through low-cohesion tissue and under tension.</summary>
        public bool IsStrand(Constraint c)
        {
            if (!c.alive || c.plastic <= 1.02f || Prop(c.tissue).cohesion > StrandCohesion) return false;
            int oa = c.a * 3, ob = c.b * 3;
            float len = (float)Math.Sqrt(Sq(pos[ob] - pos[oa]) + Sq(pos[ob + 1] - pos[oa + 1]) + Sq(pos[ob + 2] - pos[oa + 2]));
            return len > c.rest0 * (1 + c.baseStrain + 0.5f * Prop(c.tissue).yieldStrain);
        }

        /// <summary>Strand segments for rendering: deformed endpoints and thinness w (0 fresh .. 1 about to snap). Returns the count written.</summary>
        public int StrandData(Vector4[] outA, Vector4[] outB)
        {
            int n = 0;
            foreach (var c in constraints)
            {
                if (n >= outA.Length) break;
                if (!IsStrand(c)) continue;
                int oa = c.a * 3, ob = c.b * 3;
                float len = (float)Math.Sqrt(Sq(pos[ob] - pos[oa]) + Sq(pos[ob + 1] - pos[oa + 1]) + Sq(pos[ob + 2] - pos[oa + 2]));
                float w = Mathf.Clamp01((len / c.rest0 - 1 - c.baseStrain) / (Prop(c.tissue).tearStrain * c.tearMul));
                outA[n] = new Vector4(pos[oa], pos[oa + 1], pos[oa + 2], w);
                outB[n] = new Vector4(pos[ob], pos[ob + 1], pos[ob + 2], w);
                n++;
            }
            return n;
        }

        /// <summary>Constraint segments for the GPU probe: (a.xyz, throughTissue), (b.xyz, 0) per constraint.</summary>
        public Vector4[] SegmentData(Volume volume)
        {
            var o = new Vector4[constraints.Count * 2];
            for (int n = 0; n < constraints.Count; n++)
            {
                var c = constraints[n]; int a = c.a * 3, b = c.b * 3;
                o[n * 2] = new Vector4(mat[a], mat[a + 1], mat[a + 2], c.through ? 1f : 0f);
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
