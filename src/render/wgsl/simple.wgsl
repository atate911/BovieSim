// PBR for drapes and instruments (vertex buffers) under the surgical light rig, with procedural
// fabric kinds: 1 blue nonwoven drape, 2 absorbent reinforcement pad, 3 incise film.

struct Vtx { @location(0) pos: vec3f, @location(1) nrm: vec3f, @location(2) uv: vec2f };
struct Material {
  color: vec4f,      // rgb albedo (linear), a = metallic
  params: vec4f,     // x rough, y kind (0 solid, 1 drape, 2 pad, 3 film), z emissive, w unused
  model: mat4x4f,
};

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(2) var shadowMapA: texture_depth_2d;
@group(0) @binding(3) var shadowSampler: sampler_comparison;
@group(0) @binding(8) var shadowMapB: texture_depth_2d;
@group(1) @binding(0) var<uniform> mat: Material;
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

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4f {
  let Ngeo = normalize(in.nrm);
  var N = Ngeo;
  let V = normalize(frame.cameraPos.xyz - in.wpos);
  var albedo = mat.color.rgb;
  var rough = mat.params.x;
  let metallic = mat.color.a;
  let kind = mat.params.y;
  var sheen = 0.0;
  var f0 = mix(vec3f(0.04), albedo, metallic);
  let p = in.wpos;
  if (kind > 0.5 && kind < 1.5) {
    // SMS nonwoven drape: embossed bonding dots on a 1.3 mm pitch, fibrous mottling, soft wrinkles, velvet sheen
    let w = worley3(vec3f(p.x, 0.0, p.z) * 0.77 + vec3f(0.5));
    let bond = 1.0 - smoothstep(0.22, 0.34, w.x);
    let mottle = 0.9 + 0.2 * fbm3(p * 0.9, 3);
    albedo = albedo * mottle * (1.0 - 0.10 * bond);
    N = normalize(N + wrinkleNormal(p, 0.11, 0.9));
    rough = 0.92; sheen = 0.35;
  } else if (kind > 1.5 && kind < 2.5) {
    // absorbent reinforcement: quilted fleece, matte, lighter
    let q = worley3(vec3f(p.x, 0.0, p.z) * 0.16);
    let dimple = smoothstep(0.1, 0.6, q.x);
    albedo = albedo * (0.92 + 0.16 * fbm3(p * 0.5, 3)) * (0.94 + 0.08 * dimple);
    N = normalize(N + wrinkleNormal(p, 0.2, 0.35));
    rough = 0.96; sheen = 0.5;
  } else if (kind > 2.5) {
    // incise film: thin glossy plastic with fine creases
    N = normalize(N + wrinkleNormal(p, 0.33, 0.45));
    rough = 0.2; f0 = vec3f(0.045);
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
  color += F_Schlick(f0, NoV) * envReflection(R, in.wpos) * (1.0 - rough) * (0.4 + 0.6 * metallic);
  if (frame.arcLight.w > 0.0) {
    let toA = frame.arcLight.xyz - in.wpos; let dist2 = max(dot(toA, toA), 0.25);
    let a = shadeLight(N, V, normalize(toA), diff, rough, f0, 0.0, 0.0);
    color += (a.diffuse + a.specular) * vec3f(0.7, 0.8, 1.0) * frame.arcLight.w / dist2;
  }
  color += albedo * mat.params.z;
  return vec4f(color, 1.0);
}
