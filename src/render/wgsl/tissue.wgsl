// Tissue surface: vertex pulling from the extraction buffer, procedural tissue materials,
// thermal-state appearance, surgical light with shadows, wrap/translucency SSS approximation.

struct Vertex {
  px: f32, py: f32, pz: f32, cell: u32,
  nx: f32, ny: f32, nz: f32, attr: u32,
};

struct BloodCol { h: f32, age: f32, surf: f32, hNext: f32 };
@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var<storage, read> verts: array<Vertex>;
@group(0) @binding(2) var shadowMapA: texture_depth_2d;
@group(0) @binding(3) var shadowSampler: sampler_comparison;
@group(0) @binding(4) var<storage, read> blood: array<BloodCol>;
@group(0) @binding(5) var<uniform> vinfo: VolumeInfo;
struct LatticeInfo { nx: u32, ny: u32, nz: u32, pad: u32, origin: vec3f, spacing: f32 };
@group(0) @binding(6) var<uniform> lat: LatticeInfo;
@group(0) @binding(7) var<storage, read> latDisp: array<vec4f>;
@group(0) @binding(8) var shadowMapB: texture_depth_2d;
struct ShadowSel { head: u32, pad0: u32, pad1: u32, pad2: u32 };
@group(0) @binding(9) var<uniform> shadowSel: ShadowSel;

/** Trilinear lattice displacement at a material point (mm). */
fn latticeDisp(p: vec3f) -> vec3f {
  if (lat.nx < 2u) { return vec3f(0.0); }
  let f = (p - lat.origin) / lat.spacing;
  let i0 = clamp(i32(floor(f.x)), 0, i32(lat.nx) - 2);
  let j0 = clamp(i32(floor(f.y)), 0, i32(lat.ny) - 2);
  let k0 = clamp(i32(floor(f.z)), 0, i32(lat.nz) - 2);
  let t = clamp(f - vec3f(f32(i0), f32(j0), f32(k0)), vec3f(0.0), vec3f(1.0));
  var d = vec3f(0.0);
  for (var dk = 0; dk < 2; dk++) { for (var dj = 0; dj < 2; dj++) { for (var di = 0; di < 2; di++) {
    let n = u32(i0 + di) + lat.nx * (u32(j0 + dj) + lat.ny * u32(k0 + dk));
    let w = select(1.0 - t.x, t.x, di == 1) * select(1.0 - t.y, t.y, dj == 1) * select(1.0 - t.z, t.z, dk == 1);
    d += latDisp[n].xyz * w;
  }}}
  return d;
}

/** Bilinear blood depth (mm) and age (s) at a world x,z. */
fn bloodAt(p: vec3f) -> vec2f {
  let fx = p.x / vinfo.hMm + f32(vinfo.nx) * 0.5 - 0.5;
  let fz = p.z / vinfo.hMm + f32(vinfo.ny) * 0.5 - 0.5;
  let i0 = i32(floor(fx)); let j0 = i32(floor(fz));
  let tx = fract(fx); let tz = fract(fz);
  var acc = vec2f(0.0);
  for (var dj = 0; dj < 2; dj++) { for (var di = 0; di < 2; di++) {
    let i = clamp(i0 + di, 0, i32(vinfo.nx) - 1); let j = clamp(j0 + dj, 0, i32(vinfo.ny) - 1);
    let b = blood[u32(i) + vinfo.nx * u32(j)];
    let w = select(1.0 - tx, tx, di == 1) * select(1.0 - tz, tz, dj == 1);
    // blood belongs to the surface of its column; ignore it on vertices well below that surface
    let onSurface = smoothstep(-1.5, -0.3, p.y - b.surf);
    acc += vec2f(b.h, b.age) * w * onSurface;
  }}
  return acc;
}

