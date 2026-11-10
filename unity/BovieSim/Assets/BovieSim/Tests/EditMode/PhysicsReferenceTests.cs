using System;
using NUnit.Framework;
using UnityEngine;

namespace BovieSim.Tests
{
    /// <summary>Ports of the web client's vitest suite (tests/*.test.ts): the same analytic checks on the C# reference kernels.</summary>
    public class DiffusionTests
    {
        [Test]
        public void GaussianHotSpotSpreadsLikeTheAnalyticSolution()
        {
            double alpha = 9.8e-8, h = 0.25e-3, s0 = 1.0e-3;
            int n = 41, c = 20;
            var T = new Reference.Grid3(n, n, n);
            for (int k = 0; k < n; k++) for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
                    {
                        double r2 = ((i - c) * (i - c) + (j - c) * (j - c) + (k - c) * (k - c)) * h * h;
                        T.data[T.I(i, j, k)] = (float)Math.Exp(-r2 / (2 * s0 * s0));
                    }
            double dt = Reference.StableDt(alpha, h) * 0.5; int steps = 40;
            for (int s = 0; s < steps; s++) T = Reference.DiffuseStep(T, alpha, h, dt);
            double t = steps * dt, s2 = s0 * s0 + 2 * alpha * t;
            double expectedPeak = Math.Pow(s0 * s0 / s2, 1.5);
            Assert.Less(Math.Abs(T.data[T.I(c, c, c)] - expectedPeak) / expectedPeak, 0.02);
            double rr = 4 * h, expectedOff = expectedPeak * Math.Exp(-rr * rr / (2 * s2));
            Assert.Less(Math.Abs(T.data[T.I(c + 4, c, c)] - expectedOff) / expectedOff, 0.03);
        }

        [Test]
        public void ConservesEnergyWithAdiabaticBoundaries()
        {
            var T = new Reference.Grid3(12, 12, 12, 37);
            T.data[T.I(6, 6, 6)] = 300;
            double sum0 = 0; foreach (var v in T.data) sum0 += v;
            for (int s = 0; s < 30; s++) T = Reference.DiffuseStep(T, 1e-7, 0.25e-3, Reference.StableDt(1e-7, 0.25e-3) * 0.9);
            double sum1 = 0; foreach (var v in T.data) sum1 += v;
            Assert.Less(Math.Abs(sum1 - sum0) / sum0, 1e-5);
        }
    }

    public class FieldSolverTests
    {
        static double Run(float sigma, int n, double radiusVox, int iters, double h, out Reference.Grid3 phi, out Reference.Grid3 sg)
        {
            int c = n / 2;
            sg = new Reference.Grid3(n, n, n, sigma);
            var fixedMask = new bool[n * n * n]; var phiF = new float[n * n * n];
            for (int k = 0; k < n; k++) for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
                    {
                        int idx = sg.I(i, j, k);
                        double r = Math.Sqrt((i - c) * (i - c) + (j - c) * (j - c) + (k - c) * (k - c));
                        if (r <= radiusVox) { fixedMask[idx] = true; phiF[idx] = 1; }
                        if (i == 0 || j == 0 || k == 0 || i == n - 1 || j == n - 1 || k == n - 1) { fixedMask[idx] = true; phiF[idx] = 0; }
                    }
            phi = Reference.JacobiSolve(sg, fixedMask, phiF, iters);
            return Reference.JoulePower(phi, sg, h);
        }

        [Test]
        public void SphericalElectrodeGivesInverseRPotentialAndConsistentPower()
        {
            int n = 41, c = 20; double h = 0.25e-3; float sigma = 0.3f;
            Reference.Grid3 phi, sg;
            double P = Run(sigma, n, 2.0, 900, h, out phi, out sg);
            double a = 2.0 * h, b = c * h;
            Func<double, double> model = r => (1 / r - 1 / b) / (1 / a - 1 / b);
            foreach (int dr in new[] { 4, 7, 10 })
            {
                double m = model(dr * h);
                Assert.Less(Math.Abs(phi.data[sg.I(c + dr, c, c)] - m) / m, 0.15, "phi at r = " + dr + " voxels");
            }
            double rAnalytic = (1 / a - 1 / b) / (4 * Math.PI * sigma);
            Assert.Less(Math.Abs(1 / P - rAnalytic) / rAnalytic, 0.5);
            // current through a closed cube around the electrode equals the Joule power at 1 V
            int s = 8; double I = 0;
            for (int u = -s; u <= s; u++) for (int v = -s; v <= s; v++)
                {
                    int[,] f = {
                        { c + s, c + u, c + v, c + s + 1, c + u, c + v }, { c - s, c + u, c + v, c - s - 1, c + u, c + v },
                        { c + u, c + s, c + v, c + u, c + s + 1, c + v }, { c + u, c - s, c + v, c + u, c - s - 1, c + v },
                        { c + u, c + v, c + s, c + u, c + v, c + s + 1 }, { c + u, c + v, c - s, c + u, c + v, c - s - 1 } };
                    for (int q = 0; q < 6; q++)
                        I += sigma * (phi.data[sg.I(f[q, 0], f[q, 1], f[q, 2])] - phi.data[sg.I(f[q, 3], f[q, 4], f[q, 5])]) / h * h * h;
                }
            Assert.Less(Math.Abs(I - P) / P, 0.08);
        }

