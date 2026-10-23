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
            Assert.LessOrEqual(h[0], 0.05 + 1e-6);
        }

        [Test]
        public void BloodNeverFlowsUphill()
        {
            var surf = new double[] { 0, 2, 4 }; var h = new double[] { 0.5, 0, 0 };
            for (int s = 0; s < 50; s++) h = Reference.FlowStep(surf, h);
            Assert.AreEqual(0, h[2]); Assert.AreEqual(0, h[1]);
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

        [Test]
        public void CoagulationShrinksRestLengthsByUpTo12Percent()
        {
            var lat = new Lattice(Bed(), 3);
            var sev = new byte[lat.constraints.Count]; var om = new float[lat.constraints.Count];
            for (int i = 0; i < om.Length; i++) om[i] = 5;
            lat.ApplyProbe(sev, om);
            var c = lat.constraints[0];
            Assert.AreEqual(0.88f, c.rest / c.rest0, 0.005f);
        }
    }

    public class ScenarioTests
    {
        [TestCase("freeplay")] [TestCase("hemostasis")] [TestCase("lipoma")]
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
            foreach (var t in new[] { "tissue.carbonized", "bleed.charPlug", "forceps.buzzed", "blood.poolConducts" })
                Assert.IsFalse(string.IsNullOrEmpty(bus.Teaching(t)), "no teaching text for " + t);
        }
    }
}