struct VSOut {
  @builtin(position) pos: vec4f,
  @location(0) wpos: vec3f,
  @location(1) nrm: vec3f,
  @location(2) @interpolate(flat) cell: u32,
  @location(3) tempC: f32,
  @location(4) water: f32,
  @location(5) omega: f32,
};

fn unpackTemp(a: u32) -> f32 { return f32(a & 0xffffu) / 50.0; }
fn unpackWater(a: u32) -> f32 { return f32((a >> 16u) & 0xffu) / 255.0; }
fn unpackOmega(a: u32) -> f32 { return f32((a >> 24u) & 0xffu) / 255.0 * 2.0; }

@vertex
fn vs_main(@builtin(vertex_index) vi: u32) -> VSOut {
  let v = verts[vi];
  var o: VSOut;
  let pm = vec3f(v.px, v.py, v.pz);
  o.wpos = pm + latticeDisp(pm);
  o.pos = frame.viewProj * vec4f(o.wpos, 1.0);
  o.nrm = vec3f(v.nx, v.ny, v.nz);
  o.cell = v.cell;
  o.tempC = unpackTemp(v.attr);
  o.water = unpackWater(v.attr);
  o.omega = unpackOmega(v.attr);
  return o;
}

@vertex
fn vs_shadow(@builtin(vertex_index) vi: u32) -> @builtin(position) vec4f {
  let v = verts[vi];
  let pm = vec3f(v.px, v.py, v.pz);
  let wp = vec4f(pm + latticeDisp(pm), 1.0);
  if (shadowSel.head == 1u) { return frame.light2ViewProj * wp; }
  return frame.lightViewProj * wp;
}

// ---------- tissue ids (must match core/tissueProperties.ts) ----------
const T_AIR = 0u; const T_EPI = 1u; const T_DERMIS = 2u; const T_FAT = 3u; const T_SEPTUM = 4u;
const T_SFASCIA = 5u; const T_DFASCIA = 6u; const T_MUSCLE = 7u; const T_ARTERY = 8u; const T_VEIN = 9u;
const T_NERVE = 10u; const T_LIPOMA = 11u; const T_CAPSULE = 12u; const T_BLOOD = 13u;
const S_NORMAL = 0u; const S_COAG = 1u; const S_DESIC = 2u; const S_CARBON = 3u;
const FLAG_FIBRE_X = 1u << 25u;

struct Mat { albedo: vec3f, rough: f32, f0: f32, sss: f32, sssColor: vec3f, nrmDetail: vec3f, emissive: vec3f };

fn skinTone(t: f32) -> vec3f {
  // light to dark skin, in linear space
  let light = vec3f(0.80, 0.56, 0.46);
  let mid = vec3f(0.55, 0.33, 0.22);
  let dark = vec3f(0.22, 0.12, 0.08);
  return select(mix(mid, dark, (t - 0.5) * 2.0), mix(light, mid, t * 2.0), t < 0.5);
}

