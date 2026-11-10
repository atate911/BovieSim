// Tonemap + vignette. Fullscreen triangle.
@group(0) @binding(0) var hdr: texture_2d<f32>;
@group(0) @binding(1) var samp: sampler;
@group(0) @binding(2) var<uniform> frame: Frame;

struct VSOut { @builtin(position) pos: vec4f, @location(0) uv: vec2f };

@vertex
fn vs_main(@builtin(vertex_index) vi: u32) -> VSOut {
  var o: VSOut;
  let x = f32((vi << 1u) & 2u); let y = f32(vi & 2u);
  o.pos = vec4f(x * 2.0 - 1.0, 1.0 - y * 2.0, 0.0, 1.0);
  o.uv = vec2f(x, y);
  return o;
}

// Khronos PBR Neutral tone mapper: compresses highlights without the hue and saturation shifts of
// filmic curves, so blood stays red and fat stays yellow instead of drifting toward orange.
fn pbrNeutral(cIn: vec3f) -> vec3f {
  let startCompression = 0.8 - 0.04;
  let desaturation = 0.15;
  var c = cIn;
  let x = min(c.r, min(c.g, c.b));
  let offset = select(0.04, x - 6.25 * x * x, x < 0.08);
  c -= offset;
  let peak = max(c.r, max(c.g, c.b));
  if (peak < startCompression) { return c; }
  let d = 1.0 - startCompression;
  let newPeak = 1.0 - d * d / (peak + d - startCompression);
  c *= newPeak / peak;
  let g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
  return mix(c, vec3f(newPeak), g);
}

fn srgb(c: vec3f) -> vec3f {
  let lo = c * 12.92;
  let hi = 1.055 * pow(c, vec3f(1.0 / 2.4)) - 0.055;
  return select(hi, lo, c <= vec3f(0.0031308));
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4f {
  var c = textureSample(hdr, samp, in.uv).rgb;
  c *= frame.params0.w;
  // slight bloom-ish glow for bright arcs and lamp reflections: cheap 4-tap blur of overbright
  let texel = 1.0 / frame.resolution.xy;
  var glow = vec3f(0.0);
  for (var i = 0; i < 4; i++) {
    let o = vec2f(select(-1.0, 1.0, (i & 1) == 1), select(-1.0, 1.0, (i & 2) == 2)) * texel * 3.0;
    glow += max(textureSample(hdr, samp, in.uv + o).rgb * frame.params0.w - 1.5, vec3f(0.0));
  }
  c += glow * 0.12;
  c = clamp(pbrNeutral(max(c, vec3f(0.0))), vec3f(0.0), vec3f(1.0));
  // the light field falls off gently toward the periphery of the view
  let d = distance(in.uv, vec2f(0.5));
  c *= 1.0 - smoothstep(0.5, 1.0, d) * 0.25;
  return vec4f(srgb(c), 1.0);
}
