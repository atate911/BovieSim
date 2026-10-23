using NUnit.Framework;

namespace BovieSim.Tests
{
    /// <summary>Ports of tests/hands.test.ts: two hands on one pointer.</summary>
    public class HandControllerTests
    {
        static void Run(HandController h, params bool[] frames) { for (int i = 0; i + 1 < frames.Length; i += 2) h.Update(frames[i], frames[i + 1]); }
        static double S(HandBState s) { return (double)(int)s; }

        [Test]
        public void HandAOwnsTheButtonWhenSpaceIsUp()
        {
            var h = new HandController();
            h.Update(false, true);
            Assert.IsTrue(h.pressedA); Assert.IsTrue(h.pressEdgeA); Assert.AreEqual(S(HandBState.Parked), S(h.stateB), 0);
            h.Update(false, true);
            Assert.IsFalse(h.pressEdgeA);
        }

        [Test]
        public void SpaceHandsThePointerToHandB()
        {
            var h = new HandController();
            h.Update(true, false);
            Assert.AreEqual(S(HandBState.Live), S(h.stateB), 0); Assert.IsTrue(h.followB); Assert.IsFalse(h.engagedB);
            h.Update(true, true);
            Assert.IsTrue(h.engagedB); Assert.IsTrue(h.regrabB); Assert.IsFalse(h.pressedA);
            h.Update(true, true);
            Assert.IsFalse(h.regrabB); Assert.IsTrue(h.engagedB);
            h.Update(true, false);
            Assert.IsFalse(h.engagedB);
            h.Update(false, false);
            Assert.AreEqual(S(HandBState.Parked), S(h.stateB), 0);
        }

        [Test]
        public void ReleasingSpaceWhileHoldingLatchesAndConsumesTheButton()
        {
            var h = new HandController();
            Run(h, true, false, true, true, true, true);
            h.Update(false, true);
            Assert.AreEqual(S(HandBState.Latched), S(h.stateB), 0); Assert.IsTrue(h.engagedB); Assert.IsFalse(h.pressedA);
            h.Update(false, false);
            Assert.AreEqual(S(HandBState.Latched), S(h.stateB), 0); Assert.IsTrue(h.engagedB);
            h.Update(false, true);
            Assert.IsTrue(h.pressedA); Assert.AreEqual(S(HandBState.Latched), S(h.stateB), 0);
        }

        [Test]
        public void ALatchedHandComesBackLiveStillHoldingAndRegraspsOnANewPress()
        {
            var h = new HandController();
            Run(h, true, false, true, true, false, true, false, false);
            Assert.AreEqual(S(HandBState.Latched), S(h.stateB), 0);
            h.Update(true, false);
            Assert.AreEqual(S(HandBState.Live), S(h.stateB), 0); Assert.IsTrue(h.engagedB); Assert.IsFalse(h.followB);
            h.Update(false, false);
            Assert.AreEqual(S(HandBState.Latched), S(h.stateB), 0);
            Run(h, true, false, true, true);
            Assert.IsTrue(h.regrabB); Assert.IsTrue(h.followB);
            h.Update(true, false);
            Assert.IsFalse(h.engagedB);
            h.Update(false, false);
            Assert.AreEqual(S(HandBState.Parked), S(h.stateB), 0);
        }

        [Test]
        public void ReleaseBDropsALatchedHold()
        {
            var h = new HandController();
            Run(h, true, false, true, true, false, true);
            h.ReleaseB();
            Assert.IsFalse(h.engagedB); Assert.AreEqual(S(HandBState.Parked), S(h.stateB), 0);
        }
    }

    /// <summary>Ports of tests/dissection.test.ts: what steel does to each tissue.</summary>
    public class DissectionTests
    {
        static double O(BluntOutcome o) { return (double)(int)o; }

        [Test]
        public void SpreadingPartsFatButNotTheCapsule()
        {
            Assert.AreEqual(O(BluntOutcome.Parts), O(Dissection.Blunt(Tissue.Fat)), 0);
            Assert.AreEqual(O(BluntOutcome.Resists), O(Dissection.Blunt(Tissue.LipomaCapsule)), 0);
            Assert.AreEqual(O(BluntOutcome.Resists), O(Dissection.Blunt(Tissue.Lipoma)), 0);
        }

        [Test]
        public void SeptaFasciaeAndSkinResist()
        {
            foreach (var t in new[] { Tissue.Septum, Tissue.SuperficialFascia, Tissue.DeepFascia, Tissue.Dermis, Tissue.Epidermis, Tissue.Muscle })
                Assert.AreEqual(O(BluntOutcome.Resists), O(Dissection.Blunt(t)), 0, t.ToString());
        }

        [Test]
        public void SmallVesselsTearLargerOnesAndNervesAreStretched()
        {
            Assert.AreEqual(O(BluntOutcome.Tears), O(Dissection.Blunt(Tissue.Vein, 0.25f)), 0);
            Assert.AreEqual(O(BluntOutcome.Stretched), O(Dissection.Blunt(Tissue.Artery, 0.8f)), 0);
            Assert.AreEqual(O(BluntOutcome.Stretched), O(Dissection.Blunt(Tissue.Nerve)), 0);
        }

        [Test]
        public void ALargerVesselKeepsItsFatCuffInTheSpread()
        {
            Assert.IsTrue(Dissection.FatKeepsCuff(0.5f));
            Assert.IsFalse(Dissection.FatKeepsCuff(0.3f));
            Assert.IsFalse(Dissection.FatKeepsCuff(0));
        }

        [Test]
        public void ABladeLeavesFractionalOccupancyAtItsFace()
        {
            float h = 0.25f;
            Assert.AreEqual(0, Dissection.SharpKeep(-h, h), 1e-6);
            Assert.AreEqual(0.5, Dissection.SharpKeep(0, h), 1e-6);
            Assert.AreEqual(1, Dissection.SharpKeep(h, h), 1e-6);
        }
    }
}
