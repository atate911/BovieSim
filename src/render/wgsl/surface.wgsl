// Surface extraction: marching tetrahedra over the occupancy field (iso = 0.5).
// Emits a compact triangle soup with per-vertex tissue attributes using an atomic counter.
// VOLUME_INFO is prepended at pipeline creation.

struct Vertex {
  px: f32, py: f32, pz: f32, cell: u32,
  nx: f32, ny: f32, nz: f32, attr: u32,
};
struct Counter { count: atomic<u32>, cap: u32, pad0: u32, pad1: u32 };

@group(0) @binding(0) var<uniform> info: VolumeInfo;
@group(0) @binding(1) var<storage, read> occ: array<f32>;
@group(0) @binding(2) var<storage, read> cells: array<u32>;
@group(0) @binding(3) var<storage, read> temp: array<f32>;
@group(0) @binding(4) var<storage, read> water: array<f32>;
@group(0) @binding(5) var<storage, read> omega: array<f32>;
@group(0) @binding(6) var<storage, read_write> verts: array<Vertex>;
@group(0) @binding(7) var<storage, read_write> counter: Counter;

const ISO: f32 = 0.5;

fn occAt(i: i32, j: i32, k: i32) -> f32 {
  let ci = clamp(i, 0, i32(info.nx) - 1);
  let cj = clamp(j, 0, i32(info.ny) - 1);
  let ck = clamp(k, 0, i32(info.nz) - 1);
  return occ[vidx(info, u32(ci), u32(cj), u32(ck))];
}

fn occSmooth(i: i32, j: i32, k: i32) -> f32 {
  // 7-point smoothing of the occupancy field for normal estimation
  return (occAt(i, j, k) * 2.0 + occAt(i + 1, j, k) + occAt(i - 1, j, k) + occAt(i, j + 1, k) + occAt(i, j - 1, k) + occAt(i, j, k + 1) + occAt(i, j, k - 1)) / 8.0;
}
fn gradAt(i: i32, j: i32, k: i32) -> vec3f {
  // gradient in voxel index space (i, j, k); mapped to world axes later
  return vec3f(occSmooth(i + 1, j, k) - occSmooth(i - 1, j, k),
               occSmooth(i, j + 1, k) - occSmooth(i, j - 1, k),
               occSmooth(i, j, k + 1) - occSmooth(i, j, k - 1)) * 0.5;
}

fn packAttr(t: f32, w: f32, om: f32) -> u32 {
  let tq = u32(clamp(t * 50.0, 0.0, 65535.0));
  let wq = u32(clamp(w, 0.0, 1.0) * 255.0);
  let oq = u32(clamp(om * 0.5, 0.0, 1.0) * 255.0);
  return tq | (wq << 16u) | (oq << 24u);
}

struct Corner { p: vec3f, f: f32, g: vec3f, idx: u32 };

var<private> corners: array<Corner, 8>;

fn edgeVertex(a: u32, b: u32) -> Vertex {
  let ca = corners[a];
  let cb = corners[b];
  let t = clamp((ISO - ca.f) / (cb.f - ca.f), 0.0, 1.0);
  let p = mix(ca.p, cb.p, t);
  let g = mix(ca.g, cb.g, t);
  // index-space gradient -> world axes: i->x, j->z, k->y; surface normal points toward air (-grad)
  let n = normalize(-vec3f(g.x, g.z, g.y) + vec3f(1e-6, 0.0, 0.0));
  let solid = select(cb.idx, ca.idx, ca.f >= ISO);
  let w = voxelToWorld(info, p);
  var v: Vertex;
  v.px = w.x; v.py = w.y; v.pz = w.z;
  v.cell = cells[solid];
  v.nx = n.x; v.ny = n.y; v.nz = n.z;
  v.attr = packAttr(temp[solid], water[solid], omega[solid]);
  return v;
}

fn emitTri(a: Vertex, b: Vertex, c: Vertex) {
  var v0 = a; var v1 = b; var v2 = c;
  // consistent winding: geometric normal must agree with the field normal
  let pa = vec3f(v0.px, v0.py, v0.pz);
  let pb = vec3f(v1.px, v1.py, v1.pz);
  let pc = vec3f(v2.px, v2.py, v2.pz);
  let gn = cross(pb - pa, pc - pa);
  let fn_ = vec3f(v0.nx, v0.ny, v0.nz) + vec3f(v1.nx, v1.ny, v1.nz) + vec3f(v2.nx, v2.ny, v2.nz);
  if (dot(gn, fn_) < 0.0) { let tmp = v1; v1 = v2; v2 = tmp; }
  let base = atomicAdd(&counter.count, 3u);
  if (base + 3u <= counter.cap) {
    verts[base] = v0; verts[base + 1u] = v1; verts[base + 2u] = v2;
  }
}

