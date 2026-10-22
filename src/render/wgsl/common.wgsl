// Shared frame uniforms, lighting and procedural helpers.

struct Frame {
  viewProj: mat4x4f,
  view: mat4x4f,
  lightViewProj: mat4x4f,
  cameraPos: vec4f,       // xyz, w = time (s)
  lightDir: vec4f,        // xyz toward light, w = light intensity
  lightColor: vec4f,      // rgb, w = ambient intensity
  arcLight: vec4f,        // xyz position (mm), w = intensity (0 when generator idle)
  params0: vec4f,         // x = skin tone 0..1, y = prep type (0 none,1 chg,2 betadine), z = langer angle rad, w = exposure
  params1: vec4f,         // x = debug mode, y = bed half x (mm), z = bed half z (mm), w = shadow bias
  resolution: vec4f,      // xy pixels
};

const PI: f32 = 3.14159265;

// ---------- hashing & noise ----------
fn hash13(p: vec3f) -> f32 {
  var q = fract(p * vec3f(0.1031, 0.1030, 0.0973));
  q += dot(q, q.yzx + 33.33);
  return fract((q.x + q.y) * q.z);
}
fn hash33(p: vec3f) -> vec3f {
  var q = fract(p * vec3f(0.1031, 0.1030, 0.0973));
  q += dot(q, q.yxz + 33.33);
  return fract((q.xxy + q.yxx) * q.zyx);
}
fn vnoise3(p: vec3f) -> f32 {
  let i = floor(p); let f = fract(p);
  let u = f * f * (3.0 - 2.0 * f);
  let n000 = hash13(i); let n100 = hash13(i + vec3f(1,0,0));
  let n010 = hash13(i + vec3f(0,1,0)); let n110 = hash13(i + vec3f(1,1,0));
  let n001 = hash13(i + vec3f(0,0,1)); let n101 = hash13(i + vec3f(1,0,1));
  let n011 = hash13(i + vec3f(0,1,1)); let n111 = hash13(i + vec3f(1,1,1));
  let x00 = mix(n000, n100, u.x); let x10 = mix(n010, n110, u.x);
  let x01 = mix(n001, n101, u.x); let x11 = mix(n011, n111, u.x);
  return mix(mix(x00, x10, u.y), mix(x01, x11, u.y), u.z);
}
fn fbm3(p: vec3f, oct: i32) -> f32 {
  var s = 0.0; var a = 0.5; var q = p; var n = 0.0;
  for (var o = 0; o < oct; o++) { s += a * vnoise3(q); n += a; a *= 0.5; q = q * 2.07 + vec3f(13.1, 7.7, 3.3); }
  return s / n;
}
// Worley (cellular) noise: returns (F1, F2, cell hash)
fn worley3(p: vec3f) -> vec3f {
  let i = floor(p); let f = fract(p);
  var f1 = 8.0; var f2 = 8.0; var id = 0.0;
  for (var z = -1; z <= 1; z++) { for (var y = -1; y <= 1; y++) { for (var x = -1; x <= 1; x++) {
    let g = vec3f(f32(x), f32(y), f32(z));
    let o = hash33(i + g);
    let d = length(g + o - f);
    if (d < f1) { f2 = f1; f1 = d; id = hash13(i + g); } else if (d < f2) { f2 = d; }
  }}}
  return vec3f(f1, f2, id);
}

// ---------- BRDF ----------
fn D_GGX(NoH: f32, a: f32) -> f32 {
  let a2 = a * a; let d = NoH * NoH * (a2 - 1.0) + 1.0;
  return a2 / (PI * d * d + 1e-5);
}
fn V_Smith(NoV: f32, NoL: f32, a: f32) -> f32 {
  let a2 = a * a;
  let gv = NoL * sqrt(NoV * NoV * (1.0 - a2) + a2);
  let gl = NoV * sqrt(NoL * NoL * (1.0 - a2) + a2);
  return 0.5 / (gv + gl + 1e-5);
}
fn F_Schlick(f0: vec3f, VoH: f32) -> vec3f { return f0 + (1.0 - f0) * pow(1.0 - VoH, 5.0); }

struct Shade { diffuse: vec3f, specular: vec3f };

// Direct lighting for one light; wrap = subsurface wrap factor (0 hard, ~0.5 soft)
fn shadeLight(N: vec3f, V: vec3f, L: vec3f, albedo: vec3f, rough: f32, f0: vec3f, wrap: f32) -> Shade {
  let H = normalize(V + L);
  let NoL = dot(N, L); let NoV = max(dot(N, V), 1e-3);
  let NoH = max(dot(N, H), 0.0); let VoH = max(dot(V, H), 0.0);
  let a = max(rough * rough, 0.002);
  let wrapped = clamp((NoL + wrap) / (1.0 + wrap), 0.0, 1.0);
  let F = F_Schlick(f0, VoH);
  let spec = D_GGX(NoH, a) * V_Smith(NoV, max(NoL, 0.0), a) * F * max(NoL, 0.0);
  var s: Shade;
  s.diffuse = albedo * wrapped * (1.0 - F) / PI;
  s.specular = spec;
  return s;
}

fn hemiAmbient(N: vec3f, sky: vec3f, ground: vec3f) -> vec3f {
  let t = N.y * 0.5 + 0.5;
  return mix(ground, sky, t);
}

fn tempColor(t: f32) -> vec3f {
  // 37 -> 100 blue->cyan->green->yellow->red->white above 300
  let x = clamp((t - 37.0) / 263.0, 0.0, 1.0);
  let c1 = mix(vec3f(0.1, 0.1, 0.5), vec3f(0.0, 0.8, 0.9), smoothstep(0.0, 0.25, x));
  let c2 = mix(c1, vec3f(0.2, 0.9, 0.2), smoothstep(0.2, 0.45, x));
  let c3 = mix(c2, vec3f(1.0, 0.9, 0.1), smoothstep(0.4, 0.65, x));
  let c4 = mix(c3, vec3f(1.0, 0.2, 0.05), smoothstep(0.6, 0.85, x));
  return mix(c4, vec3f(1.0), smoothstep(0.85, 1.0, x));
}
