// Electrothermal, bleeding and mechanics kernels. VOLUME_INFO is prepended at pipeline creation.
//
//  jacobi      : quasi-static potential  div(sigma grad phi) = 0 on a subdomain, unit electrode voltage
//  heating     : q_unit = sigma |grad phi|^2 (W/m^3 at 1 V) and the total unit power (-> impedance)
//  generator   : FT10 output curve -> delivered power and V_rms^2
//  bioheat     : Pennes step with phase change, Arrhenius damage, state transitions, vaporisation
//  blood*      : exposed vessel ends and raw surfaces bleed into a per-column blood layer that flows, soaks in,
//                clots with time and heat, boils under the electrode, and is lifted by suction or gauze
//  dissect     : blunt dissection: a spreading or pushing instrument separates low-cohesion planes
//  tearCarve   : links torn by the mechanics lattice carve the tissue they ran through
//  probe       : samples the voxel state along lattice links (severed, damage, dryness, char)
//  pick, slice : cursor hit point and the cross-section inspector

struct SimParams {
  box0: vec4i,     // subdomain origin i,j,k and size S
  tbox0: vec4i,    // thermal box origin
  tbox1: vec4i,    // thermal box size
  tip: vec4f,      // electrode tip (mm), w = reach (mm) for the contact set
  axis: vec4f,     // unit axis tip->handle, w = electrode length (mm)
  wdir: vec4f,     // blade width direction, w = half width (mm)
  ndir: vec4f,     // blade normal direction, w = half thickness (mm)
  gen: vec4f,      // x setting W, y zLo, z zHi, w vMaxRms
  gen2: vec4f,     // x vapFactor, y active (0/1), z electrode type (0 blade 1 needle 2 ball 3 forceps), w sigmaScale
  nf: vec4f,       // x near-field fraction (unresolved dissipation deposited in wet contact voxels), yzw 0
  dt: vec4f,       // x dt (s), y time (s), z heatLimitK, w contact tolerance (mm)
  ray: vec4f,      // pick ray origin (mm)
  rayDir: vec4f,   // pick ray direction, w = max distance
  sliceO: vec4f,   // slice plane origin (mm, top centre), w = width (mm)
  sliceU: vec4f,   // slice horizontal unit axis, w = pixel width
  sliceV: vec4f,   // slice vertical unit axis (downward), w = pixel height
  sliceParams: vec4f, // x depth (mm), y mode, z 0, w 0
  wbox0: vec4i,    // wound box origin (bleeding scan), w = substeps this frame
  wbox1: vec4i,    // wound box size
  tool2: vec4f,    // secondary tool point A (mm), w = radius (mm)
  tool2b: vec4f,   // secondary tool point B (mm), w = mode (0 none, 1 grasp, 2 suction, 3 gauze, 4 spread, 5 push)
  tool3: vec4f,    // secondary tool velocity (mm/s), w = pressure 0..1
  dis: vec4f,      // x blunt dissection cohesion threshold, y carve radius (mm), z gauze absorbs (1) or is saturated (0), w 0
};

struct SimState {
  pSumFixed: atomic<u32>,   // sum of q_unit * dV, fixed point 1e8
  contactCount: atomic<u32>,
  vapFixed: atomic<u32>,    // vaporised volume, fixed point (voxels * 1000)
  wetSumFixed: atomic<u32>, // sum over contact voxels of water fraction (x1000): near-field weighting
  z: f32, p: f32, v2: f32, iRms: f32,
  hit: vec4f,               // xyz hit point mm, w = 1 if hit
  hitCell: u32, hitIdx: u32, pad1: u32, pad2: u32,
  maxTemp: atomic<u32>,     // max temperature * 100 in the thermal box
  carbonCount: atomic<u32>,
  coagCount: atomic<u32>,
  muscleHeat: atomic<u32>,  // heated muscle voxels (T > 45) in the thermal box this step
  bloodLostFixed: atomic<u32>, // cumulative blood lost, microlitres
  exposedArt: atomic<u32>,  // exposed bleeding arterial voxels
  exposedVen: atomic<u32>,
  sealedCount: atomic<u32>,
  charPlugCount: atomic<u32>,
  rebleedCount: atomic<u32>,
  poolVolFixed: atomic<u32>, // pooled blood, nanolitres
  poolAtTipFixed: atomic<u32>, // blood depth under the electrode tip, micrometres
  bleedN: atomic<u32>, pad4: u32, pad5: u32, pad6: u32,
  bleedPts: array<vec4f, 8>,   // exposed bleeding voxels this frame: xyz (mm), w = ml/s (arterial > 0, venous < 0)
  spatterN: atomic<u32>,    // boiling blood droplets thrown this frame
  boilCols: atomic<u32>,    // blood columns boiling this frame
  oozeVoxels: atomic<u32>,  // raw-surface voxels oozing this frame
  clotVolFixed: atomic<u32>, // clotted blood in the field, nanolitres
  dissected: atomic<u32>,   // voxels separated by blunt dissection this frame
  torn: atomic<u32>,        // voxels carved by mechanical tears this frame
  gauzeSoakFixed: atomic<u32>, // blood absorbed by the gauze this frame, nanolitres
  visceraHeat: atomic<u32>, // viscera voxels above 45 C this step
  visceraVapFixed: atomic<u32>, // cumulative vaporised viscera (voxels * 1000)
  oozeUlFixed: atomic<u32>,  // cumulative capillary ooze, microlitres
  pad7: u32, pad8: u32,
  spatterPts: array<vec4f, 4>, // boiling spatter sources: xyz (mm), w = microlitres
  pad9: vec4u,
};

struct TissueProp { sigma: f32, k: f32, rho: f32, c: f32, wb: f32, water: f32, eaR: f32, lnA: f32, cohesion: f32, ooze: f32, tear: f32, pad: f32 };
struct PropTable { t: array<TissueProp, 17> };

@group(0) @binding(0) var<uniform> info: VolumeInfo;
@group(0) @binding(1) var<uniform> sim: SimParams;
@group(0) @binding(2) var<uniform> props: PropTable;
@group(0) @binding(3) var<storage, read_write> state: SimState;
@group(0) @binding(4) var<storage, read_write> cells: array<u32>;
@group(0) @binding(5) var<storage, read_write> occ: array<f32>;
@group(0) @binding(6) var<storage, read_write> water: array<f32>;
@group(0) @binding(7) var<storage, read_write> omega: array<f32>;
@group(0) @binding(8) var<storage, read_write> heat: array<f32>;
// ping-pong pairs
@group(1) @binding(0) var<storage, read> srcA: array<f32>;      // potential (jacobi) or temp (bioheat)
@group(1) @binding(1) var<storage, read_write> dstA: array<f32>;

const T_AIR = 0u; const T_EPI = 1u; const T_FAT = 3u; const T_LIPOMA = 11u; const T_ARTERY = 8u; const T_VEIN = 9u; const T_NERVE = 10u; const T_MUSCLE = 7u; const T_BLOOD = 13u; const T_VISCERA = 15u;
const FLAG_COAPTED = 1u << 26u; const FLAG_SEALED = 1u << 27u; const FLAG_CHARPLUG = 1u << 28u; const FLAG_BLOODCOVER = 1u << 29u; const FLAG_REBLED = 1u << 30u; const FLAG_CLOTCOVER = 1u << 31u;
const SIGMA_BLOOD: f32 = 0.7;
/** ml/s from a fully transected vessel of radius r (mm), arterial, mean over the pulse */
fn vesselFlowMlS(rMm: f32) -> f32 { return 0.25 * rMm * rMm * rMm; }
const S_NORMAL = 0u; const S_COAG = 1u; const S_DESIC = 2u; const S_CARBON = 3u; const S_VAPOR = 4u; const S_DISSECT = 5u;
const BLOOD_RHO_C: f32 = 1050.0 * 3617.0;
const L_VAP: f32 = 2.26e6;
const T_BOIL: f32 = 100.0;
const T_CARBON: f32 = 220.0;
const CLOCK_MAX = 31u;
/** share of a bleeding end's output that leaves as droplets (the CPU particle system lands them back into the layer) */
const ART_JET_FRACTION: f32 = 0.7;
const VEN_JET_FRACTION: f32 = 0.15;

fn hash13(p: vec3f) -> f32 {
  var q = fract(p * vec3f(0.1031, 0.1030, 0.0973));
  q += dot(q, q.yzx + 33.33);
  return fract((q.x + q.y) * q.z);
}

