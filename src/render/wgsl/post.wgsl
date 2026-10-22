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

fn aces(x: vec3f) -> vec3f {
  let a = 2.51; let b = 0.03; let c = 2.43; let d = 0.59; let e = 0.14;
  return clamp((x * (a * x + b)) / (x * (c * x + d) + e), vec3f(0.0), vec3f(1.0));
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4f {
  var c = textureSample(hdr, samp, in.uv).rgb;
  c *= frame.params0.w;
  // slight bloom-ish glow for bright arcs: cheap 4-tap blur of overbright
  let texel = 1.0 / frame.resolution.xy;
  var glow = vec3f(0.0);
  for (var i = 0; i < 4; i++) {
    let o = vec2f(select(-1.0, 1.0, (i & 1) == 1), select(-1.0, 1.0, (i & 2) == 2)) * texel * 3.0;
    glow += max(textureSample(hdr, samp, in.uv + o).rgb * frame.params0.w - 1.5, vec3f(0.0));
  }
  c += glow * 0.15;
  c = aces(c);
  let d = distance(in.uv, vec2f(0.5));
  c *= 1.0 - smoothstep(0.55, 0.95, d) * 0.45;
  // sRGB encode
  c = pow(c, vec3f(1.0 / 2.2));
  return vec4f(c, 1.0);
}
