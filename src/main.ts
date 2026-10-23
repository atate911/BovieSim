import freeplay from '../scenarios/freeplay.json';
import hemostasis from '../scenarios/hemostasis.json';
import lipoma from '../scenarios/lipoma.json';
import { validateScenario } from '@/core/scenario/schema';
import { ForcepsTool } from '@/tools/forceps';
import { SuctionTool } from '@/tools/suction';
import { ToolKind, TOOL_ORDER, TOOL_LABEL } from '@/tools/instrument';
import { buildForcepsArm, buildFrazier, buildSkinHook } from '@/render/meshes';
import { mat4, vec4 } from 'gl-matrix';
import { Lattice } from '@/mechanics/lattice';
import { HookTool } from '@/tools/hooks';
import { buildAnatomy, carveSdf } from '@/core/scenario/anatomy';
import { initWebGPU } from '@/render/gpu';
import { SurfaceExtractor } from '@/render/surface';
import { Renderer, SceneParams } from '@/render/renderer';
import { OrbitCamera } from '@/render/camera';
import { buildDrapeSet, buildPencilBody, buildPencilButtons, buildBlade } from '@/render/meshes';
import { Volume, cellType, cellState } from '@/core/volume';
import { TISSUE_NAMES, STATE_NAMES } from '@/core/tissueProperties';
import { PhysicsPipeline } from '@/physics/pipeline';
import { ElectrodeTool } from '@/tools/electrode';
import { attachPointer } from '@/input/pointer';
import { Pedal } from '@/input/pedal';
import { FT10, FT10_MODES } from '@/core/generator/ft10';
import { ActivationAudio } from '@/ui/audio';
import { SliceView } from '@/ui/sliceView';
import { vec3 } from 'gl-matrix';
import { EventBus } from '@/education/events';
import { FT10Panel } from '@/ui/ft10Panel';

const params = new URLSearchParams(location.search);
const headless = params.get('headless') === '1';
const voxelOverride = params.has('voxel') ? parseFloat(params.get('voxel')!) : undefined;
const seedOverride = params.has('seed') ? parseInt(params.get('seed')!, 10) : undefined;
let debugMode = params.has('debug') ? parseInt(params.get('debug')!, 10) : 0;
/** scripted activation for headless checks: auto=cut|coag,watts,seconds[,ndcX0,ndcY0[,ndcX1,ndcY1]] (moves linearly over the duration) */
const auto = params.get('auto')?.split(',') ?? null;
const SCENARIOS: Record<string, unknown> = { freeplay, hemostasis, lipoma };
const scenarioId = params.get('scenario') ?? 'freeplay';
/** headless: starting tool and whether forceps jaws are closed during the scripted activation */
const toolParam = (params.get('tool') ?? 'pencil') as ToolKind;
const graspParam = params.get('grasp') === '1';
const holdParam = params.get('hold') === '1';
/** headless: raise the held instrument at this rate (mm/s) after 0.5 s, to simulate lifting out a freed lesion */
const liftRate = params.has('lift') ? parseFloat(params.get('lift')!) : 0;
/** headless: aim the cursor at a bed point (x, z mm on the tissue surface) instead of NDC coordinates */
const aim = params.get('aim')?.split(',').map(parseFloat) ?? null;
/** headless fixed simulation step per frame (s); larger values make mechanics checks cheaper */
const headlessDt = params.has('dt') ? parseFloat(params.get('dt')!) : 1 / 30;
/** headless gap measurement between two surface points: gap=x0,z0,x1,z1 (mm) */
const gapPts = params.get('gap')?.split(',').map(parseFloat) ?? [-1.5, 12, 1.5, 12];

declare global { interface Window { __sim: Record<string, unknown>; __simControl: Record<string, (...a: number[]) => unknown> } }

function fatal(msg: string) {
  document.getElementById('app')!.innerHTML = `<div class="fatal"><div><h1>Bovie Sim cannot start</h1><p>${msg}</p></div></div>`;
  console.error(msg);
}