        [Test]
        public void FatCarriesFarLessCurrentThanDermis()
        {
            Reference.Grid3 phi, sg;
            double pDermis = Run(0.3f, 25, 1.5, 400, 0.25e-3, out phi, out sg);
            double pFat = Run(0.04f, 25, 1.5, 400, 0.25e-3, out phi, out sg);
            Assert.Greater(pDermis / pFat, 6); Assert.Less(pDermis / pFat, 9);
        }
    }

    public class DamageTests
    {
        static TissueProps Skin { get { return TissueTable.Props[(int)Tissue.Dermis]; } }

        [Test]
        public void IntegratedDamageMatchesClosedFormAtConstantTemperature()
        {
            double om = 0; for (int i = 0; i < 1000; i++) om += Reference.ArrheniusConst(60, 0.001, Skin.lnA, Skin.Ea);
            Assert.Less(Math.Abs(om - Reference.ArrheniusConst(60, 1.0, Skin.lnA, Skin.Ea)) / om, 1e-4);
        }

        [Test]
        public void SkinCoagulatesInSecondsAt60CButNotInAnHourAt43C()
        {
            Assert.Greater(Reference.ArrheniusConst(60, 1.5, Skin.lnA, Skin.Ea), 1);
            Assert.Less(Reference.ArrheniusConst(43, 3600, Skin.lnA, Skin.Ea), 1);
        }

        [Test]
        public void DamageRateIsSteep()
        {
            double r = Reference.ArrheniusConst(70, 1, Skin.lnA, Skin.Ea) / Reference.ArrheniusConst(55, 1, Skin.lnA, Skin.Ea);
            Assert.Greater(r, 1e4); Assert.Less(r, 5e4);
        }

        [Test]
        public void BoilingHoldsAt100CAndConservesEnergy()
        {
            double rho = 1109, c = 3391, w0 = 0.65, e = 5.0e8;
            var r = Reference.DepositWithBoiling(37, w0, e, rho, c);
            Assert.AreEqual(100, r.T, 1e-5);
            Assert.Less(Math.Abs((100 - 37) * rho * c + r.latentUsed - e) / e, 1e-6);
            Assert.Less(r.w, w0); Assert.Greater(r.w, 0);
        }

        [Test]
        public void RisesAbove100COnlyOnceDry()
        {
            double rho = 1109, c = 3391, w0 = 0.65;
            double eAll = (100 - 37) * rho * c + w0 * rho * 2.26e6;
            var r = Reference.DepositWithBoiling(37, w0, eAll * 1.2, rho, c);
            Assert.AreEqual(0, r.w, 1e-6); Assert.Greater(r.T, 100);
        }
    }

    public class GeneratorTests
    {
        [Test]
        public void ConstantPowerBand()
        {
            var m = FT10.Mode(ModeId.Pure);
            foreach (var z in new[] { m.zLo, 300f, 800f, m.zHi }) Assert.AreEqual(30, FT10.Output(m, 30, z).power, 1e-4);
        }

        [Test]
        public void CurrentLimitedIntoLowImpedance()
        {
            var m = FT10.Mode(ModeId.Pure);
            var o = FT10.Output(m, 30, 50);
            Assert.Less(o.power, 30); Assert.AreEqual(30 * 50 / m.zLo, o.power, 1e-4);
        }

        [Test]
        public void VoltageLimitedIntoHighImpedance()
        {
            var m = FT10.Mode(ModeId.Pure);
            var o = FT10.Output(m, 30, 8000);
            Assert.Less(o.power, 30); Assert.LessOrEqual(o.vRms, m.vPeak / m.crest + 1e-3);
        }

        [Test]
        public void CoagBridgesLargerGapsThanCut()
        {
            Assert.Greater(FT10.Mode(ModeId.Fulgurate).ArcReachMm, FT10.Mode(ModeId.Pure).ArcReachMm * 5);
            Assert.Greater(FT10.Mode(ModeId.Spray).ArcReachMm, FT10.Mode(ModeId.Fulgurate).ArcReachMm);
        }

