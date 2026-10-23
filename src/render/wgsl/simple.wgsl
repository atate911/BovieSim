// PBR for drapes and instruments (vertex buffers) under the surgical light rig. Materials carry optional
// texture maps (albedo, normal, roughness: CC0 sets or the user's own, see docs/textures.md) sampled
// triplanar in the mesh's local space; without them, procedural kinds: 1 blue nonwoven drape, 2 absorbent
// reinforcement pad, 3 incise film, and brushed satin steel for metals.

struct Vtx { @location(0) pos: vec3f, @location(1) nrm: vec3f, @location(2) uv: vec2f };
struct Material {
  color: vec4f,      // rgb albedo or texture tint (linear), a = metallic
  params: vec4f,     // x rough, y kind (0 solid, 1 drape, 2 pad, 3 film), z emissive, w texture flags (1 albedo, 2 normal, 4 roughness)
  tex: vec4f,        // x tile size (mm), y normal strength, z roughness scale, w unused
  model: mat4x4f,
  invModel: mat4x4f,
};

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(2) var shadowMapA: texture_depth_2d;
@group(0) @binding(3) var shadowSampler: sampler_comparison;
@group(0) @binding(8) var shadowMapB: texture_depth_2d;
@group(1) @binding(0) var<uniform> mat: Material;
@group(1) @binding(1) var texAlbedo: texture_2d<f32>;
@group(1) @binding(2) var texNormal: texture_2d<f32>;
@group(1) @binding(3) var texRough: texture_2d<f32>;
@group(1) @binding(4) var texSampler: sampler;
struct ShadowSel { head: u32, pad0: u32, pad1: u32, pad2: u32 };
@group(0) @binding(9) var<uniform> shadowSel: ShadowSel;

struct VSOut { @builtin(position) pos: vec4f, @location(0) wpos: vec3f, @location(1) nrm: vec3f, @location(2) uv: vec2f };

@vertex
fn vs_main(v: Vtx) -> VSOut {
  var o: VSOut;
  let wp = mat.model * vec4f(v.pos, 1.0);
  o.wpos = wp.xyz;
  o.pos = frame.viewProj * wp;
  o.nrm = normalize((mat.model * vec4f(v.nrm, 0.0)).xyz);
  o.uv = v.uv;
  return o;
}

@vertex
fn vs_shadow(v: Vtx) -> @builtin(position) vec4f {
  let wp = mat.model * vec4f(v.pos, 1.0);
  if (shadowSel.head == 1u) { return frame.light2ViewProj * wp; }
  return frame.lightViewProj * wp;
}

/** Small wrinkle normals for cloth and film (the geometry carries the large folds). */
fn wrinkleNormal(p: vec3f, scale: f32, amp: f32) -> vec3f {
  let e = 0.4;
  let h0 = fbm3(p * scale, 3);
  let hx = fbm3((p + vec3f(e, 0.0, 0.0)) * scale, 3);
  let hz = fbm3((p + vec3f(0.0, 0.0, e)) * scale, 3);
  return vec3f((hx - h0) / e, 0.0, (hz - h0) / e) * amp;
}

