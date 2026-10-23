// Camera-facing billboards for steam, smoke and arcs (instanced).
struct Particle { pos: vec3f, size: f32, color: vec3f, alpha: f32, dir: vec3f, kind: f32 };
@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var<storage, read> parts: array<Particle>;

struct VSOut { @builtin(position) pos: vec4f, @location(0) uv: vec2f, @location(1) color: vec3f, @location(2) alpha: f32, @location(3) kind: f32 };

@vertex
fn vs_main(@builtin(vertex_index) vi: u32, @builtin(instance_index) ii: u32) -> VSOut {
  let p = parts[ii];
  let corners = array<vec2f, 6>(vec2f(-1,-1), vec2f(1,-1), vec2f(1,1), vec2f(-1,-1), vec2f(1,1), vec2f(-1,1));
  let c = corners[vi];
  // camera basis from the view matrix rows
  let right = vec3f(frame.view[0][0], frame.view[1][0], frame.view[2][0]);
  let up = vec3f(frame.view[0][1], frame.view[1][1], frame.view[2][1]);
  var wp: vec3f;
  if (p.kind > 1.5) {
    // arc: thin quad along dir, width 0.25 mm, length size
    let along = normalize(p.dir);
    let view = normalize(frame.cameraPos.xyz - p.pos);
    let side = normalize(cross(along, view));
    wp = p.pos + along * (c.y * 0.5 + 0.5) * p.size + side * c.x * 0.18;
  } else {
    wp = p.pos + (right * c.x + up * c.y) * p.size;
  }
  var o: VSOut;
  o.pos = frame.viewProj * vec4f(wp, 1.0);
  o.uv = c; o.color = p.color; o.alpha = p.alpha; o.kind = p.kind;
  return o;
}

@fragment
fn fs_main(in: VSOut) -> @location(0) vec4f {
  if (in.kind > 1.5) {
    let core = 1.0 - smoothstep(0.0, 1.0, abs(in.uv.x));
    let flick = 0.7 + 0.3 * hash13(vec3f(in.uv.y * 40.0, frame.cameraPos.w * 60.0, 1.0));
    let c = in.color * core * core * in.alpha * flick * 6.0;
    return vec4f(c, 0.0); // additive
  }
  let r = length(in.uv);
  if (in.kind > 2.5) {
    // blood droplet: solid glossy bead
    let disc = 1.0 - smoothstep(0.75, 1.0, r);
    if (disc <= 0.0) { discard; }
    let nz = sqrt(max(1.0 - r * r, 0.0));
    let nrm = normalize(vec3f(in.uv.x, in.uv.y, nz));
    let lit = in.color * (0.35 + 0.65 * max(nrm.y * 0.6 + nz * 0.6, 0.0)) * frame.lightColor.rgb + vec3f(0.6) * pow(max(nz, 0.0), 30.0) * 0.5;
    return vec4f(lit * disc, disc);
  }
  let soft = 1.0 - smoothstep(0.35, 1.0, r);
  let n = fbm3(vec3f(in.uv * 2.5, frame.cameraPos.w * 0.4 + in.alpha * 7.0), 2);
  let a = soft * in.alpha * (0.55 + 0.6 * n);
  // front lighting from the surgical light
  let lit = in.color * (0.65 + 0.35 * max(normalize(frame.lightPos.xyz).y, 0.0)) * frame.lightColor.rgb;
  return vec4f(lit * a, a); // premultiplied
}
