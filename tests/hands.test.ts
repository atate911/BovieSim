import { describe, it, expect } from 'vitest';
import { HandController } from '@/tools/hands';

/** Drives the controller through a sequence of (space, button) frames. */
function run(h: HandController, frames: [boolean, boolean][]) { for (const [s, p] of frames) h.update(s, p); }

describe('two hands on one pointer', () => {
  it('hand A owns the button when Space is up', () => {
    const h = new HandController();
    h.update(false, true);
    expect(h.pressedA).toBe(true); expect(h.pressEdgeA).toBe(true); expect(h.stateB).toBe('parked');
    h.update(false, true);
    expect(h.pressEdgeA).toBe(false);
  });

  it('Space hands the pointer to hand B; a press grasps, a release lets go', () => {
    const h = new HandController();
    h.update(true, false);
    expect(h.stateB).toBe('live'); expect(h.followB).toBe(true); expect(h.engagedB).toBe(false);
    h.update(true, true);
    expect(h.engagedB).toBe(true); expect(h.regrabB).toBe(true); expect(h.pressedA).toBe(false);
    h.update(true, true);
    expect(h.regrabB).toBe(false); expect(h.engagedB).toBe(true);
    h.update(true, false);
    expect(h.engagedB).toBe(false);
    h.update(false, false);
    expect(h.stateB).toBe('parked');
  });

  it('releasing Space while holding latches hand B, and the still-held button does not reach hand A', () => {
    const h = new HandController();
    run(h, [[true, false], [true, true], [true, true]]);
    h.update(false, true);               // Space up, button still down
    expect(h.stateB).toBe('latched'); expect(h.engagedB).toBe(true);
    expect(h.pressedA).toBe(false);      // consumed: no accidental hand-switch activation
    h.update(false, false);
    expect(h.stateB).toBe('latched'); expect(h.engagedB).toBe(true);
    h.update(false, true);
    expect(h.pressedA).toBe(true);       // a fresh press is hand A's
    expect(h.stateB).toBe('latched');
  });

  it('a latched hand comes back live still holding, re-grasps on a new press, lets go on release', () => {
    const h = new HandController();
    run(h, [[true, false], [true, true], [false, true], [false, false]]);
    expect(h.stateB).toBe('latched');
    h.update(true, false);
    expect(h.stateB).toBe('live'); expect(h.engagedB).toBe(true); expect(h.followB).toBe(false);
    h.update(false, false);
    expect(h.stateB).toBe('latched');   // nothing changed: still holding
    run(h, [[true, false], [true, true]]);
    expect(h.regrabB).toBe(true); expect(h.followB).toBe(true);
    h.update(true, false);
    expect(h.engagedB).toBe(false);
    h.update(false, false);
    expect(h.stateB).toBe('parked');
  });

  it('releaseB drops a latched hold', () => {
    const h = new HandController();
    run(h, [[true, false], [true, true], [false, true]]);
    h.releaseB();
    expect(h.engagedB).toBe(false); expect(h.stateB).toBe('parked');
  });
});