fn inVolume(i: i32, j: i32, k: i32) -> bool {
  return i >= 0 && j >= 0 && k >= 0 && i < i32(info.nx) && j < i32(info.ny) && k < i32(info.nz);
}
fn idx3(i: i32, j: i32, k: i32) -> u32 { return vidx(info, u32(i), u32(j), u32(k)); }
fn worldOf(i: i32, j: i32, k: i32) -> vec3f { return voxelToWorld(info, vec3f(f32(i), f32(j), f32(k))); }
fn voxelOfWorld(p: vec3f) -> vec3i {
  return vec3i(i32(round(p.x / info.hMm + f32(info.nx) * 0.5 - 0.5)), i32(round(p.z / info.hMm + f32(info.ny) * 0.5 - 0.5)), i32(round(p.y / info.hMm + f32(info.nz - info.airLayers) - 0.5)));
}

fn isTissue(idx: u32) -> bool { return occ[idx] >= 0.5 && cellType(cells[idx]) != T_AIR; }
fn isBridging(ty: u32) -> bool { return ty == T_ARTERY || ty == T_VEIN || ty == T_NERVE; }

/** Kind of air in a voxel: 0 tissue, 1 the original air above the skin, 2 a bluntly dissected plane, 3 cut (a raw wound surface). */
fn airKind(idx: u32) -> u32 {
  let c = cells[idx];
  if (occ[idx] >= 0.5 && cellType(c) != T_AIR) { return 0u; }
  let st = cellState(c);
  if (st == S_VAPOR) { return 3u; }
  if (st == S_DISSECT) { return 2u; }
  return 1u;
}
/** Strongest air kind among the 6 neighbours: 0 buried, 1 skin surface, 2 dissected plane, 3 cut surface. */
fn exposure(i: i32, j: i32, k: i32) -> u32 {
  let nb = array<vec3i, 6>(vec3i(1,0,0), vec3i(-1,0,0), vec3i(0,1,0), vec3i(0,-1,0), vec3i(0,0,1), vec3i(0,0,-1));
  var e = 0u;
  for (var n = 0; n < 6; n++) {
    let o = nb[n];
    let ii = i + o.x; let jj = j + o.y; let kk = k + o.z;
    if (!inVolume(ii, jj, kk)) { continue; }
    e = max(e, airKind(idx3(ii, jj, kk)));
  }
  return e;
}
fn hasAirNeighbour(i: i32, j: i32, k: i32) -> bool { return exposure(i, j, k) != 0u; }

/** Signed distance (mm) from a world point to the electrode surface. */
fn electrodeDistance(p: vec3f) -> f32 {
  let rel = p - sim.tip.xyz;
  let t = dot(rel, sim.axis.xyz);
  let etype = sim.gen2.z;
  if (etype > 2.5) {
    // forceps jaws: capsule from the tip along wdir for axis.w (jaw span), radius ndir.w
    let tc = clamp(dot(rel, sim.wdir.xyz), 0.0, sim.axis.w);
    return length(rel - sim.wdir.xyz * tc) - sim.ndir.w;
  } else if (etype > 1.5) {
    // ball: sphere of radius ndir.w centred one radius up the axis
    return length(rel - sim.axis.xyz * sim.ndir.w) - sim.ndir.w;
  } else if (etype > 0.5) {
    // needle: capsule radius wdir.w along the axis
    let tc = clamp(t, 0.0, sim.axis.w);
    return length(rel - sim.axis.xyz * tc) - sim.wdir.w;
  }
  // blade: box, half extents (length along axis, half width, half thickness), with a rounded tip
  let u = dot(rel, sim.wdir.xyz);
  let v = dot(rel, sim.ndir.xyz);
  let q = vec3f(abs(t - sim.axis.w * 0.5) - sim.axis.w * 0.5, abs(u) - sim.wdir.w, abs(v) - sim.ndir.w);
  return length(max(q, vec3f(0.0))) + min(max(q.x, max(q.y, q.z)), 0.0);
}

fn segmentDistance(p: vec3f, a: vec3f, b: vec3f) -> f32 {
  let ab = b - a;
  let t = clamp(dot(p - a, ab) / max(dot(ab, ab), 1e-6), 0.0, 1.0);
  return length(p - (a + ab * t));
}

fn sigmaOf(idx: u32) -> f32 {
  let c = cells[idx];
  let ty = cellType(c);
  if (ty == T_AIR || occ[idx] < 0.5) { return 0.0; }
  let st = cellState(c);
  if (st == S_CARBON) { return 0.002; }
  let pr = props.t[ty];
  let w = water[idx] / max(pr.water, 1e-3);
  // desiccation raises impedance sharply; temperature coefficient ~2 %/K up to boiling
  let g = clamp(w, 0.0, 1.0);
  let tempC = clamp(srcA[idx], 37.0, 100.0);
  return pr.sigma * (1.0 + 0.02 * (tempC - 37.0)) * (0.01 + 0.99 * g * g) * sim.gen2.w;
}

// For the Jacobi pass srcA is the potential; sigma needs temperature, so a cheap approximation
// uses 37 C (conductivity temperature dependence is applied through the water term only).
fn sigmaOfPot(idx: u32) -> f32 {
  let c = cells[idx];
  let ty = cellType(c);
  if (ty == T_AIR || occ[idx] < 0.5) { return 0.0; }
  // a surface voxel under a pool conducts like blood; under clotted coagulum far less
  if ((c & FLAG_BLOODCOVER) != 0u) { return SIGMA_BLOOD * select(1.0, 0.3, (c & FLAG_CLOTCOVER) != 0u) * sim.gen2.w; }
  let st = cellState(c);
  if (st == S_CARBON) { return 0.002; }
  let pr = props.t[ty];
  let g = clamp(water[idx] / max(pr.water, 1e-3), 0.0, 1.0);
  return pr.sigma * (0.01 + 0.99 * g * g) * sim.gen2.w;
}

fn harmonic(a: f32, b: f32) -> f32 { return select(0.0, 2.0 * a * b / (a + b), a + b > 0.0); }

/**
 * Electrode contact set: tissue within the contact tolerance of the electrode surface (in cut mode
 * the steam envelope confines conduction to a thin front, ~0.1 mm; coag modes couple through a
 * thicker desiccating layer), plus surface tissue the arc can reach across air.
 */
fn isContact(i: i32, j: i32, k: i32) -> bool {
  let d = electrodeDistance(worldOf(i, j, k));
  if (d <= max(sim.dt.w, 0.3 * info.hMm)) { return true; }
  if (d <= sim.tip.w) { return hasAirNeighbour(i, j, k); }
  // current spreads through a blood pool: covered surface voxels within 3 mm of the tip couple to it
  if (d <= 3.0 && (cells[idx3(i, j, k)] & FLAG_BLOODCOVER) != 0u) { return true; }
  return false;
}

// ------------------------------------------------------------------ field solve
// Analytic seed: phi ~ a / (d + a) around the electrode surface, then red-black Gauss-Seidel
// with over-relaxation in place on dstA. Seeding every frame removes the lag of a warm start
// behind a moving electrode; SOR converges in O(S) sweeps instead of O(S^2).
const SEED_A: f32 = 0.35; // mm
const SOR_OMEGA: f32 = 1.5;

@compute @workgroup_size(4, 4, 4)
fn seedPotential(@builtin(global_invocation_id) gid: vec3u) {
  let S = sim.box0.w;
  if (i32(gid.x) >= S || i32(gid.y) >= S || i32(gid.z) >= S) { return; }
  let i = sim.box0.x + i32(gid.x); let j = sim.box0.y + i32(gid.y); let k = sim.box0.z + i32(gid.z);
  if (!inVolume(i, j, k)) { return; }
  let idx = idx3(i, j, k);
  if (sigmaOfPot(idx) <= 0.0) { dstA[idx] = 0.0; return; }
  let d = max(electrodeDistance(worldOf(i, j, k)), 0.0);
  let half = f32(S) * 0.5 * info.hMm;
  // 1/r seed with the box boundary pulled to zero
  dstA[idx] = clamp(SEED_A / (d + SEED_A) - SEED_A / (half + SEED_A), 0.0, 1.0);
}