fn triWeights(n: vec3f) -> vec3f { let w = pow(abs(n), vec3f(4.0)); return w / (w.x + w.y + w.z); }
fn triSample(t: texture_2d<f32>, p: vec3f, w: vec3f) -> vec4f {
  return textureSample(t, texSampler, p.zy) * w.x + textureSample(t, texSampler, p.xz) * w.y + textureSample(t, texSampler, p.xy) * w.z;
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4f {
  let Ngeo = normalize(in.nrm);
  var N = Ngeo;
  let V = normalize(frame.cameraPos.xyz - in.wpos);
  var albedo = mat.color.rgb;
  var rough = mat.params.x;
  let metallic = mat.color.a;
  let kind = mat.params.y;
  let flags = u32(mat.params.w);
  var sheen = 0.0;
  var f0 = mix(vec3f(0.04), albedo, metallic);
  let p = in.wpos;
  // local frame of the mesh (instruments move; drapes are at the origin)
  let lp = (mat.invModel * vec4f(p, 1.0)).xyz;
  let ln = normalize((mat.invModel * vec4f(Ngeo, 0.0)).xyz);
  let axis = normalize(mat.model[1].xyz);

  if (flags != 0u) {
    // texture maps, triplanar in local millimetres
    let w = triWeights(ln);
    let q = lp / mat.tex.x;
    if ((flags & 1u) != 0u) { albedo = albedo * triSample(texAlbedo, q, w).rgb; }
    if ((flags & 4u) != 0u) { rough = clamp(triSample(texRough, q, w).r * mat.tex.z, 0.04, 1.0); }
    if ((flags & 2u) != 0u) {
      let tx = textureSample(texNormal, texSampler, q.zy).xyz * 2.0 - 1.0;
      let ty = textureSample(texNormal, texSampler, q.xz).xyz * 2.0 - 1.0;
      let tz = textureSample(texNormal, texSampler, q.xy).xyz * 2.0 - 1.0;
      let pert = vec3f(0.0, tx.y, tx.x) * w.x + vec3f(ty.x, 0.0, ty.y) * w.y + vec3f(tz.x, tz.y, 0.0) * w.z;
      N = normalize(N + (mat.model * vec4f(pert * mat.tex.y, 0.0)).xyz);
    }
    f0 = mix(vec3f(0.04), albedo, metallic);
    if (kind > 0.5 && kind < 2.5) {
      // cloth tiles are often painted flat: keep a little procedural fibre mottling and the sheen on top of them
      let fib = fbm3(vec3f(lp.x * 1.6, lp.z * 0.35, 3.0), 3);
      let fib2 = fbm3(lp * 0.5, 3);
      albedo = albedo * (0.9 + 0.2 * fib) * (0.94 + 0.12 * fib2);
      sheen = 0.35;
    }
  } else if (kind > 0.5 && kind < 1.5) {
    // SMS nonwoven drape: fibre streaks at three scales, small embossed bonding dots on a 1.6 mm grid, soft wrinkles, velvet sheen
    let fib = fbm3(vec3f(lp.x * 1.6, lp.z * 0.35, 3.0), 3);
    let fib2 = fbm3(lp * 0.5, 3);
    let fib3 = fbm3(vec3f(lp.x * 4.0, lp.z * 1.2, 9.0), 2);
    let g = fract(vec2f(lp.x, lp.z) / 1.6) - 0.5;
    let bond = 1.0 - smoothstep(0.10, 0.20, length(g));
    albedo = albedo * (0.84 + 0.32 * fib) * (0.92 + 0.16 * fib2) * (0.9 + 0.2 * fib3) * (1.0 - 0.08 * bond);
    N = normalize(N + wrinkleNormal(lp, 0.11, 0.9) + vec3f(g.x, 0.0, g.y) * bond * 0.25 + vec3f(fib3 - 0.5, 0.0, fib - 0.5) * 0.12);
    rough = 0.9 - 0.1 * bond; sheen = 0.35;
  } else if (kind > 1.5 && kind < 2.5) {
    // absorbent reinforcement: quilted fleece dimples on a 6 mm cell, matte, lighter
    let q = worley3(vec3f(lp.x, 0.0, lp.z) * 0.16);
    let dimple = smoothstep(0.1, 0.6, q.x);
    let fib3 = fbm3(vec3f(lp.x * 3.0, lp.z * 1.0, 9.0), 2);
    albedo = albedo * (0.9 + 0.2 * fbm3(lp * 0.5, 3)) * (0.9 + 0.16 * dimple) * (0.92 + 0.16 * fib3);
    let e = 0.6;
    let dq = vec2f(worley3(vec3f(lp.x + e, 0.0, lp.z) * 0.16).x - q.x, worley3(vec3f(lp.x, 0.0, lp.z + e) * 0.16).x - q.x) / e;
    N = normalize(N + wrinkleNormal(lp, 0.2, 0.5) + vec3f(dq.x, 0.0, dq.y) * 0.35);
    rough = 0.96; sheen = 0.5;
  } else if (kind > 2.5) {
    // incise film: thin glossy plastic with fine creases
    N = normalize(N + wrinkleNormal(lp, 0.33, 0.45));
    rough = 0.2; f0 = vec3f(0.045);
  } else if (metallic > 0.5) {
    // satin-finished surgical steel: fine brush lines along the instrument axis, roughness that varies a little
    let rel = p - mat.model[3].xyz;
    let along = dot(rel, axis);
    let T0 = normalize(cross(axis, select(vec3f(0.0, 0.0, 1.0), vec3f(1.0, 0.0, 0.0), abs(axis.z) > 0.9)));
    let B0 = cross(axis, T0);
    let ang = atan2(dot(Ngeo, B0), dot(Ngeo, T0));
    let streak = fbm3(vec3f(along * 0.12, ang * 9.0, 7.0), 3) - 0.5;
    let around = cross(axis, Ngeo);
    if (length(around) > 0.05) { N = normalize(N + normalize(around) * streak * 0.5); }
    rough = clamp(rough + 0.12 * (fbm3(p * 2.0, 2) - 0.5), 0.2, 0.75);
  }
  if (dot(N, V) < 0.0) { N = normalize(N - V * dot(N, V) * 1.01); }
  let diff = albedo * (1.0 - metallic);
  let px = in.pos.xy;
  var color = vec3f(0.0);
  for (var i = 0; i < 2; i++) {
    let h = lightHead(i, in.wpos, Ngeo, px);
    let d = shadeLight(N, V, h.L, diff, rough, f0, 0.0, domeWiden());
    let NoL = max(dot(N, h.L), 0.0);
    color += (d.diffuse + d.specular) * h.radiance * h.shadow;
    // cloth sheen: fibres catch the light at grazing view angles
    color += (albedo * 0.6 + 0.1) * sheen * pow(1.0 - max(dot(N, V), 0.0), 3.5) * NoL * h.radiance * h.shadow * 0.4;
  }
  // ambient: cool room above, blue drape bounce from around
  let amb = hemiAmbient(N, vec3f(0.50, 0.56, 0.68), vec3f(0.18, 0.24, 0.34)) * frame.lightColor.w;
  color += diff * amb;
  let NoV = max(dot(N, V), 0.0);
  let R = reflect(-V, N);
  color += F_Schlick(f0, NoV) * envReflection(R, in.wpos, rough) * mix(1.0, 0.7, rough) * (0.4 + 0.6 * metallic);
  if (frame.arcLight.w > 0.0) {
    let toA = frame.arcLight.xyz - in.wpos; let dist2 = max(dot(toA, toA), 0.25);
    let a = shadeLight(N, V, normalize(toA), diff, rough, f0, 0.0, 0.0);
    color += (a.diffuse + a.specular) * vec3f(0.7, 0.8, 1.0) * frame.arcLight.w / dist2;
  }
  color += albedo * mat.params.z;
  return vec4f(color, 1.0);
}
