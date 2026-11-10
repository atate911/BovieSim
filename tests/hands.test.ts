import { describe, it, expect } from 'vitest';
import { HandController, HandInput } from '@/tools/hands';

const up: HandInput = { down: false, dragMm: 0, nearHeld: false, sticky: true };
const press = (o: Partial<HandInput> = {}): HandInput => ({ down: true, dragMm: 0, nearHeld: false, sticky: true, ...o });

describe('two hands on one mouse: the left hand on the right button', () => {
  it('a press on the tissue takes hold there, a drag pulls, and letting go leaves it holding', () => {
    const h = new HandController();
    h.update(press());
    expect(h.engagedB).toBe(true); expect(h.regrabB).toBe(true); expect(h.followB).toBe(true); expect(h.stateB).toBe('live');
    h.update(press({ dragMm: 4 }));
    expect(h.regrabB).toBe(false); expect(h.followB).toBe(true);
    h.update(up);
    expect(h.engagedB).toBe(true); expect(h.stateB).toBe('latched'); expect(h.followB).toBe(false); expect(h.releasedB).toBe(false);
    // the pointer is the right hand's again; nothing changes while the button stays up
    h.update(up); h.update(up);
    expect(h.stateB).toBe('latched');
  });

  it('a press without a drag still holds: forceps closed on a vessel stay closed', () => {
    const h = new HandController();
    h.update(press()); h.update(up);
    expect(h.engagedB).toBe(true); expect(h.stateB).toBe('latched');
  });

  it('pressing on the held instrument and dragging changes the pull without taking a new hold', () => {
    const h = new HandController();
    h.update(press()); h.update(press({ dragMm: 3 })); h.update(up);
    h.update(press({ nearHeld: true }));
    expect(h.regrabB).toBe(false); expect(h.adjusting).toBe(true);
    expect(h.followB).toBe(false);             // not yet moved: a click must not nudge it
    h.update(press({ nearHeld: true, dragMm: 2.5 }));
    expect(h.followB).toBe(true);
    h.update(up);
    expect(h.engagedB).toBe(true); expect(h.stateB).toBe('latched');
  });

  it('a click on the held instrument lets go', () => {
    const h = new HandController();
    h.update(press()); h.update(up);
    h.update(press({ nearHeld: true })); h.update(press({ nearHeld: true, dragMm: 0.3 }));
    h.update(up);
    expect(h.engagedB).toBe(false); expect(h.releasedB).toBe(true); expect(h.stateB).toBe('parked');
    h.update(up);
    expect(h.releasedB).toBe(false);
  });

  it('a press somewhere else moves the hold there', () => {
    const h = new HandController();
    h.update(press()); h.update(up);
    h.update(press({ nearHeld: false }));
    expect(h.regrabB).toBe(true); expect(h.adjusting).toBe(false); expect(h.engagedB).toBe(true);
    h.update(up);
    expect(h.stateB).toBe('latched');
  });

  it('suction only acts while its button is down', () => {
    const h = new HandController();
    h.update(press({ sticky: false }));
    expect(h.engagedB).toBe(true);
    h.update({ ...up, sticky: false });
    expect(h.engagedB).toBe(false); expect(h.stateB).toBe('parked');
  });

  it('changing the instrument drops the hold', () => {
    const h = new HandController();
    h.update(press()); h.update(up);
    h.releaseB();
    expect(h.engagedB).toBe(false); expect(h.stateB).toBe('parked'); expect(h.releasedB).toBe(true);
  });
});