fn relax(gid: vec3u, color: u32) {
  let S = sim.box0.w;
  if (i32(gid.x) >= S || i32(gid.y) >= S || i32(gid.z) >= S) { return; }
  if (((gid.x + gid.y + gid.z) & 1u) != color) { return; }
  let i = sim.box0.x + i32(gid.x); let j = sim.box0.y + i32(gid.y); let k = sim.box0.z + i32(gid.z);
  if (!inVolume(i, j, k)) { return; }
  let idx = idx3(i, j, k);
  // subdomain boundary = far electrode (0 V)
  if (gid.x == 0u || gid.y == 0u || gid.z == 0u || i32(gid.x) == S - 1 || i32(gid.y) == S - 1 || i32(gid.z) == S - 1) { dstA[idx] = 0.0; return; }
  let s0 = sigmaOfPot(idx);
  if (s0 <= 0.0) { dstA[idx] = 0.0; return; }
  if (isContact(i, j, k)) { dstA[idx] = 1.0; return; }
  var num = 0.0; var den = 0.0;
  let nb = array<vec3i, 6>(vec3i(1,0,0), vec3i(-1,0,0), vec3i(0,1,0), vec3i(0,-1,0), vec3i(0,0,1), vec3i(0,0,-1));
  for (var n = 0; n < 6; n++) {
    let o = nb[n];
    let ii = i + o.x; let jj = j + o.y; let kk = k + o.z;
    if (!inVolume(ii, jj, kk)) { continue; }
    let nidx = idx3(ii, jj, kk);
    let g = harmonic(s0, sigmaOfPot(nidx));
    num += g * dstA[nidx]; den += g;
  }
  if (den <= 0.0) { dstA[idx] = 0.0; return; }
  let gs = num / den;
  dstA[idx] = mix(dstA[idx], gs, SOR_OMEGA);
}

@compute @workgroup_size(4, 4, 4)
fn relaxRed(@builtin(global_invocation_id) gid: vec3u) { relax(gid, 0u); }
@compute @workgroup_size(4, 4, 4)
fn relaxBlack(@builtin(global_invocation_id) gid: vec3u) { relax(gid, 1u); }

// ------------------------------------------------------------------ heating
var<workgroup> wgSum: array<f32, 64>;
var<workgroup> wgContact: array<u32, 64>;
var<workgroup> wgWet: array<f32, 64>;

fn wetFraction(idx: u32) -> f32 {
  let ty = cellType(cells[idx]);
  return clamp(water[idx] / max(props.t[ty].water, 1e-3), 0.0, 1.0);
}
/**
 * Near-field weight of a contact voxel: wet tissue in the electrode's path (inside or at its
 * surface) takes the unresolved dissipation; tissue a fraction of a millimetre from the surface
 * sits across the steam layer and takes almost none. This is what lets the blade carve its own
 * groove instead of heating the tissue alongside its faces.
 */
fn nearFieldWeight(i: i32, j: i32, k: i32, idx: u32) -> f32 {
  let d = max(electrodeDistance(worldOf(i, j, k)), 0.0);
  return wetFraction(idx) * exp(-d / 0.05);
}

@compute @workgroup_size(4, 4, 4)
fn heating(@builtin(global_invocation_id) gid: vec3u, @builtin(local_invocation_index) li: u32) {
  let S = sim.box0.w;
  var q = 0.0; var contact = 0u; var wet = 0.0;
  let i = sim.box0.x + i32(gid.x); let j = sim.box0.y + i32(gid.y); let k = sim.box0.z + i32(gid.z);
  let inside = i32(gid.x) < S && i32(gid.y) < S && i32(gid.z) < S && inVolume(i, j, k);
  if (inside) {
    let idx = idx3(i, j, k);
    let s0 = sigmaOfPot(idx);
    if (s0 > 0.0) {
      let hm = info.hM;
      // discrete dissipation consistent with the harmonic-mean scheme: half of every face's
      // g (dphi)^2 belongs to this voxel, so the volume sum equals V*I exactly at convergence
      let phi0 = srcA[idx];
      let nb = array<vec3i, 6>(vec3i(1,0,0), vec3i(-1,0,0), vec3i(0,1,0), vec3i(0,-1,0), vec3i(0,0,1), vec3i(0,0,-1));
      for (var n = 0; n < 6; n++) {
        let o = nb[n];
        let ii = i + o.x; let jj = j + o.y; let kk = k + o.z;
        if (!inVolume(ii, jj, kk)) { continue; }
        let nidx = idx3(ii, jj, kk);
        let g = harmonic(s0, sigmaOfPot(nidx));
        let d = srcA[nidx] - phi0;
        q += 0.5 * g * d * d / (hm * hm);
      }
      if (isContact(i, j, k)) { contact = 1u; wet = nearFieldWeight(i, j, k, idx); }
    }
    heat[idx] = q;
  }
  wgSum[li] = q * info.hM * info.hM * info.hM;
  wgContact[li] = contact;
  wgWet[li] = wet;
  workgroupBarrier();
  for (var s = 32u; s > 0u; s >>= 1u) {
    if (li < s) { wgSum[li] += wgSum[li + s]; wgContact[li] += wgContact[li + s]; wgWet[li] += wgWet[li + s]; }
    workgroupBarrier();
  }
  if (li == 0u) {
    atomicAdd(&state.pSumFixed, u32(clamp(wgSum[0] * 1e8, 0.0, 4.0e9)));
    atomicAdd(&state.contactCount, wgContact[0]);
    atomicAdd(&state.wetSumFixed, u32(wgWet[0] * 1000.0));
  }
}

// ------------------------------------------------------------------ generator
@compute @workgroup_size(1)
fn generator() {
  let pUnit = f32(atomicLoad(&state.pSumFixed)) * 1e-8;   // W at 1 V -> 1/Z
  let contacts = atomicLoad(&state.contactCount);
  let setting = sim.gen.x; let zLo = sim.gen.y; let zHi = sim.gen.z; let vMax = sim.gen.w;
  if (pUnit <= 1e-12 || contacts == 0u || sim.gen2.y < 0.5) {
    state.z = 1e9; state.p = 0.0; state.v2 = select(0.0, vMax * vMax, sim.gen2.y > 0.5); state.iRms = 0.0; return;
  }
  let z = 1.0 / pUnit;
  var p = setting;
  if (z < zLo) { p = setting * z / zLo; } else if (z > zHi) { p = setting * zHi / z; }
  var v2 = p * z;
  if (v2 > vMax * vMax) { v2 = vMax * vMax; p = v2 / z; }
  state.z = z; state.p = p; state.v2 = v2; state.iRms = sqrt(v2) / z;
}

// ------------------------------------------------------------------ bioheat
fn inSubdomain(i: i32, j: i32, k: i32) -> bool {
  let S = sim.box0.w;
  return i >= sim.box0.x && j >= sim.box0.y && k >= sim.box0.z && i < sim.box0.x + S && j < sim.box0.y + S && k < sim.box0.z + S;
}

