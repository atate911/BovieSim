namespace BovieSim
{
    public enum HandBState { Parked, Live, Latched }

    /// <summary>
    /// Two hands on one pointer (port of src/tools/hands.ts). The dominant hand (A) is the pointer. Holding Space hands
    /// the pointer to the non-dominant hand (B): press to grasp or catch, drag to pull, release to let go. Letting go of
    /// Space while B is holding latches it: the instrument stays where it is, keeping its traction, and the pointer
    /// returns to hand A. Holding Space again brings B back live still holding; a new press re-grasps at the pointer, a
    /// release lets go, and releasing Space with nothing held parks the instrument.
    /// </summary>
    public sealed class HandController
    {
        public HandBState stateB = HandBState.Parked;
        /// <summary>hand B is holding (jaws closed, hook caught, suction on)</summary>
        public bool engagedB;
        /// <summary>true on the frame hand B should (re)attach at the pointer</summary>
        public bool regrabB;
        /// <summary>hand B's pose follows the pointer this frame</summary>
        public bool followB;
        /// <summary>the button as hand A should see it: never while Space is held, and not until released after a latch</summary>
        public bool pressedA;
        /// <summary>press edge for hand A this frame</summary>
        public bool pressEdgeA;
        bool pressedSinceLive, consumeA, lastPressed, lastPressedA;

        public void Update(bool spaceHeld, bool pressed)
        {
            bool pressEdge = pressed && !lastPressed;
            regrabB = false;
            if (spaceHeld)
            {
                if (stateB != HandBState.Live) { stateB = HandBState.Live; pressedSinceLive = false; }
                if (pressEdge) { engagedB = true; regrabB = true; pressedSinceLive = true; }
                if (!pressed && pressedSinceLive) engagedB = false;
                followB = !engagedB || pressedSinceLive;
                consumeA = consumeA || pressed;
                pressedA = false;
            }
            else
            {
                if (stateB == HandBState.Live) stateB = engagedB ? HandBState.Latched : HandBState.Parked;
                followB = false;
                if (consumeA && !pressed) consumeA = false;
                pressedA = pressed && !consumeA;
            }
            pressEdgeA = pressedA && !lastPressedA;
            lastPressedA = pressedA;
            lastPressed = pressed;
        }

        /// <summary>Drops hand B's hold (its instrument was changed or set to none).</summary>
        public void ReleaseB() { engagedB = false; if (stateB == HandBState.Latched) stateB = HandBState.Parked; }
    }
}
