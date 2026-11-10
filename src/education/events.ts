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
  | 'tissue.areolarContact' | 'tissue.visceraContact'
  | 'generator.settingChanged' | 'generator.remFault' | 'tool.changed'
  | 'vessel.exposed' | 'bleed.start' | 'bleed.sealed' | 'bleed.charPlug' | 'vessel.rebleed' | 'vessel.spasm'
  | 'blood.poolConducts' | 'blood.boiling' | 'blood.clotted' | 'field.obscured' | 'suction.used' | 'suction.clot' | 'forceps.buzzed'
  | 'tissue.oozing' | 'tissue.torn' | 'plane.dissected' | 'gauze.dab' | 'gauze.wipe' | 'gauze.saturated' | 'viscera.heated'
  | 'muscle.stimulated' | 'lesion.freed' | 'lesion.delivered' | 'lesion.breached';

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
  'tissue.areolarContact': 'Loose areolar tissue: the wispy plane that spreads apart bluntly. Energy here is rarely needed; push and spread with the hemostat.',
  'tissue.visceraContact': 'Viscera: thin wall, very vascular, low impedance. Current here cooks deep fast and a hole does not show until later. Lowest power, briefest touch, or no energy at all.',
  'vessel.spasm': 'A small vessel has stopped on its own: arterioles spasm and venules clot within seconds. Do not disturb the clot; pressure, not energy.',
  'blood.boiling': 'The electrode is boiling the blood: spatter, steam and a skin of coagulum that hides the bleeder and insulates it from the current. Clear the pool first.',
  'blood.clotted': 'Clotted blood does not flow and suction will not lift it. Wipe it off with gauze, or grasp and lift it with forceps, then find the vessel underneath.',
  'suction.clot': 'The suction is over clot: it takes the liquid and leaves the coagulum. Use gauze to wipe the clot away.',
  'tissue.oozing': 'The raw surface is weeping from its capillaries. Most ooze stops on its own in about a minute; a few seconds of gauze pressure speeds it up. Coag only what keeps bleeding.',
  'tissue.torn': 'Tissue tore under traction. Fat and areolar strands give way easily; fascia and dermis hold. Torn vessels bleed and torn parenchyma oozes.',
  'plane.dissected': 'The plane is opening bluntly: lobules part along their septa and areolar tissue strings out. Vessels crossing the plane stay as bridges until you cut or tear them.',
  'gauze.dab': 'Dabbing lifts liquid blood and presses the capillaries closed. Hold still for a few seconds for the ooze to settle.',
  'gauze.wipe': 'Wiping drags the clot off and restarts the ooze, and a small vessel that had spasmed shut can bleed again. Dab, do not wipe.',
  'gauze.saturated': 'The gauze is saturated and no longer absorbs. Discard it for a fresh one (U).',
  'viscera.heated': 'Heat is spreading into the viscus. Full-thickness injury here means a delayed perforation.',
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
  private lastViscera = -1e9; private lastBoil = -1e9; private clotWarned = false; private lastSuctionClot = -1e9; private oozeReported = false;
  private tornAccum = 0; private lastTorn = -1e9; private dissectAccum = 0; private lastDissect = -1e9;
  private lastGauzeDab = false; private lastWipe = -1e9; private lastGauzeSat = false;
  private lastExposedNonzero = 0; private lastSealedAtBleed = 0; private lastPlugAtBleed = 0; private lastSpasm = -1e9;

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
        else if (ty === Tissue.Areolar) this.push(t, 'tissue.areolarContact', name);
        else if (ty === Tissue.Viscera || ty === Tissue.Serosa) this.push(t, 'tissue.visceraContact', name, 'warn');
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
    if (active && r.visceraHeatVoxels > 40 && t - this.lastViscera > 2.0) { this.push(t, 'viscera.heated', `${r.visceraHeatVoxels} viscera voxels above 45 °C`, 'bad'); this.lastViscera = t; }
    // blood under the electrode boils and coagulates; clot resists suction
    if (active && r.boilColumns > 0 && t - this.lastBoil > 2.0) { this.push(t, 'blood.boiling', `${Math.round(r.boilColumns)} columns of blood boiling under the electrode`, 'warn'); this.lastBoil = t; }
    if (r.clotMl > 0.3 && !this.clotWarned) { this.push(t, 'blood.clotted', `${r.clotMl.toFixed(1)} ml of clot in the field`); this.clotWarned = true; }
    if (r.clotMl < 0.1) this.clotWarned = false;
    if (this.suctionOn && r.clotMl > 0.2 && r.poolAtTipMm > 0.2 && t - this.lastSuctionClot > 4.0) { this.push(t, 'suction.clot', 'clot under the suction tip', 'warn'); this.lastSuctionClot = t; }
    // raw surfaces ooze, tissue tears, planes open
    if (r.oozeVoxels > 10 && !this.oozeReported) { this.push(t, 'tissue.oozing', `${r.oozeVoxels} raw-surface voxels oozing`); this.oozeReported = true; }
    if (r.oozeVoxels === 0) this.oozeReported = false;
    this.tornAccum += r.tornVoxels;
    if (this.tornAccum > 0 && t - this.lastTorn > 1.0) { this.push(t, 'tissue.torn', `${this.tornAccum} voxels torn`, 'warn'); this.tornAccum = 0; this.lastTorn = t; }
    this.dissectAccum += r.dissectedVoxels;
    if (this.dissectAccum > 150 && t - this.lastDissect > 2.0) { this.push(t, 'plane.dissected', `${this.dissectAccum} voxels separated bluntly`); this.dissectAccum = 0; this.lastDissect = t; }
    // gauze: dab vs wipe, saturation
    if (this.gauzeDab && !this.lastGauzeDab) this.push(t, 'gauze.dab', 'pressure on the field');
    this.lastGauzeDab = this.gauzeDab;
    if (this.gauzeWipe && t - this.lastWipe > 3.0) { this.push(t, 'gauze.wipe', 'wiping the raw surface', 'warn'); this.lastWipe = t; }
    if (this.gauzeSaturated && !this.lastGauzeSat) this.push(t, 'gauze.saturated', 'gauze full', 'warn');
    this.lastGauzeSat = this.gauzeSaturated;
    // a small vessel that was bleeding has stopped without being sealed: spasm / thrombosis
    if (exposed === 0 && this.lastExposedNonzero > 0 && r.sealedVoxels === this.lastSealedAtBleed && r.charPlugVoxels === this.lastPlugAtBleed && !this.suctionOn && t - this.lastSpasm > 5.0) {
      this.push(t, 'vessel.spasm', 'bleeding stopped on its own'); this.lastSpasm = t;
    }
    if (exposed > 0) { this.lastExposedNonzero = t; this.lastSealedAtBleed = r.sealedVoxels; this.lastPlugAtBleed = r.charPlugVoxels; } else this.lastExposedNonzero = 0;
    const settings = JSON.stringify(gen.settings);
    if (this.lastSettings && settings !== this.lastSettings) this.push(t, 'generator.settingChanged', `${gen.settings.cutMode} ${gen.settings.cutWatts} W / ${gen.settings.coagMode} ${gen.settings.coagWatts} W`);
    this.lastSettings = settings;
    if (toolName !== this.lastTool) { if (this.lastTool) this.push(t, 'tool.changed', toolName); this.lastTool = toolName; }
    void frameDt;
  }

  /** set by the app each frame */
  suctionOn = false; buzzing = false; gauzeDab = false; gauzeWipe = false; gauzeSaturated = false;
  recent(n: number): SimEvent[] { return this.events.slice(-n); }
}