@compute @workgroup_size(4, 4, 4)
fn bioheat(@builtin(global_invocation_id) gid: vec3u) {
  if (i32(gid.x) >= sim.tbox1.x || i32(gid.y) >= sim.tbox1.y || i32(gid.z) >= sim.tbox1.z) { return; }
  let i = sim.tbox0.x + i32(gid.x); let j = sim.tbox0.y + i32(gid.y); let k = sim.tbox0.z + i32(gid.z);
  if (!inVolume(i, j, k)) { return; }
  let idx = idx3(i, j, k);
  var c = cells[idx];
  let ty = cellType(c);
  var st = cellState(c);
  let T0 = srcA[idx];
  if (ty == T_AIR || occ[idx] < 0.5) { dstA[idx] = 37.0; return; }
  let pr = props.t[ty];
  let dt = sim.dt.x;
  let hm = info.hM;
  let rhoc = pr.rho * pr.c;
  // conduction: harmonic-mean conductivity, air neighbours adiabatic
  var lap = 0.0;
  let nb = array<vec3i, 6>(vec3i(1,0,0), vec3i(-1,0,0), vec3i(0,1,0), vec3i(0,-1,0), vec3i(0,0,1), vec3i(0,0,-1));
  for (var n = 0; n < 6; n++) {
    let o = nb[n];
    let ii = i + o.x; let jj = j + o.y; let kk = k + o.z;
    if (!inVolume(ii, jj, kk)) { continue; }
    let nidx = idx3(ii, jj, kk);
    let nc = cells[nidx];
    let nty = cellType(nc);
    if (nty == T_AIR || occ[nidx] < 0.5) { continue; }
    let kn = props.t[nty].k;
    let kf = harmonic(pr.k, kn);
    lap += kf * (srcA[nidx] - T0);
  }
  lap = lap / (hm * hm);
  // perfusion (lost once coagulated)
  let om0 = omega[idx];
  var w = water[idx];
  let perf = pr.wb * BLOOD_RHO_C * (T0 - 37.0) * select(1.0, 0.0, om0 > 1.0);
  // Joule heating from the last field solve (only valid inside the current subdomain while active),
  // split into the resolved field part and the unresolved near-field layer at the electrode surface,
  // which is deposited directly into wet contact voxels (current favours the fresh, conductive front)
  var q = 0.0;
  if (sim.gen2.y > 0.5 && inSubdomain(i, j, k)) {
    let fnf = sim.nf.x;
    q = heat[idx] * state.v2 * (1.0 - fnf);
    if (fnf > 0.0 && isContact(i, j, k)) {
      let wetSum = f32(atomicLoad(&state.wetSumFixed)) * 1e-3;
      if (wetSum > 1e-3) {
        let myWeight = nearFieldWeight(i, j, k, idx);
        q += state.p * fnf * (myWeight / wetSum) / (hm * hm * hm);
      }
    }
  }
  // heating-rate limiter for stability of the explicit step
  let dTq = min(q * dt / rhoc, sim.dt.z);
  var T1 = T0 + (lap - perf) * dt / rhoc + dTq;
  var o = occ[idx];
  // phase change: energy above boiling goes into latent heat while water remains
  if (T1 > T_BOIL && w > 0.0) {
    let excess = (T1 - T_BOIL) * rhoc;              // J/m3 available above boiling
    let waterMass = w * pr.rho;                     // kg/m3
    let eBoilAll = waterMass * L_VAP;
    let used = min(excess, eBoilAll);
    let fracBoiled = used / max(eBoilAll, 1e-6);
    let dw = w * fracBoiled;
    // continuous-wave energy at the arc removes tissue; interrupted waveforms mostly desiccate
    let vap = sim.gen2.x * select(0.0, 1.0, q > 0.0);
    let eVapVoxel = pr.rho * (pr.c * (T_BOIL - 37.0) + pr.water * L_VAP);
    let dOcc = vap * used / max(eVapVoxel, 1.0);
    o = o - dOcc;
    w = w - dw * (1.0 - vap * 0.5);
    T1 = T_BOIL + (excess - used) / rhoc;
    if (dOcc > 0.0) {
      atomicAdd(&state.vapFixed, u32(dOcc * 1000.0));
      if (ty == T_VISCERA) { atomicAdd(&state.visceraVapFixed, u32(dOcc * 1000.0)); }
    }
  }
  // fat liquefies above its melting point: treat as slow loss of material under the electrode
  if ((ty == T_FAT || ty == T_LIPOMA) && T1 > 60.0 && q > 0.0) {
    o = o - sim.gen2.x * 0.2 * dt * clamp((T1 - 60.0) / 40.0, 0.0, 1.0) * 10.0;
  }
  // Arrhenius damage (Kelvin)
  let TK = T1 + 273.15;
  var om = om0 + dt * exp(pr.lnA - pr.eaR / TK);
  om = min(om, 1e4);
  // state transitions
  if (o < 0.5) {
    // the voxel is gone: cut air, a raw wound surface for its neighbours
    c = packCellState(0u, S_VAPOR); st = S_VAPOR; o = max(o, 0.0); w = 0.0; T1 = 37.0;
  } else {
    if (w <= 0.03 * pr.water && T1 > T_CARBON) { st = S_CARBON; }
    else if (w <= 0.03 * pr.water) { st = max(st, S_DESIC); }
    else if (om > 0.53 && st == S_NORMAL) { st = S_COAG; }
    c = packCellState(c, st);
    // coagulated capillaries and dried or charred surfaces have stopped oozing
    if (om > 1.0 || st >= S_DESIC) { c = packCellClock(c, CLOCK_MAX); }
    // vessel wall collagen denatured: sealed if the lumen was apposed (forceps) or the vessel is
    // small enough to shrink closed; otherwise an open lumen under a char plug that can rebleed
    if ((ty == T_ARTERY || ty == T_VEIN) && om > 1.0 && (c & (FLAG_SEALED | FLAG_CHARPLUG)) == 0u) {
      let r = f32(cellAux(c)) * 0.05;
      // apposed walls, small vessels, and unexposed vessel inside the coagulum thrombose (sealed);
      // only an open lumen coagulated without apposition becomes a char plug that can rebleed
      if ((c & FLAG_COAPTED) != 0u || r <= 0.5 || exposure(i, j, k) != 3u) { c |= FLAG_SEALED; atomicAdd(&state.sealedCount, 1u); }
      else { c |= FLAG_CHARPLUG; atomicAdd(&state.charPlugCount, 1u); }
    }
    if (ty == T_MUSCLE && T1 > 45.0) { atomicAdd(&state.muscleHeat, 1u); }
    if (ty == T_VISCERA && T1 > 45.0) { atomicAdd(&state.visceraHeat, 1u); }
  }
  cells[idx] = c; occ[idx] = o; water[idx] = max(w, 0.0); omega[idx] = om;
  dstA[idx] = T1;
  // stats
  atomicMax(&state.maxTemp, u32(max(T1, 0.0) * 100.0));
  if (st == S_CARBON) { atomicAdd(&state.carbonCount, 1u); }
  if (om > 0.53 && om0 <= 0.53) { atomicAdd(&state.coagCount, 1u); }
}

// ------------------------------------------------------------------ blood layer
// One record per surface column (i, j): h = blood depth (mm) above the tissue top, age (s) since it last
// moved, surf = tissue top height (mm, world Y), hNext = scratch for the flow step, clot = fraction of the
// column that has coagulated (by standing or by heat), temp = blood temperature (C), stain = how deeply
// blood has soaked into the raw tissue surface (0..1), topK = k index of the top tissue voxel.
struct BloodCol { h: f32, age: f32, surf: f32, hNext: f32, clot: f32, temp: f32, stain: f32, topK: f32 };
struct Drops { n: vec4u, d: array<vec4f, 16> };   // droplet landings this frame: xyz (mm), w = volume (ul)
struct Tears { n: vec4u, s: array<vec4f, 32> };   // torn lattice links this frame: (a.xyz, radius), (b.xyz, tissue)
@group(2) @binding(0) var<storage, read_write> blood: array<BloodCol>;
@group(2) @binding(1) var<uniform> drops: Drops;
@group(2) @binding(2) var<uniform> tears: Tears;

const BLOOD_FILM: f32 = 0.05;   // mm, below this blood wets but does not flow
const BLOOD_VISC: f32 = 0.35;   // fraction of the head difference moved per step by a deep pool
const CLOT_AGE: f32 = 20.0;     // s standing before blood starts to gel
const CLOT_TIME: f32 = 45.0;    // s from gelling to a firm clot
const BLOOD_HEAT_FRAC: f32 = 0.35; // share of the delivered power that goes into blood under the electrode
const GAUZE_RATE: f32 = 20.0;   // mm of liquid blood depth a pressed gauze absorbs per second
const GAUZE_WIPE_SPEED: f32 = 15.0; // mm/s: faster than this with pressure is a wipe, slower a dab
const SUCTION_CAPTURE: f32 = 3.0;     // the pool within this many tip radii runs to the sucker
const SUCTION_RUN: f32 = 12.0;        // mm/s of pooled depth drawn to the tip at its rim, falling to 0 at the capture radius
const SPATTER_FRAC: f32 = 0.4;        // share of the blood boiled off a column that leaves as droplets (the rest is steam)

fn colIdx(i: i32, j: i32) -> u32 { return u32(i) + info.nx * u32(j); }

/** Adds fresh liquid blood to a column: the clot fraction is diluted so that the clot mass (depth) is conserved. */
fn addBlood(ci: u32, dh: f32) {
  let h = blood[ci].h; let h2 = h + dh;
  blood[ci].clot = select(0.0, blood[ci].clot * h / h2, h2 > 1e-6);
  blood[ci].h = h2;
}
/** Depth of a column that can flow: above the wetting film and not clotted. The same expression on both sides of a pair keeps the exchange conservative. */
fn mobileDepth(h: f32, clot: f32) -> f32 { return max(min(h - BLOOD_FILM, h * (1.0 - clot)), 0.0); }

/** Column scan: tissue top height, top voxel index and blood/clot-cover flags on the top voxel. Runs over the wound box columns. */
@compute @workgroup_size(8, 8)
fn surfaceHeight(@builtin(global_invocation_id) gid: vec3u) {
  let i = sim.wbox0.x + i32(gid.x); let j = sim.wbox0.y + i32(gid.y);
  if (i32(gid.x) >= sim.wbox1.x || i32(gid.y) >= sim.wbox1.y || i < 0 || j < 0 || i >= i32(info.nx) || j >= i32(info.ny)) { return; }
  let ci = colIdx(i, j);
  var top = -1;
  for (var k = i32(info.nz) - 1; k >= 0; k--) {
    let idx = idx3(i, j, k);
    if (occ[idx] >= 0.5 && cellType(cells[idx]) != T_AIR) { top = k; break; }
  }
  if (top < 0) { blood[ci].surf = -1e3; blood[ci].topK = -1.0; return; }
  let w = worldOf(i, j, top);
  blood[ci].surf = w.y + 0.5 * info.hMm * (occ[idx3(i, j, top)] - 0.5) * 2.0;
  blood[ci].topK = f32(top);
  let idx = idx3(i, j, top);
  let covered = blood[ci].h > 0.3;
  var c = cells[idx];
  if (covered) { c |= FLAG_BLOODCOVER; } else { c &= ~FLAG_BLOODCOVER; }
  if (covered && blood[ci].clot > 0.6) { c |= FLAG_CLOTCOVER; } else { c &= ~FLAG_CLOTCOVER; }
  cells[idx] = c;
}