fn baseMaterial(ty: u32, flags: u32, aux: u32, p: vec3f, N: vec3f) -> Mat {
  var m: Mat;
  m.sssColor = vec3f(0.9, 0.3, 0.2);
  m.nrmDetail = vec3f(0.0);
  m.emissive = vec3f(0.0);
  m.f0 = 0.04;
  let fine = fbm3(p * 3.0, 3);           // ~0.3 mm scale detail
  let med = fbm3(p * 0.6, 3);            // ~1.6 mm
  let coarse = fbm3(p * 0.12, 2);        // ~8 mm
  switch (ty) {
    case T_EPI: {
      let tone = skinTone(frame.params0.x);
      let ang = frame.params0.z;
      let dir = vec2f(cos(ang), sin(ang));
      let along = dot(p.xz, dir); let across = dot(p.xz, vec2f(-dir.y, dir.x));
      // the skin's polygonal line network: primary furrows (~1.3 mm cells, stretched along Langer's lines) and a
      // finer secondary net, as cell borders of two Worley fields; pores sit at the cell corners
      let q1 = vec3f(along * 0.9, across * 1.5, 0.3);
      let w1 = worley3(q1);
      let furrow = (1.0 - smoothstep(0.0, 0.06, w1.y - w1.x)) * (0.5 + 0.5 * fbm3(p * 0.5, 2));
      let w2 = worley3(vec3f(along * 2.4, across * 3.6, 2.1));
      let furrow2 = (1.0 - smoothstep(0.0, 0.04, w2.y - w2.x)) * 0.8;
      let pores = smoothstep(0.88, 0.96, fbm3(p * 4.0, 2)) + (1.0 - smoothstep(0.0, 0.08, w1.x)) * 0.6;
      // vascular mottling (blotchy pink / pale) at ~8 mm and a broader tone drift
      let mott = fbm3(p * 0.13, 3);
      let blotch = smoothstep(0.4, 0.75, mott);
      var alb = tone * (0.92 + 0.16 * med) * (1.0 - 0.1 * clamp(pores, 0.0, 1.0)) * (1.0 - 0.05 * furrow - 0.03 * furrow2) * (0.92 + 0.16 * coarse);
      alb = mix(alb, alb * vec3f(1.08, 0.86, 0.84), blotch * 0.6);
      alb = mix(alb, alb * vec3f(0.96, 1.02, 1.04), smoothstep(0.3, 0.1, mott) * 0.4);
      // skin prep painted on with a sponge: tinted film, thicker in the furrows and along the stroke direction
      let prep = frame.params0.y;
      let stroke = fbm3(vec3f(along * 0.06, across * 0.5, 5.0), 3);
      let film = clamp(0.55 + 0.5 * (stroke - 0.5) + 0.25 * furrow, 0.0, 1.0);
      if (prep > 0.5 && prep < 1.5) { alb = alb * mix(vec3f(1.0), vec3f(1.04, 0.80, 0.58), 0.3 + 0.35 * film); }   // tinted chlorhexidine-alcohol
      else if (prep >= 1.5) { alb = alb * mix(vec3f(1.0), vec3f(0.85, 0.55, 0.28), 0.45 + 0.4 * film); }          // povidone-iodine
      m.albedo = alb;
      // dry skin is matte; the prep leaves a glossy film with sponge-stroke variation and pooling in the furrows
      let wet = select(0.0, film, prep > 0.5);
      m.rough = mix(0.62, 0.22, wet) + 0.08 * furrow2 - 0.06 * furrow * wet;
      m.sss = 0.55;
      m.sssColor = vec3f(0.95, 0.35, 0.2);
      m.nrmDetail = vec3f(fine - 0.5, 0.0, 0.0) * 0.08 + vec3f(w1.y - w1.x - 0.3, 0.0, 0.0) * furrow * 0.3 + vec3f(0.0, 0.0, w2.y - w2.x - 0.2) * furrow2 * 0.2;
    }
    case T_DERMIS: {
      // collagenous, pink-white, with dermal plexus speckle
      let speck = smoothstep(0.6, 0.9, fbm3(p * 4.0, 2));
      m.albedo = mix(vec3f(0.82, 0.55, 0.50), vec3f(0.75, 0.25, 0.22), speck * 0.7) * (0.9 + 0.2 * fine);
      m.rough = 0.28; m.sss = 0.5; m.sssColor = vec3f(0.9, 0.3, 0.25);
      m.nrmDetail = vec3f(fine - 0.5, fine - 0.5, med - 0.5) * 0.2;
    }
    case T_FAT, T_LIPOMA: {
      // lobules: Worley cells ~2.5 mm, boundaries slightly darker/whiter (fine septa)
      let w = worley3(p * 0.4 + vec3f(f32(aux) * 0.37));
      let edge = smoothstep(0.0, 0.18, w.y - w.x);
      var yellow = vec3f(0.95, 0.78, 0.33);
      if (ty == T_LIPOMA) { yellow = vec3f(0.98, 0.86, 0.46); }
      let lobuleVar = 0.85 + 0.3 * w.z;
      var alb = yellow * lobuleVar * (0.92 + 0.16 * fine);
      alb = mix(vec3f(0.92, 0.9, 0.82), alb, edge);  // whitish boundaries
      m.albedo = alb;
      m.rough = 0.16 + 0.08 * (1.0 - edge); m.sss = 0.75; m.sssColor = vec3f(1.0, 0.75, 0.35);
      m.nrmDetail = vec3f(w.x - 0.5, 0.0, w.y - w.x - 0.3) * 0.5 + vec3f(fine - 0.5) * 0.1;
    }
    case T_SEPTUM, T_CAPSULE: {
      m.albedo = vec3f(0.92, 0.88, 0.80) * (0.9 + 0.2 * fine);
      m.rough = 0.25; m.sss = 0.3; m.sssColor = vec3f(0.9, 0.6, 0.4);
      m.nrmDetail = vec3f(fine - 0.5) * 0.15;
    }
    case T_SFASCIA, T_DFASCIA: {
      // fibrous sheen: fibres along x
      let fib = 0.5 + 0.5 * sin(p.z * 14.0 + fbm3(p * 1.5, 2) * 4.0);
      var alb = vec3f(0.93, 0.92, 0.86) * (0.9 + 0.15 * fib) * (0.92 + 0.12 * med);
      if (ty == T_SFASCIA) { alb = mix(alb, vec3f(0.95, 0.85, 0.6), 0.35); }
      m.albedo = alb;
      m.rough = 0.18 + 0.1 * fib; m.f0 = 0.05; m.sss = 0.2; m.sssColor = vec3f(0.9, 0.7, 0.5);
      m.nrmDetail = vec3f(0.0, 0.0, fib - 0.5) * 0.25;
    }
    case T_MUSCLE: {
      var fib = 0.5 + 0.5 * sin(p.z * 9.0 + fbm3(p * 1.2, 2) * 5.0);
      if ((flags & FLAG_FIBRE_X) == 0u) { fib = 0.5 + 0.5 * sin(p.x * 9.0 + fbm3(p * 1.2, 2) * 5.0); }
      m.albedo = vec3f(0.52, 0.10, 0.10) * (0.8 + 0.4 * fib) * (0.9 + 0.2 * med);
      m.rough = 0.22; m.f0 = 0.05; m.sss = 0.45; m.sssColor = vec3f(0.8, 0.15, 0.1);
      m.nrmDetail = vec3f(0.0, 0.0, fib - 0.5) * 0.3;
    }
    case T_ARTERY: { m.albedo = vec3f(0.75, 0.09, 0.07); m.rough = 0.2; m.sss = 0.4; m.sssColor = vec3f(0.9, 0.1, 0.05); }
    case T_VEIN: { m.albedo = vec3f(0.30, 0.16, 0.38); m.rough = 0.2; m.sss = 0.35; m.sssColor = vec3f(0.5, 0.15, 0.4); }
    case T_NERVE: { m.albedo = vec3f(0.92, 0.88, 0.72); m.rough = 0.3; m.sss = 0.3; m.sssColor = vec3f(0.9, 0.7, 0.5); }
    case T_BLOOD: { m.albedo = vec3f(0.35, 0.02, 0.02); m.rough = 0.05; m.f0 = 0.03; m.sss = 0.0; }
    default: { m.albedo = vec3f(0.5); m.rough = 0.5; m.sss = 0.0; }
  }
  return m;
}

