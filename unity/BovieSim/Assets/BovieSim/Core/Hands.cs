namespace BovieSim
{
    public enum HandBState { Parked, Live, Latched }

    /// <summary>
    /// Two hands on one mouse (port of src/tools/hands.ts). The right hand is the pointer and the left button, as always.
    /// The left hand is the right mouse button (a pen's barrel button): press it on the tissue and the left hand's
    /// instrument goes there and takes hold, drag to pull, and let the button go: the instrument stays where it is,
    /// holding its traction, and the pointer is the right hand's again. To change the pull, press on the held instrument
    /// and drag; to move it, press somewhere else; to take it off, click it. The controller is pure: the runner tells it
    /// whether the left-hand button is down, how far the pointer has travelled since the press and whether the press
    /// landed on the held instrument.
    /// </summary>
    public sealed class HandController
    {
        /// <summary>pointer travel below which a press and release counts as a click (mm)</summary>
        public const float ClickMm = 1.0f;
        /// <summary>a press this close to the held instrument picks the hold up again instead of grasping anew (mm)</summary>
        public const float PickupMm = 4.0f;

        public HandBState stateB = HandBState.Parked;
        /// <summary>the left hand is holding (hook caught, jaws closed, suction on)</summary>
        public bool engagedB;
        /// <summary>true on the frame the left hand should take a new hold at the pointer</summary>
        public bool regrabB;
        /// <summary>the pull follows the pointer this frame</summary>
        public bool followB;
        /// <summary>this press picked the existing hold up again (drag adjusts it, a click lets go)</summary>
        public bool adjusting;
        /// <summary>true on the frame a hold was let go</summary>
        public bool releasedB;
        bool last, moved;

        /// <param name="down">the left-hand button: right mouse button pressed on tissue with an instrument in the left hand</param>
        /// <param name="dragMm">pointer travel over the field since the press</param>
        /// <param name="nearHeld">at the press: the pointer is on the instrument the left hand is already holding with</param>
        /// <param name="sticky">false for an instrument that only acts while the button is down (suction)</param>
        public void Update(bool down, float dragMm, bool nearHeld, bool sticky)
        {
            bool edge = down && !last;
            regrabB = false; releasedB = false;
            if (edge)
            {
                moved = false;
                if (engagedB && nearHeld) adjusting = true;
                else { adjusting = false; engagedB = true; regrabB = true; }
            }
            if (down)
            {
                if (dragMm > ClickMm) moved = true;
                // a click on the held instrument must not nudge it: the pull follows only once the pointer has moved
                followB = !adjusting || moved;
                stateB = HandBState.Live;
            }
            else
            {
                if (last && engagedB && (!sticky || (adjusting && !moved))) { engagedB = false; releasedB = true; }
                followB = false; adjusting = false;
                stateB = engagedB ? HandBState.Latched : HandBState.Parked;
            }
            last = down;
        }

        /// <summary>Drops the left hand's hold (its instrument was changed or put down).</summary>
        public void ReleaseB() { if (engagedB) releasedB = true; engagedB = false; adjusting = false; stateB = HandBState.Parked; }
    }
}