fn addBloodLost(volUl: f32, idx: u32, t: f32) {
  atomicAdd(&state.bloodLostFixed, u32(volUl) + select(0u, 1u, hash13(vec3f(f32(idx), t, 1.0)) < fract(volUl)));
}

/**
 * Bleeding sources. A vessel voxel bleeds only when its lumen is open: it has a cut-air neighbour (a cut, a tear or
 * a pre-made wound), not merely a dissected plane or the skin surface beside it. Small vessels spasm and thrombose
 * with time since exposure (the exposure clock), large ones keep going. Any perfused raw surface oozes from its
 * capillaries at a tissue-specific rate that decays over about a minute unless it is wiped, and stops once
 * coagulated. Char plugs may rebleed.
 */
@compute @workgroup_size(4, 4, 4)
fn bloodSource(@builtin(global_invocation_id) gid: vec3u) {
  if (i32(gid.x) >= sim.wbox1.x || i32(gid.y) >= sim.wbox1.y || i32(gid.z) >= sim.wbox1.z) { return; }
  let i = sim.wbox0.x + i32(gid.x); let j = sim.wbox0.y + i32(gid.y); let k = sim.wbox0.z + i32(gid.z);
  if (!inVolume(i, j, k)) { return; }
  let idx = idx3(i, j, k);
  var c = cells[idx];
  let ty = cellType(c);
  if (ty == T_AIR || occ[idx] < 0.5) { return; }
  let ex = exposure(i, j, k);
  if (ex == 0u) { return; }
  let dt = sim.dt.x * f32(sim.wbox0.w);   // frame dt (substeps * dt)
  let t = sim.dt.y;
  let clock = cellClock(c);
  let ci = colIdx(i, j);
  let area = info.hMm * info.hMm;
  if (ty == T_ARTERY || ty == T_VEIN) {
    if (ex != 3u) { return; }                      // intact vessel exposed in a plane: visible, not bleeding
    let r = max(f32(cellAux(c)) * 0.05, 0.1);
    // char plug: stochastic rebleed (hash per voxel and second)
    if ((c & FLAG_CHARPLUG) != 0u) {
      let p = clamp((r - 0.5) / 1.5, 0.0, 0.9) * 0.15 * dt;
      let hsh = hash13(vec3f(f32(idx & 0xffffu), f32(idx >> 16u), floor(t * 30.0)));
      if (hsh < p) { c &= ~FLAG_CHARPLUG; c |= FLAG_REBLED; c = packCellClock(c, 0u); cells[idx] = c; atomicAdd(&state.rebleedCount, 1u); }
      return;
    }
    if ((c & (FLAG_SEALED | FLAG_COAPTED)) != 0u) { return; }
    // a gauze pressed over the end tamponades it: no flow while the pressure is held, and the clock runs faster
    if (sim.tool2b.w > 2.5 && sim.tool2b.w < 3.5 && sim.tool3.w > 0.3) {
      let p = worldOf(i, j, k);
      if (length(vec2f(p.x, p.z) - vec2f(sim.tool2.x, sim.tool2.z)) <= sim.tool2.w && abs(p.y - sim.tool2.y) < 4.0) {
        if (clock < CLOCK_MAX && hash13(vec3f(f32(idx), floor(t * 20.0), 13.0)) < dt / 0.5) { cells[idx] = packCellClock(c, clock + 1u); }
        return;
      }
    }
    // flow of the whole transected end, shared by the voxels of its cross-section
    let endVoxels = max(1.0, 3.14159 * r * r / area);
    var q = vesselFlowMlS(r) / endVoxels;           // ml/s per voxel
    var arterial = false;
    let ck = f32(clock);
    if (ty == T_ARTERY) {
      let ph = fract(t * 1.2);                       // 72 bpm
      q *= 0.35 + 1.3 * pow(max(sin(ph * 3.14159), 0.0), 3.0);
      // arterioles spasm within seconds and the smallest thrombose; larger arteries only slow a little
      if (r <= 0.35) { q *= select(exp(-ck / 6.0), 0.0, clock >= 20u); }
      else if (r <= 0.5) { q *= max(0.12, exp(-ck / 6.0)); }
      else { q *= 1.0 - 0.35 * min(1.0, ck / 16.0); }
      arterial = true;
    } else {
      q *= 0.3;
      // venules clot off with time; veins keep welling
      if (r <= 0.5) { q *= select(exp(-ck / 10.0), 0.0, clock >= 24u); } else { q *= 1.0 - 0.2 * min(1.0, ck / 16.0); }
    }
    // the exposure clock advances about once every 1.5 s
    if (clock < CLOCK_MAX && hash13(vec3f(f32(idx), floor(t * 20.0), 7.0)) < dt / 1.5) { cells[idx] = packCellClock(c, clock + 1u); }
    if (q <= 1e-7) { return; }
    if (arterial) { atomicAdd(&state.exposedArt, 1u); } else { atomicAdd(&state.exposedVen, 1u); }
    // an arterial end spurts most of its output as droplets (thrown by the particle system and landed back into the
    // layer); a vein wells up in place. The rest goes straight into the column here.
    let jet = select(VEN_JET_FRACTION, ART_JET_FRACTION, arterial);
    let slot = atomicAdd(&state.bleedN, 1u);
    var local = 1.0;
    if (slot < 8u) { state.bleedPts[slot] = vec4f(worldOf(i, j, k), select(-q, q, arterial)); local = 1.0 - jet; }
    let volUl = q * dt * 1000.0;                     // microlitres this frame
    // column depth increment: ul -> mm3 (1 ul = 1 mm3) over the column area
    // atomic float add is unavailable: columns of one vessel end are distinct voxels but may share a column;
    // accept the race on h (rare, small) and account the volume exactly
    addBlood(ci, volUl * local / area);
    addBloodLost(volUl, idx, t);
    return;
  }
  // capillary ooze from a raw, perfused, uncoagulated surface
  let pr = props.t[ty];
  if (pr.ooze <= 0.0 || clock >= CLOCK_MAX || cellState(c) != S_NORMAL || omega[idx] > 1.0) { return; }
  let planeFactor = select(1.0, 0.3, ex == 2u);     // a dissected plane weeps less than a cut surface
  let qUl = pr.ooze * area * exp(-f32(clock) / 7.0) * planeFactor; // ul/s from one exposed face
  let volUl = qUl * dt;
  addBlood(ci, volUl / area);
  addBloodLost(volUl, idx, t);
  atomicAdd(&state.oozeUlFixed, u32(volUl) + select(0u, 1u, hash13(vec3f(f32(idx), t, 3.0)) < fract(volUl)));
  atomicAdd(&state.oozeVoxels, 1u);
  // capillary hemostasis: the clock advances about once every 2 s; wiping (gauze) turns it back
  if (hash13(vec3f(f32(idx), floor(t * 20.0), 5.0)) < dt / 2.0) { cells[idx] = packCellClock(c, clock + 1u); }
}

/** Droplet landings, one thread per droplet. */
@compute @workgroup_size(16)
fn bloodDrops(@builtin(global_invocation_id) gid: vec3u) {
  let n = drops.n.x;
  if (gid.x >= n) { return; }
  let d = drops.d[gid.x];
  // a droplet splats over a ~0.8 mm radius footprint
  let r = max(i32(ceil(0.8 / info.hMm)), 1);
  let vi = i32(round(d.x / info.hMm + f32(info.nx) * 0.5 - 0.5));
  let vj = i32(round(d.z / info.hMm + f32(info.ny) * 0.5 - 0.5));
  var cnt = 0.0;
  for (var a = -r; a <= r; a++) { for (var b = -r; b <= r; b++) { if (a * a + b * b <= r * r) { cnt += 1.0; } } }
  let dh = d.w / (info.hMm * info.hMm) / max(cnt, 1.0);
  for (var a = -r; a <= r; a++) { for (var b = -r; b <= r; b++) {
    if (a * a + b * b > r * r) { continue; }
    let i = vi + a; let j = vj + b;
    if (i < 0 || j < 0 || i >= i32(info.nx) || j >= i32(info.ny)) { continue; }
    let ci = colIdx(i, j);
    addBlood(ci, dh);
  }}
}

/** Thin-film mobility: a deep pool runs, a film creeps, clotted blood stays. */
fn flowRate(h: f32, clot: f32) -> f32 {
  let mobile = max(h - BLOOD_FILM, 0.0);
  return BLOOD_VISC * clamp(mobile / 0.25, 0.15, 1.0) * (1.0 - clot);
}

