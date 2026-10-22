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
  | 'generator.settingChanged' | 'generator.remFault' | 'tool.changed';

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
    const settings = JSON.stringify(gen.settings);
    if (this.lastSettings && settings !== this.lastSettings) this.push(t, 'generator.settingChanged', `${gen.settings.cutMode} ${gen.settings.cutWatts} W / ${gen.settings.coagMode} ${gen.settings.coagWatts} W`);
    this.lastSettings = settings;
    if (toolName !== this.lastTool) { if (this.lastTool) this.push(t, 'tool.changed', toolName); this.lastTool = toolName; }
    void frameDt;
  }

  recent(n: number): SimEvent[] { return this.events.slice(-n); }
}