async function main() {
  const canvas = document.getElementById('gpu-canvas') as HTMLCanvasElement;
  const hud = document.getElementById('hud')!;
  const scenario = validateScenario(SCENARIOS[scenarioId] ?? freeplay);
  hud.textContent = 'Building anatomy…';

  const { volume, report } = buildAnatomy(scenario, voxelOverride, seedOverride);
  if (params.has('cut')) carveDemo(volume, parseInt(params.get('cut')!, 10));
  const gpu = await initWebGPU(canvas, headless);
  volume.upload(gpu.device);
  const surface = new SurfaceExtractor(gpu.device, volume);
  const physics = new PhysicsPipeline(gpu.device, volume);
  const renderer = new Renderer(gpu, surface, { blood: physics.bloodBuf, info: volume.gpu!.info });
  // pre-carved wounds are part of the wound box from the start (bleeding is scanned there)
  if (scenario.preCarve?.length) physics.markWound([0, 0, 0], [volume.dims.nx, volume.dims.ny, volume.dims.nz]);
  // mechanics lattice (XPBD) in material space; the renderer and picking sample its displacement
  const mechanicsOn = params.get('mech') !== '0';
  const lattice = new Lattice(volume, 3, scenario.skin.langerDeg * Math.PI / 180);
  if (mechanicsOn) {
    renderer.attachLattice(lattice.infoData(), lattice.count);
    physics.setConstraintSegments(lattice.segmentData(volume), lattice.constraints.length);
  }
  const hook = new HookTool();
  let lesionFreedReported = false, lesionDelivered = false, lesionBreachReported = false, clusterRiseMm = 0;
  const cam = new OrbitCamera();
  cam.attach(canvas);
  if (params.has('cam')) { const [az, el, dist] = params.get('cam')!.split(',').map(parseFloat); cam.azimuth = az; cam.elevation = el; cam.distance = dist; }

  const W = headless ? 1280 : 0, H = headless ? 800 : 0;
  const [sx, , sz] = volume.sizeMm;
  const scene: SceneParams = {
    skinTone: scenario.skin.tone,
    prep: scenario.skin.prep === 'chlorhexidine' ? 1 : scenario.skin.prep === 'betadine' ? 2 : 0,
    langerRad: scenario.skin.langerDeg * Math.PI / 180,
    exposure: 1.0, debugMode, bedHalfX: sx / 2, bedHalfZ: sz / 2,
    arcLight: { x: 0, y: 0, z: 0, intensity: 0 },
  };

  // fenestrated surgical drape: incise film on the skin, absorbent pad, then the loose blue drape (albedos in linear sRGB)
  const drape = buildDrapeSet(sx / 2, sz / 2);
  renderer.addMesh(drape.film, 'drapeFilm', [0.17, 0.24, 0.36], { rough: 0.22, kind: 3 });   // clear adhesive film at the drape edge
  const drapePad = renderer.addMesh(drape.pad, 'drapePad', [0.075, 0.18, 0.37], { rough: 0.95, kind: 2 });
  const drapeDraw = renderer.addMesh(drape.drape, 'drape', [0.06, 0.16, 0.34], { rough: 0.9, kind: 1 });
  // satin-finished stainless (f0 of steel) and off-white moulded plastic
  const STEEL: [number, number, number] = [0.62, 0.62, 0.63];
  const pencilBody = renderer.addMesh(buildPencilBody(), 'pencilBody', [0.92, 0.86, 0.72], { rough: 0.45 });
  const pencilButtons = renderer.addMesh(buildPencilButtons(), 'pencilButtons', [0.85, 0.75, 0.1], { rough: 0.5 });
  const blade = renderer.addMesh(buildBlade(), 'blade', STEEL, { rough: 0.45, metallic: 1 });

  const tool = new ElectrodeTool();
  const forceps = new ForcepsTool();
  const suction = new SuctionTool();
  const forcepsA = renderer.addMesh(buildForcepsArm(), 'forcepsA', STEEL, { rough: 0.5, metallic: 1 });
  const forcepsB = renderer.addMesh(buildForcepsArm(), 'forcepsB', STEEL, { rough: 0.5, metallic: 1 });
  const frazier = renderer.addMesh(buildFrazier(), 'frazier', STEEL, { rough: 0.5, metallic: 1 });
  const hookMesh = renderer.addMesh(buildSkinHook(), 'hook', STEEL, { rough: 0.5, metallic: 1 });
  // texture maps from public/textures (CC0 sets via scripts/fetch-textures.mjs, or your own: docs/textures.md); applied as they load
  void renderer.loadTextures([
    { slot: 'drape', draws: [drapeDraw], tileMm: 30, tintIfGrey: [0.06, 0.16, 0.34], normalStrength: 1.8 },
    { slot: 'pad', draws: [drapePad], tileMm: 30, tintIfGrey: [0.075, 0.18, 0.37], fallbackSlot: 'drape', normalStrength: 1.8 },
    { slot: 'steel', draws: [blade, forcepsA, forcepsB, frazier, hookMesh], tileMm: 12, normalStrength: 0.6 },
    { slot: 'plastic', draws: [pencilBody], tileMm: 20, tintIfGrey: [0.92, 0.86, 0.72] },
  ]);
  const hookModel = mat4.create();
  let activeTool: ToolKind = TOOL_ORDER.includes(toolParam) ? toolParam : 'pencil';
  let pencilElectrode: 'blade' | 'needle' | 'ball' = 'blade';
  const pointer = attachPointer(canvas);
  const pedal = new Pedal();
  const gen = new FT10({ cutMode: scenario.generator.cutMode, coagMode: scenario.generator.coagMode, cutWatts: scenario.generator.cutWatts, coagWatts: scenario.generator.coagWatts });
  const audio = new ActivationAudio();
  const sliceView = new SliceView(document.getElementById('overlay')!, physics);
  /** section orientation: perpendicular to the blade (lateral spread measurement) or across the view */
  let slicePerp = params.get('slice') === 'perp';
  const panel = new FT10Panel(document.getElementById('panel')!, gen);
  const events = new EventBus();
  const eventsEl = document.createElement('div'); eventsEl.id = 'events'; document.getElementById('overlay')!.appendChild(eventsEl);
  events.on((e) => {
    const div = document.createElement('div'); div.className = e.severity;
    div.innerHTML = `<b>${e.t.toFixed(1)}s ${e.type}</b> ${e.detail}${e.teaching ? `<br>${e.teaching}` : ''}`;
    eventsEl.appendChild(div); while (eventsEl.children.length > 6) eventsEl.removeChild(eventsEl.firstChild!);
  });
  physics.slice.widthMm = 16; physics.slice.depthMm = 10;
  window.addEventListener('keydown', (e) => {
    if (e.repeat) return;
    switch (e.code) {
      case 'Digit1': gen.settings.cutMode = 'pure'; break;
      case 'Digit2': gen.settings.cutMode = 'blend'; break;
      case 'Digit3': gen.settings.cutMode = 'low'; break;
      case 'Digit4': gen.settings.cutMode = 'valleylab'; break;
      case 'Digit5': gen.settings.coagMode = 'fulgurate'; break;
      case 'Digit6': gen.settings.coagMode = 'spray'; break;
      case 'BracketLeft': gen.adjust('cut', e.shiftKey ? -1 : -5); break;
      case 'BracketRight': gen.adjust('cut', e.shiftKey ? 1 : 5); break;
      case 'Semicolon': gen.adjust('coag', e.shiftKey ? -1 : -5); break;
      case 'Quote': gen.adjust('coag', e.shiftKey ? 1 : 5); break;
      case 'KeyB': pencilElectrode = 'blade'; break;
      case 'KeyN': pencilElectrode = 'needle'; break;
      case 'KeyO': pencilElectrode = 'ball'; break;
      case 'Tab': case 'KeyQ': activeTool = TOOL_ORDER[(TOOL_ORDER.indexOf(activeTool) + 1) % TOOL_ORDER.length]; e.preventDefault(); break;
      case 'KeyT': forceps.toothed = !forceps.toothed; break;
      case 'KeyR': tool.roll += Math.PI / 4; break;
      case 'KeyV': debugMode = (debugMode + 1) % 4; scene.debugMode = debugMode; break;
      case 'KeyC': physics.slice.mode = (physics.slice.mode + 1) % 6; break;
      case 'KeyH': physics.slice.enabled = !physics.slice.enabled; sliceView.canvas.style.display = physics.slice.enabled ? '' : 'none'; break;
      case 'KeyJ': slicePerp = !slicePerp; break;
    }
    gen.adjust('cut', 0); gen.adjust('coag', 0);
  });

  // headless scripted activation
  let autoUntil = -1;
  if (auto && headless) {
    pointer.ndcX = auto[3] !== undefined ? parseFloat(auto[3]) : 0.05;
    pointer.ndcY = auto[4] !== undefined ? parseFloat(auto[4]) : -0.1;
    pointer.inside = true;
    const watts = parseInt(auto[1], 10);
    if (auto[0] === 'cut') gen.settings.cutWatts = watts; else gen.settings.coagWatts = watts;
    autoUntil = parseFloat(auto[2]);
  }

  const dpr = headless ? 1 : Math.min(window.devicePixelRatio || 1, 2);
  let frames = 0, fps = 0, lastFpsT = performance.now(), lastT = performance.now(), frameIndex = 0;
  let vertexCount = 0;
  let autoDone = false;
  const loop = async () => {
    const now = performance.now();
    const frameDt = headless ? headlessDt : Math.min(0.05, (now - lastT) / 1000); // headless: fixed-step sim time
    lastT = now;
    const w = headless ? W : Math.max(8, Math.floor(canvas.clientWidth * dpr)), hgt = headless ? H : Math.max(8, Math.floor(canvas.clientHeight * dpr));
    if (!headless && (canvas.width !== w || canvas.height !== hgt)) { canvas.width = w; canvas.height = hgt; }
    renderer.resize(w, hgt);
    renderer.time = now / 1000;
    cam.update(w / hgt);

    if (aim && headless) {
      // project the aimed surface point to NDC with the current camera
      const yS = volume.surfaceY(aim[0], aim[1]);
      const clip = vec4.transformMat4(vec4.create(), [aim[0], yS + 0.3, aim[1], 1], cam.viewProj);
      pointer.ndcX = clip[0] / clip[3]; pointer.ndcY = clip[1] / clip[3]; pointer.inside = true;
    }
    // generator activation: pedal, hand switch, or script
    if (auto && headless) {
      if (physics.simTime < autoUntil) {
        gen.activation = auto[0] === 'coag' ? 'coag' : 'cut';
        if (auto[5] !== undefined && !aim) {
          const f = Math.min(1, physics.simTime / autoUntil);
          pointer.ndcX = parseFloat(auto[3]) + (parseFloat(auto[5]) - parseFloat(auto[3])) * f;
          pointer.ndcY = parseFloat(auto[4]) + (parseFloat(auto[6]) - parseFloat(auto[4])) * f;
        }
      } else { gen.activation = 'none'; if (!autoDone && physics.simTime > autoUntil + 0.5) autoDone = true; }
    } else {
      gen.activation = pedal.cut || pointer.handCut || panel.pedalCut ? 'cut' : pedal.coag || pointer.handCoag || panel.pedalCoag ? 'coag' : 'none';
    }
    panel.refresh(); panel.update(physics.readout.zOhm, physics.readout.powerW);
    const mode = gen.activeMode;
    audio.set(mode ? mode.toneHz : 500, mode !== null);

    // tool placement from last frame's GPU pick
    const r = physics.readout;
    // the GPU pick marches in material space; instruments are posed on the deformed surface for display,
    // while the physics keeps material coordinates (the ElectrodeTool is placed from the material hit)
    const hit = pointer.inside ? r.hit : null;
    const hitWorld = hit && mechanicsOn ? vec3.add(vec3.create(), hit, lattice.displacementAt(hit)) : hit;
    void hitWorld;
    const tilt = pointer.isPen && (Math.abs(pointer.tiltX) > 2 || Math.abs(pointer.tiltY) > 2) ? { x: pointer.tiltX, y: pointer.tiltY } : null;
    const pressed = (pointer.buttons & 1) === 1 || (auto !== null && headless && graspParam && (holdParam || physics.simTime < autoUntil + 0.5));
    tool.pressure = pointer.pressure; tool.tiltX = pointer.tiltX; tool.tiltY = pointer.tiltY;
    const HIDE = mat4.fromTranslation(mat4.create(), [0, -1e4, 0]);
    let suctionAt: vec3 | null = null;
    events.buzzing = false; events.suctionOn = false;
    if (activeTool === 'pencil') {
      tool.type = pencilElectrode;
      tool.place(hit, cam.position, tilt !== null);
      renderer.setModel(pencilBody, tool.model); renderer.setModel(pencilButtons, tool.model); renderer.setModel(blade, tool.model);
      renderer.setModel(forcepsA, HIDE); renderer.setModel(forcepsB, HIDE); renderer.setModel(frazier, HIDE); renderer.setModel(hookMesh, HIDE);
      physics.setSecondaryTool(vec3.create(), vec3.create(), 0, 0);
    } else if (activeTool === 'forceps') {
      forceps.closed = pressed;
      const liftedHit = hit && liftRate > 0 && pressed ? vec3.fromValues(hit[0], hit[1] + liftRate * Math.max(0, physics.simTime - 0.5), hit[2]) : hit;
      forceps.place(liftedHit, cam.position, tilt);
      renderer.setModel(forcepsA, forceps.modelA); renderer.setModel(forcepsB, forceps.modelB);
      renderer.setModel(pencilBody, HIDE); renderer.setModel(pencilButtons, HIDE); renderer.setModel(blade, HIDE); renderer.setModel(frazier, HIDE); renderer.setModel(hookMesh, HIDE);
      physics.setSecondaryTool(forceps.tipA, forceps.tipB, forceps.jawRadius, forceps.visible && forceps.closed ? 1 : 0);
      // buzzing the forceps: closed jaws become the electrode; open jaws cannot be activated
      if (forceps.visible && forceps.closed) { tool.asForceps(forceps.tipA, forceps.tipB, forceps.jawRadius, forceps.axis); events.buzzing = gen.activation !== 'none'; }
      else { tool.visible = false; if (gen.activation !== 'none') gen.activation = 'none'; }
    } else if (activeTool === 'suction') {
      suction.on = pressed;
      suction.place(hit, cam.position, tilt);
      renderer.setModel(frazier, suction.model);
      renderer.setModel(pencilBody, HIDE); renderer.setModel(pencilButtons, HIDE); renderer.setModel(blade, HIDE); renderer.setModel(forcepsA, HIDE); renderer.setModel(forcepsB, HIDE); renderer.setModel(hookMesh, HIDE);
      physics.setSecondaryTool(suction.tip, suction.tip, suction.drainRadius, suction.visible && suction.on ? 2 : 0);
      if (suction.visible && suction.on) { suctionAt = vec3.clone(suction.tip); events.suctionOn = true; }
      tool.visible = false; if (gen.activation !== 'none') gen.activation = 'none';
    } else {
      // skin hook: press to attach the nearest lattice node, drag to retract, release to let go
      let dragTarget: vec3 | null = null;
      if (pressed) {
        if (hook.node < 0 && hit) hook.node = lattice.nearestNode(hit, 4);
        if (hook.node >= 0) {
          // drag target: cursor ray intersected with the horizontal plane 2 mm above the node's start height, lifted a little
          const ray = cam.ray(pointer.ndcX, pointer.ndcY);
          const planeY = lattice.mat[hook.node * 3 + 1] + 1.5;
          const tHit = (planeY - ray.origin[1]) / ray.dir[1];
          dragTarget = tHit > 0 ? vec3.scaleAndAdd(vec3.create(), ray.origin, ray.dir, tHit) : null;
          lattice.attachments.length = 0;
          if (dragTarget) lattice.attachments.push({ node: hook.node, target: dragTarget, strength: 0.25 });
        }
      } else { hook.node = -1; lattice.attachments.length = 0; }
      hook.place(hit, cam.position, tilt, dragTarget);
      mat4.copy(hookModel, hook.model);
      renderer.setModel(hookMesh, hook.visible ? hookModel : HIDE);
      renderer.setModel(pencilBody, HIDE); renderer.setModel(pencilButtons, HIDE); renderer.setModel(blade, HIDE); renderer.setModel(forcepsA, HIDE); renderer.setModel(forcepsB, HIDE); renderer.setModel(frazier, HIDE);
      physics.setSecondaryTool(vec3.create(), vec3.create(), 0, 0);
      tool.visible = false; if (gen.activation !== 'none') gen.activation = 'none';
    }
    if (activeTool !== 'hook') { if (activeTool !== 'forceps') lattice.attachments.length = 0; }
    // forceps lift: closed jaws carry the nearest node (and with it a freed lipoma)
    if (activeTool === 'forceps') {
      if (forceps.closed && forceps.visible) {
        if (hook.node < 0) hook.node = lattice.nearestNode(forceps.mid, 3);
        lattice.attachments.length = 0;
        if (hook.node >= 0) { const lift = vec3.scaleAndAdd(vec3.create(), forceps.mid, forceps.axis, 1.0); lattice.attachments.push({ node: hook.node, target: lift, strength: 0.2 }); }
      } else { hook.node = -1; lattice.attachments.length = 0; }
    }
    const mode2 = gen.activeMode;
    renderer.setMaterial(pencilButtons, gen.activation === 'cut' ? [1, 0.9, 0.2] : gen.activation === 'coag' ? [0.2, 0.5, 1] : [0.85, 0.75, 0.1], { rough: 0.5, emissive: gen.activation !== 'none' ? 0.6 : 0 });
    scene.arcLight = mode2 && r.hit && tool.visible ? { x: tool.tip[0], y: tool.tip[1] + 1, z: tool.tip[2], intensity: mode2.kind === 'coag' ? 6 : 2.5 } : { x: 0, y: 0, z: 0, intensity: 0 };
    const ray = cam.ray(pointer.ndcX, pointer.ndcY);
    if (aim && headless) physics.setPickRay(vec3.fromValues(aim[0], 60, aim[1]), vec3.fromValues(0, -1, 0)); // vertical ray onto the aimed column
    else physics.setPickRay(ray.origin, ray.dir);
    // cross-section through the tip, in the plane perpendicular to the view direction, starting 1.5 mm above the surface
    {
      let right = vec3.fromValues(Math.cos(cam.azimuth), 0, -Math.sin(cam.azimuth));
      if (slicePerp && tool.visible) {
        // horizontal direction perpendicular to the blade's projected axis
        const ax = vec3.fromValues(tool.axis[0], 0, tool.axis[2]);
        if (vec3.length(ax) > 1e-3) { vec3.normalize(ax, ax); right = vec3.fromValues(-ax[2], 0, ax[0]); }
      }
      const anchor = r.hit ?? vec3.fromValues(0, 0, 0);
      vec3.set(physics.slice.o, anchor[0], (r.hit ? anchor[1] : 0) + 1.5, anchor[2]);
      vec3.copy(physics.slice.u, right);
    }

    // bleeding: droplets from exposed vessel ends, landing into the blood layer
    for (const bp of r.bleedPoints) renderer.particles.bleed(vec3.fromValues(bp[0], bp[1], bp[2]), bp[3], frameDt);
    // steam, smoke and arcs from the last readout
    if (mode && r.hit && r.contactVoxels > 0 && tool.visible) {
      const tip = vec3.clone(tool.tip);
      if (mode.kind === 'cut') renderer.particles.emit(0, tip, 20 + r.vapVoxels * 30, frameDt);
      else renderer.particles.emit(1, tip, 6 + (r.maxTempC >= 100 ? 10 : 0), frameDt);
      if (r.maxTempC >= 100) renderer.particles.emit(1, tip, mode.kind === 'cut' ? 3 : 4, frameDt);
      const reach = (mode.vPeak / 1000) * mode.arcMmPerKv;
      const arcs = mode.kind === 'coag' ? (mode.id === 'spray' ? 4 : 2) : (Math.random() < 0.15 ? 1 : 0);
      for (let a = 0; a < arcs; a++) {
        const ang = Math.random() * Math.PI * 2, rad = Math.random() * Math.max(reach, 0.3);
        const end = vec3.fromValues(r.hit[0] + Math.cos(ang) * rad, r.hit[1] + 0.05, r.hit[2] + Math.sin(ang) * rad);
        const start = vec3.scaleAndAdd(vec3.create(), tool.tip, tool.axis, 0.4 + Math.random() * 1.2);
        renderer.particles.arc(start, end);
      }
    }
    renderer.particles.update(frameDt, suctionAt, (x, z) => volume.surfaceY(x, z));
    for (const l of renderer.particles.landings) physics.drops.push(l);
    renderer.particles.landings.length = 0;
    renderer.particles.upload(gpu.device);
    events.update(physics.simTime, r, gen, activeTool === 'pencil' ? tool.type : activeTool, frameDt);

    // mechanics: apply the last probe (severed / shrunk constraints), muscle twitch, step, upload
    if (mechanicsOn) {
      const probe = physics.takeProbe();
      if (probe) lattice.applyProbe(probe.severed, probe.omega);
      if (mode && mode.kind === 'coag' && r.muscleHeatVoxels > 20 && tool.visible) lattice.stimulate(tool.tip, Math.min(2, 0.5 + r.iRms * 2), physics.simTime);
      lattice.step(frameDt, physics.simTime);
      renderer.uploadLattice(lattice.disp);
      if (lattice.lipomaFree && !lesionFreedReported) { lesionFreedReported = true; events.emit({ t: physics.simTime, type: 'lesion.freed', detail: `${lattice.capsuleTotal - lattice.capsuleAlive}/${lattice.capsuleTotal} capsule links cut`, teaching: 'The capsule is free around most of its circumference: the lipoma will deliver with gentle traction.', severity: 'info' }); }
      if (lattice.lipomaFree && !lesionDelivered) {
        // delivered when the freed cluster has risen out of its bed by 4 mm on average (traction lifts it clear of the fat)
        let dy = 0, cn = 0; for (let n = 0; n < lattice.count; n++) if (lattice.cluster[n] === 1) { dy += lattice.pos[n * 3 + 1] - lattice.mat[n * 3 + 1]; cn++; }
        clusterRiseMm = cn > 0 ? dy / cn : 0;
        if (clusterRiseMm > 4) { lesionDelivered = true; events.emit({ t: physics.simTime, type: 'lesion.delivered', detail: `lipoma lifted ${clusterRiseMm.toFixed(1)} mm out of its bed`, severity: 'info' }); }
      }
      if (mode && r.hit && tool.visible && cellType(r.hitCell) === 11 && !lesionBreachReported) { lesionBreachReported = true; events.emit({ t: physics.simTime, type: 'lesion.breached', detail: 'energy applied inside the lipoma', teaching: 'You have entered the lipoma. Fat spills, the plane is lost and fragments left behind recur.', severity: 'bad' }); }
    }

    renderer.writeFrame(cam, scene);
    const enc = gpu.device.createCommandEncoder();
    const changed = physics.step(enc, tool, gen, frameDt);
    if (surface.dirty || changed) surface.encode(enc);
    renderer.render(enc);
    gpu.device.queue.submit([enc.finish()]);
    if (headless) { await gpu.device.queue.onSubmittedWorkDone(); await renderer.present2D(canvas); }
    physics.read();
    physics.readProbe();
    physics.readSlice().then((ok) => { if (ok) sliceView.draw(); });
    surface.readCount().then((c) => { vertexCount = c; });

    frames++; frameIndex++;
    if (now - lastFpsT > 500) { fps = frames * 1000 / (now - lastFpsT); frames = 0; lastFpsT = now; }
    const hv = r.hit ? `${TISSUE_NAMES[cellType(r.hitCell)]} (${STATE_NAMES[cellState(r.hitCell)]})  ${r.hit[0].toFixed(1)}, ${r.hit[1].toFixed(1)}, ${r.hit[2].toFixed(1)} mm` : '—';
    const cutM = FT10_MODES[gen.settings.cutMode], coagM = FT10_MODES[gen.settings.coagMode];
    const act = gen.activation === 'none' ? 'idle' : `<b>${gen.activation.toUpperCase()} ${gen.activeWatts} W</b>  Z ${isFinite(r.zOhm) && r.zOhm < 1e8 ? r.zOhm.toFixed(0) + ' Ω' : 'open'}  P ${r.powerW.toFixed(1)} W  V ${r.vRms.toFixed(0)} V  contact ${r.contactVoxels} vx`;
    hud.innerHTML = `<b>Bovie Sim</b> · ${scenario.name}\n` +
      `FT10  cut: ${cutM.label} ${gen.settings.cutWatts} W   coag: ${coagM.label} ${gen.settings.coagWatts} W   tool: <b>${TOOL_LABEL[activeTool]}</b>${activeTool === 'pencil' ? ' (' + pencilElectrode + ')' : activeTool === 'forceps' ? (forceps.closed ? ' closed' : ' open') + (forceps.toothed ? ', toothed' : ', smooth') : ''}\n` +
      `${act}\n` +
      `max T ${r.maxTempC.toFixed(0)} °C · char ${r.carbonVoxels} vx · vaporised ${physics.totals.vapVoxels.toFixed(0)} vx · sim t ${physics.simTime.toFixed(1)} s\n` +
      `blood lost ${r.bloodLostMl.toFixed(2)} ml · pooled ${r.poolMl.toFixed(2)} ml · bleeding ends: ${r.exposedArterial} art / ${r.exposedVenous} ven · sealed ${r.sealedVoxels} · char plug ${r.charPlugVoxels} · rebleeds ${r.rebleeds}\n` +
      `under tip: ${hv}\n` +
      `volume ${volume.dims.nx}×${volume.dims.ny}×${volume.dims.nz} @ ${volume.dims.h} mm · tris ${(vertexCount / 3 | 0).toLocaleString()}${vertexCount >= surface.capacity ? ' (CAPPED)' : ''} · ${fps.toFixed(0)} fps\n` +
      `Z/X pedal cut/coag · LMB hand-switch cut (pencil) / grasp (forceps) / suck (suction) · Tab tool · 1-4 cut mode · 5-6 coag mode · [ ] ; ' watts · B/N/O electrode · T toothed · R roll · V overlay · C section · RMB orbit`;
    window.__simControl = { setDebug: (n: number) => { debugMode = n; scene.debugMode = n; }, getDebug: () => scene.debugMode, setSlice: (m: number) => { physics.slice.mode = m; },
      /** damage-zone extent in the current section (needs slice mode 2): width and depth in mm of pixels with omega >= ~0.53 */
      measureSlice: () => {
        const { SLICE_W, SLICE_H } = { SLICE_W: 320, SLICE_H: 200 };
        const px = physics.slicePixels; let minX = 1e9, maxX = -1, maxY = -1; let count = 0;
        for (let y = 0; y < SLICE_H; y++) for (let x = 0; x < SLICE_W; x++) {
          const o = (x + y * SLICE_W) * 4; const rr = px[o], gg = px[o + 1], bb = px[o + 2];
          // damage ramp: blue (0.1,0.2,0.6) -> white; omega 0.53 is ~ (0.58,0.6,0.76). count pixels brighter than that, excluding electrode grey
          if (rr > 150 && gg > 150 && bb > 185 && !(Math.abs(rr - gg) < 6 && Math.abs(gg - bb) < 12 && rr > 180)) { count++; minX = Math.min(minX, x); maxX = Math.max(maxX, x); maxY = Math.max(maxY, y); }
        }
        const pxPerMmX = SLICE_W / physics.slice.widthMm, pxPerMmY = SLICE_H / physics.slice.depthMm;
        return count ? { widthMm: (maxX - minX + 1) / pxPerMmX, depthMm: (maxY + 1) / pxPerMmY - 1.5, pixels: count } : { widthMm: 0, depthMm: 0, pixels: 0 };
      }, setCam: (az: number, el: number, dist: number, tx = 0, tz = 0) => { cam.azimuth = az; cam.elevation = el; cam.distance = dist; cam.target[0] = tx; cam.target[2] = tz; } };
    window.__sim = { ready: true, frame: frameIndex, textures: renderer.loadedTextures.slice(), events: events.recent(12), tool: activeTool, lipomaFree: lattice.lipomaFree, capsule: [lattice.capsuleAlive, lattice.capsuleTotal], clusterRiseMm,
      gapMm: lattice.distanceBetween(vec3.fromValues(gapPts[0], 0, gapPts[1]), vec3.fromValues(gapPts[2], 0, gapPts[3])), vertexCount, fps, report, readout: { ...r, hit: r.hit ? Array.from(r.hit) : null }, totals: physics.totals, simTime: physics.simTime, autoDone, settings: gen.settings };
    if (!headless) requestAnimationFrame(loop); else setTimeout(loop, 10);
  };
  loop();
}

/** Demo carve for visual review: 1 = incision trench to fat, 2 = pit down to muscle, 3 = both. */
function carveDemo(volume: Volume, mode: number) {
  if (mode & 1) carveSdf(volume, (x, y, d) => Math.max(Math.abs(x) - 15, Math.abs(y - 12) - (2.0 + d * 0.3), d - 9));
  if (mode & 2) carveSdf(volume, (x, y, d) => Math.max(Math.hypot(x + 6, y + 10) - (15 - d * 0.42), d - 24));
  // 4: ring trench around a centred lipoma (lipoma scenario): frees the capsule circumferentially
  if (mode & 4) carveSdf(volume, (x, y, d) => { const r = Math.hypot(x / 1.17, y); return Math.max(Math.abs(r - 7.3) - (1.3 + d * 0.1), d - 14); });
  // 8: undermining plane beneath the lipoma (as with closed scissors), leaving a thin pedicle at the centre
  if (mode & 8) carveSdf(volume, (x, y, d) => Math.max(Math.hypot(x / 1.17, y) - 8.2, Math.abs(d - 14) - 0.6, 1.2 - Math.hypot(x, y)));
}

main().catch((e) => fatal(String(e?.message ?? e)));