/** Shallow flow of blood down the head gradient, suction and gauze; writes hNext (and clot/stain for suction and gauze). */
@compute @workgroup_size(8, 8)
fn bloodFlow(@builtin(global_invocation_id) gid: vec3u) {
  let i = sim.wbox0.x + i32(gid.x) - 4; let j = sim.wbox0.y + i32(gid.y) - 4;
  if (i32(gid.x) >= sim.wbox1.x + 8 || i32(gid.y) >= sim.wbox1.y + 8 || i < 0 || j < 0 || i >= i32(info.nx) || j >= i32(info.ny)) { return; }
  let ci = colIdx(i, j);
  let h0 = blood[ci].h; let s0 = blood[ci].surf;
  var h = h0;
  // the clot is carried as a mass (depth of clotted blood): only liquid flows, drains or soaks away, and the
  // fraction is re-derived at the end, so neither suction nor inflow can ratchet it
  let clot0 = blood[ci].clot;
  var clotMass = h0 * clot0;
  if (s0 < -500.0) { blood[ci].hNext = 0.0; return; }
  let H0 = s0 + h0;
  let nb = array<vec2i, 4>(vec2i(1,0), vec2i(-1,0), vec2i(0,1), vec2i(0,-1));
  for (var n = 0; n < 4; n++) {
    let ii = i + nb[n].x; let jj = j + nb[n].y;
    if (ii < 0 || jj < 0 || ii >= i32(info.nx) || jj >= i32(info.ny)) { continue; }
    let cj = colIdx(ii, jj);
    let sj = blood[cj].surf; if (sj < -500.0) { continue; }
    let Hj = sj + blood[cj].h;
    // outflow to lower neighbours, limited by what is mobile above the wetting film (the same expression on both
    // sides of a pair keeps the exchange conservative)
    if (H0 > Hj) {
      h -= min(flowRate(h0, clot0) * 0.25 * (H0 - Hj), mobileDepth(h0, clot0) * 0.25);
    } else if (Hj > H0) {
      h += min(flowRate(blood[cj].h, blood[cj].clot) * 0.25 * (Hj - H0), mobileDepth(blood[cj].h, blood[cj].clot) * 0.25);
    }
  }
  let dtf = sim.dt.x * f32(sim.wbox0.w);
  let mode = sim.tool2b.w;
  let w = voxelToWorld(info, vec3f(f32(i), f32(j), 0.0));
  let dxz = length(vec2f(w.x, w.z) - vec2f(sim.tool2.x, sim.tool2.z));
  if (mode > 1.5 && mode < 2.5) {
    // suction lifts liquid blood only: clot stays until it is wiped away. The columns under the tip drain at the pump
    // rate; a pool levels far faster than the creeping film model moves it, so the mobile blood of the same pool
    // within a few tip radii runs toward the lowered surface and is taken too, at a rate falling off with distance.
    // The wetting film and the stain stay (a sucker never leaves a dry field).
    let rcap = SUCTION_CAPTURE * sim.tool2.w;
    if (dxz <= rcap && abs(s0 - sim.tool2.y) < 4.0) {
      let area = 3.14159 * sim.tool2.w * sim.tool2.w;           // mm2
      let dhSuck = 2000.0 * dtf / area;                          // 2 ml/s over the tip area -> mm
      let liquid = max(h - clotMass, 0.0);
      var taken = min(liquid, dhSuck);
      if (dxz > sim.tool2.w) {
        let f = 1.0 - (dxz - sim.tool2.w) / (rcap - sim.tool2.w);
        taken = min(max(liquid - BLOOD_FILM, 0.0), SUCTION_RUN * f * f * dtf);
      }
      h -= taken;
    }
  } else if (mode > 2.5 && mode < 3.5) {
    // gauze: a dab soaks up liquid blood and tamponades the surface; a wipe also drags off clot and disturbs the
    // capillaries, so a wiped surface starts oozing again (and so do small vessels that had spasmed shut)
    let pressure = sim.tool3.w;
    if (dxz <= sim.tool2.w && abs(s0 - sim.tool2.y) < 3.0 && pressure > 0.05) {
      let speed = length(sim.tool3.xyz);
      let wiping = speed > GAUZE_WIPE_SPEED;
      let liquid = max(h - clotMass, 0.0);
      let dLiquid = min(liquid, GAUZE_RATE * pressure * sim.dis.z * dtf);   // dis.z = 0 once the gauze is saturated
      let dClot = select(0.0, min(clotMass, 4.0 * dtf), wiping);
      h = max(h - dLiquid - dClot, 0.0); clotMass -= dClot;
      atomicAdd(&state.gauzeSoakFixed, u32((dLiquid + dClot) * info.hMm * info.hMm * 1000.0));
      let top = i32(blood[ci].topK);
      if (top >= 0) {
        let tidx = idx3(i, j, top);
        var c = cells[tidx];
        let clock = cellClock(c);
        if (wiping) {
          if (clock > 6u) { cells[tidx] = packCellClock(c, 6u); }
          blood[ci].stain = blood[ci].stain * max(0.0, 1.0 - 2.0 * dtf);
        } else if (pressure > 0.3 && clock < CLOCK_MAX && hash13(vec3f(f32(tidx), floor(sim.dt.y * 20.0), 9.0)) < dtf / 0.4) {
          // tamponade: pressure hastens capillary hemostasis about five-fold
          cells[tidx] = packCellClock(c, clock + 1u);
        }
      }
    }
  }
  h = max(h, clotMass);   // liquid may leave, the clot stays
  blood[ci].hNext = max(h, 0.0);
  blood[ci].clot = select(0.0, clamp(clotMass / h, 0.0, 1.0), h > 1e-5);
}

/** Commit the flow step, age static blood, heat, boil and clot it, soak the raw surface, and gather pool statistics. */
@compute @workgroup_size(8, 8)
fn bloodCommit(@builtin(global_invocation_id) gid: vec3u) {
  let i = sim.wbox0.x + i32(gid.x) - 4; let j = sim.wbox0.y + i32(gid.y) - 4;
  if (i32(gid.x) >= sim.wbox1.x + 8 || i32(gid.y) >= sim.wbox1.y + 8 || i < 0 || j < 0 || i >= i32(info.nx) || j >= i32(info.ny)) { return; }
  let ci = colIdx(i, j);
  let dtf = sim.dt.x * f32(sim.wbox0.w);
  var b = blood[ci];
  let hNew = b.hNext;
  let moved = abs(hNew - b.h) > 1e-4;
  var h = hNew;
  b.age = select(b.age + dtf, 0.0, moved || hNew < BLOOD_FILM);
  var clot = b.clot;
  var temp = b.temp;
  let top = i32(b.topK);
  let w = voxelToWorld(info, vec3f(f32(i), f32(j), 0.0));
  let area = info.hMm * info.hMm;
  if (h > 1e-5 && b.surf > -500.0) {
    // blood temperature follows the tissue it lies on and cools toward body temperature
    if (top >= 0) { temp += (srcA[idx3(i, j, top)] - temp) * min(1.0, 2.0 * dtf); }
    if (temp < 37.0) { temp = 37.0; }
    temp += (37.0 - temp) * min(1.0, 0.1 * dtf);
    // the electrode in a pool: a share of the delivered power boils and cooks the blood around it. The column's blood
    // body runs from the tissue top to the pool surface; a submerged electrode heats every column it passes through.
    if (sim.gen2.y > 0.5 && state.p > 0.0) {
      let d = min(electrodeDistance(vec3f(w.x, b.surf + h, w.z)), min(electrodeDistance(vec3f(w.x, b.surf + 0.5 * h, w.z)), electrodeDistance(vec3f(w.x, b.surf + 0.1 * min(h, 1.0), w.z))));
      if (d < 0.8) {
        let nCols = max(1.0, 12.6 / area);                          // ~2 mm footprint
        let e = state.p * BLOOD_HEAT_FRAC * dtf / nCols;             // J into this column
        let vol = h * area * 1e-9;                                   // m3
        temp += min(e / (BLOOD_RHO_C * max(vol, 1e-12)), 60.0);
      }
    }
    if (temp > T_BOIL) {
      // boiling: the excess energy evaporates liquid blood and throws spatter
      let vol = h * area * 1e-9;
      let excess = (temp - T_BOIL) * BLOOD_RHO_C * vol;              // J
      let dm = excess / L_VAP;                                        // kg
      let dh = min(dm / 1050.0 / (area * 1e-6) * 1000.0, h * (1.0 - clot)); // mm of liquid
      let clotMassB = h * clot;
      h = h - dh;
      clot = select(0.0, min(1.0, clotMassB / h), h > 1e-5);
      temp = T_BOIL;
      atomicAdd(&state.boilCols, 1u);
      // part of what left the column is thrown as droplets (the particle system lands them back into the layer: blood
      // is conserved, not conjured); the columns that get no slot this frame simply steam off
      if (dh > 1e-4 && hash13(vec3f(f32(ci), floor(sim.dt.y * 30.0), 11.0)) < 0.25) {
        let slot = atomicAdd(&state.spatterN, 1u);
        if (slot < 4u) { state.spatterPts[slot] = vec4f(w.x, b.surf + h, w.z, SPATTER_FRAC * dh * area); }
      }
    }
    // coagulation: thermal (blood proteins denature in under a second at 70 C) and by standing
    let bp = props.t[T_BLOOD];
    var dClot = dtf * exp(bp.lnA - bp.eaR / (temp + 273.15));
    if (b.age > CLOT_AGE) { dClot += dtf / CLOT_TIME; }
    clot = min(1.0, clot + dClot);
    // fresh raw tissue under the blood soaks it up (visible stain that suction does not remove)
    if (top >= 0) {
      let ct = cells[idx3(i, j, top)];
      if (cellState(ct) == S_NORMAL && airKind(idx3(i, j, top)) == 0u) { b.stain = min(1.0, b.stain + 0.6 * dtf * min(h / 0.2, 1.0)); }
    }
    atomicAdd(&state.poolVolFixed, u32(h * area * 1000.0));
    atomicAdd(&state.clotVolFixed, u32(h * clot * area * 1000.0));
    if (length(vec2f(w.x, w.z) - vec2f(sim.tip.x, sim.tip.z)) < 1.5) { atomicMax(&state.poolAtTipFixed, u32(h * 1000.0)); }
  } else {
    temp = 37.0;
    if (h <= 1e-5) { clot = 0.0; }
  }
  b.h = h; b.clot = clot; b.temp = temp;
  blood[ci] = b;
}

