/**
 * Physics -> education event bus. Every event is something the learner could have seen or
 * should have noticed; scenarios reference these names in failurePoints / decisionPoints.
 */
import type { SimReadout } from '@/physics/pipeline';
import type { FT10 } from '@/core/generator/ft10';
import { cellType, cellState } from '@/core/volume';
import { Tissue, TissueState, TISSUE_NAMES } from '@/core/tissueProperties';

export type SimEventType =
  | 'activation.start' | 'activation.end' | 'activation.noContact'
  | 'tissue.coagulated' | 'tissue.desiccated' | 'tissue.carbonized' | 'tissue.vaporized'
  | 'tissue.fatContact' | 'tissue.fasciaContact' | 'tissue.muscleContact' | 'tissue.vesselContact' | 'tissue.nerveContact'
  | 'generator.settingChanged' | 'generator.remFault' | 'tool.changed'
  | 'vessel.exposed' | 'bleed.start' | 'bleed.sealed' | 'bleed.charPlug' | 'vessel.rebleed'
  | 'blood.poolConducts' | 'field.obscured' | 'suction.used' | 'forceps.buzzed'
  | 'muscle.stimulated' | 'lesion.freed' | 'lesion.delivered' | 'lesion.breached'
  | 'incision.made' | 'skin.oozing' | 'plane.developed' | 'plane.resists'
  | 'vessel.stretched' | 'vessel.torn' | 'nerve.stretched' | 'instrument.contact';

export interface SimEvent {
  t: number;
  type: SimEventType;
  detail: string;
  /** what the learner saw and what it meant (from the visual spec) */
  teaching?: string;
  severity: 'info' | 'warn' | 'bad';
}

const TEACHING: Partial<Record<SimEventType, string>> = {
  'activation.noContact': 'Generator active with the electrode off tissue: in cut mode nothing happens; in coag mode you are spraying. Touch first, then activate.',
  'tissue.carbonized': 'Black char: the tissue is desiccated and now insulates. More energy only spreads heat sideways. Lift, lower power, use short taps.',
  'tissue.desiccated': 'Tissue has dried out under the electrode: impedance rises and the generator can no longer deliver the set power.',
  'tissue.coagulated': 'Blanching means proteins have denatured (Ω > 0.53). This is the intended effect of coag; at a skin edge it is a thermal injury.',
  'tissue.fatContact': 'Fat: high impedance, low water. Cut mode sputters and stalls; move faster, use blend, or dissect bluntly.',
  'tissue.fasciaContact': 'White glistening sheet: deep fascia. Decide whether to stay above it or go through.',
  'tissue.muscleContact': 'Muscle: low impedance, bleeds readily, twitches on coag.',
  'tissue.vesselContact': 'You are on a vessel. Direct coag chars an open lumen without sealing it; grasp and buzz the forceps instead.',
  'tissue.nerveContact': 'Nerve: heat within 2 mm causes a sensory deficit.',
  'bleed.start': 'Bright red and pulsatile is arterial: grasp it with forceps and buzz the forceps, or tie it if it is over 2 mm. Dark and welling is venous: pressure, then fulgurate the surface.',
  'bleed.sealed': 'Walls apposed and collagen denatured across the lumen: a coaptive seal that holds.',
  'bleed.charPlug': 'Direct coag on an open lumen: a char plug over the vessel, not a seal. It can rebleed when the pressure rises or the plug is disturbed.',
  'vessel.rebleed': 'The char plug gave way. Grasp the vessel and buzz the forceps this time.',
  'blood.poolConducts': 'Current is spreading through the pool: wide shallow burns and no seal. Suction and pressure first, then identify the source.',
  'field.obscured': 'The field is filling with blood. Suction, pressure, find the source: do not buzz blind.',
  'forceps.buzzed': 'Energy delivered through the jaws. Effective only if a vessel is held between them with the tips not touching other tissue.',
  'muscle.stimulated': 'Current through muscle made it jump. Shorter bursts, cut rather than coag, and check the return pad.',
  'lesion.freed': 'The capsule is free around most of its circumference: the lipoma will deliver with gentle traction.',
  'lesion.breached': 'You have entered the lipoma. Fat spills, the plane is lost and fragments left behind recur.',
  'incision.made': 'Steel divides without heat: the edges are clean and there is no thermal rim. Counter-traction with the other hand keeps the line straight and opens the incision as you go.',
  'skin.oozing': 'The dermal plexus oozes from a fresh skin edge. It stops by itself in a minute or two; a touch of coag on a persistent point is enough. Do not paint the whole edge.',
  'plane.developed': 'Fat parts along the plane of least resistance: spreading finds the capsule without cutting into it. Stay on the shiny surface.',
  'plane.resists': 'That does not spread: a septum, fascia or the capsule itself. Divide it sharply (scissors, or a light cut with the pencil) and spread again beyond it.',
  'vessel.stretched': 'A vessel is tented in the spread. Stop, see it, control it (grasp and buzz, or tie) before it tears.',
  'vessel.torn': 'A small vessel tore under blunt force. It will ooze: grasp the end and buzz it.',
  'nerve.stretched': 'A nerve is being stretched. Traction injury is as real as thermal injury: ease off and dissect around it.',
  'instrument.contact': 'The active electrode touched another instrument. Current left through it and burned the tissue under it, an alternate-site burn where you were not looking. Keep the pencil clear of other metal.',
};

