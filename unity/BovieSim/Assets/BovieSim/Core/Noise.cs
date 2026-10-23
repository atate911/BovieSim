using System;

namespace BovieSim
{
    /// <summary>Deterministic RNG and noise. Bit-identical to src/core/noise.ts.</summary>
    public sealed class Mulberry32
    {
        uint a;
        public Mulberry32(uint seed) { a = seed; }
        public double Next()
        {
            unchecked
            {
                a = a + 0x6d2b79f5u;
                uint t = a;
                t = (t ^ (t >> 15)) * (t | 1u);
                t ^= t + (t ^ (t >> 7)) * (t | 61u);
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }
    }

    public static class Noise
    {
        static double Hash2(int ix, int iy, int seed)
        {
            unchecked
            {
                uint h = (uint)((long)ix * 374761393L + (long)iy * 668265263L + (long)seed * 2246822519L);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return h / 4294967296.0;
            }
        }

        static double Smooth(double t) { return t * t * (3 - 2 * t); }

        /// <summary>2D value noise in [0,1], cell size 1.</summary>
        public static double Value2(double x, double y, int seed = 0)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            double fx = Smooth(x - ix), fy = Smooth(y - iy);
            double a = Hash2(ix, iy, seed), b = Hash2(ix + 1, iy, seed), c = Hash2(ix, iy + 1, seed), d = Hash2(ix + 1, iy + 1, seed);
            return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
        }

        public static double Fbm2(double x, double y, int octaves, int seed = 0)
        {
            double sum = 0, amp = 0.5, norm = 0, f = 1;
            for (int o = 0; o < octaves; o++) { sum += amp * Value2(x * f, y * f, seed + o * 101); norm += amp; amp *= 0.5; f *= 2.03; }
            return sum / norm;
        }

        public struct Cell { public double f1, f2, cx, cy; public int id; }

        /// <summary>2D jittered-grid Voronoi: nearest / second-nearest distances and cell id.</summary>
        public static Cell Voronoi2(double x, double y, int seed = 0)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            var r = new Cell { f1 = 1e9, f2 = 1e9 };
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int gx = ix + dx, gy = iy + dy;
                    double px = gx + 0.15 + 0.7 * Hash2(gx, gy, seed);
                    double py = gy + 0.15 + 0.7 * Hash2(gx, gy, seed + 7);
                    double d = Math.Sqrt((px - x) * (px - x) + (py - y) * (py - y));
                    if (d < r.f1) { r.f2 = r.f1; r.f1 = d; r.id = (((gx & 0xffff) * 31) + ((gy & 0xffff) * 17)) & 0xff; r.cx = px; r.cy = py; }
                    else if (d < r.f2) r.f2 = d;
                }
            return r;
        }

        // ---- tileable variants (used to bake seamless material textures) -------------------------

        static double HashP(int ix, int iy, int period, int seed)
        {
            ix = ((ix % period) + period) % period; iy = ((iy % period) + period) % period;
            return Hash2(ix, iy, seed);
        }

        /// <summary>Tileable value noise with integer period (cells).</summary>
        public static double Value2P(double x, double y, int period, int seed)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            double fx = Smooth(x - ix), fy = Smooth(y - iy);
            double a = HashP(ix, iy, period, seed), b = HashP(ix + 1, iy, period, seed), c = HashP(ix, iy + 1, period, seed), d = HashP(ix + 1, iy + 1, period, seed);
            return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
        }

        /// <summary>Tileable fBm over the unit square: u, v in [0,1), base period in cells, octaves double the period.</summary>
        public static double Fbm2P(double u, double v, int period, int octaves, int seed)
        {
            double sum = 0, amp = 0.5, norm = 0; int p = period;
            for (int o = 0; o < octaves; o++) { sum += amp * Value2P(u * p, v * p, p, seed + o * 101); norm += amp; amp *= 0.5; p *= 2; }
            return sum / norm;
        }

        /// <summary>Tileable Voronoi over the unit square with `period` cells per side.</summary>
        public static Cell Voronoi2P(double u, double v, int period, int seed)
        {
            double x = u * period, y = v * period;
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            var r = new Cell { f1 = 1e9, f2 = 1e9 };
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int gx = ix + dx, gy = iy + dy;
                    double px = gx + 0.1 + 0.8 * HashP(gx, gy, period, seed);
                    double py = gy + 0.1 + 0.8 * HashP(gx, gy, period, seed + 7);
                    double d = Math.Sqrt((px - x) * (px - x) + (py - y) * (py - y));
                    if (d < r.f1) { r.f2 = r.f1; r.f1 = d; r.id = (int)(HashP(gx, gy, period, seed + 13) * 255); r.cx = px; r.cy = py; }
                    else if (d < r.f2) r.f2 = d;
                }
            return r;
        }
    }
}