/** Forceps grasp: set or clear FLAG_COAPTED inside the jaw capsule (tool2 -> tool2b, radius tool2.w). */
@compute @workgroup_size(4, 4, 4)
fn coaptMark(@builtin(global_invocation_id) gid: vec3u) {
  let S = 12;
  let ci = i32(round(sim.tool2.x / info.hMm + f32(info.nx) * 0.5 - 0.5)) - S / 2;
  let cj = i32(round(sim.tool2.z / info.hMm + f32(info.ny) * 0.5 - 0.5)) - S / 2;
  let ck = i32(round(sim.tool2.y / info.hMm + f32(info.nz - info.airLayers) - 0.5)) - S / 2;
  let i = ci + i32(gid.x); let j = cj + i32(gid.y); let k = ck + i32(gid.z);
  if (i32(gid.x) >= S || i32(gid.y) >= S || i32(gid.z) >= S || !inVolume(i, j, k)) { return; }
  let idx = idx3(i, j, k);
  var c = cells[idx];
  if (cellType(c) == T_AIR) { return; }
  let p = worldOf(i, j, k);
  let a = sim.tool2.xyz; let b = sim.tool2b.xyz;
  let d = segmentDistance(p, a, b);
  // a vessel held in the jaws is flattened as a whole: the coapted zone widens with the vessel radius
  var rad = sim.tool2.w;
  let ty = cellType(c);
  if (ty == T_ARTERY || ty == T_VEIN) { rad = max(rad, 2.2 * f32(cellAux(c)) * 0.05 + 0.3); }
  let inside = d <= rad && sim.tool2b.w > 0.5 && sim.tool2b.w < 1.5;
  if (inside) { c |= FLAG_COAPTED; } else { c &= ~FLAG_COAPTED; }
  cells[idx] = c;
}

// ------------------------------------------------------------------ blunt dissection
// The jaws enter the tissue at tool3.xyz and their tips (tool2 -> tool2b) spread or push. Voxels within the carve
// radius of the two blades (entry to each tip) or of the span between the tips, that belong to a plane with cohesion
// at or below the instrument's threshold and already touch air (the tips can only separate what they have reached),
// become dissected air. Vessels and nerves are never divided: they stay as bridging strands across the opened plane
// until they are torn or cut.
@compute @workgroup_size(4, 4, 4)
fn dissect(@builtin(global_invocation_id) gid: vec3u) {
  let a = sim.tool2.xyz; let b = sim.tool2b.xyz; let e = sim.tool3.xyz; let r = sim.dis.y;
  let lo = voxelOfWorld(min(min(a, b), e) - vec3f(r));
  let i = lo.x + i32(gid.x); let j = lo.y + i32(gid.y); let k = lo.z + i32(gid.z);
  if (!inVolume(i, j, k)) { return; }
  let idx = idx3(i, j, k);
  if (!isTissue(idx)) { return; }
  let ty = cellType(cells[idx]);
  if (isBridging(ty) || props.t[ty].cohesion > sim.dis.x) { return; }
  let p = worldOf(i, j, k);
  if (min(segmentDistance(p, a, b), min(segmentDistance(p, e, a), segmentDistance(p, e, b))) > r) { return; }
  if (exposure(i, j, k) == 0u) { return; }
  occ[idx] = 0.0; water[idx] = 0.0;
  cells[idx] = packCellState(0u, S_DISSECT);
  atomicAdd(&state.dissected, 1u);
}

// ------------------------------------------------------------------ mechanical tears
// Each torn lattice link carves the middle of its path: the tissue the link ran through (any soft tissue for a
// soft-tissue link; a vessel or nerve link carves only its own type). The carved voxels are cut air, so a torn
// vessel bleeds and torn parenchyma oozes.
@compute @workgroup_size(16)
fn tearCarve(@builtin(global_invocation_id) gid: vec3u) {
  if (gid.x >= tears.n.x) { return; }
  let A = tears.s[gid.x * 2u]; let B = tears.s[gid.x * 2u + 1u];
  let r = A.w; let linkTissue = u32(B.w);
  let a = mix(A.xyz, B.xyz, 0.3); let b = mix(A.xyz, B.xyz, 0.7);
  let lo = voxelOfWorld(min(a, b) - vec3f(r)); let hi = voxelOfWorld(max(a, b) + vec3f(r));
  for (var k = lo.z; k <= hi.z; k++) { for (var j = lo.y; j <= hi.y; j++) { for (var i = lo.x; i <= hi.x; i++) {
    if (!inVolume(i, j, k)) { continue; }
    let idx = idx3(i, j, k);
    if (!isTissue(idx)) { continue; }
    let ty = cellType(cells[idx]);
    let allowed = ty == linkTissue || (!isBridging(linkTissue) && !isBridging(ty) && props.t[ty].cohesion < 0.95);
    if (!allowed) { continue; }
    if (segmentDistance(worldOf(i, j, k), a, b) > r) { continue; }
    occ[idx] = 0.0; water[idx] = 0.0;
    cells[idx] = packCellState(0u, S_VAPOR);
    atomicAdd(&state.torn, 1u);
  }}}
}

// ------------------------------------------------------------------ constraint probe (mechanics)
// For each lattice constraint segment (material mm endpoints) sample 5 points: severed if any
// sample is air where the segment originally passed through tissue; omegaMax for shrinkage; max dryness
// (1 - water / water0) and whether any sample is char for brittleness.
struct Seg { a: vec4f, b: vec4f };   // xyz endpoints, a.w = 1 if the segment initially ran through tissue
struct Probe { flags: u32, omega: f32, dry: f32, pad1: u32 };
// bound in group 1 (same slots and access modes as srcA/dstA, which this entry point does not use)
@group(1) @binding(0) var<storage, read> segs: array<Seg>;
@group(1) @binding(1) var<storage, read_write> probes: array<Probe>;

@compute @workgroup_size(64)
fn constraintProbe(@builtin(global_invocation_id) gid: vec3u) {
  let n = gid.x;
  if (n >= arrayLength(&segs)) { return; }
  let sg = segs[n];
  var severed = 0u; var om = 0.0; var tissueSamples = 0u; var dry = 0.0; var charred = 0u;
  for (var t = 0; t < 5; t++) {
    let f = (f32(t) + 0.5) / 5.0;
    let p = mix(sg.a.xyz, sg.b.xyz, f);
    let v = voxelOfWorld(p);
    if (!inVolume(v.x, v.y, v.z)) { continue; }
    let idx = idx3(v.x, v.y, v.z);
    let c = cells[idx];
    if (occ[idx] >= 0.5 && cellType(c) != T_AIR) {
      tissueSamples++; om = max(om, omega[idx]);
      let w0 = max(props.t[cellType(c)].water, 1e-3);
      dry = max(dry, 1.0 - clamp(water[idx] / w0, 0.0, 1.0));
      if (cellState(c) == S_CARBON) { charred = 4u; }
    }
    else if (sg.a.w > 0.5) { severed = 1u; }
  }
  probes[n].flags = severed | select(0u, 2u, tissueSamples == 0u) | charred;
  probes[n].omega = om;
  probes[n].dry = dry;
}

