/**
 * Two hands on one mouse.
 *
 * The right hand is the pointer and the left button, as always. The left hand is the right mouse button (a pen's
 * barrel button): press it on the tissue and the left hand's instrument goes there and takes hold (the hook catches,
 * the forceps close), drag to pull, and let the button go: the instrument stays where it is, holding its traction.
 * The pointer is the right hand's again, and both instruments are on the field. To change the pull, press the right
 * button on the held instrument and drag; to move it, press it somewhere else; to take it off, click it.
 *
 * This is how the non-dominant hand works at the table: it places a retractor or a forceps, holds still while the
 * dominant hand works, and moves now and then. While it holds, the keyboard under the user's own left hand trims the
 * pull (slide, raise, lower), so traction can follow the dissection without the mouse leaving the right hand.
 *
 * The controller is pure: the app tells it whether the left-hand button is down, how far the pointer has travelled
 * since the press, and whether the press landed on the held instrument; it answers what the left hand should do.
 */
export type HandBState = 'parked' | 'live' | 'latched';

export interface HandInput {
  /** the left-hand button: right mouse button pressed on tissue with an instrument in the left hand */
  down: boolean;
  /** pointer travel over the field since the press, mm */
  dragMm: number;
  /** at the press: the pointer is on the instrument the left hand is already holding with */
  nearHeld: boolean;
  /** false for an instrument that only acts while the button is down (suction) */
  sticky: boolean;
}

/** pointer travel below which a press and release counts as a click (mm) */
export const CLICK_MM = 1.0;
/** a press this close to the held instrument's tip picks the hold up again instead of grasping anew (mm) */
export const PICKUP_MM = 4.0;

export class HandController {
  stateB: HandBState = 'parked';
  /** the left hand is holding (hook caught, jaws closed, suction on) */
  engagedB = false;
  /** true on the frame the left hand should take a new hold at the pointer */
  regrabB = false;
  /** the pull follows the pointer this frame */
  followB = false;
  /** this press picked the existing hold up again (drag adjusts it, a click lets go) */
  adjusting = false;
  /** true on the frame a hold was let go */
  releasedB = false;
  private last = false;
  private moved = false;

  update(i: HandInput) {
    const edge = i.down && !this.last;
    this.regrabB = false; this.releasedB = false;
    if (edge) {
      this.moved = false;
      if (this.engagedB && i.nearHeld) this.adjusting = true;
      else { this.adjusting = false; this.engagedB = true; this.regrabB = true; }
    }
    if (i.down) {
      if (i.dragMm > CLICK_MM) this.moved = true;
      // a click on the held instrument must not nudge it: the pull follows only once the pointer has moved
      this.followB = !this.adjusting || this.moved;
      this.stateB = 'live';
    } else {
      if (this.last && this.engagedB && (!i.sticky || (this.adjusting && !this.moved))) { this.engagedB = false; this.releasedB = true; }
      this.followB = false; this.adjusting = false;
      this.stateB = this.engagedB ? 'latched' : 'parked';
    }
    this.last = i.down;
  }

  /** Drops the left hand's hold (its instrument was changed or put down). */
  releaseB() { if (this.engagedB) this.releasedB = true; this.engagedB = false; this.adjusting = false; this.stateB = 'parked'; }
}
