// Simple PBR for drapes and instruments (vertex buffers), with procedural cloth option.

struct Vtx { @location(0) pos: vec3f, @location(1) nrm: vec3f, @location(2) uv: vec2f };
struct Material {
  color: vec4f,      // rgb albedo, a = metallic
  params: vec4f,     // x rough, y kind (0 solid, 1 cloth, 2 towel), z emissive, w unused
  model: mat4x4f,
};

@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(2) var shadowMap: texture_depth_2d;
@group(0) @binding(3) var shadowSampler: sampler_comparison;
@group(1) @binding(0) var<uniform> mat: Material;

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
  return frame.lightViewProj * (mat.model * vec4f(v.pos, 1.0));
}

fn shadowFactor(wpos: vec3f, N: vec3f, L: vec3f) -> f32 {
  let bias = frame.params1.w * (1.0 + 2.0 * (1.0 - max(dot(N, L), 0.0)));
  let lp = frame.lightViewProj * vec4f(wpos + N * bias, 1.0);
  let ndc = lp.xyz / lp.w;
  let uv = vec2f(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
  if (any(uv < vec2f(0.0)) || any(uv > vec2f(1.0))) { return 1.0; }
  let texel = 1.0 / vec2f(textureDimensions(shadowMap));
  var s = 0.0;
  for (var y = -1; y <= 1; y++) { for (var x = -1; x <= 1; x++) {
    s += textureSampleCompareLevel(shadowMap, shadowSampler, uv + vec2f(f32(x), f32(y)) * texel, ndc.z - 0.0005);
  }}
  return s / 9.0;
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4f {
  var N = normalize(in.nrm);
  let V = normalize(frame.cameraPos.xyz - in.wpos);
  var albedo = mat.color.rgb;
  var rough = mat.params.x;
  let metallic = mat.color.a;
  let kind = mat.params.y;
  if (kind > 0.5) {
    // cloth: weave + folds
    let p = in.wpos;
    let weave = 0.5 + 0.5 * sin(p.x * 12.0) * sin(p.z * 12.0);
    let folds = fbm3(p * 0.02, 3);
    let crease = abs(fbm3(p * 0.06 + vec3f(9.0), 3) - 0.5);
    let e = 0.6;
    let foldN = vec3f(fbm3((p + vec3f(e, 0.0, 0.0)) * 0.02, 3) - folds, 0.0, fbm3((p + vec3f(0.0, 0.0, e)) * 0.02, 3) - folds) * 1.5
             + vec3f(abs(fbm3((p + vec3f(e, 0.0, 0.0)) * 0.06 + vec3f(9.0), 3) - 0.5) - crease, 0.0, abs(fbm3((p + vec3f(0.0, 0.0, e)) * 0.06 + vec3f(9.0), 3) - 0.5) - crease) * 2.5;
    N = normalize(N + foldN);
    albedo = albedo * (0.93 + 0.07 * weave) * (0.9 + 0.2 * folds) * (0.85 + 0.3 * smoothstep(0.0, 0.25, crease));
    if (kind > 1.5) { albedo *= (0.9 + 0.2 * fbm3(p * 0.4, 2)); }
    rough = 0.85;
  }
  if (dot(N, V) < 0.0) { N = normalize(N - V * dot(N, V) * 1.01); }
  let L = normalize(frame.lightDir.xyz);
  let lightCol = frame.lightColor.rgb * frame.lightDir.w;
  let sh = shadowFactor(in.wpos, normalize(in.nrm), L);
  let f0 = mix(vec3f(0.04), albedo, metallic);
  let diff = albedo * (1.0 - metallic);
  let d = shadeLight(N, V, L, diff, rough, f0, 0.0);
  var color = (d.diffuse + d.specular) * lightCol * sh;
  let amb = hemiAmbient(N, vec3f(0.55, 0.6, 0.7), vec3f(0.3, 0.25, 0.22)) * frame.lightColor.w;
  color += diff * amb;
  let NoV = max(dot(N, V), 0.0);
  // metals: reflect a soft OR environment (bright ceiling light, dim surround)
  let R = reflect(-V, N);
  let env = mix(vec3f(0.25, 0.27, 0.3), vec3f(1.6, 1.6, 1.5), pow(max(R.y, 0.0), 3.0)) * frame.lightColor.w * 2.0;
  color += F_Schlick(f0, NoV) * env * (1.0 - rough) * (0.4 + 0.6 * metallic);
  if (frame.arcLight.w > 0.0) {
    let toA = frame.arcLight.xyz - in.wpos; let dist2 = max(dot(toA, toA), 0.25);
    let a = shadeLight(N, V, normalize(toA), diff, rough, f0, 0.0);
    color += (a.diffuse + a.specular) * vec3f(0.7, 0.8, 1.0) * frame.arcLight.w / dist2;
  }
  color += albedo * mat.params.z;
  return vec4f(color, 1.0);
}
