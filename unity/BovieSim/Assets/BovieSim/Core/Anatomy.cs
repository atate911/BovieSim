using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BovieSim
{
    public struct VesselInfo { public float x, y, r; public bool arterial; }

    public sealed class AnatomyReport
    {
        public readonly List<VesselInfo> vessels = new List<VesselInfo>();
        public float fatMmActual, deepFasciaDepthMm;
        /// <summary>depth (mm) of the serosal surface when the scenario has an organ (0 otherwise)</summary>
        public float organDepthMm;
        public bool hasLesion;
        public float lesionX, lesionY, lesionZ;
        public double fillMs;
    }

    /// <summary>
    /// Port of src/core/scenario/anatomy.ts. Same seed gives the same bed as the web client.
    /// Planes: a loose areolar sheet on either face of Scarpa's fascia and above the deep fascia, and an areolar
    /// sheath around a lipoma's capsule; an optional organ (muscle, serosa, viscera) below the deep fascia.
    /// </summary>
    public static class Anatomy
    {
        public delegate float Sdf(float x, float y, float depth);

        public static Volume Build(Scenario sc, out AnatomyReport report, float voxelOverride = 0f, int seedOverride = int.MinValue)
        {
            var sw = Stopwatch.StartNew();
            float h = voxelOverride > 0 ? voxelOverride : sc.bed.voxelMm;
            int airLayers = Math.Max(4, (int)Math.Round(1.5 / h));
            int nx = (int)Math.Round(sc.bed.sizeMm[0] / h), ny = (int)Math.Round(sc.bed.sizeMm[1] / h);
            int nz = (int)Math.Round(sc.bed.sizeMm[2] / h) + airLayers;
            var vol = new Volume(nx, ny, nz, h, airLayers);
            int seed = seedOverride != int.MinValue ? seedOverride : sc.vessels.seed;
            var rng = new Mulberry32(unchecked((uint)(seed * 7919 + 13)));
            var L = sc.layers;
            float areolar = L.areolarMm >= 0 ? L.areolarMm : 0.5f;
            double fatScale = 1 + L.variation * (rng.Next() * 2 - 1);
            float fatMm = (float)(L.fatMm * fatScale);
            var organ = sc.organ != null && sc.organ.IsPresent ? sc.organ : null;
            var cell = vol.cell; var occupancy = vol.occupancy; var water = vol.water;
            float sx = nx * h, sy = ny * h;
            int surfK = vol.SurfaceK;
            var P = TissueTable.Props;

            // ---- per-column precompute -----------------------------------------------------------
            int nc = nx * ny;
            var colSurf = new float[nc]; var colDermis = new float[nc];
            var colScarpaTop = new float[nc]; var colScarpaBot = new float[nc];
            var colDeepTop = new float[nc]; var colDeepBot = new float[nc];
            var colSeptum = new bool[nc]; var colLobule = new byte[nc];
            float lobule = sc.septa.lobuleMm, septHalf = sc.septa.thicknessMm * 0.5f;
            // columns are independent: fill rows in parallel (the noise functions are pure)
            System.Threading.Tasks.Parallel.For(0, ny, j =>
            {
                for (int i = 0; i < nx; i++)
                {
                    double x = (i + 0.5) * h, y = (j + 0.5) * h;
                    int c = i + nx * j;
                    double undul = (Noise.Fbm2(x / 18, y / 18, 3, seed) - 0.5) * 0.5 + (Noise.Fbm2(x / 2.5, y / 2.5, 2, seed + 3) - 0.5) * 0.12;
                    colSurf[c] = (float)(surfK + undul / h);
                    float dermBot = (float)(L.epidermisMm + L.dermisMm * (0.9 + 0.2 * Noise.Fbm2(x / 12, y / 12, 2, seed + 11)));
                    colDermis[c] = dermBot;
                    float fatHere = (float)(fatMm * (0.85 + 0.3 * Noise.Fbm2(x / 25, y / 25, 3, seed + 17)));
                    float scarpa = dermBot + fatHere * L.superficialFasciaAt;
                    colScarpaTop[c] = scarpa; colScarpaBot[c] = scarpa + L.superficialFasciaMm;
                    colDeepTop[c] = dermBot + fatHere; colDeepBot[c] = dermBot + fatHere + L.deepFasciaMm;
                    double wx = x + (Noise.Fbm2(x / 9, y / 9, 2, seed + 23) - 0.5) * 2.5;
                    double wy = y + (Noise.Fbm2(x / 9, y / 9, 2, seed + 29) - 0.5) * 2.5;
                    var vor = Noise.Voronoi2(wx / lobule, wy / lobule, seed);
                    colSeptum[c] = (vor.f2 - vor.f1) * lobule * 0.5 < septHalf;
                    colLobule[c] = (byte)vor.id;
                }
            });

            // ---- layer fill -----------------------------------------------------------------------
            float wEpi = P[(int)Tissue.Epidermis].water, wDerm = P[(int)Tissue.Dermis].water, wFat = P[(int)Tissue.Fat].water;
            float wSept = P[(int)Tissue.Septum].water, wFasc = P[(int)Tissue.DeepFascia].water, wMus = P[(int)Tissue.Muscle].water;
            float wAre = P[(int)Tissue.Areolar].water, wSer = P[(int)Tissue.Serosa].water, wVis = P[(int)Tissue.Viscera].water;
            uint cEpi = Cell.Pack(Tissue.Epidermis), cMus = Cell.Pack(Tissue.Muscle, 0, 0, Cell.FlagFibreX);
            uint cScarpa = Cell.Pack(Tissue.SuperficialFascia), cDeep = Cell.Pack(Tissue.DeepFascia);
            uint cAre = Cell.Pack(Tissue.Areolar), cSer = Cell.Pack(Tissue.Serosa), cVis = Cell.Pack(Tissue.Viscera);
            uint cDermDeep = Cell.Pack(Tissue.Dermis, 0, 0, Cell.FlagDermalPlexus), cDerm = Cell.Pack(Tissue.Dermis);
            System.Threading.Tasks.Parallel.For(0, ny, j =>
            {
                for (int i = 0; i < nx; i++)
                {
                    int c = i + nx * j;
                    float surf = colSurf[c];
                    float dermBot = colDermis[c], sTop = colScarpaTop[c], sBot = colScarpaBot[c], dTop = colDeepTop[c], dBot = colDeepBot[c];
                    float musBot = organ != null ? dBot + organ.muscleMm : 1e9f;
                    float serBot = organ != null ? musBot + organ.serosaMm : 1e9f;
                    bool sept = colSeptum[c];
                    uint cFat = Cell.Pack(Tissue.Fat, 0, colLobule[c]), cSept = Cell.Pack(Tissue.Septum, 0, colLobule[c]);
                    int topK = (int)Math.Floor(surf - 0.5f);
                    for (int k = 0; k < nz; k++)
                    {
                        int idx = i + nx * (j + ny * k);
                        float occ = Math.Min(1f, Math.Max(0f, surf - k));
                        occupancy[idx] = occ;
                        if (occ <= 0) { cell[idx] = 0; water[idx] = 0; continue; }
                        float depth = (surf - (k + 0.5f)) * h;
                        uint cc; float w;
                        // septal attachments tether the areolar planes, so a septum column stays fibrous through them
                        if (k >= topK || depth < L.epidermisMm) { cc = cEpi; w = wEpi; }
                        else if (depth < dermBot) { cc = depth > dermBot - 0.6f ? cDermDeep : cDerm; w = wDerm; }
                        else if (depth < sTop - areolar) { if (sept) { cc = cSept; w = wSept; } else { cc = cFat; w = wFat; } }
                        else if (depth < sTop) { if (sept) { cc = cSept; w = wSept; } else { cc = cAre; w = wAre; } }
                        else if (depth < sBot) { cc = cScarpa; w = wFasc; }
                        else if (depth < sBot + areolar) { if (sept) { cc = cSept; w = wSept; } else { cc = cAre; w = wAre; } }
                        else if (depth < dTop - areolar) { if (sept) { cc = cSept; w = wSept; } else { cc = cFat; w = wFat; } }
                        else if (depth < dTop) { if (sept) { cc = cSept; w = wSept; } else { cc = cAre; w = wAre; } }
                        else if (depth < dBot) { cc = cDeep; w = wFasc; }
                        else if (depth < musBot) { cc = cMus; w = wMus; }
                        else if (depth < serBot) { cc = cSer; w = wSer; }
                        else { cc = cVis; w = wVis; }
                        cell[idx] = cc; water[idx] = w;
                    }
                }
            });

            report = new AnatomyReport { fatMmActual = fatMm, deepFasciaDepthMm = L.epidermisMm + L.dermisMm + fatMm };
            if (organ != null) report.organDepthMm = report.deepFasciaDepthMm + L.deepFasciaMm + organ.muscleMm;
            var rep = report;

            // ---- painters ---------------------------------------------------------------------------
            Action<float, float, float, float, float, Tissue, float, float> paintCylinderZ = (x0, y0, r, depthFrom, depthTo, type, meander, phase) =>
            {
                float rv = Math.Max(r, h * 0.6f);
                int auxR = Math.Min(255, (int)Math.Round(r / 0.05f));
                uint cc = Cell.Pack(type, 0, auxR); float wv = P[(int)type].water;
                int iMin = Math.Max(0, (int)Math.Floor((x0 - rv - meander) / h)), iMax = Math.Min(nx - 1, (int)Math.Ceiling((x0 + rv + meander) / h));
                int jMin = Math.Max(0, (int)Math.Floor((y0 - rv - meander) / h)), jMax = Math.Min(ny - 1, (int)Math.Ceiling((y0 + rv + meander) / h));
                for (int k = 0; k < nz; k++)
                    for (int j = jMin; j <= jMax; j++)
                        for (int i = iMin; i <= iMax; i++)
                        {
                            int c = i + nx * j;
                            float depth = (colSurf[c] - (k + 0.5f)) * h;
                            if (depth < depthFrom || depth > depthTo) continue;
                            float mx = x0 + meander * (float)Math.Sin(depth * 0.9 + phase), my = y0 + meander * (float)Math.Cos(depth * 0.7 + phase * 1.3);
                            float dx = (i + 0.5f) * h - mx, dy = (j + 0.5f) * h - my;
                            if (dx * dx + dy * dy <= rv * rv)
                            {
                                int idx = i + nx * (j + ny * k);
                                if (occupancy[idx] > 0) { cell[idx] = cc; water[idx] = wv; }
                            }
                        }
            };
            Action<float, float, float, float, float, float, float, Tissue, int> paintSegment = (ax, ay, az, bx, by, bz, r, type, aux) =>
            {
                float rv = Math.Max(r, h * 0.6f);
                uint cc = Cell.Pack(type, 0, aux); float wv = P[(int)type].water;
                int iMin = Math.Max(0, (int)Math.Floor((Math.Min(ax, bx) - rv) / h)), iMax = Math.Min(nx - 1, (int)Math.Ceiling((Math.Max(ax, bx) + rv) / h));
                int jMin = Math.Max(0, (int)Math.Floor((Math.Min(ay, by) - rv) / h)), jMax = Math.Min(ny - 1, (int)Math.Ceiling((Math.Max(ay, by) + rv) / h));
                float dMin = Math.Min(az, bz) - rv, dMax = Math.Max(az, bz) + rv;
                float abx = bx - ax, aby = by - ay, abz = bz - az;
                float ab2 = abx * abx + aby * aby + abz * abz; if (ab2 == 0) ab2 = 1;
                for (int j = jMin; j <= jMax; j++)
                    for (int i = iMin; i <= iMax; i++)
                    {
                        int c = i + nx * j; float surf = colSurf[c];
                        int kHi = Math.Min(nz - 1, (int)Math.Ceiling(surf - dMin / h)), kLo = Math.Max(0, (int)Math.Floor(surf - dMax / h) - 1);
                        float px = (i + 0.5f) * h, py = (j + 0.5f) * h;
                        for (int k = kLo; k <= kHi; k++)
                        {
                            float pz = (surf - (k + 0.5f)) * h;
                            float t = ((px - ax) * abx + (py - ay) * aby + (pz - az) * abz) / ab2;
                            t = Math.Max(0, Math.Min(1, t));
                            float qx = ax + abx * t - px, qy = ay + aby * t - py, qz = az + abz * t - pz;
                            if (qx * qx + qy * qy + qz * qz <= rv * rv)
                            {
                                int idx = i + nx * (j + ny * k);
                                if (occupancy[idx] > 0) { cell[idx] = cc; water[idx] = wv; }
                            }
                        }
                    }
            };
            Action<float, float, float, bool> placeVessel = (x, y, r, arterial) =>
            {
                int c = Math.Min(nc - 1, Math.Max(0, (int)Math.Floor(x / h) + nx * (int)Math.Floor(y / h)));
                var type = arterial ? Tissue.Artery : Tissue.Vein;
                paintCylinderZ(x, y, r, colDermis[c] - 0.3f, colDeepBot[c] + 1.5f, type, (float)(0.6 + rng.Next() * 0.8), (float)(rng.Next() * 6.28));
                double ang = rng.Next() * 6.28, len = 6 + rng.Next() * 10;
                paintSegment(x, y, colDeepTop[c] - r, x + (float)(Math.Cos(ang) * len), y + (float)(Math.Sin(ang) * len), colDeepTop[c] - r, r * 1.1f, type, (int)Math.Round(r * 1.1 / 0.05));
                rep.vessels.Add(new VesselInfo { x = x - sx / 2, y = y - sy / 2, r = r, arterial = arterial });
            };

            // ---- vessels ------------------------------------------------------------------------------
            foreach (var v in sc.vessels.@explicit) placeVessel(v.xMm + sx / 2, v.yMm + sy / 2, v.rMm, v.arterial);
            float areaCm2 = (sx / 10) * (sy / 10);
            int nPerf = (int)Math.Round(sc.vessels.perforatorsPerCm2 * areaCm2);
            float rMin = sc.vessels.radiusMm[0], rMax = sc.vessels.radiusMm[1];
            for (int n = 0; n < nPerf; n++)
            {
                float x = 0, y = 0;
                for (int tries = 0; tries < 40; tries++)
                {
                    x = (float)((0.08 + 0.84 * rng.Next()) * sx); y = (float)((0.08 + 0.84 * rng.Next()) * sy);
                    int c = (int)Math.Floor(x / h) + nx * (int)Math.Floor(y / h);
                    if (colSeptum[c]) break;
                }
                float r = (float)(rMin + (rMax - rMin) * Math.Pow(rng.Next(), 1.6));
                placeVessel(x, y, r, rng.Next() < sc.vessels.arterialFraction);
            }
            // subserosal vessels run a millimetre under the organ surface; an optional bleeder lies in the serosa itself
            if (organ != null)
            {
                int nSer = (int)Math.Round(0.4 * areaCm2);
                for (int n = 0; n < nSer; n++)
                {
                    float x = (float)((0.1 + 0.8 * rng.Next()) * sx), y = (float)((0.1 + 0.8 * rng.Next()) * sy);
                    int c = (int)Math.Floor(x / h) + nx * (int)Math.Floor(y / h);
                    float r = (float)(0.25 + 0.45 * rng.Next());
                    float d0 = colDeepBot[c] + organ.muscleMm + organ.serosaMm + 1.0f + r;
                    double ang = rng.Next() * 6.28, len = 8 + rng.Next() * 10;
                    paintSegment(x, y, d0, x + (float)(Math.Cos(ang) * len), y + (float)(Math.Sin(ang) * len), d0 + 0.3f, r, rng.Next() < 0.5 ? Tissue.Artery : Tissue.Vein, (int)Math.Round(r / 0.05));
                }
                if (organ.HasBleeder)
                {
                    var bl = organ.bleeder;
                    float x = bl.xMm + sx / 2, y = bl.yMm + sy / 2;
                    int c = Math.Min(nc - 1, Math.Max(0, (int)Math.Floor(x / h) + nx * (int)Math.Floor(y / h)));
                    float d0 = colDeepBot[c] + organ.muscleMm + 0.1f;
                    double ang = bl.angleDeg * Math.PI / 180; float len = bl.lengthMm / 2;
                    paintSegment(x - (float)Math.Cos(ang) * len, y - (float)Math.Sin(ang) * len, d0, x + (float)Math.Cos(ang) * len, y + (float)Math.Sin(ang) * len, d0, bl.rMm, bl.arterial ? Tissue.Artery : Tissue.Vein, (int)Math.Round(bl.rMm / 0.05));
                    rep.vessels.Add(new VesselInfo { x = bl.xMm, y = bl.yMm, r = bl.rMm, arterial = bl.arterial });
                }
            }

            // ---- nerves ----------------------------------------------------------------------------------
            foreach (var nv in sc.nerves)
            {
                float ax = nv.fromMm[0] + sx / 2, ay = nv.fromMm[1] + sy / 2, bx = nv.toMm[0] + sx / 2, by = nv.toMm[1] + sy / 2;
                const int steps = 12;
                for (int s = 0; s < steps; s++)
                {
                    float t0 = s / (float)steps, t1 = (s + 1) / (float)steps;
                    float w0 = (float)Math.Sin(t0 * 9.1) * 1.2f, w1 = (float)Math.Sin(t1 * 9.1) * 1.2f;
                    paintSegment(ax + (bx - ax) * t0, ay + (by - ay) * t0 + w0, nv.depthMm, ax + (bx - ax) * t1, ay + (by - ay) * t1 + w1, nv.depthMm, nv.radiusMm, Tissue.Nerve, 0);
                }
            }

            // ---- lesion -----------------------------------------------------------------------------------
            if (sc.lesion.IsLipoma)
            {
                var le = sc.lesion;
                float cx = le.centerMm[0] + sx / 2, cy = le.centerMm[1] + sy / 2;
                float a = le.sizeMm[0], b = le.sizeMm[1], cz = le.sizeMm[2];
                int cc0 = Math.Min(nc - 1, Math.Max(0, (int)Math.Floor(cx / h) + nx * (int)Math.Floor(cy / h)));
                float topDepth = le.topDepthMm;
                if (le.plane == "subfascial") topDepth = colDeepBot[cc0] + 0.5f;
                if (le.plane == "intramuscular") topDepth = colDeepBot[cc0] + 3f;
                float cdz = topDepth + cz, cap = le.capsuleMm;
                float sheath = le.areolarMm >= 0 ? le.areolarMm : 0.4f;
                uint cLip = Cell.Pack(Tissue.Lipoma), cCap = Cell.Pack(Tissue.LipomaCapsule);
                float wLip = P[(int)Tissue.Lipoma].water, wCap = P[(int)Tissue.LipomaCapsule].water;
                int iMin = Math.Max(0, (int)Math.Floor((cx - a - cap - sheath) / h)), iMax = Math.Min(nx - 1, (int)Math.Ceiling((cx + a + cap + sheath) / h));
                int jMin = Math.Max(0, (int)Math.Floor((cy - b - cap - sheath) / h)), jMax = Math.Min(ny - 1, (int)Math.Ceiling((cy + b + cap + sheath) / h));
                float minAxis = Math.Min(a, Math.Min(b, cz));
                float shell = cap / minAxis, shellA = sheath / minAxis;
                for (int j = jMin; j <= jMax; j++)
                    for (int i = iMin; i <= iMax; i++)
                    {
                        int c = i + nx * j; float surf = colSurf[c];
                        float px = (i + 0.5f) * h, py = (j + 0.5f) * h;
                        double lobN = 1 + 0.08 * (Noise.Fbm2(px / 3.0, py / 3.0, 2, seed + 41) - 0.5) * 2;
                        for (int k = 0; k < nz; k++)
                        {
                            float pz = (surf - (k + 0.5f)) * h;
                            if (pz < topDepth - cap - sheath - h || pz > cdz + cz + cap + sheath + h) continue;
                            float ex = (px - cx) / a, ey = (py - cy) / b, ez = (pz - cdz) / cz;
                            double rr = Math.Sqrt(ex * ex + ey * ey + ez * ez) * lobN;
                            int idx = i + nx * (j + ny * k);
                            if (occupancy[idx] <= 0) continue;
                            if (rr <= 1 - shell) { cell[idx] = cLip; water[idx] = wLip; }
                            else if (rr <= 1) { cell[idx] = cCap; water[idx] = wCap; }
                            else if (rr <= 1 + shellA)
                            {
                                // the areolar plane around the capsule, except where the host is already fibrous or a vessel
                                var t = Cell.Type(cell[idx]);
                                if (t == Tissue.Fat || t == Tissue.Lipoma) { cell[idx] = cAre; water[idx] = wAre; }
                            }
                        }
                    }
                paintSegment(cx + a * 0.3f, cy, colDeepTop[cc0] - 0.2f, cx + a * 0.2f, cy, cdz + cz * 0.9f, 0.5f, Tissue.Artery, (int)Math.Round(0.5 / 0.05));
                rep.hasLesion = true; rep.lesionX = cx - sx / 2; rep.lesionY = -cdz; rep.lesionZ = cy - sy / 2;
            }

            // ---- pre-existing wound -------------------------------------------------------------------
            // the floor can follow a layer (the exposed deep fascia, muscle or serosa) instead of a fixed depth
            Func<PreCarve, float, float, float> floorDepth = (pc, x, y) =>
            {
                if (string.IsNullOrEmpty(pc.floor)) return pc.depthMm;
                int i = Math.Min(nx - 1, Math.Max(0, (int)Math.Floor((x + sx / 2) / h))), j = Math.Min(ny - 1, Math.Max(0, (int)Math.Floor((y + sy / 2) / h)));
                int c = i + nx * j;
                float fromSurface = pc.floor == "deepFascia" ? colDeepTop[c] : pc.floor == "muscle" ? colDeepBot[c] : colDeepBot[c] + (organ != null ? organ.muscleMm : 0f);
                // layer depths are below the actual (undulating) surface; the carve works in nominal-surface depth
                return fromSurface + 0.15f - (colSurf[c] - surfK) * h;
            };
            foreach (var pc in sc.preCarve)
            {
                var p = pc;
                CarveSdf(vol, (x, y, d) =>
                {
                    float cx = p.centerMm[0], cy = p.centerMm[1];
                    float dd = d - floorDepth(p, x, y);
                    if (p.kind == "pit")
                    {
                        float sl = p.slope >= 0 ? p.slope : 0.4f;
                        return Math.Max((float)Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - (p.lengthMm - d * sl), dd);
                    }
                    double ang = p.angleDeg * Math.PI / 180; float ca = (float)Math.Cos(ang), sa = (float)Math.Sin(ang);
                    float u = (x - cx) * ca + (y - cy) * sa, vv = -(x - cx) * sa + (y - cy) * ca;
                    float slope = p.slope >= 0 ? p.slope : 0.3f;
                    return Math.Max(Math.Max(Math.Abs(u) - p.lengthMm / 2, Math.Abs(vv) - (p.widthMm + d * slope)), dd);
                });
            }

            report.fillMs = sw.Elapsed.TotalMilliseconds;
            return vol;
        }

        /// <summary>Carves a region given by a signed distance (mm, negative inside) in bed coordinates
        /// (x, y from the bed centre; depth below the nominal surface). Removed voxels become cut air.</summary>
        public static void CarveSdf(Volume v, Sdf sdf)
        {
            int surfK = v.SurfaceK; float h = v.h;
            for (int k = 0; k < v.nz; k++)
                for (int j = 0; j < v.ny; j++)
                    for (int i = 0; i < v.nx; i++)
                    {
                        float x = (i + 0.5f - v.nx * 0.5f) * h, y = (j + 0.5f - v.ny * 0.5f) * h, depth = (surfK - (k + 0.5f)) * h;
                        float d = sdf(x, y, depth);
                        if (d < h)
                        {
                            int idx = v.Index(i, j, k);
                            float keep = Math.Min(1f, Math.Max(0f, 0.5f + d / h));
                            float before = v.occupancy[idx];
                            v.occupancy[idx] = Math.Min(before, keep);
                            if (v.occupancy[idx] < 0.5f && before >= 0.5f) { v.cell[idx] = Cell.CutAir; v.water[idx] = 0; }
                        }
                    }
        }

        /// <summary>Demo carves for review, same codes as the web ?cut= option (bit flags 1, 2, 4, 8).</summary>
        public static void CarveDemo(Volume v, int mode)
        {
            if ((mode & 1) != 0) CarveSdf(v, (x, y, d) => Math.Max(Math.Max(Math.Abs(x) - 15, Math.Abs(y - 12) - (2.0f + d * 0.3f)), d - 9));
            if ((mode & 2) != 0) CarveSdf(v, (x, y, d) => Math.Max((float)Math.Sqrt((x + 6) * (x + 6) + (y + 10) * (y + 10)) - (15 - d * 0.42f), d - 24));
            if ((mode & 4) != 0) CarveSdf(v, (x, y, d) => { float r = (float)Math.Sqrt((x / 1.17f) * (x / 1.17f) + y * y); return Math.Max(Math.Abs(r - 7.3f) - (1.3f + d * 0.1f), d - 14); });
            if ((mode & 8) != 0) CarveSdf(v, (x, y, d) => { float r = (float)Math.Sqrt((x / 1.17f) * (x / 1.17f) + y * y); return Math.Max(Math.Max(r - 8.2f, Math.Abs(d - 14) - 0.6f), 1.2f - (float)Math.Sqrt(x * x + y * y)); });
        }
    }
}
