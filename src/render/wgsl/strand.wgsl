// Tissue strands: yielded lattice links through areolar tissue, fat or capsule drawn as thin translucent
// filaments between their deformed endpoints (line list). Thinness w (0 fresh .. 1 about to snap) fades and
// whitens the strand.
struct StrandVtx { @location(0) pos: vec3f, @location(1) w: f32 };
@group(0) @binding(0) var<uniform> frame: Frame;
struct VSOut { @builtin(position) pos: vec4f, @location(0) w: f32, @location(1) wpos: vec3f };

@vertex
fn vs_main(v: StrandVtx) -> VSOut {
  var o: VSOut;
  o.pos = frame.viewProj * vec4f(v.pos, 1.0);
  o.w = v.w; o.wpos = v.pos;
  return o;
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4f {
  // pale pink-white connective strand, glossy wet; thins and pales as it is drawn out
  let base = mix(vec3f(0.93, 0.80, 0.78), vec3f(0.98, 0.96, 0.94), in.w);
  let L = normalize(frame.lightDir.xyz);
  let lit = base * (0.55 + 0.45 * max(L.y, 0.0)) * frame.lightColor.rgb * frame.lightDir.w * 0.6 + base * frame.lightColor.w * 0.6;
  let a = mix(0.85, 0.35, in.w);
  return vec4f(lit * a, a);
}
