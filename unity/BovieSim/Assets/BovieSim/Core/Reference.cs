using System;

namespace BovieSim
{
    /// <summary>
    /// CPU reference implementations of the physics kernels (port of src/physics/reference.ts) plus the
    /// vessel and blood-flow rules. Used by the EditMode tests and as the readable spec of the HLSL.
    /// </summary>
    public static class Reference
    {
        public sealed class Grid3
        {
            public readonly int nx, ny, nz; public readonly float[] data;
            public Grid3(int nx, int ny, int nz, float fill = 0) { this.nx = nx; this.ny = ny; this.nz = nz; data = new float[nx * ny * nz]; if (fill != 0) for (int i = 0; i < data.Length; i++) data[i] = fill; }
            public int I(int i, int j, int k) { return i + nx * (j + ny * k); }
        }

        static readonly int[,] Nb = { { 1, 0, 0 }, { -1, 0, 0 }, { 0, 1, 0 }, { 0, -1, 0 }, { 0, 0, 1 }, { 0, 0, -1 } };

        public static double StableDt(double alpha, double h) { return h * h / (6 * alpha); }

        /// <summary>One explicit heat-equation step, adiabatic boundaries; h in metres.</summary>
        public static Grid3 DiffuseStep(Grid3 T, double alpha, double h, double dt)
        {
            var o = new Grid3(T.nx, T.ny, T.nz);
            double r = alpha * dt / (h * h);
            for (int k = 0; k < T.nz; k++) for (int j = 0; j < T.ny; j++) for (int i = 0; i < T.nx; i++)
                    {
                        float c = T.data[T.I(i, j, k)]; double lap = 0;
                        for (int n = 0; n < 6; n++)
                        {
                            int ii = i + Nb[n, 0], jj = j + Nb[n, 1], kk = k + Nb[n, 2];
                            if (ii < 0 || jj < 0 || kk < 0 || ii >= T.nx || jj >= T.ny || kk >= T.nz) continue;
                            lap += T.data[T.I(ii, jj, kk)] - c;
                        }
                        o.data[T.I(i, j, k)] = (float)(c + r * lap);
                    }
            return o;
        }

        static double Harmonic(double a, double b) { return a + b > 0 ? 2 * a * b / (a + b) : 0; }

        /// <summary>Jacobi relaxation of div(sigma grad phi) = 0 with Dirichlet voxels.</summary>
        public static Grid3 JacobiSolve(Grid3 sigma, bool[] fixedMask, float[] phiFixed, int iters)
        {
            var a = new Grid3(sigma.nx, sigma.ny, sigma.nz); var b = new Grid3(sigma.nx, sigma.ny, sigma.nz);
            for (int n = 0; n < sigma.data.Length; n++) if (fixedMask[n]) a.data[n] = phiFixed[n];
            for (int it = 0; it < iters; it++)
            {
                for (int k = 0; k < sigma.nz; k++) for (int j = 0; j < sigma.ny; j++) for (int i = 0; i < sigma.nx; i++)
                        {
                            int idx = sigma.I(i, j, k);
                            if (fixedMask[idx]) { b.data[idx] = phiFixed[idx]; continue; }
                            double s0 = sigma.data[idx];
                            if (s0 <= 0) { b.data[idx] = 0; continue; }
                            double num = 0, den = 0;
                            for (int n = 0; n < 6; n++)
                            {
                                int ii = i + Nb[n, 0], jj = j + Nb[n, 1], kk = k + Nb[n, 2];
                                if (ii < 0 || jj < 0 || kk < 0 || ii >= sigma.nx || jj >= sigma.ny || kk >= sigma.nz) continue;
                                int ni = sigma.I(ii, jj, kk);
                                double g = Harmonic(s0, sigma.data[ni]);
                                num += g * a.data[ni]; den += g;
                            }
                            b.data[idx] = den > 0 ? (float)(num / den) : 0;
                        }
                var t = a; a = b; b = t;
            }
            return a;
        }

        /// <summary>Scheme-consistent Joule power (W) at 1 V: sum over faces g (dphi)^2 h.</summary>
        public static double JoulePower(Grid3 phi, Grid3 sigma, double h)
        {
            double sum = 0;
            for (int k = 0; k < phi.nz; k++) for (int j = 0; j < phi.ny; j++) for (int i = 0; i < phi.nx; i++)
                    {
                        int idx = phi.I(i, j, k); double s0 = sigma.data[idx];
                        if (s0 <= 0) continue;
                        double q = 0;
                        for (int n = 0; n < 6; n++)
                        {
                            int ii = i + Nb[n, 0], jj = j + Nb[n, 1], kk = k + Nb[n, 2];
                            if (ii < 0 || jj < 0 || kk < 0 || ii >= phi.nx || jj >= phi.ny || kk >= phi.nz) continue;
                            int ni = phi.I(ii, jj, kk);
                            double d = phi.data[ni] - phi.data[idx];
                            q += 0.5 * Harmonic(s0, sigma.data[ni]) * d * d / (h * h);
                        }
                        sum += q * h * h * h;
                    }
            return sum;
        }

        public static double ArrheniusConst(double tempC, double t, double lnA, double Ea)
        { return t * Math.Exp(lnA - Ea / (TissueTable.R * (tempC + 273.15))); }

        public struct BoilResult { public double T, w, latentUsed; }

        /// <summary>Deposits energy e (J/m3) into a voxel with water fraction w0; mirrors the Bioheat kernel.</summary>
        public static BoilResult DepositWithBoiling(double T0, double w0, double e, double rho, double c, double L = 2.26e6, double tBoil = 100)
        {
            double rhoc = rho * c, T = T0 + e / rhoc, w = w0, used = 0;
            if (T > tBoil && w > 0)
            {
                double excess = (T - tBoil) * rhoc, eAll = w * rho * L;
                used = Math.Min(excess, eAll);
                w = w - w * (used / eAll);
                T = tBoil + (excess - used) / rhoc;
            }
            return new BoilResult { T = T, w = w, latentUsed = used };
        }

        // ---- vessels and blood (mirrors Blood.compute / Bioheat) ----------------------------------

        public static double VesselFlowMlS(double rMm) { return 0.25 * rMm * rMm * rMm; }

        /// <summary>0 none, 1 sealed, 2 char plug</summary>
        public static int SealOutcome(double omega, bool coapted, double rMm, bool exposed = true)
        {
            if (omega <= 1) return 0;
            return coapted || rMm <= 0.5 || !exposed ? 1 : 2;
        }

        public static double RebleedPerSecond(double rMm) { return Math.Min(Math.Max((rMm - 0.5) / 1.5, 0), 0.9) * 0.15; }

        /// <summary>One blood flow step on a row of columns (head-driven exchange with a wetting film).</summary>
        public static double[] FlowStep(double[] surf, double[] h, double visc = 0.35, double film = 0.05)
        {
            var o = (double[])h.Clone();
            for (int i = 0; i < h.Length; i++)
            {
                double H0 = surf[i] + h[i];
                for (int s = -1; s <= 1; s += 2)
                {
                    int j = i + s; if (j < 0 || j >= h.Length) continue;
                    double Hj = surf[j] + h[j];
                    if (H0 > Hj) o[i] -= Math.Min(visc * 0.25 * (H0 - Hj), Math.Max(h[i] - film, 0) * 0.25);
                    else if (Hj > H0) o[i] += Math.Min(visc * 0.25 * (Hj - H0), Math.Max(h[j] - film, 0) * 0.25);
                }
            }
            for (int i = 0; i < o.Length; i++) o[i] = Math.Max(o[i], 0);
            return o;
        }
    }
}
