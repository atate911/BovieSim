import freeplay from '../scenarios/freeplay.json';
import hemostasis from '../scenarios/hemostasis.json';
import lipoma from '../scenarios/lipoma.json';
import { validateScenario } from '@/core/scenario/schema';
import { ForcepsTool } from '@/tools/forceps';
import { SuctionTool } from '@/tools/suction';
import { ScalpelTool } from '@/tools/scalpel';
import { ScissorsTool } from '@/tools/scissors';
import { HandController } from '@/tools/hands';
import { ToolKind, TOOL_ORDER, TOOL_LABEL, HandBKind, HAND_B_ORDER, metalDistance } from '@/tools/instrument';
import { buildForcepsArm, buildFrazier, buildSkinHook, buildScalpel, buildScissorsArm } from '@/render/meshes';
import { mat4, vec4 } from 'gl-matrix';
import { Lattice, Attachment } from '@/mechanics/lattice';
import { HookTool } from '@/tools/hooks';
import { buildAnatomy, carveSdf } from '@/core/scenario/anatomy';
import { initWebGPU } from '@/render/gpu';
import { SurfaceExtractor } from '@/render/surface';
import { Renderer, SceneParams } from '@/render/renderer';
import { OrbitCamera } from '@/render/camera';
import { buildDrapes, buildPencilBody, buildPencilButtons, buildBlade } from '@/render/meshes';
import { Volume, cellType, cellState } from '@/core/volume';
import { TISSUE_NAMES, STATE_NAMES } from '@/core/tissueProperties';
import { PhysicsPipeline, SLICE_W, SLICE_H } from '@/physics/pipeline';
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
/** scripted activation for headless checks: auto=cut|coag|none,watts,seconds[,ndcX0,ndcY0[,ndcX1,ndcY1]] (moves linearly over the duration) */
const auto = params.get('auto')?.split(',') ?? null;
const SCENARIOS: Record<string, unknown> = { freeplay, hemostasis, lipoma };
const scenarioId = params.get('scenario') ?? 'freeplay';
/** headless: starting instruments (dominant hand, and the non-dominant hand), and whether the button is held during the script */
const toolParam = (params.get('tool') ?? 'pencil') as ToolKind;
const handBParam = (params.get('handB') ?? 'none') as HandBKind;
const graspParam = params.get('grasp') === '1' || params.get('press') === '1';
const holdParam = params.get('hold') === '1';
/** headless: Ctrl held (scissors spread) */
const spreadParam = params.get('spread') === '1';
/** headless: Space held (the pointer drives the non-dominant hand) for this many seconds of sim time */
const spaceUntil = params.has('space') ? parseFloat(params.get('space')!) : -1;
/** headless: offset of the non-dominant hand's drag target from the pointer (x, z mm): a retraction pull */
const pullParam = params.get('pull')?.split(',').map(parseFloat) ?? null;
/** headless: raise the held instrument at this rate (mm/s) after 0.5 s, to simulate lifting out a freed lesion */
const liftRate = params.has('lift') ? parseFloat(params.get('lift')!) : 0;
/** headless: aim the cursor at a bed point (x, z mm on the tissue surface) instead of NDC coordinates */
const aim = params.get('aim')?.split(',').map(parseFloat) ?? null;
/** headless fixed simulation step per frame (s); larger values make mechanics checks cheaper */
const headlessDt = params.has('dt') ? parseFloat(params.get('dt')!) : 1 / 30;
/** headless gap measurement between two surface points: gap=x0,z0,x1,z1 (mm) */
const gapPts = params.get('gap')?.split(',').map(parseFloat) ?? [-1.5, 12, 1.5, 12];
/** depth peel of the 3D view at start: peel=block|dissect[,mm below the skin] */
const peelParam = params.get('peel')?.split(',') ?? null;

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

  renderer.addMesh(buildDrapes(sx / 2, sz / 2), 'drapes', [0.12, 0.22, 0.40], { rough: 0.9, kind: 1 });
  const steel: [number, number, number] = [0.85, 0.85, 0.86];
  const pencilBody = renderer.addMesh(buildPencilBody(), 'pencilBody', [0.92, 0.86, 0.72], { rough: 0.45 });
  const pencilButtons = renderer.addMesh(buildPencilButtons(), 'pencilButtons', [0.85, 0.75, 0.1], { rough: 0.5 });
  const blade = renderer.addMesh(buildBlade(), 'blade', steel, { rough: 0.25, metallic: 1 });

  // ---- instruments: the dominant hand (A, the pointer) and the non-dominant hand (B, driven while Space is held, then latched)
  const hands = new HandController();
  const tool = new ElectrodeTool();
  const scalpel = new ScalpelTool();
  const scissors = new ScissorsTool();
  const forceps = new ForcepsTool();
  const suction = new SuctionTool();
  const hook = new HookTool();
  const forcepsB = new ForcepsTool();
  const suctionB = new SuctionTool();
  const hookB = new HookTool();
  const scalpelMesh = renderer.addMesh(buildScalpel(), 'scalpel', steel, { rough: 0.3, metallic: 1 });
  const scissorsA = renderer.addMesh(buildScissorsArm(scissors.jawLength, scissors.shankLength), 'scissorsA', [0.9, 0.9, 0.92], { rough: 0.22, metallic: 0.9 });
  const scissorsB = renderer.addMesh(buildScissorsArm(scissors.jawLength, scissors.shankLength), 'scissorsB', [0.9, 0.9, 0.92], { rough: 0.22, metallic: 0.9 });
  const forcepsArmA = renderer.addMesh(buildForcepsArm(), 'forcepsA', [0.9, 0.9, 0.92], { rough: 0.22, metallic: 0.9 });
  const forcepsArmB = renderer.addMesh(buildForcepsArm(), 'forcepsB', [0.9, 0.9, 0.92], { rough: 0.22, metallic: 0.9 });
  const frazier = renderer.addMesh(buildFrazier(), 'frazier', [0.8, 0.8, 0.82], { rough: 0.35, metallic: 1 });
  const hookMesh = renderer.addMesh(buildSkinHook(), 'hook', [0.8, 0.8, 0.82], { rough: 0.3, metallic: 1 });
  const forcepsBArmA = renderer.addMesh(buildForcepsArm(), 'forcepsBA', [0.9, 0.9, 0.92], { rough: 0.22, metallic: 0.9 });
  const forcepsBArmB = renderer.addMesh(buildForcepsArm(), 'forcepsBB', [0.9, 0.9, 0.92], { rough: 0.22, metallic: 0.9 });
  const frazierB = renderer.addMesh(buildFrazier(), 'frazierB', [0.8, 0.8, 0.82], { rough: 0.35, metallic: 1 });
  const hookMeshB = renderer.addMesh(buildSkinHook(), 'hookB', [0.8, 0.8, 0.82], { rough: 0.3, metallic: 1 });
  let toolA: ToolKind = TOOL_ORDER.includes(toolParam) ? toolParam : 'pencil';
  let toolB: HandBKind = HAND_B_ORDER.includes(handBParam) ? handBParam : 'none';
  let pencilElectrode: 'blade' | 'needle' | 'ball' = 'blade';
  /** last pick for each hand (the GPU pick follows the pointer, which serves one hand at a time) */
  let hitA: vec3 | null = null, hitB: vec3 | null = null;
  /** lattice node each hand is holding, and the traction target (frozen while hand B is latched) */
  let nodeA = -1, nodeB = -1;
  const targetB = vec3.create();
  /** lift (mm) from the wheel while holding */
  let liftA = 0, liftB = 0;
  let spaceHeld = false;
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
  // depth peel of the 3D view (display only; the physics keeps the whole bed): K cycles off / block cut /
  // dissection, , and . step the depth (Shift: 0.1 mm), Ctrl + wheel scrubs it. Block cut hides everything
  // above the plane; dissection strips skin, fat, septa and fasciae above it, leaving vessels, the nerve,
  // the lipoma and any tissue the energy has changed standing in 3D.
  const bedDepthMm = volume.sizeMm[1];
  let peelMode = 0, peelMm = 3;
  if (peelParam) { peelMode = peelParam[0] === 'dissect' ? 2 : peelParam[0] === 'block' ? 1 : 0; if (peelParam[1] !== undefined && isFinite(parseFloat(peelParam[1]))) peelMm = parseFloat(peelParam[1]); }
  const stepPeel = (d: number) => { peelMm = Math.min(bedDepthMm, Math.max(0, peelMm + d)); };
  canvas.addEventListener('wheel', (e) => {
    if (e.ctrlKey) { stepPeel(e.deltaY * 0.0025); return; }
    // the wheel lifts an instrument that is holding tissue (traction, delivering a freed lipoma); otherwise it zooms (camera.ts)
    if (spaceHeld && hands.engagedB) liftB = Math.min(15, Math.max(0, liftB - e.deltaY * 0.01));
    else if (!spaceHeld && nodeA >= 0) liftA = Math.min(15, Math.max(0, liftA - e.deltaY * 0.01));
  }, { passive: true });
  window.addEventListener('keyup', (e) => { if (e.code === 'Space') spaceHeld = false; });
  window.addEventListener('keydown', (e) => {
    const scrub = e.code === 'Comma' || e.code === 'Period' || e.code === 'PageDown' || e.code === 'PageUp';
    if (e.code === 'Space') { spaceHeld = true; e.preventDefault(); return; }
    if (e.repeat && !scrub) return;
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
      case 'Tab': case 'KeyQ':
        if (e.shiftKey) { toolB = HAND_B_ORDER[(HAND_B_ORDER.indexOf(toolB) + 1) % HAND_B_ORDER.length]; hands.releaseB(); nodeB = -1; liftB = 0; }
        else { toolA = TOOL_ORDER[(TOOL_ORDER.indexOf(toolA) + 1) % TOOL_ORDER.length]; nodeA = -1; liftA = 0; }
        e.preventDefault(); break;
      case 'KeyT': forceps.toothed = !forceps.toothed; forcepsB.toothed = forceps.toothed; break;
      case 'KeyR': tool.roll += Math.PI / 4; break;
      case 'KeyV': debugMode = (debugMode + 1) % 4; scene.debugMode = debugMode; break;
      case 'KeyC': physics.slice.mode = (physics.slice.mode + 1) % 6; break;
      case 'KeyH': physics.slice.enabled = !physics.slice.enabled; sliceView.canvas.style.display = physics.slice.enabled ? '' : 'none'; break;
      case 'KeyJ': slicePerp = !slicePerp; break;
      case 'KeyK': peelMode = (peelMode + 1) % 3; break;
      case 'Comma': case 'PageDown': stepPeel(e.shiftKey ? 0.1 : 0.5); e.preventDefault(); break;
      case 'Period': case 'PageUp': stepPeel(e.shiftKey ? -0.1 : -0.5); e.preventDefault(); break;
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
    if (auto[0] === 'cut') gen.settings.cutWatts = watts; else if (auto[0] === 'coag') gen.settings.coagWatts = watts;
    autoUntil = parseFloat(auto[2]);
  }

  const dpr = headless ? 1 : Math.min(window.devicePixelRatio || 1, 2);
  let frames = 0, fps = 0, lastFpsT = performance.now(), lastT = performance.now(), frameIndex = 0;
  let vertexCount = 0;
  let autoDone = false;
  const HIDE = mat4.fromTranslation(mat4.create(), [0, -1e4, 0]);
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
        gen.activation = auto[0] === 'coag' ? 'coag' : auto[0] === 'cut' ? 'cut' : 'none';
        if (auto[5] !== undefined && !aim) {
          const f = Math.min(1, physics.simTime / autoUntil);
          pointer.ndcX = parseFloat(auto[3]) + (parseFloat(auto[5]) - parseFloat(auto[3])) * f;
          pointer.ndcY = parseFloat(auto[4]) + (parseFloat(auto[6]) - parseFloat(auto[4])) * f;
        }
      } else { gen.activation = 'none'; if (!autoDone && physics.simTime > autoUntil + 0.5) autoDone = true; }
      spaceHeld = physics.simTime < spaceUntil;
    }
    // the button: pointer, or the headless script; the hand controller decides which hand it belongs to
    const rawPressed = (pointer.buttons & 1) === 1 || (auto !== null && headless && graspParam && (holdParam || physics.simTime < autoUntil + 0.5));
    hands.update(spaceHeld, rawPressed);
    const pressedA = hands.pressedA;
    const ctrl = pointer.ctrl || (headless && spreadParam);
    if (!(auto && headless)) {
      gen.activation = pedal.cut || (pointer.handCut && pressedA) || panel.pedalCut ? 'cut' : pedal.coag || (pointer.handCoag && pressedA) || panel.pedalCoag ? 'coag' : 'none';
    }
    panel.refresh(); panel.update(physics.readout.zOhm, physics.readout.powerW);
    const mode = gen.activeMode;
    audio.set(mode ? mode.toneHz : 500, mode !== null);

    // tool placement from last frame's GPU pick
    const r = physics.readout;
    // the GPU pick marches in material space; instruments are posed on the deformed surface for display,
    // while the physics keeps material coordinates (the ElectrodeTool is placed from the material hit)
    const pick = pointer.inside ? r.hit : null;
    if (hands.followB) hitB = pick; else if (!spaceHeld) hitA = pick;
    const tilt = pointer.isPen && (Math.abs(pointer.tiltX) > 2 || Math.abs(pointer.tiltY) > 2) ? { x: pointer.tiltX, y: pointer.tiltY } : null;
    tool.pressure = pointer.pressure; tool.tiltX = pointer.tiltX; tool.tiltY = pointer.tiltY;
    scalpel.pressure = pointer.pressure; scissors.pressure = pointer.pressure;
    let suctionAt: vec3 | null = null;
    events.buzzing = false; events.suctionOn = false; events.instrumentContact = false;
    lattice.attachments.length = 0;
    const attachments: Attachment[] = lattice.attachments;
    let tool2: { a: vec3; b: vec3; r: number; mode: 0 | 1 | 2 } = { a: vec3.create(), b: vec3.create(), r: 0, mode: 0 };

    // ---- hand B (non-dominant): forceps, hook or suction; live while Space is held, latched where it was left
    let showB = toolB !== 'none' && hands.stateB !== 'parked';
    if (toolB === 'none') { hands.releaseB(); nodeB = -1; }
    if (!hands.engagedB) { nodeB = -1; liftB = 0; }
    if (showB) {
      const follow = hands.followB;
      const hb = follow ? hitB : null;
      if (toolB === 'forceps') {
        forcepsB.closed = hands.engagedB;
        if (follow) forcepsB.place(hb ? vec3.fromValues(hb[0], hb[1] + liftB, hb[2]) : null, cam.position, tilt, 0.3, true);
        if (!forcepsB.visible && follow) showB = false;
        if (forcepsB.closed && forcepsB.visible) {
          if (hands.regrabB || nodeB < 0) nodeB = lattice.nearestNode(forcepsB.mid, 3);
          if (follow) vec3.scaleAndAdd(targetB, forcepsB.mid, forcepsB.axis, 1.0);
          if (nodeB >= 0) attachments.push({ node: nodeB, target: vec3.clone(targetB), strength: 0.2 });
          tool2 = { a: forcepsB.tipA, b: forcepsB.tipB, r: forcepsB.jawRadius, mode: 1 };
        }
      } else if (toolB === 'hook') {
        if (hands.engagedB && hb && (hands.regrabB || nodeB < 0)) nodeB = lattice.nearestNode(hb, 4);
        if (hands.engagedB && nodeB >= 0 && follow) {
          // drag target: cursor ray intersected with the horizontal plane 1.5 mm above the node's start height, plus the lift
          const ray = cam.ray(pointer.ndcX, pointer.ndcY);
          const planeY = lattice.mat[nodeB * 3 + 1] + 1.5;
          const tHit = (planeY - ray.origin[1]) / ray.dir[1];
          if (tHit > 0) {
            vec3.scaleAndAdd(targetB, ray.origin, ray.dir, tHit);
            if (pullParam && headless) { targetB[0] += pullParam[0]; targetB[2] += pullParam[1]; }
            targetB[1] += liftB;
          }
        }
        if (hands.engagedB && nodeB >= 0) attachments.push({ node: nodeB, target: vec3.clone(targetB), strength: 0.25 });
        if (follow) hookB.place(hb, cam.position, tilt, hands.engagedB && nodeB >= 0 ? targetB : null, true);
        if (!hookB.visible && follow) showB = false;
      } else {
        suctionB.on = hands.engagedB;
        if (follow) suctionB.place(hb, cam.position, tilt, true);
        if (!suctionB.visible && follow) showB = false;
        if (suctionB.visible && suctionB.on) { suctionAt = vec3.clone(suctionB.tip); events.suctionOn = true; tool2 = { a: suctionB.tip, b: suctionB.tip, r: suctionB.drainRadius, mode: 2 }; }
      }
    }
    renderer.setModel(forcepsBArmA, showB && toolB === 'forceps' ? forcepsB.modelA : HIDE); renderer.setModel(forcepsBArmB, showB && toolB === 'forceps' ? forcepsB.modelB : HIDE);
    renderer.setModel(hookMeshB, showB && toolB === 'hook' ? hookB.model : HIDE);
    renderer.setModel(frazierB, showB && toolB === 'suction' ? suctionB.model : HIDE);

    // ---- hand A (dominant): the pointer, unless Space hands it to B (then A keeps its last pose)
    let electrode = false;
    if (toolA === 'pencil') {
      tool.type = pencilElectrode;
      tool.place(hitA, cam.position, tilt !== null);
      electrode = tool.visible;
      renderer.setModel(pencilBody, tool.model); renderer.setModel(pencilButtons, tool.model); renderer.setModel(blade, tool.model);
    } else {
      renderer.setModel(pencilBody, HIDE); renderer.setModel(pencilButtons, HIDE); renderer.setModel(blade, HIDE);
    }
    if (toolA === 'scalpel') {
      scalpel.place(hitA, cam.position, tilt, pressedA);
      renderer.setModel(scalpelMesh, scalpel.visible ? scalpel.model : HIDE);
      if (scalpel.visible && scalpel.cutting) physics.setMechCut(1, scalpel.sweeping ? scalpel.prevCentre : scalpel.centre, scalpel.centre, scalpel.axis, scalpel.wdir, scalpel.ndir, scalpel.halfLength, scalpel.halfWidth, scalpel.halfThickness, 0);
    } else renderer.setModel(scalpelMesh, HIDE);
    if (toolA === 'scissors') {
      const action = pressedA && ctrl ? 'spread' : hands.pressEdgeA ? 'snip' : 'none';
      scissors.place(hitA, cam.position, tilt, action, frameDt);
      renderer.setModel(scissorsA, scissors.visible ? scissors.modelA : HIDE); renderer.setModel(scissorsB, scissors.visible ? scissors.modelB : HIDE);
      if (scissors.visible && scissors.snip) physics.setMechCut(1, scissors.centre, scissors.centre, scissors.axis, scissors.wdir, scissors.ndir, scissors.halfLength, Math.max(scissors.open, 2) / 2, 0.15, 0);
      else if (scissors.visible && scissors.spreading) physics.setMechCut(2, scissors.centre, scissors.centre, scissors.axis, scissors.wdir, scissors.ndir, scissors.halfLength, scissors.open / 2, 0.4, scissors.spreadRadius);
    } else { renderer.setModel(scissorsA, HIDE); renderer.setModel(scissorsB, HIDE); }
    if (toolA === 'forceps') {
      forceps.closed = pressedA;
      if (!forceps.closed) { nodeA = -1; liftA = 0; }
      const lifted = hitA ? vec3.fromValues(hitA[0], hitA[1] + liftA + (liftRate > 0 && pressedA ? liftRate * Math.max(0, physics.simTime - 0.5) : 0), hitA[2]) : null;
      forceps.place(lifted, cam.position, tilt);
      renderer.setModel(forcepsArmA, forceps.modelA); renderer.setModel(forcepsArmB, forceps.modelB);
      if (forceps.visible && forceps.closed) {
        if (tool2.mode === 0) tool2 = { a: forceps.tipA, b: forceps.tipB, r: forceps.jawRadius, mode: 1 };
        // closed jaws carry the nearest node (and with it a freed lipoma)
        if (nodeA < 0) nodeA = lattice.nearestNode(forceps.mid, 3);
        if (nodeA >= 0) attachments.push({ node: nodeA, target: vec3.scaleAndAdd(vec3.create(), forceps.mid, forceps.axis, 1.0), strength: 0.2 });
        // buzzing single-handed: the closed jaws are the electrode (a pedal activation)
        tool.asForceps(forceps.tipA, forceps.tipB, forceps.jawRadius, forceps.axis); electrode = true;
        events.buzzing = gen.activation !== 'none';
      }
    } else { renderer.setModel(forcepsArmA, HIDE); renderer.setModel(forcepsArmB, HIDE); }
    if (toolA === 'suction') {
      suction.on = pressedA;
      suction.place(hitA, cam.position, tilt);
      renderer.setModel(frazier, suction.model);
      if (suction.visible && suction.on) { suctionAt = vec3.clone(suction.tip); events.suctionOn = true; if (tool2.mode === 0) tool2 = { a: suction.tip, b: suction.tip, r: suction.drainRadius, mode: 2 }; }
    } else renderer.setModel(frazier, HIDE);
    if (toolA === 'hook') {
      // skin hook: press to attach the nearest lattice node, drag to retract, release to let go
      let dragTarget: vec3 | null = null;
      if (pressedA) {
        if (nodeA < 0 && hitA) nodeA = lattice.nearestNode(hitA, 4);
        if (nodeA >= 0) {
          const ray = cam.ray(pointer.ndcX, pointer.ndcY);
          const planeY = lattice.mat[nodeA * 3 + 1] + 1.5;
          const tHit = (planeY - ray.origin[1]) / ray.dir[1];
          dragTarget = tHit > 0 ? vec3.scaleAndAdd(vec3.create(), ray.origin, ray.dir, tHit) : null;
          if (dragTarget) { dragTarget[1] += liftA; attachments.push({ node: nodeA, target: dragTarget, strength: 0.25 }); }
        }
      } else { nodeA = -1; liftA = 0; }
      hook.place(hitA, cam.position, tilt, dragTarget);
      renderer.setModel(hookMesh, hook.visible ? hook.model : HIDE);
    } else renderer.setModel(hookMesh, HIDE);
    if (toolA !== 'forceps' && toolA !== 'hook') nodeA = -1;

    // ---- the pencil against the other hand's instrument: buzzing the forceps, or an alternate-site burn
    if (toolA === 'pencil' && tool.visible && showB && gen.activation !== 'none') {
      if (toolB === 'forceps' && forcepsB.closed && metalDistance(tool.tip, forcepsB.mid, forcepsB.axis, 45, 1.6) < 1.0) {
        tool.asForceps(forcepsB.tipA, forcepsB.tipB, forcepsB.jawRadius, forcepsB.axis); events.buzzing = true;
      } else if (toolB === 'hook' && hookB.visible && metalDistance(tool.tip, hookB.tip, hookB.axis, 40, 1.0) < 1.0) {
        tool.asForceps(hookB.tip, vec3.scaleAndAdd(vec3.create(), hookB.tip, hookB.axis, 1.5), 0.4, hookB.axis); events.instrumentContact = true;
      } else if (toolB === 'suction' && suctionB.visible && metalDistance(tool.tip, suctionB.tip, suctionB.axis, 50, 1.7) < 1.0) {
        tool.asForceps(suctionB.tip, vec3.scaleAndAdd(vec3.create(), suctionB.tip, suctionB.axis, 2.0), 1.5, suctionB.axis); events.instrumentContact = true;
      }
    }
    if (!electrode) { tool.visible = false; if (gen.activation !== 'none') gen.activation = 'none'; }
    physics.setSecondaryTool(tool2.a, tool2.b, tool2.r, tool2.mode);
    cam.zoomEnabled = !((spaceHeld && hands.engagedB) || (!spaceHeld && nodeA >= 0));

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
    const toolName = (toolA === 'pencil' ? tool.type : toolA) + (toolB !== 'none' ? ` + ${toolB}` : '');
    events.update(physics.simTime, r, gen, toolName, frameDt);

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

    // depth peel: a display-only clip plane in the surface extraction; the section marks it with a dashed line
    surface.setClip(peelMode, [0, 1, 0], -peelMm);
    physics.slice.peelY = peelMode ? -peelMm : 1e9;

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
    const handAText = `<b>${TOOL_LABEL[toolA]}</b>${toolA === 'pencil' ? ' (' + pencilElectrode + ')' : toolA === 'forceps' ? (forceps.closed ? ' closed' : ' open') + (forceps.toothed ? ', toothed' : ', smooth') + (liftA > 0 ? `, lifted ${liftA.toFixed(0)} mm` : '') : toolA === 'scalpel' ? ` (${scalpel.cutting ? 'cutting ' + scalpel.depth.toFixed(1) + ' mm deep' : 'hold the button to cut'})` : toolA === 'scissors' ? ` (${scissors.spreading ? 'spreading ' + scissors.open.toFixed(0) + ' mm' : 'click: snip · Ctrl+hold: spread'})` : ''}`;
    const handBText = toolB === 'none' ? 'none (Shift+Tab)' : `<b>${TOOL_LABEL[toolB]}</b> ${hands.stateB === 'live' ? (hands.engagedB ? 'holding, live' : 'live') : hands.stateB === 'latched' ? 'latched, holding' + (liftB > 0 ? ` (lifted ${liftB.toFixed(0)} mm)` : '') : 'parked (hold Space)'}`;
    hud.innerHTML = `<b>Bovie Sim</b> · ${scenario.name}\n` +
      `FT10  cut: ${cutM.label} ${gen.settings.cutWatts} W   coag: ${coagM.label} ${gen.settings.coagWatts} W\n` +
      `right hand: ${handAText}   left hand: ${handBText}\n` +
      `${act}\n` +
      `max T ${r.maxTempC.toFixed(0)} °C · char ${r.carbonVoxels} vx · vaporised ${physics.totals.vapVoxels.toFixed(0)} vx · cut ${physics.totals.cutVoxels} vx · spread ${physics.totals.bluntVoxels} vx · sim t ${physics.simTime.toFixed(1)} s\n` +
      `blood lost ${r.bloodLostMl.toFixed(2)} ml · pooled ${r.poolMl.toFixed(2)} ml · bleeding ends: ${r.exposedArterial} art / ${r.exposedVenous} ven${r.oozeVoxels > 0 ? ` · skin edge oozing (${r.oozeVoxels})` : ''} · sealed ${r.sealedVoxels} · char plug ${r.charPlugVoxels} · rebleeds ${r.rebleeds}\n` +
      `under tip: ${hv}${lattice.capsuleTotal > 0 ? ` · capsule links ${lattice.capsuleAlive}/${lattice.capsuleTotal}${lattice.lipomaFree ? ' FREE' : ''}` : ''}\n` +
      `peel: ${peelMode ? `<b>${peelMode === 1 ? 'block cut' : 'dissection'} ${peelMm.toFixed(1)} mm</b> below the skin (display only; K mode · , . depth · Ctrl+wheel)` : 'off (K)'}\n` +
      `volume ${volume.dims.nx}×${volume.dims.ny}×${volume.dims.nz} @ ${volume.dims.h} mm · tris ${(vertexCount / 3 | 0).toLocaleString()}${vertexCount >= surface.capacity ? ' (CAPPED)' : ''} · ${fps.toFixed(0)} fps\n` +
      `Z/X pedal · LMB: hand switch / cut / snip / grasp / suck / catch · Tab right-hand tool · Shift+Tab left-hand tool · hold Space: left hand (release to latch) · wheel while holding: lift · 1-4 5-6 modes · [ ] ; ' watts · B/N/O electrode · T toothed · R roll · V overlay · C section · K peel · RMB orbit`;
    window.__simControl = { setDebug: (n: number) => { debugMode = n; scene.debugMode = n; }, getDebug: () => scene.debugMode, setSlice: (m: number) => { physics.slice.mode = m; },
      /** damage-zone extent in the current section (needs slice mode 2): width and depth in mm of pixels with omega >= ~0.53 */
      measureSlice: () => {
        const px = physics.slicePixels; let minX = 1e9, maxX = -1, maxY = -1; let count = 0;
        for (let y = 0; y < SLICE_H; y++) for (let x = 0; x < SLICE_W; x++) {
          const o = (x + y * SLICE_W) * 4; const rr = px[o], gg = px[o + 1], bb = px[o + 2];
          // damage ramp: blue (0.1,0.2,0.6) -> white; omega 0.53 is ~ (0.58,0.6,0.76). count pixels brighter than that, excluding electrode grey
          if (rr > 150 && gg > 150 && bb > 185 && !(Math.abs(rr - gg) < 6 && Math.abs(gg - bb) < 12 && rr > 180)) { count++; minX = Math.min(minX, x); maxX = Math.max(maxX, x); maxY = Math.max(maxY, y); }
        }
        const pxPerMmX = SLICE_W / physics.slice.widthMm, pxPerMmY = SLICE_H / physics.slice.depthMm;
        return count ? { widthMm: (maxX - minX + 1) / pxPerMmX, depthMm: (maxY + 1) / pxPerMmY - 1.5, pixels: count } : { widthMm: 0, depthMm: 0, pixels: 0 };
      }, setCam: (az: number, el: number, dist: number, tx = 0, tz = 0) => { cam.azimuth = az; cam.elevation = el; cam.distance = dist; cam.target[0] = tx; cam.target[2] = tz; },
      /** depth peel of the 3D view: mode 0 off, 1 block cut, 2 dissection; depth in mm below the skin */
      setPeel: (mode: number, mm: number) => { peelMode = Math.max(0, Math.min(2, mode | 0)); peelMm = Math.min(bedDepthMm, Math.max(0, mm)); },
      /** gap (mm) between the lattice nodes nearest two surface points (x0, z0) and (x1, z1): incision gape and retraction checks */
      gapAt: (x0: number, z0: number, x1: number, z1: number) => lattice.distanceBetween(vec3.fromValues(x0, 0, z0), vec3.fromValues(x1, 0, z1)),
      /** displacement (mm) of a lattice node from its material position */
      nodeDisp: (n: number) => [lattice.pos[n * 3] - lattice.mat[n * 3], lattice.pos[n * 3 + 1] - lattice.mat[n * 3 + 1], lattice.pos[n * 3 + 2] - lattice.mat[n * 3 + 2]] };
    window.__sim = { ready: true, frame: frameIndex, events: events.recent(12), tool: toolA, toolB, handB: hands.stateB, holdingB: hands.engagedB, nodeB, peel: { mode: peelMode, mm: peelMm }, lipomaFree: lattice.lipomaFree, capsule: [lattice.capsuleAlive, lattice.capsuleTotal], clusterRiseMm,
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
  // 16: a 16 mm trench over a centred lipoma down to its capsule (the scalpel incision, deepened), for dissection checks
  if (mode & 16) carveSdf(volume, (x, y, d) => Math.max(Math.abs(x) - 8, Math.abs(y) - (0.6 + d * 0.35), d - 3.2));
}

main().catch((e) => fatal(String(e?.message ?? e)));
