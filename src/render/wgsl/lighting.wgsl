// Surgical light rig shared by the tissue and instrument/drape shaders: two overhead dome heads with
// percentage-closer soft shadows (sharp at contact, wide penumbra away from it, as under a large light
// dome), a widened specular lobe for the dome's angular size, and the reflected lamp for metals.
// Expects the including shader to declare `frame`, `shadowMapA`, `shadowMapB` and `shadowSampler`.

// Vogel disc: evenly spread taps that rotate per pixel into fine grain rather than banding
fn vogel(i: i32, n: i32, rot: f32) -> vec2f {
  let r = sqrt((f32(i) + 0.5) / f32(n));
  let th = f32(i) * 2.3999632 + rot;
  return vec2f(cos(th), sin(th)) * r;
}

// interleaved gradient noise: a stable per-pixel rotation of the shadow kernel
fn ign(px: vec2f) -> f32 { return fract(52.9829189 * fract(dot(px, vec2f(0.06711056, 0.00583715)))); }

fn pcss(map: texture_depth_2d, cmp: sampler_comparison, lvp: mat4x4f, wpos: vec3f, N: vec3f, L: vec3f, texelMm: f32, px: vec2f) -> f32 {
  let bias = frame.params1.w * (1.0 + 2.0 * (1.0 - max(dot(N, L), 0.0)));
  let lp = lvp * vec4f(wpos + N * bias, 1.0);
  let ndc = lp.xyz / lp.w;
  let uv = vec2f(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
  if (any(uv < vec2f(0.0)) || any(uv > vec2f(1.0))) { return 1.0; }
  let dims = vec2f(textureDimensions(map));
  let z = ndc.z - 0.0005;
  let rot = ign(px) * 6.2831853;
  // blocker search over the widest penumbra the dome makes at the field (~4 mm)
  let searchTex = 4.0 / texelMm;
  var blockers = 0.0; var zsum = 0.0;
  for (var i = 0; i < 12; i++) {
    let c = vec2i(clamp(uv * dims + vogel(i, 12, rot) * searchTex, vec2f(0.0), dims - vec2f(1.0)));
    let d = textureLoad(map, c, 0);
    if (d < z) { blockers += 1.0; zsum += d; }
  }
  if (blockers < 0.5) { return 1.0; }
  // penumbra width = blocker-to-receiver distance x tan(dome angular radius)
  let penMm = (z - zsum / blockers) * frame.params2.w * frame.params2.x;
  let radius = clamp(penMm / texelMm, 1.0, searchTex);
  var s = 0.0;
  for (var i = 0; i < 24; i++) {
    s += textureSampleCompareLevel(map, cmp, uv + vogel(i, 24, rot) * radius / dims, z);
  }
  return s / 24.0;
}

struct Head { L: vec3f, radiance: vec3f, shadow: f32 };

/** One light head (0 = A, 1 = B) seen from a surface point; Ngeo is the unperturbed normal for the shadow bias. */
fn lightHead(idx: i32, wpos: vec3f, Ngeo: vec3f, px: vec2f) -> Head {
  var h: Head;
  if (idx == 0) {
    h.L = normalize(frame.lightPos.xyz - wpos);
    h.radiance = frame.lightColor.rgb * frame.lightPos.w;
    h.shadow = pcss(shadowMapA, shadowSampler, frame.lightViewProj, wpos, Ngeo, h.L, frame.params2.y, px);
  } else {
    h.L = normalize(frame.light2Pos.xyz - wpos);
    h.radiance = frame.lightColor.rgb * frame.light2Pos.w;
    h.shadow = pcss(shadowMapB, shadowSampler, frame.light2ViewProj, wpos, Ngeo, h.L, frame.params2.z, px);
  }
  return h;
}

/** Specular widening for the dome's angular size (Karis sphere-light approximation). */
fn domeWiden() -> f32 { return frame.params2.x * 0.5; }

/**
 * Surroundings reflected in a glossy surface. Below the horizon the lit field itself (blue drape and pink
 * tissue inside the ~100 klux pool, as bright as the skin); above, the dim theatre ceiling. The two lamp domes
 * (~5 x the field's radiance) are seen through a lobe that widens with roughness, so satin instruments show a
 * broad silver glow rather than a pin-point reflection; the lobe keeps the dome's energy as it widens.
 */
fn envReflection(R: vec3f, wpos: vec3f, rough: f32) -> vec3f {
  let k = frame.lightColor.w / 0.22;
  let up = smoothstep(-0.3, 0.5, R.y);
  var env = mix(vec3f(0.30, 0.38, 0.50), vec3f(0.14, 0.15, 0.18), up) * k;
  let sigma0 = atan(frame.params2.x);
  let sigma = sigma0 + rough * rough * 2.4;
  let keep = (sigma0 * sigma0) / (sigma * sigma);
  let thA = acos(clamp(dot(R, normalize(frame.lightPos.xyz - wpos)), -1.0, 1.0));
  let thB = acos(clamp(dot(R, normalize(frame.light2Pos.xyz - wpos)), -1.0, 1.0));
  let lamps = exp(-(thA * thA) / (sigma * sigma)) * frame.lightPos.w + exp(-(thB * thB) / (sigma * sigma)) * frame.light2Pos.w;
  return env + frame.lightColor.rgb * lamps * 2.2 * keep;
}