        [Test]
        public void NeverExceedsModeMaximum()
        {
            Assert.LessOrEqual(FT10.Output(FT10.Mode(ModeId.Fulgurate), 300, 500).power, 120);
            var g = new FT10(); g.coagMode = ModeId.Spray; g.coagWatts = 500; g.Adjust(false, 0);
            Assert.AreEqual(120, g.coagWatts);
        }
    }

    public class BleedTests
    {
        [Test]
        public void FlowScalesSteeplyWithRadius()
        {
            Assert.AreEqual(37, Reference.VesselFlowMlS(1.0) / Reference.VesselFlowMlS(0.3), 0.5);
            Assert.Greater(Reference.VesselFlowMlS(1.0), 0.1); Assert.Less(Reference.VesselFlowMlS(1.0), 0.6);
        }

        [Test]
        public void CoaptiveBuzzSealsDirectCoagOnlySealsSmallVessels()
        {
            Assert.AreEqual(1, Reference.SealOutcome(1.5, true, 1.5));
            Assert.AreEqual(1, Reference.SealOutcome(1.5, false, 0.4));
            Assert.AreEqual(2, Reference.SealOutcome(1.5, false, 1.0));
            Assert.AreEqual(0, Reference.SealOutcome(0.8, true, 1.0));
            // an intact vessel exposed in a dissected plane coagulates to a seal, not a plug: its lumen was never open
            Assert.AreEqual(1, Reference.SealOutcome(1.5, false, 1.0, 2));
        }

        [Test]
        public void VesselBleedsOnlyOntoCutAir()
        {
            Assert.IsTrue(Reference.VesselBleeds(3));
            Assert.IsFalse(Reference.VesselBleeds(2)); Assert.IsFalse(Reference.VesselBleeds(1)); Assert.IsFalse(Reference.VesselBleeds(0));
        }

        [Test]
        public void SmallVesselsSpasmAndStopLargeArteriesKeepBleeding()
        {
            Assert.AreEqual(1, Reference.SpasmFactor(0.3, true, 0), 1e-9);
            Assert.Less(Reference.SpasmFactor(0.3, true, 8), 0.3);
            Assert.AreEqual(0, Reference.SpasmFactor(0.3, true, 20), 1e-9);
            Assert.AreEqual(0.12, Reference.SpasmFactor(0.5, true, 31), 1e-3);
            Assert.Greater(Reference.SpasmFactor(1.0, true, 31), 0.6);
            Assert.AreEqual(0, Reference.SpasmFactor(0.4, false, 31), 1e-9);
            Assert.AreEqual(0.8, Reference.SpasmFactor(1.0, false, 31), 1e-3);
            Assert.AreEqual(Math.Exp(-1), Reference.SpasmFactor(0.3, true, 6), 1e-6);
        }

        [Test]
        public void RawSurfacesOozeByTissueAndSettleWithinAMinute()
        {
            Assert.Greater(Reference.OozeUlPerS(Tissue.Muscle, 0.25, 0, 3), Reference.OozeUlPerS(Tissue.Fat, 0.25, 0, 3) * 10);
            Assert.Greater(Reference.OozeUlPerS(Tissue.Viscera, 0.25, 0, 3), Reference.OozeUlPerS(Tissue.Dermis, 0.25, 0, 3) * 1.5);
            Assert.AreEqual(0, Reference.OozeUlPerS(Tissue.Epidermis, 0.25, 0, 3), 1e-12);
            Assert.AreEqual(0, Reference.OozeUlPerS(Tissue.Artery, 0.25, 0, 3), 1e-12);
            Assert.AreEqual(0.3 * Reference.OozeUlPerS(Tissue.Fat, 0.25, 0, 3), Reference.OozeUlPerS(Tissue.Fat, 0.25, 0, 2), 1e-9);
            // a 30 mm dermal incision: two walls 2 mm deep at 0.25 mm voxels, the clock stepping about every 2 s
            double faces = 2 * (30 / 0.25) * (2 / 0.25), total = 0;
            for (int clock = 0; clock < 31; clock++) total += faces * Reference.OozeUlPerS(Tissue.Dermis, 0.25, clock, 3) * 2.0;
            Assert.Greater(total / 1000, 0.05); Assert.Less(total / 1000, 0.5);
            Assert.Less(Reference.OozeUlPerS(Tissue.Dermis, 0.25, 30, 3) / Reference.OozeUlPerS(Tissue.Dermis, 0.25, 0, 3), 0.02);
        }

        [Test]
        public void LargerCharPlugsRebleedMore()
        {
            Assert.AreEqual(0, Reference.RebleedPerSecond(0.5));
            Assert.Greater(Reference.RebleedPerSecond(1.0), 0);
            Assert.Greater(Reference.RebleedPerSecond(2.0), Reference.RebleedPerSecond(1.0));
        }

