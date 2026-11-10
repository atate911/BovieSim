using NUnit.Framework;
using UnityEngine;

namespace BovieSim.Tests
{
    /// <summary>Ports of tests/hands.test.ts: the left hand on the right mouse button.</summary>
    public class HandControllerTests
    {
        static double S(HandBState s) { return (double)(int)s; }
        static void Press(HandController h, float dragMm = 0, bool nearHeld = false, bool sticky = true) { h.Update(true, dragMm, nearHeld, sticky); }
        static void Up(HandController h, bool sticky = true) { h.Update(false, 0, false, sticky); }

        [Test]
        public void APressTakesHoldADragPullsAndLettingGoLeavesItHolding()
        {
            var h = new HandController();
            Press(h);
            Assert.IsTrue(h.engagedB); Assert.IsTrue(h.regrabB); Assert.IsTrue(h.followB); Assert.AreEqual(S(HandBState.Live), S(h.stateB), 0);
            Press(h, 4);
            Assert.IsFalse(h.regrabB); Assert.IsTrue(h.followB);
            Up(h);
            Assert.IsTrue(h.engagedB); Assert.AreEqual(S(HandBState.Latched), S(h.stateB), 0); Assert.IsFalse(h.followB); Assert.IsFalse(h.releasedB);
            Up(h); Up(h);
            Assert.AreEqual(S(HandBState.Latched), S(h.stateB), 0);
        }

        [Test]
        public void APressWithoutADragStillHolds()
        {
            var h = new HandController();
            Press(h); Up(h);
            Assert.IsTrue(h.engagedB); Assert.AreEqual(S(HandBState.Latched), S(h.stateB), 0);
        }

        [Test]
        public void PressingOnTheHeldInstrumentAndDraggingChangesThePull()
        {
            var h = new HandController();
            Press(h); Press(h, 3); Up(h);
            Press(h, 0, true);
            Assert.IsFalse(h.regrabB); Assert.IsTrue(h.adjusting); Assert.IsFalse(h.followB);
            Press(h, 2.5f, true);
            Assert.IsTrue(h.followB);
            Up(h);
            Assert.IsTrue(h.engagedB); Assert.AreEqual(S(HandBState.Latched), S(h.stateB), 0);
        }

        [Test]
        public void AClickOnTheHeldInstrumentLetsGo()
        {
            var h = new HandController();
            Press(h); Up(h);
            Press(h, 0, true); Press(h, 0.3f, true);
            Up(h);
            Assert.IsFalse(h.engagedB); Assert.IsTrue(h.releasedB); Assert.AreEqual(S(HandBState.Parked), S(h.stateB), 0);
            Up(h);
            Assert.IsFalse(h.releasedB);
        }

        [Test]
        public void APressSomewhereElseMovesTheHoldThere()
        {
            var h = new HandController();
            Press(h); Up(h);
            Press(h);
            Assert.IsTrue(h.regrabB); Assert.IsFalse(h.adjusting); Assert.IsTrue(h.engagedB);
            Up(h);
            Assert.AreEqual(S(HandBState.Latched), S(h.stateB), 0);
        }

        [Test]
        public void SuctionOnlyActsWhileItsButtonIsDown()
        {
            var h = new HandController();
            Press(h, 0, false, false);
            Assert.IsTrue(h.engagedB);
            Up(h, false);
            Assert.IsFalse(h.engagedB); Assert.AreEqual(S(HandBState.Parked), S(h.stateB), 0);
        }

        [Test]
        public void ChangingTheInstrumentDropsTheHold()
        {
            var h = new HandController();
            Press(h); Up(h);
            h.ReleaseB();
            Assert.IsFalse(h.engagedB); Assert.AreEqual(S(HandBState.Parked), S(h.stateB), 0); Assert.IsTrue(h.releasedB);
        }
    }

    /// <summary>Port of the mapping test in tests/steel.test.ts: what is under the cursor is what is seen there.</summary>
    public class WorldToMaterialTests
    {
        static Volume Bed(int sizeMm = 30, int depthMm = 18)
        {
            const int air = 2; int n = sizeMm, nz = depthMm + air;
            var v = new Volume(n, n, nz, 1f, air);
            for (int k = 0; k < nz; k++) for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
            {
                int idx = v.Index(i, j, k);
                float depth = v.SurfaceK - (k + 0.5f);
                if (depth < 0) { v.occupancy[idx] = 0; v.cell[idx] = 0; continue; }
                v.occupancy[idx] = 1;
                v.cell[idx] = Cell.Pack(depth < 2 ? Tissue.Dermis : depth < 14 ? Tissue.Fat : Tissue.Muscle);
            }
            return v;
        }

        [Test]
        public void MapsAPointOnRetractedTissueBackToWhereThatTissueLies()
        {
            var lat = new Lattice(Bed(), 3f);
            // a skin node dragged 5 mm sideways and 2 mm up and its neighbour half as far: one cell stretched to more than
            // twice its size, the next squeezed, where a plain fixed-point inverse no longer converges
            int node = lat.NearestNode(new Vector3(0, 0, 0), 2);
            Assert.IsTrue(node >= 0);
            lat.pos[node * 3] -= 5; lat.pos[node * 3 + 1] += 2;
            lat.pos[(node - 1) * 3] -= 2.5f; lat.pos[(node - 1) * 3 + 1] += 1;
            double worst = 0;
            foreach (float dx in new[] { -2.5f, -1f, 0f, 1f, 2.5f })
                foreach (float dy in new[] { -2.5f, -1f, -0.2f })
                    foreach (float dz in new[] { -2f, 0f, 2f })
                    {
                        var m = lat.Mat(node) + new Vector3(dx, dy, dz);
                        var back = lat.WorldToMaterial(lat.MaterialToWorld(m));
                        worst = System.Math.Max(worst, (m - back).magnitude);
                    }
            Assert.Less(worst, 0.15, "round trip error (mm)");
            var home = lat.WorldToMaterial(lat.Pos(node));
            Assert.Less((home - lat.Mat(node)).magnitude, 0.15, "the node maps back to where it belongs");
        }
    }
}
