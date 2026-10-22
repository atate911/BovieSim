import { FT10, FT10_MODES, CutMode, CoagMode } from '@/core/generator/ft10';

/**
 * Valleylab FT10 front panel (monopolar side). Touch targets mirror the real layout:
 * a CUT block (mode + watts, +/-), a COAG block, activation indicators, REM status,
 * and an on-screen footswitch for trackpad users.
 */
export class FT10Panel {
  readonly el: HTMLElement;
  private cutW!: HTMLElement; private coagW!: HTMLElement; private cutMode!: HTMLElement; private coagMode!: HTMLElement;
  private cutLamp!: HTMLElement; private coagLamp!: HTMLElement; private rem!: HTMLElement; private zEl!: HTMLElement;
  pedalCut = false; pedalCoag = false;
  constructor(parent: HTMLElement, private gen: FT10) {
    this.el = document.createElement('div');
    this.el.className = 'ft10';
    this.el.innerHTML = `
      <div class="ft10-head"><span class="ft10-brand">Valleylab™ FT10</span><span class="ft10-rem" data-rem>REM ●</span></div>
      <div class="ft10-body">
        <div class="ft10-block cut">
          <div class="ft10-title">MONOPOLAR CUT <span class="ft10-lamp" data-lamp="cut"></span></div>
          <div class="ft10-mode" data-mode="cut"></div>
          <div class="ft10-watts"><button data-adj="cut:-5">−</button><span data-w="cut"></span><button data-adj="cut:5">+</button></div>
        </div>
        <div class="ft10-block coag">
          <div class="ft10-title">MONOPOLAR COAG <span class="ft10-lamp" data-lamp="coag"></span></div>
          <div class="ft10-mode" data-mode="coag"></div>
          <div class="ft10-watts"><button data-adj="coag:-5">−</button><span data-w="coag"></span><button data-adj="coag:5">+</button></div>
        </div>
        <div class="ft10-block bipolar dim">
          <div class="ft10-title">BIPOLAR</div>
          <div class="ft10-mode">— not fitted —</div>
        </div>
      </div>
      <div class="ft10-foot">
        <span class="ft10-z" data-z>Z — Ω</span>
        <div class="ft10-pedals"><button class="pedal cut" data-pedal="cut">CUT</button><button class="pedal coag" data-pedal="coag">COAG</button></div>
      </div>`;
    parent.appendChild(this.el);
    const q = (s: string) => this.el.querySelector(s) as HTMLElement;
    this.cutW = q('[data-w="cut"]'); this.coagW = q('[data-w="coag"]'); this.cutMode = q('[data-mode="cut"]'); this.coagMode = q('[data-mode="coag"]');
    this.cutLamp = q('[data-lamp="cut"]'); this.coagLamp = q('[data-lamp="coag"]'); this.rem = q('[data-rem]'); this.zEl = q('[data-z]');
    this.el.querySelectorAll<HTMLButtonElement>('[data-adj]').forEach((b) => b.addEventListener('click', () => { const [k, d] = b.dataset.adj!.split(':'); gen.adjust(k as 'cut' | 'coag', parseInt(d, 10)); this.refresh(); }));
    this.cutMode.addEventListener('click', () => { gen.cycleMode('cut'); this.refresh(); });
    this.coagMode.addEventListener('click', () => { gen.cycleMode('coag'); this.refresh(); });
    this.el.querySelectorAll<HTMLButtonElement>('[data-pedal]').forEach((b) => {
      const set = (v: boolean) => { if (b.dataset.pedal === 'cut') this.pedalCut = v; else this.pedalCoag = v; };
      b.addEventListener('pointerdown', (e) => { set(true); b.setPointerCapture(e.pointerId); });
      b.addEventListener('pointerup', () => set(false)); b.addEventListener('pointercancel', () => set(false)); b.addEventListener('pointerleave', () => set(false));
    });
    // keep keyboard focus on the canvas
    this.el.addEventListener('pointerdown', (e) => e.stopPropagation());
    this.refresh();
  }
  setMode(kind: 'cut' | 'coag', id: CutMode | CoagMode) {
    if (kind === 'cut') this.gen.settings.cutMode = id as CutMode; else this.gen.settings.coagMode = id as CoagMode;
    this.gen.adjust(kind, 0); this.refresh();
  }
  refresh() {
    const s = this.gen.settings;
    this.cutW.textContent = String(s.cutWatts); this.coagW.textContent = String(s.coagWatts);
    this.cutMode.textContent = FT10_MODES[s.cutMode].label.toUpperCase();
    this.coagMode.textContent = FT10_MODES[s.coagMode].label.toUpperCase();
  }
  update(zOhm: number, powerW: number) {
    this.cutLamp.classList.toggle('on', this.gen.activation === 'cut');
    this.coagLamp.classList.toggle('on', this.gen.activation === 'coag');
    this.rem.classList.toggle('fault', this.gen.remFault);
    this.rem.textContent = this.gen.remFault ? 'REM FAULT' : 'REM ●';
    this.zEl.textContent = this.gen.activation === 'none' ? 'Z — Ω' : `Z ${isFinite(zOhm) && zOhm < 1e8 ? zOhm.toFixed(0) : '—'} Ω · ${powerW.toFixed(0)} W`;
  }
}