        [Test]
        public void BloodConservesVolumeAndRunsIntoTheTrench()
        {
            var surf = new double[] { 0, 0, -4, -8, -8, -4, 0, 0 };
            var h = new double[] { 1, 0, 0, 0, 0, 0, 0, 0 };
            for (int s = 0; s < 400; s++) h = Reference.FlowStep(surf, h);
            double tot = 0; foreach (var v in h) tot += v;
            Assert.AreEqual(1.0, tot, 1e-6);
            Assert.Greater(h[3] + h[4], 0.6);
            Assert.Less(h[0], 0.06);
        }

        [Test]
        public void BloodNeverFlowsUphill()
        {
            var surf = new double[] { 0, 2, 4 }; var h = new double[] { 0.5, 0, 0 };
            for (int s = 0; s < 50; s++) h = Reference.FlowStep(surf, h);
            Assert.AreEqual(0, h[2]); Assert.AreEqual(0, h[1]);
        }

        static double Spread(double h0, double clot0)
        {
            var surf = new double[5]; var h = new double[] { h0, 0, 0, 0, 0 }; var c = new double[] { clot0, 0, 0, 0, 0 };
            for (int s = 0; s < 10; s++) h = Reference.FlowStep(surf, h, c);
            return (h0 - h[0]) / h0;
        }

        [Test]
        public void DeepPoolsSpreadFasterThanFilmsAndClotDoesNotMove()
        {
            Assert.Greater(Spread(1.0, 0), Spread(0.12, 0) * 2);
            Assert.AreEqual(0, Spread(0.04, 0), 1e-12);   // below the wetting film nothing flows
            Assert.AreEqual(0, Spread(1.0, 1.0), 1e-12);  // a firm clot
            Assert.Less(Spread(1.0, 0.5), Spread(1.0, 0));
        }

        [Test]
        public void BloodClotsInUnderASecondAt70CAndNotWithinAMinuteAt45C()
        {
            var b = TissueTable.Props[(int)Tissue.Blood];
            Assert.Greater(Reference.ArrheniusConst(70, 1.0, b.lnA, b.Ea), 1);
            Assert.Less(Reference.ArrheniusConst(45, 60, b.lnA, b.Ea), 0.01);
            Assert.Less(Reference.ArrheniusConst(55, 60, b.lnA, b.Ea), 0.3);
        }

        [Test]
        public void SuctionLiftsTheLiquidAndLeavesTheClot()
        {
            double clot = 0.4; double h = Reference.Suck(1.0, ref clot, 10);
            Assert.AreEqual(0.4, h, 1e-6); Assert.AreEqual(1.0, clot, 1e-6);
            clot = 0; h = Reference.Suck(1.0, ref clot, 0.3);
            Assert.AreEqual(0.7, h, 1e-6); Assert.AreEqual(0, clot, 1e-9);
            clot = 1.0; Assert.AreEqual(0.5, Reference.Suck(0.5, ref clot, 10), 1e-6);
        }

        [Test]
        public void ClotIsAMassFreshBloodDilutesItAndSuctionCannotRatchetIt()
        {
            double h = 0.5, clot = 1e-4;
            for (int s = 0; s < 200; s++) { h = Reference.Suck(h, ref clot, 0.2); h = Reference.AddBlood(h, ref clot, 0.2); }
            Assert.AreEqual(0.5 * 1e-4, h * clot, 1e-9);
            Assert.Less(clot, 1e-3);
            double c2 = 0.25; double h2 = Reference.Suck(2.0, ref c2, 10);
            Assert.AreEqual(0.5, h2, 1e-6); Assert.AreEqual(1, c2, 1e-6);
            Reference.AddBlood(h2, ref c2, 1.5);
            Assert.AreEqual(0.25, c2, 1e-6);
            var flowed = Reference.FlowStep(new double[] { 0, -10 }, new double[] { 1.0, 0 }, new double[] { 1.0, 0 });
            Assert.AreEqual(1.0, flowed[0], 1e-9);
        }
    }

    public class DissectionTests
    {
        [Test]
        public void SpreadingSeparatesLoosePlanesNeverDermisFasciaMuscleOrVessels()
        {
            foreach (var t in new[] { Tissue.Areolar, Tissue.Fat, Tissue.LipomaCapsule, Tissue.Septum }) Assert.IsTrue(Reference.Dissects(t, true), t.ToString());
            foreach (var t in new[] { Tissue.Lipoma, Tissue.Dermis, Tissue.Epidermis, Tissue.DeepFascia, Tissue.SuperficialFascia, Tissue.Muscle, Tissue.Artery, Tissue.Vein, Tissue.Nerve, Tissue.Viscera, Tissue.Serosa })
                Assert.IsFalse(Reference.Dissects(t, true), t.ToString());
        }