export class EventBus {
  readonly events: SimEvent[] = [];
  private listeners: ((e: SimEvent) => void)[] = [];
  private lastContactType = -1;
  private lastActive = false;
  private noContactSince = -1;
  private lastCarbon = 0;
  private lastSettings = '';
  private lastTool = '';
  private noContactReported = false;
  private coagAccum = 0;
  private lastCoagReport = -1e9;
  private lastExposed = 0; private lastSealed = 0; private lastCharPlug = 0; private lastRebleeds = 0;
  private poolWarned = false; private obscuredWarned = false; private lastSuction = false; private lastMuscle = -1e9; private lastBuzz = false;
  private cutAccum = 0; private lastCutT = -1e9; private oozeWarned = false; private lastOozeT = -1e9;
  private bluntAccum = 0; private lastPlane = -1e9; private resistT = 0; private lastResist = -1e9;
  private lastStretch = -1e9; private lastTorn = -1e9; private lastNerve = -1e9; private lastContact = false;

  on(fn: (e: SimEvent) => void) { this.listeners.push(fn); }
  emit(e: SimEvent) { this.events.push(e); for (const l of this.listeners) l(e); }
  private push(t: number, type: SimEventType, detail: string, severity: SimEvent['severity'] = 'info') {
    this.emit({ t, type, detail, teaching: TEACHING[type], severity });
  }