fn doTet(a: u32, b: u32, c: u32, d: u32) {
  let ia = corners[a].f >= ISO; let ib = corners[b].f >= ISO;
  let ic = corners[c].f >= ISO; let id = corners[d].f >= ISO;
  let n = u32(ia) + u32(ib) + u32(ic) + u32(id);
  if (n == 0u || n == 4u) { return; }
  var ins = array<u32, 4>(0u, 0u, 0u, 0u);
  var outs = array<u32, 4>(0u, 0u, 0u, 0u);
  var ni = 0u; var no = 0u;
  if (ia) { ins[ni] = a; ni++; } else { outs[no] = a; no++; }
  if (ib) { ins[ni] = b; ni++; } else { outs[no] = b; no++; }
  if (ic) { ins[ni] = c; ni++; } else { outs[no] = c; no++; }
  if (id) { ins[ni] = d; ni++; } else { outs[no] = d; no++; }
  if (n == 1u) {
    emitTri(edgeVertex(ins[0], outs[0]), edgeVertex(ins[0], outs[1]), edgeVertex(ins[0], outs[2]));
  } else if (n == 3u) {
    emitTri(edgeVertex(outs[0], ins[0]), edgeVertex(outs[0], ins[1]), edgeVertex(outs[0], ins[2]));
  } else {
    let v00 = edgeVertex(ins[0], outs[0]);
    let v01 = edgeVertex(ins[0], outs[1]);
    let v11 = edgeVertex(ins[1], outs[1]);
    let v10 = edgeVertex(ins[1], outs[0]);
    emitTri(v00, v01, v11);
    emitTri(v00, v11, v10);
  }
}

@compute @workgroup_size(4, 4, 4)
fn main(@builtin(global_invocation_id) gid: vec3u) {
  if (gid.x + 1u >= info.nx || gid.y + 1u >= info.ny || gid.z + 1u >= info.nz) { return; }
  let i = i32(gid.x); let j = i32(gid.y); let k = i32(gid.z);
  // corner offsets (di, dj, dk): 0:(0,0,0) 1:(1,0,0) 2:(1,1,0) 3:(0,1,0) 4:(0,0,1) 5:(1,0,1) 6:(1,1,1) 7:(0,1,1)
  let offs = array<vec3i, 8>(vec3i(0,0,0), vec3i(1,0,0), vec3i(1,1,0), vec3i(0,1,0), vec3i(0,0,1), vec3i(1,0,1), vec3i(1,1,1), vec3i(0,1,1));
  var allIn = true; var allOut = true;
  for (var c = 0u; c < 8u; c++) {
    let o = offs[c];
    let ii = i + o.x; let jj = j + o.y; let kk = k + o.z;
    let idx = vidx(info, u32(ii), u32(jj), u32(kk));
    let f = occ[idx];
    corners[c].f = f;
    corners[c].idx = idx;
    corners[c].p = vec3f(f32(ii), f32(jj), f32(kk));
    allIn = allIn && (f >= ISO);
    allOut = allOut && (f < ISO);
  }
  if (allIn || allOut) { return; }
  for (var c = 0u; c < 8u; c++) {
    let o = offs[c];
    corners[c].g = gradAt(i + o.x, j + o.y, k + o.z);
  }
  doTet(0u, 5u, 1u, 6u);
  doTet(0u, 1u, 2u, 6u);
  doTet(0u, 2u, 3u, 6u);
  doTet(0u, 3u, 7u, 6u);
  doTet(0u, 7u, 4u, 6u);
  doTet(0u, 4u, 5u, 6u);
}

@compute @workgroup_size(1)
fn finalize(@builtin(global_invocation_id) gid: vec3u) {
  // indirect draw args live in the counter buffer's tail region written by TS; here we clamp count
  let c = atomicLoad(&counter.count);
  if (c > counter.cap) { atomicStore(&counter.count, counter.cap); }
}