        [Test]
        public void PushingFollowsOnlyTheLoosestPlanes()
        {
            Assert.IsTrue(Reference.Dissects(Tissue.Areolar, false)); Assert.IsTrue(Reference.Dissects(Tissue.Fat, false)); Assert.IsTrue(Reference.Dissects(Tissue.LipomaCapsule, false));
            Assert.IsFalse(Reference.Dissects(Tissue.Septum, false)); Assert.IsFalse(Reference.Dissects(Tissue.Lipoma, false));
        }

        [Test]
        public void MechanicalOrderingsMatchTraction()
        {
            Func<Tissue, float> T = t => TissueTable.Props[(int)t].tearStrain;
            Assert.Less(T(Tissue.DeepFascia), T(Tissue.Fat)); Assert.Less(T(Tissue.Fat), T(Tissue.Dermis)); Assert.LessOrEqual(T(Tissue.Dermis), T(Tissue.Artery));
            Assert.Less(T(Tissue.Viscera), T(Tissue.Muscle));
            Assert.Greater(TissueTable.Props[(int)Tissue.Areolar].tearStrain / TissueTable.Props[(int)Tissue.Areolar].yieldStrain, 2.5f);
            Func<Tissue, float> C = t => TissueTable.Props[(int)t].cohesion;
            Assert.Less(C(Tissue.Areolar), C(Tissue.LipomaCapsule)); Assert.Less(C(Tissue.LipomaCapsule), C(Tissue.Fat)); Assert.Less(C(Tissue.Fat), C(Tissue.Septum)); Assert.Less(C(Tissue.Septum), C(Tissue.DeepFascia));
            Assert.AreEqual(1f, C(Tissue.Dermis), 1e-6f);
        }
    }

    public class LatticeTests
    {
        /// <summary>2 mm dermis over fat over muscle, optional lipoma ball (same as tests/lattice.test.ts).</summary>
        static Volume Bed(float sizeMm = 30, float depthMm = 18, float h = 1, bool lipoma = false)
        {
            int air = 2, n = Mathf.RoundToInt(sizeMm / h), nz = Mathf.RoundToInt(depthMm / h) + air;
            var v = new Volume(n, n, nz, h, air);
            for (int k = 0; k < nz; k++) for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
                    {
                        int idx = v.Index(i, j, k);
                        float depth = (v.SurfaceK - (k + 0.5f)) * h;
                        if (depth < 0) { v.occupancy[idx] = 0; v.cell[idx] = 0; continue; }
                        v.occupancy[idx] = 1;
                        var ty = depth < 2 ? Tissue.Dermis : depth < 14 ? Tissue.Fat : Tissue.Muscle;
                        if (lipoma)
                        {
                            float x = (i + 0.5f - n / 2f) * h, y = (j + 0.5f - n / 2f) * h;
                            float r = Mathf.Sqrt(x * x + y * y + (depth - 7) * (depth - 7));
                            if (r < 4) ty = Tissue.Lipoma; else if (r < 4.8f) ty = Tissue.LipomaCapsule;
                        }
                        v.cell[idx] = Cell.Pack(ty);
                    }
            return v;
        }

        [Test]
        public void SettlesUnderPreTensionWithoutDrift()
        {
            var lat = new Lattice(Bed(), 3);
            for (int s = 0; s < 60; s++) lat.Step(1 / 60f, s / 60f);
            float maxD = 0; for (int n = 0; n < lat.count; n++) maxD = Mathf.Max(maxD, ((Vector3)lat.disp[n]).magnitude);
            Assert.Less(maxD, 1.0f);
        }

        [Test]
        public void SeveredSkinLineGapes()
        {
            var lat = new Lattice(Bed(), 3, 0, 0.94f, 0.97f);
            for (int s = 0; s < 30; s++) lat.Step(1 / 60f, s / 60f);
            var sev = new byte[lat.constraints.Count]; var om = new float[lat.constraints.Count];
            for (int i = 0; i < lat.constraints.Count; i++)
            {
                var c = lat.constraints[i];
                float xa = lat.mat[c.a * 3] - 1.5f, xb = lat.mat[c.b * 3] - 1.5f, ya = lat.mat[c.a * 3 + 1], yb = lat.mat[c.b * 3 + 1];
                if (xa * xb < 0 && Mathf.Max(ya, yb) > -4.5f) sev[i] = 1;
            }
            lat.ApplyProbe(sev, om);
            for (int s = 0; s < 180; s++) lat.Step(1 / 60f, s / 60f);
            float gap = lat.DistanceBetween(Vector3.zero, new Vector3(3, 0, 0));
            Assert.Greater(gap, 3.6f); Assert.Less(gap, 7.0f);
        }