fn applyThermalState(mIn: Mat, st: u32, omega: f32, water: f32, water0: f32, tempC: f32, p: vec3f) -> Mat {
  var m = mIn;
  // continuous blanching with Arrhenius damage: proteins denature -> opaque, whiter, less SSS
  let blanch = smoothstep(0.25, 1.0, omega);
  let whiteish = mix(vec3f(0.88, 0.84, 0.80), vec3f(0.80, 0.78, 0.76), fbm3(p * 2.0, 2));
  m.albedo = mix(m.albedo, whiteish * (0.6 + 0.4 * luminance(m.albedo) * 1.6), blanch * 0.85);
  m.sss = m.sss * (1.0 - 0.8 * blanch);
  m.rough = mix(m.rough, 0.5, blanch * 0.6);
  // desiccation: water loss -> dull tan/brown, shrinks (visual only), matte
  let dry = clamp(1.0 - water / max(water0, 1e-3), 0.0, 1.0);
  m.albedo = mix(m.albedo, vec3f(0.45, 0.33, 0.22), smoothstep(0.4, 0.95, dry) * 0.9);
  m.rough = mix(m.rough, 0.7, smoothstep(0.4, 0.95, dry));
  m.sss = m.sss * (1.0 - dry);
  if (st == S_CARBON) {
    let gloss = fbm3(p * 6.0, 2);
    m.albedo = vec3f(0.025, 0.02, 0.018) * (0.7 + 0.6 * gloss);
    m.rough = 0.45 + 0.35 * gloss; m.f0 = 0.06; m.sss = 0.0;
    m.nrmDetail = vec3f(gloss - 0.5) * 0.6;
  } else if (st == S_DESIC) {
    m.albedo = mix(m.albedo, vec3f(0.40, 0.30, 0.20), 0.6);
    m.rough = 0.75; m.sss = 0.0;
  } else if (st == S_COAG) {
    m.albedo = mix(m.albedo, whiteish, 0.5);
    m.sss *= 0.3;
  }
  // boiling: glinting micro-bubbles at the surface above 100 C
  if (tempC > 98.0) {
    let t = frame.cameraPos.w;
    let bub = smoothstep(0.75, 0.9, vnoise3(p * 12.0 + vec3f(0.0, t * 9.0, 0.0)));
    m.rough = mix(m.rough, 0.05, bub * 0.8);
    m.nrmDetail += vec3f(bub) * 0.4;
  }
  // incandescence of char above ~500 C
  if (tempC > 450.0) {
    let g = smoothstep(450.0, 900.0, tempC);
    m.emissive = vec3f(1.0, 0.35, 0.08) * g * 3.0;
  }
  return m;
}

