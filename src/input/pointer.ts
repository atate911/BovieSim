/** Pointer state with pen pressure and tilt (Pointer Events). Mouse gives pressure 0.5 when pressed. */
export interface PointerState {
  ndcX: number; ndcY: number;
  inside: boolean;
  pressure: number;
  tiltX: number; tiltY: number;
  isPen: boolean;
  buttons: number;
  /** left button without modifiers: hand-switch cut; with Ctrl: hand-switch coag */
  handCut: boolean; handCoag: boolean;
}

export function attachPointer(el: HTMLElement): PointerState {
  const st: PointerState = { ndcX: 0, ndcY: 0, inside: false, pressure: 0.5, tiltX: 0, tiltY: 0, isPen: false, buttons: 0, handCut: false, handCoag: false };
  const update = (e: PointerEvent) => {
    const r = el.getBoundingClientRect();
    st.ndcX = ((e.clientX - r.left) / r.width) * 2 - 1;
    st.ndcY = 1 - ((e.clientY - r.top) / r.height) * 2;
    st.isPen = e.pointerType === 'pen';
    st.pressure = st.isPen ? e.pressure : (e.buttons & 1 ? 0.5 : 0.35);
    st.tiltX = e.tiltX ?? 0; st.tiltY = e.tiltY ?? 0;
    st.buttons = e.buttons;
    const plain = (e.buttons & 1) === 1 && !e.altKey && !e.shiftKey;
    st.handCut = plain && !e.ctrlKey;
    st.handCoag = plain && e.ctrlKey;
  };
  el.addEventListener('pointermove', update);
  el.addEventListener('pointerdown', (e) => { update(e); if (e.button === 0 && !e.altKey && !e.shiftKey) e.preventDefault(); });
  el.addEventListener('pointerup', update);
  el.addEventListener('pointerenter', (e) => { st.inside = true; update(e); });
  el.addEventListener('pointerleave', (e) => { st.inside = false; update(e); st.handCut = false; st.handCoag = false; });
  return st;
}