        [Test]
        public void LipomaFreesOnceMostCapsuleLinksAreCut()
        {
            var lat = new Lattice(Bed(30, 18, 1, true), 3);
            Assert.Greater(lat.capsuleTotal, 10);
            var sev = new byte[lat.constraints.Count]; var om = new float[lat.constraints.Count];
            int cut = 0;
            for (int i = 0; i < lat.constraints.Count; i++) if (lat.constraints[i].kind == 1 && cut < lat.capsuleTotal * 0.3f) { sev[i] = 1; cut++; }
            lat.ApplyProbe(sev, om);
            Assert.IsFalse(lat.lipomaFree);
            for (int i = 0; i < lat.constraints.Count; i++) if (lat.constraints[i].kind == 1 && lat.constraints[i].alive && cut < lat.capsuleTotal * 0.5f) { sev[i] = 1; cut++; }
            lat.ApplyProbe(sev, om);
            Assert.IsTrue(lat.lipomaFree);
        }

        static Volume UniformBed(Tissue ty, float sizeMm = 30, float depthMm = 18, float h = 1)
        {
            int air = 2, n = Mathf.RoundToInt(sizeMm / h), nz = Mathf.RoundToInt(depthMm / h) + air;
            var v = new Volume(n, n, nz, h, air);
            for (int k = 0; k < nz; k++) for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
                    {
                        int idx = v.Index(i, j, k);
                        float depth = (v.SurfaceK - (k + 0.5f)) * h;
                        if (depth < 0) { v.occupancy[idx] = 0; v.cell[idx] = 0; continue; }
                        v.occupancy[idx] = 1; v.cell[idx] = Cell.Pack(ty);
                    }
            return v;
        }

        /// <summary>Hooks the surface node nearest the bed centre and lifts it by liftMm over `seconds`.</summary>
        static int Pull(Lattice lat, float liftMm, float seconds = 2)
        {
            int node = lat.NearestNode(Vector3.zero, 3);
            Assert.GreaterOrEqual(node, 0);
            var start = lat.Mat(node);
            int steps = Mathf.RoundToInt(seconds * 60);
            for (int s = 0; s < steps; s++)
            {
                float f = (s + 1) / (float)steps;
                lat.attachments.Clear();
                lat.attachments.Add(new Attachment { node = node, target = new Vector3(start.x, start.y + liftMm * f, start.z), strength = 0.8f });
                lat.Step(1 / 60f, s / 60f);
            }
            return node;
        }

        static Constraint By(Lattice lat, Tissue t) { foreach (var c in lat.constraints) if (c.tissue == t && c.kind == 0) return c; return null; }

        [Test]
        public void RestingPreTensionNeitherYieldsNorTears()
        {
            var lat = new Lattice(Bed(), 3);
            for (int s = 0; s < 60; s++) lat.Step(1 / 60f, s / 60f);
            Assert.AreEqual(0, lat.yieldedLinks); Assert.AreEqual(0, lat.tornLinks);
        }

        [Test]
        public void CoagulationShrinksEachTissueByItsOwnCollagenFraction()
        {
            var lat = new Lattice(Bed(), 3);
            var sev = new byte[lat.constraints.Count]; var om = new float[lat.constraints.Count];
            for (int i = 0; i < om.Length; i++) om[i] = 5;
            lat.ApplyProbe(sev, om);
            Assert.AreEqual(0.85f, By(lat, Tissue.Dermis).rest / By(lat, Tissue.Dermis).rest0, 0.005f);
            Assert.AreEqual(0.90f, By(lat, Tissue.Muscle).rest / By(lat, Tissue.Muscle).rest0, 0.005f);
            Assert.AreEqual(0.96f, By(lat, Tissue.Fat).rest / By(lat, Tissue.Fat).rest0, 0.005f);
            for (int i = 0; i < om.Length; i++) om[i] = 0.9f;
            lat.ApplyProbe(sev, om);
            float r = By(lat, Tissue.Dermis).rest / By(lat, Tissue.Dermis).rest0;
            Assert.Greater(r, 0.85f); Assert.Less(r, 1.0f);
        }

        [Test]
        public void CollagenStiffensWithStrainFatAndAreolarBarely()
        {
            Func<Tissue, float> ratio = t => Lattice.EffectiveCompliance(t, 0) / Lattice.EffectiveCompliance(t, 0.3f);
            Assert.Greater(ratio(Tissue.DeepFascia), 30f);
            Assert.Greater(ratio(Tissue.Dermis), 1.5f);
            Assert.Less(ratio(Tissue.Dermis), ratio(Tissue.LipomaCapsule));
            Assert.Less(ratio(Tissue.Fat), 1.5f);
            Assert.Less(ratio(Tissue.Areolar), 1.2f);
            Assert.Less(Lattice.EffectiveCompliance(Tissue.DeepFascia, 0), Lattice.EffectiveCompliance(Tissue.Fat, 0) / 20);
            Assert.Less(Lattice.EffectiveCompliance(Tissue.Fat, 0), Lattice.EffectiveCompliance(Tissue.Areolar, 0));
        }