fn luminance(c: vec3f) -> f32 { return dot(c, vec3f(0.2126, 0.7152, 0.0722)); }

fn waterInitial(ty: u32) -> f32 {
  switch (ty) {
    case T_EPI: { return 0.5; }
    case T_DERMIS: { return 0.65; }
    case T_FAT, T_LIPOMA: { return 0.18; }
    case T_MUSCLE: { return 0.75; }
    case T_ARTERY, T_VEIN, T_BLOOD: { return 0.8; }
    case T_NERVE: { return 0.7; }
    default: { return 0.6; }
  }
}

fn perturbNormal(N: vec3f, p: vec3f, detail: vec3f) -> vec3f {
  // build a tangent frame from world axes and tilt the normal by the detail vector
  var T = normalize(cross(N, vec3f(0.0, 1.0, 0.0)));
  if (length(T) < 0.01) { T = vec3f(1.0, 0.0, 0.0); }
  let B = cross(N, T);
  return normalize(N + T * detail.x + B * detail.z + N * detail.y * 0.2);
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4f {
  let ty = in.cell & 0xffu;
  let st = (in.cell >> 8u) & 0xffu;
  let aux = (in.cell >> 16u) & 0xffu;
  let flags = in.cell & 0xff000000u;
  let p = in.wpos;
  var N = normalize(in.nrm);
  let V = normalize(frame.cameraPos.xyz - p);

  let debug = frame.params1.x;
  if (debug > 0.5) {
    var c = vec3f(0.0);
    if (debug < 1.5) { c = tempColor(in.tempC); }
    else if (debug < 2.5) { c = mix(vec3f(0.1, 0.2, 0.6), vec3f(1.0, 0.95, 0.9), clamp(in.omega, 0.0, 1.0)); }
    else { c = hash33(vec3f(f32(ty) * 7.1, 1.0, 2.0)); }
    let l = 0.4 + 0.6 * max(dot(N, normalize(frame.lightPos.xyz - p)), 0.0);
    return vec4f(c * l, 1.0);
  }

  var m = baseMaterial(ty, flags, aux, p, N);
  m = applyThermalState(m, st, in.omega, in.water, waterInitial(ty), in.tempC, p);
  N = perturbNormal(N, p, m.nrmDetail);
  // blood film / pool on upward-facing surfaces
  let bl = bloodAt(p);
  if (bl.x > 0.005 && N.y > -0.3) {
    let depth = bl.x;
    let film = smoothstep(0.0, 0.25, depth);                 // thin wet film: tints and glosses
    let pool = smoothstep(0.15, 0.6, depth);                 // standing blood: opaque, dark
    let clot = smoothstep(45.0, 70.0, bl.y);
    var bc = mix(vec3f(0.55, 0.06, 0.05), vec3f(0.16, 0.01, 0.01), pool);
    bc = mix(bc, vec3f(0.22, 0.04, 0.03), clot);
    m.albedo = mix(m.albedo, m.albedo * vec3f(0.9, 0.3, 0.25), film * (1.0 - pool) * 0.8);
    m.albedo = mix(m.albedo, bc, pool);
    m.rough = mix(m.rough, mix(0.07, 0.55, clot), max(film * 0.8, pool));
    m.sss = m.sss * (1.0 - pool);
    m.f0 = mix(m.f0, 0.04, pool);
    // pooled blood levels out: flatten the normal
    N = normalize(mix(N, vec3f(0.0, 1.0, 0.0), pool * 0.85));
  }
  if (dot(N, V) < 0.0) { N = normalize(N - V * dot(N, V) * 1.01); }

  let f0 = vec3f(m.f0);
  let wrap = m.sss * 0.6;
  let Ngeo = normalize(in.nrm);
  let px = in.pos.xy;
  var color = vec3f(0.0);
  for (var i = 0; i < 2; i++) {
    let h = lightHead(i, p, Ngeo, px);
    let d = shadeLight(N, V, h.L, m.albedo, m.rough, f0, wrap, domeWiden());
    color += (d.diffuse + d.specular) * h.radiance * h.shadow;
    // translucency: light leaking through thin/soft tissue toward the viewer
    let Lt = normalize(-h.L + N * 0.3);
    let trans = pow(clamp(dot(V, -Lt), 0.0, 1.0), 3.0) * m.sss;
    color += m.sssColor * m.albedo * trans * h.radiance * 0.35 * (0.5 + 0.5 * h.shadow);
  }

  // ambient: cool room and drape bounce from above, warm bounce from the wound below
  let amb = hemiAmbient(N, vec3f(0.50, 0.56, 0.68), vec3f(0.36, 0.17, 0.13)) * frame.lightColor.w;
  color += m.albedo * amb * (1.0 - 0.5 * m.f0);
  // wet sheen: the room and the lamp domes reflected in the moist surface
  let NoV = max(dot(N, V), 0.0);
  let Fa = F_Schlick(f0, NoV);
  color += Fa * envReflection(reflect(-V, N), p, m.rough) * mix(0.6, 0.15, m.rough);

  // arc / electrode point light
  if (frame.arcLight.w > 0.0) {
    let toA = frame.arcLight.xyz - p;
    let dist2 = max(dot(toA, toA), 0.25);
    let La = normalize(toA);
    let a = shadeLight(N, V, La, m.albedo, m.rough, f0, wrap, 0.0);
    color += (a.diffuse + a.specular) * vec3f(0.7, 0.8, 1.0) * frame.arcLight.w / dist2;
  }

  color += m.emissive;
  return vec4f(color, 1.0);
}
