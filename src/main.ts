import freeplay from '../scenarios/freeplay.json';
import hemostasis from '../scenarios/hemostasis.json';
import lipoma from '../scenarios/lipoma.json';
import viscera from '../scenarios/viscera.json';
import { validateScenario } from '@/core/scenario/schema';
import { ForcepsTool } from '@/tools/forceps';
import { HemostatTool } from '@/tools/hemostat';
import { GauzeTool } from '@/tools/gauze';
import { SuctionTool } from '@/tools/suction';
import { ScalpelTool } from '@/tools/scalpel';
import { ScissorsTool } from '@/tools/scissors';
import { HandController, PICKUP_MM } from '@/tools/hands';
import { ToolKind, TOOL_ORDER, TOOL_LABEL, HandBKind, HAND_B_ORDER, HAND_B_LABEL, metalDistance } from '@/tools/instrument';
import { buildForcepsArm, buildFrazier, buildSkinHook, buildHemostatArm, buildGauze, buildScalpel, buildScissorsBlade } from '@/render/meshes';
import { mat4, vec4 } from 'gl-matrix';
import { Lattice, Attachment } from '@/mechanics/lattice';
import { HookTool } from '@/tools/hooks';
import { buildAnatomy, carveSdf } from '@/core/scenario/anatomy';
import { initWebGPU } from '@/render/gpu';
import { SurfaceExtractor } from '@/render/surface';
import { Renderer, SceneParams } from '@/render/renderer';
import { OrbitCamera } from '@/render/camera';
import { buildDrapeSet, buildPencilBody, buildPencilButtons, buildBlade } from '@/render/meshes';
import { Volume, cellType, cellState } from '@/core/volume';
import { TISSUE_NAMES, STATE_NAMES, Tissue } from '@/core/tissueProperties';
import { PhysicsPipeline, SecondaryToolMode, SLICE_W, SLICE_H } from '@/physics/pipeline';
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
const SCENARIOS: Record<string, unknown> = { freeplay, hemostasis, lipoma, viscera };
const scenarioId = params.get('scenario') ?? 'freeplay';
/** headless: starting tool and whether forceps jaws are closed during the scripted activation */
const toolParam = (params.get('tool') ?? 'pencil') as ToolKind;
const graspParam = params.get('grasp') === '1';
const holdParam = params.get('hold') === '1';
/** headless: hold the hemostat spread key during the scripted activation */
const spreadParam = params.get('spread') === '1';
/** headless: the left hand's instrument, and seconds the left-hand button is held at the aim point from t = 0 (it then stays latched) */
const handBParam = (params.get('handB') ?? 'none') as HandBKind;
const leftUntil = params.has('left') ? parseFloat(params.get('left')!) : -1;
/** headless: drag of the left hand while its button is held (x, z mm[, lift mm]), reached at 60 % of the hold */
const pullParam = params.get('pull')?.split(',').map(parseFloat) ?? null;
/** headless: where the pointer goes once the left hand is latched (x, z mm on the field as seen): the right hand works there */
const aim2 = params.get('aim2')?.split(',').map(parseFloat) ?? null;
/** headless: the scripted activation (and button) starts at this sim time instead of 0 */
const autoFrom = params.has('from') ? parseFloat(params.get('from')!) : 0;
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
  if (scenario.preCarve?.length || params.has('cut')) physics.markWound([0, 0, 0], [volume.dims.nx, volume.dims.ny, volume.dims.nz]);
  // mechanics lattice (XPBD) in material space; the renderer and picking sample its displacement
  const mechanicsOn = params.get('mech') !== '0';
  const lattice = new Lattice(volume, 3, scenario.skin.langerDeg * Math.PI / 180);
  const strandBuf = new Float32Array(new ArrayBuffer(Renderer.MAX_STRANDS * 8 * 4));
  let strandCount = 0;
  if (mechanicsOn) {
    renderer.attachLattice(lattice.infoData(), lattice.count);
    physics.setConstraintSegments(lattice.segmentData(volume), lattice.constraints.length);
    // the pick follows the tissue as the lattice displaces it: what is under the cursor is what is seen there
    const lb = renderer.latticeBuffers; physics.setLattice(lb.info, lb.disp);
  }
  /** world (as seen) -> material (where the voxels are); the identity until tissue is displaced */
  const toMat = (p: vec3): vec3 => mechanicsOn ? lattice.worldToMaterial(p) : vec3.clone(p);
  const toWorld = (p: vec3): vec3 => mechanicsOn ? lattice.materialToWorld(p) : vec3.clone(p);
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
  const drape = buildDrapeSet(sx / 2, sz / 2, scenario.vessels?.seed ?? 0);
  renderer.addMesh(drape.film, 'drapeFilm', [0.46, 0.31, 0.25], { rough: 0.18, kind: 3 });   // clear incise film: the skin shows through, glossy
  const drapePad = renderer.addMesh(drape.pad, 'drapePad', [0.075, 0.18, 0.37], { rough: 0.95, kind: 2 });
  const drapeDraw = renderer.addMesh(drape.drape, 'drape', [0.06, 0.16, 0.34], { rough: 0.9, kind: 1 });
  // four cotton towels square the window on top of the drape, held by Backhaus clips at the corners
  const towelDraws = drape.towels.map((t, i) => renderer.addMesh(t, 'towel' + i, [0.065, 0.17, 0.30], { rough: 0.95, kind: 4 }));
  const clipDraws = drape.clips.map((c, i) => renderer.addMesh(c.mesh, 'towelClip' + i, [0.62, 0.62, 0.63], { rough: 0.5, metallic: 1, model: c.model }));
  // satin-finished stainless (f0 of steel) and off-white moulded plastic
  const STEEL: [number, number, number] = [0.62, 0.62, 0.63];
  const pencilBody = renderer.addMesh(buildPencilBody(), 'pencilBody', [0.92, 0.86, 0.72], { rough: 0.45 });
  const pencilButtons = renderer.addMesh(buildPencilButtons(), 'pencilButtons', [0.85, 0.75, 0.1], { rough: 0.5 });
  const blade = renderer.addMesh(buildBlade(), 'blade', STEEL, { rough: 0.45, metallic: 1 });

  // the right hand (the pointer and the left button) holds any instrument; the left hand (the right button) holds a
  // hook, forceps or suction of its own and stays where it is put
  const hands = new HandController();
  const tool = new ElectrodeTool();
  const scalpel = new ScalpelTool();
  const scissors = new ScissorsTool();
  const forcepsL = new ForcepsTool();
  const suctionL = new SuctionTool();
  const hookL = new HookTool();
  const forceps = new ForcepsTool();
  const hemostat = new HemostatTool();
  const gauze = new GauzeTool();
  const suction = new SuctionTool();
  const forcepsA = renderer.addMesh(buildForcepsArm(), 'forcepsA', STEEL, { rough: 0.5, metallic: 1 });
  const forcepsB = renderer.addMesh(buildForcepsArm(), 'forcepsB', STEEL, { rough: 0.5, metallic: 1 });
  const hemoA = renderer.addMesh(buildHemostatArm(hemostat.armLength), 'hemostatA', STEEL, { rough: 0.5, metallic: 1 });
  const hemoB = renderer.addMesh(buildHemostatArm(hemostat.armLength), 'hemostatB', STEEL, { rough: 0.5, metallic: 1 });
  const gauzePad = renderer.addMesh(buildGauze(gauze.radius), 'gauze', [0.96, 0.96, 0.94], { rough: 0.95, kind: 4 });   // cotton weave (towel kind), stays procedural
  const frazier = renderer.addMesh(buildFrazier(), 'frazier', STEEL, { rough: 0.5, metallic: 1 });
  const hookMesh = renderer.addMesh(buildSkinHook(), 'hook', STEEL, { rough: 0.5, metallic: 1 });
  const scalpelMesh = renderer.addMesh(buildScalpel(), 'scalpel', STEEL, { rough: 0.45, metallic: 1 });
  const scissorsA = renderer.addMesh(buildScissorsBlade(scissors.jawLength, scissors.shankLength), 'scissorsA', STEEL, { rough: 0.5, metallic: 1 });
  const scissorsB = renderer.addMesh(buildScissorsBlade(scissors.jawLength, scissors.shankLength), 'scissorsB', STEEL, { rough: 0.5, metallic: 1 });
  // the left hand's set
  const forcepsLA = renderer.addMesh(buildForcepsArm(), 'forcepsLA', STEEL, { rough: 0.5, metallic: 1 });
  const forcepsLB = renderer.addMesh(buildForcepsArm(), 'forcepsLB', STEEL, { rough: 0.5, metallic: 1 });
  const frazierL = renderer.addMesh(buildFrazier(), 'frazierL', STEEL, { rough: 0.5, metallic: 1 });
  const hookMeshL = renderer.addMesh(buildSkinHook(), 'hookL', STEEL, { rough: 0.5, metallic: 1 });
  const allInstruments = [pencilBody, pencilButtons, blade, scalpelMesh, scissorsA, scissorsB, forcepsA, forcepsB, hemoA, hemoB, gauzePad, frazier, hookMesh];
  // texture maps from public/textures (CC0 sets via scripts/fetch-textures.mjs, or your own: docs/textures.md); applied as they load
  void renderer.loadTextures([
    { slot: 'drape', draws: [drapeDraw], tileMm: 30, tintIfGrey: [0.06, 0.16, 0.34], normalStrength: 1.8 },
    { slot: 'pad', draws: [drapePad], tileMm: 30, tintIfGrey: [0.075, 0.18, 0.37], fallbackSlot: 'drape', normalStrength: 1.8 },
    { slot: 'towel', draws: towelDraws, tileMm: 30, tintIfGrey: [0.065, 0.17, 0.30], fallbackSlot: 'pad', normalStrength: 1.8 },
    { slot: 'steel', draws: [blade, scalpelMesh, scissorsA, scissorsB, forcepsA, forcepsB, hemoA, hemoB, frazier, hookMesh, forcepsLA, forcepsLB, frazierL, hookMeshL, ...clipDraws], tileMm: 12, normalStrength: 0.6 },
    { slot: 'plastic', draws: [pencilBody], tileMm: 20, tintIfGrey: [0.92, 0.86, 0.72] },
  ]);
  const hookModel = mat4.create();
  let activeTool: ToolKind = TOOL_ORDER.includes(toolParam) ? toolParam : 'pencil';
  let pencilElectrode: 'blade' | 'needle' | 'ball' = 'blade';
  let hemoNodes: [number, number] = [-1, -1];
  const pointer = attachPointer(canvas);
  const pedal = new Pedal();
  let spaceHeld = false;
  // ---- the left hand
  let toolB: HandBKind = HAND_B_ORDER.includes(handBParam) ? handBParam : 'none';
  /** last pick for each hand (the pointer serves one hand at a time; the other keeps its place) */
  let hitA: vec3 | null = null;
  /** where the left hand's instrument sits (world mm), the node it holds, and the press it is being dragged from */
  const poseB = vec3.create(), baseB = vec3.create(), pressPointB = vec3.create();
  /** from the node the left hand holds to the point of the instrument on it: the instrument is drawn on the tissue it caught, wherever the pull has taken it */
  const grabOffsetB = vec3.create();
  /** where the left hand's instrument is seen: on its node, or at the pull target when it holds no node */
  const heldPointB = (): vec3 => nodeB >= 0 ? vec3.fromValues(lattice.pos[nodeB * 3] + grabOffsetB[0], lattice.pos[nodeB * 3 + 1] + grabOffsetB[1], lattice.pos[nodeB * 3 + 2] + grabOffsetB[2]) : vec3.clone(poseB);
  /** pull strength of the left hand's hold: an incised edge follows about two thirds of the pull, intact skin barely moves */
  const HOLD_STRENGTH = 0.4;
  let nodeB = -1, planeYB = 0, liftSincePressB = 0, pressValidB = false;
  /** the right button belongs to the left hand from a press on tissue until it is released */
  let leftClaimed = false, lastLeftDown = false, scriptPressAt = 0;
  /** keys under the user's left hand that trim the held instrument: slide (W A S D), raise (E), lower (F) */
  const trim = { w: false, a: false, s: false, d: false, e: false, f: false };
  /** scissors: a quick click snips, a hold pushes the closed tips in */
  let scissorsPressAt = -1, scissorsSpreadUsed = false, lastPressedA = false;
  /** the cursor ray passes within reach of the instrument the left hand is holding with */
  const rayNearHeld = (): boolean => {
    if (!hands.engagedB || toolB === 'none' || toolB === 'suction') return false;
    const ray = cam.ray(pointer.ndcX, pointer.ndcY);
    const tip = toolB === 'hook' ? hookL.tip : forcepsL.mid;
    const rel = vec3.sub(vec3.create(), tip, ray.origin);
    const t = vec3.dot(rel, ray.dir);
    return t > 0 && vec3.length(vec3.scaleAndAdd(rel, rel, ray.dir, -t)) < PICKUP_MM;
  };
  // a right-button press on the tissue (or on the held instrument) is the left hand's; anywhere else it orbits the camera
  cam.claimRight = () => { leftClaimed = toolB !== 'none' && ((pointer.inside && physics.readout.hit !== null) || rayNearHeld()); return leftClaimed; };
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
  const trimKey = (code: string, down: boolean): boolean => {
    const k = ({ KeyW: 'w', KeyA: 'a', KeyS: 's', KeyD: 'd', KeyE: 'e', KeyF: 'f' } as Record<string, keyof typeof trim>)[code];
    if (!k) return false;
    trim[k] = down; return true;
  };
  window.addEventListener('keyup', (e) => { if (e.code === 'Space') spaceHeld = false; trimKey(e.code, false); });
  window.addEventListener('blur', () => { spaceHeld = false; for (const k of Object.keys(trim) as (keyof typeof trim)[]) trim[k] = false; });
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
    // while the left hand is being placed (right button down), the wheel lifts what it holds instead of zooming
    if (lastLeftDown && hands.engagedB) liftSincePressB = Math.min(15, Math.max(-5, liftSincePressB - e.deltaY * 0.01));
  }, { passive: true });
  window.addEventListener('keydown', (e) => {
    if (e.code === 'Space') { spaceHeld = true; e.preventDefault(); }
    if (!e.ctrlKey && !e.metaKey && trimKey(e.code, true)) return;
    const scrub = e.code === 'Comma' || e.code === 'Period' || e.code === 'PageDown' || e.code === 'PageUp';
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
        // Tab: the right hand's next instrument; Shift+Tab: the left hand's (taking it out of the field first)
        if (e.shiftKey) { toolB = HAND_B_ORDER[(HAND_B_ORDER.indexOf(toolB) + 1) % HAND_B_ORDER.length]; hands.releaseB(); nodeB = -1; }
        else activeTool = TOOL_ORDER[(TOOL_ORDER.indexOf(activeTool) + 1) % TOOL_ORDER.length];
        e.preventDefault(); break;
      case 'Backquote': activeTool = TOOL_ORDER[(TOOL_ORDER.indexOf(activeTool) + TOOL_ORDER.length - 1) % TOOL_ORDER.length]; break;
      case 'KeyT': forceps.toothed = !forceps.toothed; forcepsL.toothed = forceps.toothed; break;
      case 'KeyR': tool.roll += Math.PI / 4; break;
      case 'KeyU': gauze.fresh(); break;
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
    if (auto[0] === 'cut') gen.settings.cutWatts = watts; else gen.settings.coagWatts = watts;
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

    // headless: the pointer aims at a field point; once the left hand is latched it moves on to where the right hand works
    const aimNow = headless ? (aim2 && physics.simTime >= Math.max(leftUntil, 0) ? aim2 : aim) : null;
    if (aimNow) {
      // project the aimed surface point to NDC with the current camera
      const yS = volume.surfaceY(aimNow[0], aimNow[1]);
      const clip = vec4.transformMat4(vec4.create(), [aimNow[0], yS + 0.3, aimNow[1], 1], cam.viewProj);
      pointer.ndcX = clip[0] / clip[3]; pointer.ndcY = clip[1] / clip[3]; pointer.inside = true;
    }
    // generator activation: pedal, hand switch (the pencil's own buttons), or script
    const scriptT = physics.simTime - autoFrom;
    if (auto && headless) {
      if (scriptT >= 0 && scriptT < autoUntil) {
        gen.activation = auto[0] === 'coag' ? 'coag' : auto[0] === 'cut' ? 'cut' : 'none';
        if (auto[5] !== undefined && !aim) {
          const f = Math.min(1, scriptT / autoUntil);
          pointer.ndcX = parseFloat(auto[3]) + (parseFloat(auto[5]) - parseFloat(auto[3])) * f;
          pointer.ndcY = parseFloat(auto[4]) + (parseFloat(auto[6]) - parseFloat(auto[4])) * f;
        }
      } else { gen.activation = 'none'; if (!autoDone && scriptT > autoUntil + 0.5) autoDone = true; }
    } else {
      const handSwitch = activeTool === 'pencil';
      gen.activation = pedal.cut || (handSwitch && pointer.handCut) || panel.pedalCut ? 'cut' : pedal.coag || (handSwitch && pointer.handCoag) || panel.pedalCoag ? 'coag' : 'none';
    }
    panel.refresh(); panel.update(physics.readout.zOhm, physics.readout.powerW);
    const mode = gen.activeMode;
    audio.set(mode ? mode.toneHz : 500, mode !== null);

    // ---- instruments, from last frame's GPU pick.
    // The pick marches the field as it is seen (displaced by the lattice), so its hit is a world point on the deformed
    // tissue. Instruments are posed there; the voxel kernels get the same points mapped back to material coordinates.
    const r = physics.readout;
    const pick = pointer.inside ? r.hit : null;
    const tilt = pointer.isPen && (Math.abs(pointer.tiltX) > 2 || Math.abs(pointer.tiltY) > 2) ? { x: pointer.tiltX, y: pointer.tiltY } : null;
    const scripted = auto !== null && headless && scriptT >= 0 && (holdParam || scriptT < autoUntil + 0.5);
    const pressed = (pointer.buttons & 1) === 1 || (scripted && graspParam);
    const spread = spaceHeld || (scripted && spreadParam);
    const pressEdgeA = pressed && !lastPressedA, releaseEdgeA = !pressed && lastPressedA;
    lastPressedA = pressed;
    // the left-hand button: the right mouse button from a press on the tissue until it is let go
    if (!pointer.right) leftClaimed = false;
    // (headless: the scripted press starts once the first pick has come back, and its pull is timed from there)
    const leftDown = toolB !== 'none' && ((leftClaimed && pointer.right) || (headless && physics.simTime < leftUntil && (lastLeftDown || r.hit !== null)));
    const leftEdge = leftDown && !lastLeftDown;
    if (leftEdge) scriptPressAt = physics.simTime;
    // the pointer serves one hand at a time; the right hand keeps its place while the left is being put down
    if (!leftDown) hitA = pick;
    const hit = hitA;
    tool.pressure = pointer.pressure; tool.tiltX = pointer.tiltX; tool.tiltY = pointer.tiltY;
    let suctionAt: vec3 | null = null;
    events.buzzing = false; events.suctionOn = false; events.gauzeDab = false; events.gauzeWipe = false; events.gauzeSaturated = gauze.saturated; events.instrumentContact = false;
    lattice.attachments.length = 0;
    const attachments: Attachment[] = lattice.attachments;
    // the kernels have one secondary-tool slot (grasp, suction, gauze, dissection): the right hand's instrument takes
    // it when it needs it, otherwise the left hand's grasp or suction
    type Slot = { a: vec3; b: vec3; r: number; mode: SecondaryToolMode; motion?: vec3; motionIsPoint?: boolean; pressure?: number; absorb?: number };
    let slotA: Slot | null = null, slotB: Slot | null = null;

    // ---- the left hand: hook, forceps or suction on the right mouse button; it stays where it is put
    const rayNow = cam.ray(pointer.ndcX, pointer.ndcY);
    /** the cursor on the horizontal plane the left hand is being dragged in (plus the scripted pull when headless) */
    const dragPoint = (): vec3 | null => {
      const t = (planeYB - rayNow.origin[1]) / rayNow.dir[1];
      if (!(t > 0)) return null;
      const c = vec3.scaleAndAdd(vec3.create(), rayNow.origin, rayNow.dir, t);
      if (headless && pullParam && leftUntil > 0) { const f = Math.min(1, Math.max(0, physics.simTime - scriptPressAt) / (0.6 * leftUntil)); c[0] += pullParam[0] * f; c[2] += pullParam[1] * f; liftSincePressB = (pullParam[2] ?? 0) * f; }
      return c;
    };
    let dragMm = 0;
    if (leftDown && !leftEdge && pressValidB) { const c = dragPoint(); if (c) dragMm = Math.hypot(c[0] - pressPointB[0], c[2] - pressPointB[2]) + Math.abs(liftSincePressB); }
    hands.update({ down: leftDown, dragMm, nearHeld: leftEdge && rayNearHeld(), sticky: toolB !== 'suction' });
    if (hands.releasedB) nodeB = -1;
    if (leftEdge && toolB !== 'suction') {
      liftSincePressB = 0; pressValidB = false;
      if (hands.regrabB) {
        // a new hold where the pointer is: the hook works 1.5 mm above the surface it catches, the forceps on it
        if (pick) {
          planeYB = pick[1] + (toolB === 'hook' ? 1.5 : 0);
          const c = dragPoint() ?? vec3.clone(pick);
          vec3.copy(pressPointB, c); vec3.copy(baseB, c); vec3.copy(poseB, c); pressValidB = true;
          // the hook catches the nearest node; the forceps close on what is between their tips
          nodeB = lattice.nearestNode(pick, toolB === 'hook' ? 4 : 3);
          if (nodeB >= 0) vec3.set(grabOffsetB, pick[0] - lattice.pos[nodeB * 3], pick[1] - lattice.pos[nodeB * 3 + 1], pick[2] - lattice.pos[nodeB * 3 + 2]);
          if (toolB === 'hook' && nodeB < 0) hands.releaseB();   // nothing there to catch
        } else hands.releaseB();
      } else if (hands.adjusting) {
        // the existing hold picked up again: the drag continues from where the instrument is
        planeYB = poseB[1];
        const c = dragPoint() ?? vec3.clone(poseB);
        vec3.copy(pressPointB, c); vec3.copy(baseB, poseB); pressValidB = true;
      }
    }
    if (hands.followB && pressValidB && toolB !== 'suction') {
      const c = dragPoint();
      if (c) vec3.set(poseB, baseB[0] + c[0] - pressPointB[0], baseB[1] + liftSincePressB, baseB[2] + c[2] - pressPointB[2]);
    }
    if (hands.engagedB && !leftDown && toolB !== 'suction') {
      // the user's own left hand on the keyboard trims the pull while the mouse works: slide across the field
      // (W away, S nearer, A and D sideways, as seen on screen), E raises, F lowers
      const sp = 8 * frameDt, ca = Math.cos(cam.azimuth), sa = Math.sin(cam.azimuth);
      const dx = (trim.d ? 1 : 0) - (trim.a ? 1 : 0), dz = (trim.s ? 1 : 0) - (trim.w ? 1 : 0), dy = (trim.e ? 1 : 0) - (trim.f ? 1 : 0);
      poseB[0] += (ca * dx + sa * dz) * sp; poseB[2] += (-sa * dx + ca * dz) * sp; poseB[1] = Math.min(25, Math.max(-bedDepthMm, poseB[1] + dy * sp));
    }
    let showB = false;
    if (toolB === 'hook') {
      showB = hands.engagedB && nodeB >= 0;
      if (showB) {
        attachments.push({ node: nodeB, target: vec3.sub(vec3.create(), poseB, grabOffsetB), strength: HOLD_STRENGTH });
        hookL.place(null, cam.position, null, heldPointB(), true);
      }
    } else if (toolB === 'forceps') {
      showB = hands.engagedB;
      if (showB) {
        // closed jaws carry the node they caught and press together what lies between them
        forcepsL.closed = true;
        forcepsL.place(heldPointB(), cam.position, null, 0.3, true);
        if (nodeB >= 0) attachments.push({ node: nodeB, target: vec3.sub(vec3.create(), poseB, grabOffsetB), strength: HOLD_STRENGTH });
        slotB = { a: forcepsL.tipA, b: forcepsL.tipB, r: forcepsL.jawRadius, mode: 1 };
      }
    } else if (toolB === 'suction') {
      // suction only acts while its button is held; it follows the pointer
      showB = leftDown && pick !== null;
      suctionL.on = showB;
      if (showB) {
        suctionL.place(pick, cam.position, null, true);
        suctionAt = vec3.clone(suctionL.tip); events.suctionOn = true;
        slotB = { a: suctionL.tip, b: suctionL.tip, r: suctionL.drainRadius, mode: 2 };
      }
    }
    renderer.setModel(hookMeshL, showB && toolB === 'hook' ? hookL.model : HIDE);
    renderer.setModel(forcepsLA, showB && toolB === 'forceps' ? forcepsL.modelA : HIDE); renderer.setModel(forcepsLB, showB && toolB === 'forceps' ? forcepsL.modelB : HIDE);
    renderer.setModel(frazierL, showB && toolB === 'suction' ? suctionL.model : HIDE);
    cam.zoomEnabled = !(leftDown && hands.engagedB);
    lastLeftDown = leftDown;

    // ---- the right hand: the pointer and the left button
    const show = (...visible: typeof allInstruments) => { for (const d of allInstruments) if (!visible.includes(d)) renderer.setModel(d, HIDE); };
    let electrode = false;
    if (activeTool !== 'forceps' && activeTool !== 'hook') hook.node = -1;
    if (activeTool !== 'hemostat') hemoNodes = [-1, -1];
    if (activeTool === 'pencil') {
      tool.type = pencilElectrode;
      tool.place(hit, cam.position, tilt !== null);
      renderer.setModel(pencilBody, tool.model); renderer.setModel(pencilButtons, tool.model); renderer.setModel(blade, tool.model);
      show(pencilBody, pencilButtons, blade);
      electrode = tool.visible;
    } else if (activeTool === 'scalpel') {
      // steel: while the button is held the blade is in the tissue and divides what it sweeps through, with no heat
      scalpel.pressure = pointer.pressure;
      scalpel.place(hit, cam.position, tilt, pressed);
      renderer.setModel(scalpelMesh, scalpel.visible ? scalpel.model : HIDE);
      show(scalpelMesh);
      if (scalpel.visible && scalpel.cutting) physics.setMechCut(toMat(scalpel.sweeping ? scalpel.prevCentre : scalpel.centre), toMat(scalpel.centre), scalpel.axis, scalpel.wdir, scalpel.ndir, scalpel.halfLength, scalpel.halfWidth, scalpel.halfThickness);
    } else if (activeTool === 'scissors') {
      // a quick click snips the sheet between the tips (taken where the tips were held); held, the closed tips go in
      // and the spread key opens them: blunt dissection with the hemostat's rules
      if (pressEdgeA) { scissorsPressAt = performance.now(); scissorsSpreadUsed = false; }
      if (pressed && spread) scissorsSpreadUsed = true;
      if (releaseEdgeA && scissors.visible && !scissorsSpreadUsed && performance.now() - scissorsPressAt < 350)
        physics.setMechCut(toMat(scissors.centre), toMat(scissors.centre), scissors.axis, scissors.wdir, scissors.ndir, scissors.halfLength, scissors.restMm / 2, 0.15);
      scissors.place(hit, cam.position, tilt, pressed, spread, pointer.pressure, frameDt);
      renderer.setModel(scissorsA, scissors.visible ? scissors.modelA : HIDE); renderer.setModel(scissorsB, scissors.visible ? scissors.modelB : HIDE);
      show(scissorsA, scissorsB);
      if (scissors.visible && scissors.spreading && scissors.open > 0.5) {
        physics.setDissect(true, scissors.jawRadius);
        slotA = { a: scissors.tipA, b: scissors.tipB, r: scissors.jawRadius, mode: 4, motion: hit ?? scissors.mid, motionIsPoint: true, pressure: 0, absorb: 1 };
      }
    } else if (activeTool === 'forceps') {
      forceps.closed = pressed;
      const liftedHit = hit && liftRate > 0 && pressed ? vec3.fromValues(hit[0], hit[1] + liftRate * Math.max(0, physics.simTime - 0.5), hit[2]) : hit;
      forceps.place(liftedHit, cam.position, tilt);
      renderer.setModel(forcepsA, forceps.modelA); renderer.setModel(forcepsB, forceps.modelB);
      show(forcepsA, forcepsB);
      if (forceps.visible && forceps.closed) {
        slotA = { a: forceps.tipA, b: forceps.tipB, r: forceps.jawRadius, mode: 1 };
        // buzzing the forceps: closed jaws become the electrode (a pedal activation); open jaws cannot be activated
        tool.asForceps(forceps.tipA, forceps.tipB, forceps.jawRadius, forceps.axis); electrode = true; events.buzzing = gen.activation !== 'none';
        // forceps lift: closed jaws carry the nearest node (and with it a freed lipoma)
        if (hook.node < 0) hook.node = lattice.nearestNode(forceps.mid, 3);
        if (hook.node >= 0) attachments.push({ node: hook.node, target: vec3.scaleAndAdd(vec3.create(), forceps.mid, forceps.axis, 1.0), strength: 0.2 });
      } else hook.node = -1;
    } else if (activeTool === 'hemostat') {
      // closed jaws grasp (and can be buzzed); held closed and moving, the tips push through a plane; closed and
      // spread (Space), the jaws open inside the tissue and dissect it bluntly
      hemostat.closed = pressed;
      hemostat.animate(spread, frameDt);
      hemostat.place(hit, cam.position, tilt, pointer.pressure);
      renderer.setModel(hemoA, hemostat.modelA); renderer.setModel(hemoB, hemostat.modelB);
      show(hemoA, hemoB);
      if (hemostat.visible && hemostat.closed) {
        // the jaws entered the tissue at the surface hit: the dissected sheet runs from there to the tips
        const entry = hit ?? hemostat.mid;
        if (hemostat.spreading && hemostat.spread > 0.05) { physics.setDissect(true, hemostat.jawRadius); slotA = { a: hemostat.tipA, b: hemostat.tipB, r: hemostat.jawRadius, mode: 4, motion: entry, motionIsPoint: true, pressure: 0, absorb: 1 }; }
        else if (!hemostat.spreading && hemostat.spread < 0.05 && hemostat.travel() > 0.08) { physics.setDissect(false, hemostat.jawRadius); slotA = { a: hemostat.prevMid, b: hemostat.mid, r: hemostat.jawRadius, mode: 5, motion: entry, motionIsPoint: true, pressure: 0, absorb: 1 }; }
        else slotA = { a: hemostat.tipA, b: hemostat.tipB, r: hemostat.jawRadius, mode: 1 };
        if (!hemostat.spreading && hemostat.spread < 0.05) { tool.asForceps(hemostat.tipA, hemostat.tipB, hemostat.jawRadius, hemostat.axis); electrode = true; events.buzzing = gen.activation !== 'none'; }
        // spreading pushes the two sides of the plane apart; a grasp carries the nearest node
        if (hemoNodes[0] < 0) hemoNodes = [lattice.nearestNode(hemostat.tipA, 3), lattice.nearestNode(hemostat.tipB, 3)];
        if (hemostat.spreading) {
          if (hemoNodes[0] >= 0) attachments.push({ node: hemoNodes[0], target: vec3.clone(hemostat.tipA), strength: 0.3 });
          if (hemoNodes[1] >= 0 && hemoNodes[1] !== hemoNodes[0]) attachments.push({ node: hemoNodes[1], target: vec3.clone(hemostat.tipB), strength: 0.3 });
        } else if (hemoNodes[0] >= 0) attachments.push({ node: hemoNodes[0], target: vec3.scaleAndAdd(vec3.create(), hemostat.mid, hemostat.axis, 0.5), strength: 0.2 });
      } else hemoNodes = [-1, -1];
    } else if (activeTool === 'suction') {
      suction.on = pressed;
      suction.place(hit, cam.position, tilt);
      renderer.setModel(frazier, suction.model);
      show(frazier);
      if (suction.visible && suction.on) { slotA = { a: suction.tip, b: suction.tip, r: suction.drainRadius, mode: 2 }; suctionAt = vec3.clone(suction.tip); events.suctionOn = true; }
    } else if (activeTool === 'gauze') {
      gauze.place(hit, cam.position, tilt, pressed, pointer.pressure, frameDt);
      renderer.setModel(gauzePad, gauze.visible ? gauze.model : HIDE);
      show(gauzePad);
      const on = gauze.visible && pressed;
      if (on) slotA = { a: gauze.centre, b: gauze.centre, r: gauze.radius, mode: 3, motion: gauze.velocity, motionIsPoint: false, pressure: gauze.pressure, absorb: gauze.absorb };
      gauze.soakedUl = Math.min(gauze.capacityUl, gauze.soakedUl + r.gauzeSoakUl);
      const speed = vec3.length(gauze.velocity);
      events.gauzeDab = on && speed < 15; events.gauzeWipe = on && speed >= 15;
      // the pad reddens as it fills
      const sat = gauze.saturation;
      renderer.setMaterial(gauzePad, [0.96 - 0.6 * sat, 0.96 - 0.9 * sat, 0.94 - 0.9 * sat], { rough: 0.95 - 0.5 * sat, kind: 4 });
      // the pressed pad compresses the surface under it
      if (on) for (const n of lattice.nodesWithin(gauze.centre, gauze.radius)) {
        attachments.push({ node: n, target: vec3.fromValues(lattice.pos[n * 3], lattice.mat[n * 3 + 1] - 1.0 * gauze.pressure, lattice.pos[n * 3 + 2]), strength: 0.15 });
      }
    } else {
      // skin hook in the right hand: press to attach the nearest lattice node, drag to retract, release to let go
      let dragTarget: vec3 | null = null;
      if (pressed) {
        if (hook.node < 0 && hit) hook.node = lattice.nearestNode(hit, 4);
        if (hook.node >= 0) {
          // drag target: cursor ray intersected with the horizontal plane 1.5 mm above the node's start height
          const planeY = lattice.mat[hook.node * 3 + 1] + 1.5;
          const tHit = (planeY - rayNow.origin[1]) / rayNow.dir[1];
          dragTarget = tHit > 0 ? vec3.scaleAndAdd(vec3.create(), rayNow.origin, rayNow.dir, tHit) : null;
          if (dragTarget) attachments.push({ node: hook.node, target: dragTarget, strength: 0.25 });
        }
      } else hook.node = -1;
      hook.place(hit, cam.position, tilt, dragTarget);
      mat4.copy(hookModel, hook.model);
      renderer.setModel(hookMesh, hook.visible ? hookModel : HIDE);
      show(hookMesh);
    }
    // ---- the active pencil against the left hand's instrument: touching held forceps buzzes what they hold; touching
    // a hook or the suction diverts the current into the tissue under it (an alternate-site burn)
    if (activeTool === 'pencil' && tool.visible && showB && gen.activation !== 'none') {
      if (toolB === 'forceps' && metalDistance(tool.tip, forcepsL.mid, forcepsL.axis, 45, 1.6) < 1.0) {
        tool.asForceps(forcepsL.tipA, forcepsL.tipB, forcepsL.jawRadius, forcepsL.axis); events.buzzing = true;
      } else if (toolB === 'hook' && metalDistance(tool.tip, hookL.tip, hookL.axis, 40, 1.0) < 1.0) {
        tool.asForceps(hookL.tip, vec3.scaleAndAdd(vec3.create(), hookL.tip, hookL.axis, 1.5), 0.4, hookL.axis); events.instrumentContact = true;
      } else if (toolB === 'suction' && metalDistance(tool.tip, suctionL.tip, suctionL.axis, 50, 1.7) < 1.0) {
        tool.asForceps(suctionL.tip, vec3.scaleAndAdd(vec3.create(), suctionL.tip, suctionL.axis, 2.0), 1.5, suctionL.axis); events.instrumentContact = true;
      }
    }
    if (!electrode) { tool.visible = false; if (gen.activation !== 'none') gen.activation = 'none'; }
    // the kernels work in material coordinates: map what the instruments touch from where it is seen
    vec3.copy(tool.physTip, toMat(tool.tip));
    const slot = slotA ?? slotB;
    const tool2Mode: SecondaryToolMode = slot ? slot.mode : 0;
    if (slot) physics.setSecondaryTool(toMat(slot.a), toMat(slot.b), slot.r, slot.mode); else physics.setSecondaryTool(vec3.create(), vec3.create(), 0, 0);
    if (slot && slot.motion) physics.setSecondaryMotion(slot.motionIsPoint ? toMat(slot.motion) : slot.motion, slot.pressure ?? 0, slot.absorb ?? 1);
    else physics.setSecondaryMotion(vec3.create(), 0, 1);
    const mode2 = gen.activeMode;
    renderer.setMaterial(pencilButtons, gen.activation === 'cut' ? [1, 0.9, 0.2] : gen.activation === 'coag' ? [0.2, 0.5, 1] : [0.85, 0.75, 0.1], { rough: 0.5, emissive: gen.activation !== 'none' ? 0.6 : 0 });
    scene.arcLight = mode2 && r.hit && tool.visible ? { x: tool.tip[0], y: tool.tip[1] + 1, z: tool.tip[2], intensity: mode2.kind === 'coag' ? 6 : 2.5 } : { x: 0, y: 0, z: 0, intensity: 0 };
    const ray = rayNow;
    if (aimNow) physics.setPickRay(vec3.fromValues(aimNow[0], 60, aimNow[1]), vec3.fromValues(0, -1, 0)); // vertical ray onto the aimed column
    else physics.setPickRay(ray.origin, ray.dir);
    // cross-section through the tip, in the plane perpendicular to the view direction, starting 1.5 mm above the surface
    {
      let right = vec3.fromValues(Math.cos(cam.azimuth), 0, -Math.sin(cam.azimuth));
      if (slicePerp && tool.visible) {
        // horizontal direction perpendicular to the blade's projected axis
        const ax = vec3.fromValues(tool.axis[0], 0, tool.axis[2]);
        if (vec3.length(ax) > 1e-3) { vec3.normalize(ax, ax); right = vec3.fromValues(-ax[2], 0, ax[0]); }
      }
      // the section samples the voxel bed: its origin is the material point under the cursor
      const anchor = r.hit ? toMat(r.hit) : vec3.fromValues(0, 0, 0);
      vec3.set(physics.slice.o, anchor[0], (r.hit ? anchor[1] : 0) + 1.5, anchor[2]);
      vec3.copy(physics.slice.u, right);
    }

    // bleeding: droplets from exposed vessel ends, spatter from boiling blood, landing into the blood layer
    for (const bp of r.bleedPoints) renderer.particles.bleed(toWorld(vec3.fromValues(bp[0], bp[1], bp[2])), bp[3], frameDt);
    for (const sp of r.spatterPoints) renderer.particles.spatter(toWorld(vec3.fromValues(sp[0], sp[1], sp[2])), sp[3]);
    // steam, smoke and arcs from the last readout
    if (mode && r.hit && r.contactVoxels > 0 && tool.visible) {
      const tip = vec3.clone(tool.tip);
      if (mode.kind === 'cut') renderer.particles.emit(0, tip, 20 + r.vapVoxels * 30, frameDt);
      else renderer.particles.emit(1, tip, 6 + (r.maxTempC >= 100 ? 10 : 0), frameDt);
      if (r.maxTempC >= 100) renderer.particles.emit(1, tip, mode.kind === 'cut' ? 3 : 4, frameDt);
      if (r.boilColumns > 0) renderer.particles.emit(0, tip, 25 + r.boilColumns * 4, frameDt);
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
    for (const l of renderer.particles.landings) { const m = toMat(vec3.fromValues(l[0], l[1], l[2])); physics.drops.push([m[0], m[1], m[2], l[3]]); }
    renderer.particles.landings.length = 0;
    renderer.particles.upload(gpu.device);
    events.update(physics.simTime, r, gen, (activeTool === 'pencil' ? tool.type : activeTool) + (toolB !== 'none' ? ' + ' + toolB : ''), frameDt);

    // mechanics: apply the last probe (severed / shrunk / dried constraints), muscle twitch, step, tears, strands, upload
    if (mechanicsOn) {
      const probe = physics.takeProbe();
      if (probe) lattice.applyProbe(probe.severed, probe.omega, probe.dry);
      if (mode && mode.kind === 'coag' && r.muscleHeatVoxels > 20 && tool.visible) lattice.stimulate(tool.tip, Math.min(2, 0.5 + r.iRms * 2), physics.simTime);
      lattice.step(frameDt, physics.simTime);
      renderer.uploadLattice(lattice.disp);
      for (const t of lattice.takeTears()) physics.tears.push(t);
      strandCount = lattice.strandData(strandBuf);
      renderer.uploadStrands(strandBuf, strandCount);
      if (lattice.lipomaFree && !lesionFreedReported) { lesionFreedReported = true; events.emit({ t: physics.simTime, type: 'lesion.freed', detail: `${lattice.capsuleTotal - lattice.capsuleAlive}/${lattice.capsuleTotal} capsule links cut`, teaching: 'The capsule is free around most of its circumference: the lipoma will deliver with gentle traction.', severity: 'info' }); }
      if (lattice.lipomaFree && !lesionDelivered) {
        // delivered when the freed cluster has risen out of its bed by 4 mm on average (traction lifts it clear of the fat)
        let dy = 0, cn = 0; for (let n = 0; n < lattice.count; n++) if (lattice.cluster[n] === 1) { dy += lattice.pos[n * 3 + 1] - lattice.mat[n * 3 + 1]; cn++; }
        clusterRiseMm = cn > 0 ? dy / cn : 0;
        if (clusterRiseMm > 4) { lesionDelivered = true; events.emit({ t: physics.simTime, type: 'lesion.delivered', detail: `lipoma lifted ${clusterRiseMm.toFixed(1)} mm out of its bed`, severity: 'info' }); }
      }
      if (mode && r.hit && tool.visible && cellType(r.hitCell) === Tissue.Lipoma && !lesionBreachReported) { lesionBreachReported = true; events.emit({ t: physics.simTime, type: 'lesion.breached', detail: 'energy applied inside the lipoma', teaching: 'You have entered the lipoma. Fat spills, the plane is lost and fragments left behind recur.', severity: 'bad' }); }
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
    const toolState = activeTool === 'pencil' ? ' (' + pencilElectrode + ')' : activeTool === 'forceps' ? (forceps.closed ? ' closed' : ' open') + (forceps.toothed ? ', toothed' : ', smooth')
      : activeTool === 'hemostat' ? (hemostat.spreading ? ` spreading ${(hemostat.span).toFixed(1)} mm` : hemostat.closed ? (tool2Mode === 5 ? ' pushing' : ' closed') : ' open')
      : activeTool === 'gauze' ? ` ${(gauze.soakedUl / 1000).toFixed(1)} / ${(gauze.capacityUl / 1000).toFixed(0)} ml${gauze.saturated ? ' SATURATED' : ''} (${gauze.discarded} used)`
      : activeTool === 'scalpel' ? (scalpel.cutting ? ` cutting ${scalpel.depth.toFixed(1)} mm deep` : ' (hold the button to cut)')
      : activeTool === 'scissors' ? (scissors.spreading ? ` spreading ${scissors.open.toFixed(1)} mm` : scissors.closed ? ' closed, in the tissue' : ' (click snips · hold + Space spreads)') : '';
    const leftState = toolB === 'none' ? 'none (Shift+Tab)' : `<b>${HAND_B_LABEL[toolB]}</b> ` + (hands.stateB === 'live' ? (hands.engagedB ? 'placing' : 'on the button') : hands.stateB === 'latched' ? 'holding (W A S D slide · E / F raise, lower · right-click it to let go)' : 'ready (right button on the tissue)');
    hud.innerHTML = `<b>Bovie Sim</b> · ${scenario.name}\n` +
      `FT10  cut: ${cutM.label} ${gen.settings.cutWatts} W   coag: ${coagM.label} ${gen.settings.coagWatts} W\n` +
      `right hand: <b>${TOOL_LABEL[activeTool]}</b>${toolState}   ·   left hand: ${leftState}\n` +
      `${act}\n` +
      `max T ${r.maxTempC.toFixed(0)} °C · char ${r.carbonVoxels} vx · vaporised ${physics.totals.vapVoxels.toFixed(0)} vx · sim t ${physics.simTime.toFixed(1)} s\n` +
      `blood lost ${r.bloodLostMl.toFixed(2)} ml · pooled ${r.poolMl.toFixed(2)} ml (clot ${r.clotMl.toFixed(2)}) · ooze ${r.oozeMl.toFixed(2)} ml from ${r.oozeVoxels} vx · bleeding ends: ${r.exposedArterial} art / ${r.exposedVenous} ven · sealed ${r.sealedVoxels} · char plug ${r.charPlugVoxels} · rebleeds ${r.rebleeds}\n` +
      `mechanics: ${strandCount} strands · torn ${lattice.tornLinks} links / ${physics.totals.tornVoxels} vx · dissected ${physics.totals.dissectedVoxels} vx · cut with steel ${physics.totals.cutVoxels} vx · gauze soaked ${(physics.totals.gauzeSoakUl / 1000).toFixed(2)} ml\n` +
      `under tip: ${hv}\n` +
      `peel: ${peelMode ? `<b>${peelMode === 1 ? 'block cut' : 'dissection'} ${peelMm.toFixed(1)} mm</b> below the skin (display only; K mode · , . depth · Ctrl+wheel)` : 'off (K)'}\n` +
      `volume ${volume.dims.nx}×${volume.dims.ny}×${volume.dims.nz} @ ${volume.dims.h} mm · tris ${(vertexCount / 3 | 0).toLocaleString()}${vertexCount >= surface.capacity ? ' (CAPPED)' : ''} · ${fps.toFixed(0)} fps\n` +
      `Z/X pedal cut/coag · LMB: hand-switch cut (pencil) / cut (scalpel) / snip (scissors) / grasp (forceps, hemostat) / suck / press (gauze) · Space spread (hemostat, scissors) · U fresh gauze · Tab right-hand tool (\` back) · Shift+Tab left-hand tool\n` +
      `RMB on tissue: left hand takes hold, drag pulls, it stays when released · RMB elsewhere orbits (or Alt+LMB) · 1-4 cut mode · 5-6 coag mode · [ ] ; ' watts · B/N/O electrode · T toothed · R roll · V overlay · C section · K peel`;
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
      setTool: (n: number) => { activeTool = TOOL_ORDER[Math.max(0, Math.min(TOOL_ORDER.length - 1, n))]; },
      /** gap (mm) between the lattice nodes nearest two surface points (x0, z0) and (x1, z1): incision gape and retraction checks */
      gapAt: (x0: number, z0: number, x1: number, z1: number) => lattice.distanceBetween(vec3.fromValues(x0, 0, z0), vec3.fromValues(x1, 0, z1)),
      /** displacement (mm) of a lattice node from its material position */
      nodeDisp: (n: number) => [lattice.pos[n * 3] - lattice.mat[n * 3], lattice.pos[n * 3 + 1] - lattice.mat[n * 3 + 1], lattice.pos[n * 3 + 2] - lattice.mat[n * 3 + 2]] as unknown as number,
      bloodStats: () => physics.debugBlood() as unknown as number,
      /** depth peel of the 3D view: mode 0 off, 1 block cut, 2 dissection; depth in mm below the skin */
      setPeel: (mode: number, mm: number) => { peelMode = Math.max(0, Math.min(2, mode | 0)); peelMm = Math.min(bedDepthMm, Math.max(0, mm)); } };
    window.__sim = { ready: true, frame: frameIndex, textures: renderer.loadedTextures.slice(), events: events.recent(12), tool: activeTool, toolB, handB: hands.stateB, holdingB: hands.engagedB, nodeB, poseB: Array.from(poseB), tipMat: Array.from(tool.physTip), tipWorld: Array.from(tool.tip), peel: { mode: peelMode, mm: peelMm }, lipomaFree: lattice.lipomaFree, capsule: [lattice.capsuleAlive, lattice.capsuleTotal], clusterRiseMm,
      gapMm: lattice.distanceBetween(vec3.fromValues(gapPts[0], 0, gapPts[1]), vec3.fromValues(gapPts[2], 0, gapPts[3])), vertexCount, fps, report, readout: { ...r, hit: r.hit ? Array.from(r.hit) : null }, totals: physics.totals, simTime: physics.simTime, autoDone, settings: gen.settings,
      strands: strandCount, tornLinks: lattice.tornLinks, yieldedLinks: lattice.yieldedLinks, gauzeUl: gauze.soakedUl };
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