  /** Derives events from consecutive readouts. Call once per frame. */
  update(t: number, r: SimReadout, gen: FT10, toolName: string, frameDt: number) {
    const active = gen.activation !== 'none';
    const mode = gen.activeMode;
    if (active && !this.lastActive) { this.push(t, 'activation.start', `${gen.activation} ${mode?.label} ${gen.activeWatts} W`); this.noContactSince = t; this.noContactReported = false; }
    if (!active && this.lastActive) this.push(t, 'activation.end', `after ${(t - this.noContactSince).toFixed(1)} s`);
    this.lastActive = active;
    if (active && r.contactVoxels === 0) {
      if (!this.noContactReported && t - this.noContactSince > 0.4) { this.push(t, 'activation.noContact', `${gen.activation} with no tissue contact`, 'warn'); this.noContactReported = true; }
    } else if (active) { this.noContactSince = t; this.noContactReported = false; }
    // contact tissue type
    if (active && r.hit) {
      const ty = cellType(r.hitCell);
      if (ty !== this.lastContactType) {
        this.lastContactType = ty;
        const name = TISSUE_NAMES[ty];
        if (ty === Tissue.Fat || ty === Tissue.Lipoma) this.push(t, 'tissue.fatContact', name);
        else if (ty === Tissue.DeepFascia || ty === Tissue.SuperficialFascia) this.push(t, 'tissue.fasciaContact', name);
        else if (ty === Tissue.Muscle) this.push(t, 'tissue.muscleContact', name);
        else if (ty === Tissue.Artery || ty === Tissue.Vein) this.push(t, 'tissue.vesselContact', name, 'warn');
        else if (ty === Tissue.Nerve) this.push(t, 'tissue.nerveContact', name, 'bad');
      }
      const st = cellState(r.hitCell);
      if (st === TissueState.Desiccated && active) { /* desiccation event handled via impedance below */ }
    } else if (!active) this.lastContactType = -1;
    // carbonization
    if (r.carbonVoxels > this.lastCarbon + 2) { this.push(t, 'tissue.carbonized', `${r.carbonVoxels} voxels of char`, 'bad'); }
    this.lastCarbon = Math.max(this.lastCarbon, r.carbonVoxels);
    // coagulation volume (report in batches)
    this.coagAccum += r.newCoagVoxels;
    if (this.coagAccum > 40 && t - this.lastCoagReport > 1.0) { this.push(t, 'tissue.coagulated', `${this.coagAccum} voxels blanched`); this.coagAccum = 0; this.lastCoagReport = t; }
    // desiccation via impedance collapse of delivered power
    if (active && r.contactVoxels > 0 && mode && r.powerW < 0.6 * gen.activeWatts && r.zOhm > mode.zHi) {
      if (t - this.lastCoagReport > 1.0) { this.push(t, 'tissue.desiccated', `Z ${r.zOhm.toFixed(0)} Ω, only ${r.powerW.toFixed(0)} W delivered`, 'warn'); this.lastCoagReport = t; }
    }
    // bleeding and hemostasis
    const exposed = r.exposedArterial + r.exposedVenous;
    if (exposed > 0 && this.lastExposed === 0) {
      const kind = r.exposedArterial > 0 ? 'arterial' : 'venous';
      this.push(t, 'vessel.exposed', `${kind} vessel end exposed`);
      this.push(t, 'bleed.start', `${kind} bleeding`, r.exposedArterial > 0 ? 'warn' : 'info');
    }
    this.lastExposed = exposed;
    if (r.sealedVoxels > this.lastSealed) this.push(t, 'bleed.sealed', `${r.sealedVoxels - this.lastSealed} vessel voxels sealed`);
    this.lastSealed = Math.max(this.lastSealed, r.sealedVoxels);
    if (r.charPlugVoxels > this.lastCharPlug) this.push(t, 'bleed.charPlug', `${r.charPlugVoxels - this.lastCharPlug} vessel voxels under a char plug`, 'warn');
    this.lastCharPlug = Math.max(this.lastCharPlug, r.charPlugVoxels);
    if (r.rebleeds > this.lastRebleeds) this.push(t, 'vessel.rebleed', `char plug failed`, 'bad');
    this.lastRebleeds = Math.max(this.lastRebleeds, r.rebleeds);
    if (active && r.poolAtTipMm > 0.3) { if (!this.poolWarned) { this.push(t, 'blood.poolConducts', `${r.poolAtTipMm.toFixed(1)} mm of blood under the electrode`, 'warn'); this.poolWarned = true; } }
    else if (!active) this.poolWarned = false;
    if (r.poolMl > 0.8 && !this.obscuredWarned) { this.push(t, 'field.obscured', `${r.poolMl.toFixed(1)} ml pooled`, 'warn'); this.obscuredWarned = true; }
    if (r.poolMl < 0.3) this.obscuredWarned = false;
    if (this.suctionOn && !this.lastSuction) this.push(t, 'suction.used', 'field cleared');
    this.lastSuction = this.suctionOn;
    if (active && this.buzzing && !this.lastBuzz) {
      const ty = r.hit ? cellType(r.hitCell) : -1;
      const onVessel = ty === Tissue.Artery || ty === Tissue.Vein;
      this.push(t, 'forceps.buzzed', onVessel ? 'vessel held in the jaws' : 'no vessel in the jaws', onVessel ? 'info' : 'warn');
    }
    this.lastBuzz = active && this.buzzing;
    if (active && mode?.kind === 'coag' && r.muscleHeatVoxels > 20 && t - this.lastMuscle > 1.5) { this.push(t, 'muscle.stimulated', `${r.muscleHeatVoxels} muscle voxels in the current path`, 'warn'); this.lastMuscle = t; }
    const settings = JSON.stringify(gen.settings);
    if (this.lastSettings && settings !== this.lastSettings) this.push(t, 'generator.settingChanged', `${gen.settings.cutMode} ${gen.settings.cutWatts} W / ${gen.settings.coagMode} ${gen.settings.coagWatts} W`);
    this.lastSettings = settings;
    if (toolName !== this.lastTool) { if (this.lastTool) this.push(t, 'tool.changed', toolName); this.lastTool = toolName; }
    // steel: a scalpel stroke or scissor snip is reported when it ends
    this.cutAccum += r.cutVoxels;
    if (r.cutVoxels > 0) this.lastCutT = t;
    if (this.cutAccum > 0 && t - this.lastCutT > 0.4) { this.push(t, 'incision.made', `${this.cutAccum} voxels divided, no heat`); this.cutAccum = 0; }
    if (r.oozeVoxels > 0) { this.lastOozeT = t; if (!this.oozeWarned) { this.push(t, 'skin.oozing', `${r.oozeVoxels} dermal edge voxels oozing`); this.oozeWarned = true; } }
    else if (t - this.lastOozeT > 5) this.oozeWarned = false;
    // blunt dissection: the plane opening, or something that will not spread
    this.bluntAccum += r.bluntVoxels;
    if (this.bluntAccum > 60 && t - this.lastPlane > 1.5) { this.push(t, 'plane.developed', `${this.bluntAccum} voxels of fat separated`); this.bluntAccum = 0; this.lastPlane = t; }
    if (r.bluntResist > 30 && r.bluntVoxels < 5) this.resistT += frameDt; else this.resistT = 0;
    if (this.resistT > 0.5 && t - this.lastResist > 3) { this.push(t, 'plane.resists', `${r.bluntResist} voxels resist the spread`, 'warn'); this.lastResist = t; this.resistT = 0; }
    if (r.vesselStretch > 3 && t - this.lastStretch > 2) { this.push(t, 'vessel.stretched', `${r.vesselStretch} vessel voxels in the spread`, 'warn'); this.lastStretch = t; }
    if (r.vesselTorn > 0 && t - this.lastTorn > 1) { this.push(t, 'vessel.torn', `${r.vesselTorn} vessel voxels torn by blunt dissection`, 'bad'); this.lastTorn = t; }
    if (r.nerveStretch > 3 && t - this.lastNerve > 2) { this.push(t, 'nerve.stretched', `${r.nerveStretch} nerve voxels in the spread`, 'warn'); this.lastNerve = t; }
    if (active && this.instrumentContact && !this.lastContact) this.push(t, 'instrument.contact', 'active electrode on another instrument', 'bad');
    this.lastContact = active && this.instrumentContact;
  }

  /** set by the app each frame */
  suctionOn = false; buzzing = false; instrumentContact = false;
  recent(n: number): SimEvent[] { return this.events.slice(-n); }
}
