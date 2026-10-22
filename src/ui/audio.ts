/** Activation tones. Cut and coag have distinct pitches, as on the real generator. */
export class ActivationAudio {
  private ctx: AudioContext | null = null;
  private osc: OscillatorNode | null = null;
  private gain: GainNode | null = null;
  private current = 0;
  private ensure() {
    if (this.ctx) return;
    try {
      this.ctx = new AudioContext();
      this.gain = this.ctx.createGain(); this.gain.gain.value = 0; this.gain.connect(this.ctx.destination);
      this.osc = this.ctx.createOscillator(); this.osc.type = 'square';
      const lp = this.ctx.createBiquadFilter(); lp.type = 'lowpass'; lp.frequency.value = 1800;
      this.osc.connect(lp); lp.connect(this.gain); this.osc.start();
    } catch { this.ctx = null; }
  }
  set(hz: number, on: boolean, volume = 0.08) {
    if (on) this.ensure();
    if (!this.ctx || !this.osc || !this.gain) return;
    if (this.ctx.state === 'suspended') this.ctx.resume();
    if (on && this.current !== hz) { this.osc.frequency.setValueAtTime(hz, this.ctx.currentTime); this.current = hz; }
    this.gain.gain.setTargetAtTime(on ? volume : 0, this.ctx.currentTime, 0.01);
  }
}