        [Test]
        public void LiftingSixMillimetresTearsFatNotDermisAndAreolarStringsOut()
        {
            var fat = new Lattice(UniformBed(Tissue.Fat), 3);
            Pull(fat, 6);
            Assert.Greater(fat.tornLinks, 0);
            var tears = fat.TakeTears();
            Assert.Greater(tears.Count, 0);
            Assert.AreEqual(Tissue.Fat, tears[0].tissue);
            Assert.AreEqual(0, fat.TakeTears().Count);

            var skin = new Lattice(UniformBed(Tissue.Dermis), 3);
            Pull(skin, 6);
            Assert.AreEqual(0, skin.tornLinks);

            var are = new Lattice(UniformBed(Tissue.Areolar), 3);
            Pull(are, 4);
            Assert.AreEqual(0, are.tornLinks);
            Assert.Greater(are.yieldedLinks, 0);
            var a = new Vector4[64]; var b = new Vector4[64];
            Assert.Greater(are.StrandData(a, b), 0);
            Constraint strand = null; foreach (var c in are.constraints) if (c.alive && c.plastic > 1.05f) { strand = c; break; }
            Assert.IsNotNull(strand);
            Assert.Greater(strand.rest, strand.rest0 * 1.05f);
        }

        [Test]
        public void DriedAndCharredTissueIsBrittle()
        {
            var skin = new Lattice(UniformBed(Tissue.Dermis), 3);
            var flags = new byte[skin.constraints.Count]; var om = new float[skin.constraints.Count]; var dry = new float[skin.constraints.Count];
            for (int i = 0; i < dry.Length; i++) dry[i] = 1;
            skin.ApplyProbe(flags, om, dry);
            Pull(skin, 6);
            Assert.Greater(skin.tornLinks, 0);
            Assert.AreEqual(Tissue.Dermis, skin.TakeTears()[0].tissue);
            float tearMul, stiffMul, shrink;
            Lattice.ThermalMultipliers(Tissue.Dermis, 0, 1, false, 0.5f, out tearMul, out stiffMul, out shrink);
            Assert.AreEqual(0.35f, tearMul, 0.01f); Assert.AreEqual(0.5f, stiffMul, 0.01f);
            Lattice.ThermalMultipliers(Tissue.Dermis, 0, 0, true, 0.5f, out tearMul, out stiffMul, out shrink);
            Assert.AreEqual(0.25f, tearMul, 0.01f);
            Lattice.ThermalMultipliers(Tissue.Dermis, 2, 0, false, 0.5f, out tearMul, out stiffMul, out shrink);
            Assert.AreEqual(1 / 2.5f, stiffMul, 0.002f);
            Lattice.ThermalMultipliers(Tissue.Fat, 2, 0, false, 0.5f, out tearMul, out stiffMul, out shrink);
            Assert.Greater(stiffMul, 1f);
        }

        [Test]
        public void MuscleIsTougherAndStifferAlongItsFibres()
        {
            float tA, sA, shA, tX, sX, shX;
            Lattice.ThermalMultipliers(Tissue.Muscle, 0, 0, false, 1, out tA, out sA, out shA);
            Lattice.ThermalMultipliers(Tissue.Muscle, 0, 0, false, 0, out tX, out sX, out shX);
            Assert.Greater(tA, tX * 1.4f); Assert.Less(sA, sX * 0.5f);
        }

        [Test]
        public void VesselLinksArePreTensioned()
        {
            var v = UniformBed(Tissue.Fat);
            int n = v.nx;
            for (int k = 0; k < v.nz; k++) for (int j = n / 2 - 1; j <= n / 2; j++) for (int i = n / 2 - 1; i <= n / 2; i++)
                    { int idx = v.Index(i, j, k); if (v.occupancy[idx] >= 0.5f) v.cell[idx] = Cell.Pack(Tissue.Artery, TissueState.Normal, 20); }
            var lat = new Lattice(v, 3);
            // the softer end governs, so a fat-vessel link is "fat", but any link touching a vessel node is pre-tensioned
            int touching = 0;
            foreach (var c in lat.constraints)
            {
                if ((Tissue)lat.layer[c.a] != Tissue.Artery && (Tissue)lat.layer[c.b] != Tissue.Artery) continue;
                touching++;
                Assert.AreEqual(Lattice.VesselPretension, c.rest0 / (lat.Mat(c.b) - lat.Mat(c.a)).magnitude, 0.01f);
            }
            Assert.Greater(touching, 0);
        }
    }