// ------------------------------------------------------------------ pick
@compute @workgroup_size(1)
fn pick() {
  let o = sim.ray.xyz; let d = normalize(sim.rayDir.xyz);
  let step = info.hMm * 0.5;
  var t = 0.0;
  var hit = vec4f(0.0);
  var hc = 0u; var hi = 0u;
  for (var n = 0; n < 4000; n++) {
    let p = o + d * t;
    let v = voxelOfWorld(p);
    if (inVolume(v.x, v.y, v.z)) {
      let idx = idx3(v.x, v.y, v.z);
      if (occ[idx] >= 0.5 && cellType(cells[idx]) != T_AIR) { hit = vec4f(p, 1.0); hc = cells[idx]; hi = idx; break; }
    } else if (t > sim.rayDir.w) { break; }
    t += step;
    if (t > sim.rayDir.w) { break; }
  }
  state.hit = hit; state.hitCell = hc; state.hitIdx = hi;
}

// ------------------------------------------------------------------ slice (cross-section inspector)
// Samples the volume on a plane: origin + u * su + v * sv, writing packed RGBA8 pixels.
// mode (sim.slice.w): 0 type/state, 1 temperature, 2 damage, 3 water, 4 potential, 5 Joule heating
struct SliceOut { px: array<u32> };
@group(2) @binding(0) var<storage, read_write> sliceOut: array<u32>;

fn tissueColor(ty: u32) -> vec3f {
  switch (ty) {
    case 1u: { return vec3f(0.80, 0.55, 0.45); }
    case 2u: { return vec3f(0.85, 0.60, 0.55); }
    case 3u: { return vec3f(0.95, 0.80, 0.35); }
    case 4u: { return vec3f(0.92, 0.88, 0.80); }
    case 5u: { return vec3f(0.95, 0.88, 0.65); }
    case 6u: { return vec3f(0.93, 0.92, 0.86); }
    case 7u: { return vec3f(0.55, 0.12, 0.12); }
    case 8u: { return vec3f(0.80, 0.10, 0.08); }
    case 9u: { return vec3f(0.30, 0.18, 0.40); }
    case 10u: { return vec3f(0.92, 0.88, 0.72); }
    case 11u: { return vec3f(0.98, 0.88, 0.48); }
    case 12u: { return vec3f(0.90, 0.85, 0.70); }
    case 13u: { return vec3f(0.35, 0.02, 0.02); }
    case 14u: { return vec3f(0.93, 0.80, 0.78); }   // areolar: wispy pale pink
    case 15u: { return vec3f(0.62, 0.30, 0.30); }   // viscera: dusky red
    case 16u: { return vec3f(0.90, 0.86, 0.84); }   // serosa: glistening pale membrane
    default: { return vec3f(0.08, 0.09, 0.11); }
  }
}
fn packRGBA(c: vec3f) -> u32 {
  let q = vec3u(clamp(c, vec3f(0.0), vec3f(1.0)) * 255.0 + 0.5);
  return q.x | (q.y << 8u) | (q.z << 16u) | (255u << 24u);
}
fn tempRamp(t: f32) -> vec3f {
  let x = clamp((t - 37.0) / 263.0, 0.0, 1.0);
  let c1 = mix(vec3f(0.1, 0.1, 0.5), vec3f(0.0, 0.8, 0.9), smoothstep(0.0, 0.25, x));
  let c2 = mix(c1, vec3f(0.2, 0.9, 0.2), smoothstep(0.2, 0.45, x));
  let c3 = mix(c2, vec3f(1.0, 0.9, 0.1), smoothstep(0.4, 0.65, x));
  let c4 = mix(c3, vec3f(1.0, 0.2, 0.05), smoothstep(0.6, 0.85, x));
  return mix(c4, vec3f(1.0), smoothstep(0.85, 1.0, x));
}

@compute @workgroup_size(8, 8)
fn slice(@builtin(global_invocation_id) gid: vec3u) {
  let W = u32(sim.sliceU.w); let H = u32(sim.sliceV.w);
  if (gid.x >= W || gid.y >= H) { return; }
  let fu = (f32(gid.x) + 0.5) / f32(W) - 0.5;
  let fv = (f32(gid.y) + 0.5) / f32(H);
  // plane: horizontal extent sliceO.w mm along u, depth sliceV.x.. mm along -y
  let p = sim.sliceO.xyz + sim.sliceU.xyz * (fu * sim.sliceO.w) + sim.sliceV.xyz * (fv * sim.sliceParams.x);
  let v = voxelOfWorld(p);
  let i = v.x; let j = v.y; let k = v.z;
  var col = vec3f(0.06, 0.07, 0.09);
  if (inVolume(i, j, k)) {
    let idx = idx3(i, j, k);
    let c = cells[idx];
    let ty = cellType(c); let st = cellState(c);
    if (occ[idx] >= 0.5 && ty != T_AIR) {
      let mode = u32(sim.sliceParams.y);
      if (mode == 4u) {
        // electrical potential (srcA bound to `potential`), 0..1 of the electrode voltage
        col = tempRamp(37.0 + 263.0 * clamp(srcA[idx], 0.0, 1.0));
      } else if (mode == 5u) {
        // Joule heating density from the potential gradient, log scale over 4 decades
        let s0 = sigmaOfPot(idx);
        var q = 0.0;
        let nb = array<vec3i, 6>(vec3i(1,0,0), vec3i(-1,0,0), vec3i(0,1,0), vec3i(0,-1,0), vec3i(0,0,1), vec3i(0,0,-1));
        for (var n = 0; n < 6; n++) {
          let o = nb[n]; let ii = i + o.x; let jj = j + o.y; let kk = k + o.z;
          if (!inVolume(ii, jj, kk)) { continue; }
          let nidx = idx3(ii, jj, kk);
          let dphi = srcA[nidx] - srcA[idx];
          q += 0.5 * harmonic(s0, sigmaOfPot(nidx)) * dphi * dphi / (info.hM * info.hM);
        }
        // q at 1 V; typical peak ~1e8..1e9 W/m3 per V^2 near the electrode
        let l = clamp((log(max(q, 1.0)) / log(10.0) - 3.0) / 6.0, 0.0, 1.0);
        col = tempRamp(37.0 + 263.0 * l);
      } else if (mode == 1u) { col = tempRamp(srcA[idx]); }
      else if (mode == 2u) { col = mix(vec3f(0.1, 0.2, 0.6), vec3f(1.0, 0.95, 0.9), clamp(omega[idx], 0.0, 1.0)); if (st == S_CARBON) { col = vec3f(0.0); } }
      else if (mode == 3u) { let w0 = max(props.t[ty].water, 1e-3); col = mix(vec3f(0.5, 0.3, 0.15), vec3f(0.2, 0.5, 1.0), clamp(water[idx] / w0, 0.0, 1.0)); }
      else {
        col = tissueColor(ty);
        let blanch = smoothstep(0.25, 1.0, omega[idx]);
        col = mix(col, vec3f(0.88, 0.85, 0.82), blanch * 0.85);
        if (st == S_DESIC) { col = mix(col, vec3f(0.45, 0.33, 0.22), 0.7); }
        if (st == S_CARBON) { col = vec3f(0.03, 0.025, 0.02); }
        if (srcA[idx] > 98.0) { col = mix(col, vec3f(1.0), 0.15); }
        if ((c & FLAG_SEALED) != 0u) { col = mix(col, vec3f(0.2, 0.9, 0.3), 0.5); }
        if ((c & FLAG_CHARPLUG) != 0u) { col = mix(col, vec3f(0.1, 0.05, 0.0), 0.7); }
        if ((c & FLAG_COAPTED) != 0u) { col = mix(col, vec3f(0.3, 0.6, 1.0), 0.4); }
        // a bleeding vessel end, an oozing raw surface: fresh red tint fading as the exposure clock runs
        let ex = exposure(i, j, k);
        if (ex >= 2u && cellClock(c) < 31u && (ty == T_ARTERY || ty == T_VEIN || props.t[ty].ooze > 0.0) && st == S_NORMAL) {
          col = mix(col, vec3f(0.6, 0.05, 0.05), 0.35 * exp(-f32(cellClock(c)) / 7.0));
        }
      }
    } else if (ty == T_AIR && occ[idx] < 0.5) {
      // dissected plane: a faint haze marks where tissue was separated bluntly
      if (st == S_DISSECT) { col = vec3f(0.14, 0.13, 0.12); }
    }
  }
  if (electrodeDistance(p) <= 0.0) { col = vec3f(0.75, 0.75, 0.78); }
  sliceOut[gid.x + gid.y * W] = packRGBA(col);
}
