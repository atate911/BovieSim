/**
 * Two hands on one pointer.
 *
 * The dominant hand (A) is the pointer. Holding Space hands the pointer to the non-dominant hand (B):
 * press the button to grasp or catch, drag to pull, release to let go. Letting go of Space while B is
 * holding *latches* it: the instrument stays where it is, keeping its traction, and the pointer returns
 * to hand A. Holding Space again brings B back to life still holding; a new press re-grasps at the
 * pointer, a release lets go, and releasing Space with nothing held parks the instrument.
 *
 * This mirrors how the non-dominant hand works at the table: it places a retractor or forceps, holds
 * still while the dominant hand works, and moves only now and then. It needs no second pointing device
 * (a pen and a mouse share one cursor on Windows); touch or a gamepad can drive hand B later.
 */
export type HandBState = 'parked' | 'live' | 'latched';

export class HandController {
  stateB: HandBState = 'parked';
  /** hand B is holding (jaws closed, hook caught, suction on) */
  engagedB = false;
  /** true on the frame hand B should (re)attach at the pointer */
  regrabB = false;
  /** hand B's pose follows the pointer this frame (live, and not holding a latched grasp untouched) */
  followB = false;
  /** the button, as hand A should see it: never while Space is held, and not until released after a latch */
  pressedA = false;
  /** the button edge for hand A (press this frame) */
  pressEdgeA = false;
  private pressedSinceLive = false;
  private consumeA = false;
  private lastPressed = false;
  private lastPressedA = false;

  update(spaceHeld: boolean, pressed: boolean) {
    const pressEdge = pressed && !this.lastPressed;
    this.regrabB = false;
    if (spaceHeld) {
      if (this.stateB !== 'live') { this.stateB = 'live'; this.pressedSinceLive = false; }
      if (pressEdge) { this.engagedB = true; this.regrabB = true; this.pressedSinceLive = true; }
      if (!pressed && this.pressedSinceLive) this.engagedB = false;
      this.followB = !this.engagedB || this.pressedSinceLive;
      // the button belongs to hand B until it is released
      this.consumeA = this.consumeA || pressed;
      this.pressedA = false;
    } else {
      if (this.stateB === 'live') this.stateB = this.engagedB ? 'latched' : 'parked';
      this.followB = false;
      if (this.consumeA && !pressed) this.consumeA = false;
      this.pressedA = pressed && !this.consumeA;
    }
    this.pressEdgeA = this.pressedA && !this.lastPressedA;
    this.lastPressedA = this.pressedA;
    this.lastPressed = pressed;
  }

  /** Drops hand B's hold (e.g. when its instrument is changed or set to none). */
  releaseB() { this.engagedB = false; if (this.stateB === 'latched') this.stateB = 'parked'; }
}