    public class ScenarioTests
    {
        [TestCase("freeplay")] [TestCase("hemostasis")] [TestCase("lipoma")] [TestCase("viscera")]
        public void ScenarioParsesAndHasEducationBlock(string id)
        {
            var ta = Resources.Load<TextAsset>("BovieSim/Scenarios/" + id);
            Assert.IsNotNull(ta, "scenario JSON missing from Resources (run npm run sync:unity)");
            var sc = Scenario.FromJson(ta.text);
            Assert.AreEqual(id, sc.id);
            Assert.IsFalse(string.IsNullOrEmpty(sc.education.goal), "every scenario needs a goal");
            Assert.Greater(sc.education.success.Count, 0, "every scenario needs a definition of success");
        }

        [Test]
        public void AnatomyIsDeterministicForASeed()
        {
            var sc = Scenario.FromJson(Resources.Load<TextAsset>("BovieSim/Scenarios/freeplay").text);
            AnatomyReport r1, r2;
            var a = Anatomy.Build(sc, out r1, 1.0f); var b = Anatomy.Build(sc, out r2, 1.0f);
            Assert.AreEqual(a.count, b.count);
            for (int i = 0; i < a.count; i++) if (a.cell[i] != b.cell[i]) Assert.Fail("cell " + i + " differs");
        }

        [Test]
        public void TeachingTableCoversCoreEvents()
        {
            var bus = new EventBus(Resources.Load<TextAsset>("BovieSim/Scenarios/teaching").text);
            foreach (var t in new[] { "tissue.carbonized", "bleed.charPlug", "forceps.buzzed", "blood.poolConducts", "plane.dissected", "tissue.torn", "gauze.wipe", "blood.boiling", "viscera.heated" })
                Assert.IsFalse(string.IsNullOrEmpty(bus.Teaching(t)), "no teaching text for " + t);
        }
    }

    public class ObjectivesTests
    {
        static Objectives Make(params Criterion[] cs) { var ed = new EducationSpec(); ed.success.AddRange(cs); return new Objectives(ed); }

        [Test]
        public void LimitWarnsAt80PercentAndFailsAtTheLimit()
        {
            var c = new Criterion { metric = "bloodLostMl", op = "<", value = 5 };
            Assert.AreEqual((double)(int)ObjectiveState.Ok, (double)(int)Objectives.Evaluate(c, 1f, true), 0);
            Assert.AreEqual((double)(int)ObjectiveState.Warning, (double)(int)Objectives.Evaluate(c, 4.2f, true), 0);
            Assert.AreEqual((double)(int)ObjectiveState.Failed, (double)(int)Objectives.Evaluate(c, 5f, true), 0);
        }

        [Test]
        public void CumulativeFailureIsStickyButCurrentStateIsNot()
        {
            var o = Make(new Criterion { metric = "bloodLostMl", op = "<", value = 5 }, new Criterion { metric = "exposedBleeding", op = "==", value = 0 });
            float blood = 6, open = 1;
            o.Update(m => m == "bloodLostMl" ? blood : open);
            Assert.AreEqual((double)(int)ObjectiveState.Failed, (double)(int)o.items[0].state, 0);
            Assert.AreEqual((double)(int)ObjectiveState.Warning, (double)(int)o.items[1].state, 0);
            blood = 1; open = 0;
            o.Update(m => m == "bloodLostMl" ? blood : open);
            Assert.AreEqual((double)(int)ObjectiveState.Failed, (double)(int)o.items[0].state, 0);
            Assert.AreEqual((double)(int)ObjectiveState.Ok, (double)(int)o.items[1].state, 0);
        }

        [Test]
        public void AchievementStaysMetAndUnavailableMetricsStayPending()
        {
            var o = Make(new Criterion { metric = "lesionDelivered", op = "==", value = 1 }, new Criterion { metric = "dermalEdgeDamageMm", op = "<", value = 0.5f });
            float delivered = 0;
            o.Update(m => m == "lesionDelivered" ? delivered : float.NaN);
            Assert.AreEqual((double)(int)ObjectiveState.Pending, (double)(int)o.items[0].state, 0);
            Assert.AreEqual((double)(int)ObjectiveState.Pending, (double)(int)o.items[1].state, 0);
            delivered = 1; o.Update(m => m == "lesionDelivered" ? delivered : float.NaN);
            delivered = 0; o.Update(m => m == "lesionDelivered" ? delivered : float.NaN);
            Assert.AreEqual((double)(int)ObjectiveState.Met, (double)(int)o.items[0].state, 0);
            Assert.IsFalse(o.AllMet);
        }
    }
}
