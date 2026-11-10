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

        /// <summary>0 none, 1 sealed, 2 char plug. exposure: 0 buried, 1 skin surface, 2 dissected plane, 3 cut surface (only an open lumen can plug).</summary>
        public static int SealOutcome(double omega, bool coapted, double rMm, int exposure = 3)
        {
            if (omega <= 1) return 0;
            return coapted || rMm <= 0.5 || exposure != 3 ? 1 : 2;
        }

        /// <summary>A vessel bleeds only onto cut air (its lumen opened by cutting or tearing), not into a dissected plane or at the skin.</summary>
        public static bool VesselBleeds(int exposure) { return exposure == 3; }

        /// <summary>Spasm / thrombosis of an open vessel end with the exposure clock (0..31, about 1.5 s per step); mirrors BloodSource.</summary>
        public static double SpasmFactor(double rMm, bool arterial, int clock)
        {
            if (arterial)
            {
                if (rMm <= 0.35) return clock >= 20 ? 0 : Math.Exp(-clock / 6.0);
                if (rMm <= 0.5) return Math.Max(0.12, Math.Exp(-clock / 6.0));
                return 1 - 0.35 * Math.Min(1, clock / 16.0);
            }
            if (rMm <= 0.5) return clock >= 24 ? 0 : Math.Exp(-clock / 10.0);
            return 1 - 0.2 * Math.Min(1, clock / 16.0);
        }

        /// <summary>Capillary ooze (microlitres per second) from one exposed voxel face of a raw surface.</summary>
        public static double OozeUlPerS(Tissue t, double hMm, int clock, int exposure)
        {
            return TissueTable.Props[(int)t].oozeRate * hMm * hMm * Math.Exp(-clock / 7.0) * (exposure == 2 ? 0.3 : 1);
        }

        /// <summary>Suction mirror: liquid leaves, the clot mass stays and the fraction is re-derived. Returns the new depth; clot fraction by reference.</summary>
        public static double Suck(double h, ref double clot, double dh)
        {
            double clotMass = h * clot;
            double taken = Math.Min(Math.Max(h - clotMass, 0), dh);
            double h2 = Math.Max(h - taken, clotMass);
            clot = h2 > 1e-5 ? Math.Min(1, clotMass / h2) : 0;
            return h2;
        }

        /// <summary>Fresh blood arriving in a column dilutes the clot fraction (clot mass conserved). Returns the new depth.</summary>
        public static double AddBlood(double h, ref double clot, double dh)
        {
            double h2 = h + dh;
            clot = h2 > 1e-6 ? clot * h / h2 : 0;
            return h2;
        }

        /// <summary>Depth that can flow: above the wetting film and not clotted.</summary>
        public static double MobileDepth(double h, double clot, double film = 0.05) { return Math.Max(Math.Min(h - film, h * (1 - clot)), 0); }

        /// <summary>Blunt dissection rule: separable tissue within reach of the spreading (or pushing) tips.</summary>
        public static bool Dissects(Tissue t, bool spread)
        {
            return !TissueTable.IsBridging(t) && TissueTable.Props[(int)t].cohesion <= (spread ? TissueTable.DissectSpreadCohesion : TissueTable.DissectPushCohesion);
        }

        public static double RebleedPerSecond(double rMm) { return Math.Min(Math.Max((rMm - 0.5) / 1.5, 0), 0.9) * 0.15; }

        /// <summary>Thin-film mobility: a deep pool runs, a film creeps, clotted blood stays (mirrors FlowRate in Sim.compute).</summary>
        public static double FlowRate(double h, double clot, double visc = 0.35, double film = 0.05)
        {
            double mobile = Math.Max(h - film, 0);
            return visc * Math.Min(Math.Max(mobile / 0.25, 0.15), 1) * (1 - clot);
        }

        /// <summary>One blood flow step on a row of columns (head-driven exchange with a wetting film and thin-film mobility).</summary>
        public static double[] FlowStep(double[] surf, double[] h, double[] clot = null, double visc = 0.35, double film = 0.05)
        {
            var o = (double[])h.Clone();
            if (clot == null) clot = new double[h.Length];
            for (int i = 0; i < h.Length; i++)
            {
                double H0 = surf[i] + h[i];
                for (int s = -1; s <= 1; s += 2)
                {
                    int j = i + s; if (j < 0 || j >= h.Length) continue;
                    double Hj = surf[j] + h[j];
                    if (H0 > Hj) o[i] -= Math.Min(FlowRate(h[i], clot[i], visc, film) * 0.25 * (H0 - Hj), MobileDepth(h[i], clot[i], film) * 0.25);
                    else if (Hj > H0) o[i] += Math.Min(FlowRate(h[j], clot[j], visc, film) * 0.25 * (Hj - H0), MobileDepth(h[j], clot[j], film) * 0.25);
                }
            }
            for (int i = 0; i < o.Length; i++) o[i] = Math.Max(o[i], 0);
            return o;
        }
    }
}
