/** Footswitch: Z = cut (yellow pedal), X = coag (blue pedal). */
export class Pedal {
  cut = false; coag = false;
  constructor(target: Window = window) {
    target.addEventListener('keydown', (e) => { if (e.repeat) return; if (e.code === 'KeyZ') this.cut = true; if (e.code === 'KeyX') this.coag = true; });
    target.addEventListener('keyup', (e) => { if (e.code === 'KeyZ') this.cut = false; if (e.code === 'KeyX') this.coag = false; });
    target.addEventListener('blur', () => { this.cut = false; this.coag = false; });
  }
}
