import freeplay from '../scenarios/freeplay.json';
import { validateScenario } from '@/core/scenario/schema';
import { buildAnatomy } from '@/core/scenario/anatomy';
import { initWebGPU } from '@/render/gpu';
import { SurfaceExtractor } from '@/render/surface';
import { Renderer, SceneParams } from '@/render/renderer';
import { OrbitCamera } from '@/render/camera';
import { buildDrapes, buildPencilBody, buildPencilButtons, buildBlade } from '@/render/meshes';
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

declare global { interface Window { __sim: Record<string, unknown>; __simControl: Record<string, (...a: number[]) => unknown> } }

function fatal(msg: string) {
  document.getElementById('app')!.innerHTML = `<div class="fatal"><div><h1>Bovie Sim cannot start</h1><p>${msg}</p></div></div>`;
  console.error(msg);
}

async function main() {
  const canvas = document.getElementById('gpu-canvas') as HTMLCanvasElement;
  const hud = document.getElementById('hud')!;
  const scenario = validateScenario(freeplay);
  hud.textContent = 'Building anatomy…';

  const { volume, report } = buildAnatomy(scenario, voxelOverride, seedOverride);
  if (params.has('cut')) carveDemo(volume, parseInt(params.get('cut')!, 10));
  const gpu = await initWebGPU(canvas, headless);
  volume.upload(gpu.device);
  const surface = new SurfaceExtractor(gpu.device, volume);
  const renderer = new Renderer(gpu, surface);
  const physics = new PhysicsPipeline(gpu.device, volume);
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
  const pencilBody = renderer.addMesh(buildPencilBody(), 'pencilBody', [0.92, 0.86, 0.72], { rough: 0.45 });
  const pencilButtons = renderer.addMesh(buildPencilButtons(), 'pencilButtons', [0.85, 0.75, 0.1], { rough: 0.5 });
  const blade = renderer.addMesh(buildBlade(), 'blade', [0.85, 0.85, 0.86], { rough: 0.25, metallic: 1 });

  const tool = new ElectrodeTool();
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
      case 'KeyB': tool.type = 'blade'; break;
      case 'KeyN': tool.type = 'needle'; break;
      case 'KeyO': tool.type = 'ball'; break;
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
    const frameDt = headless ? 1 / 30 : Math.min(0.05, (now - lastT) / 1000); // headless: fixed-step sim time
    lastT = now;
    const w = headless ? W : Math.max(8, Math.floor(canvas.clientWidth * dpr)), hgt = headless ? H : Math.max(8, Math.floor(canvas.clientHeight * dpr));
    if (!headless && (canvas.width !== w || canvas.height !== hgt)) { canvas.width = w; canvas.height = hgt; }
    renderer.resize(w, hgt);
    renderer.time = now / 1000;
    cam.update(w / hgt);

    // generator activation: pedal, hand switch, or script
    if (auto && headless) {
      if (physics.simTime < autoUntil) {
        gen.activation = auto[0] === 'coag' ? 'coag' : 'cut';
        if (auto[5] !== undefined) {
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
    tool.pressure = pointer.pressure; tool.tiltX = pointer.tiltX; tool.tiltY = pointer.tiltY;
    tool.place(pointer.inside ? r.hit : null, cam.position, pointer.isPen && (Math.abs(pointer.tiltX) > 2 || Math.abs(pointer.tiltY) > 2));
    renderer.setModel(pencilBody, tool.model); renderer.setModel(pencilButtons, tool.model); renderer.setModel(blade, tool.model);
    renderer.setMaterial(pencilButtons, gen.activation === 'cut' ? [1, 0.9, 0.2] : gen.activation === 'coag' ? [0.2, 0.5, 1] : [0.85, 0.75, 0.1], { rough: 0.5, emissive: gen.activation !== 'none' ? 0.6 : 0 });
    scene.arcLight = mode && r.hit ? { x: tool.tip[0], y: tool.tip[1] + 1, z: tool.tip[2], intensity: mode.kind === 'coag' ? 6 : 2.5 } : { x: 0, y: 0, z: 0, intensity: 0 };
    const ray = cam.ray(pointer.ndcX, pointer.ndcY);
    physics.setPickRay(ray.origin, ray.dir);
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

    // steam, smoke and arcs from the last readout
    if (mode && r.hit && r.contactVoxels > 0) {
      const tip = vec3.clone(tool.tip);
      if (mode.kind === 'cut') renderer.particles.emit(0, tip, 30 + r.vapVoxels * 40, frameDt);
      else renderer.particles.emit(1, tip, 12 + (r.maxTempC >= 100 ? 18 : 0), frameDt);
      if (r.maxTempC >= 100) renderer.particles.emit(1, tip, mode.kind === 'cut' ? 5 : 8, frameDt);
      const reach = (mode.vPeak / 1000) * mode.arcMmPerKv;
      const arcs = mode.kind === 'coag' ? (mode.id === 'spray' ? 4 : 2) : (Math.random() < 0.15 ? 1 : 0);
      for (let a = 0; a < arcs; a++) {
        const ang = Math.random() * Math.PI * 2, rad = Math.random() * Math.max(reach, 0.3);
        const end = vec3.fromValues(r.hit[0] + Math.cos(ang) * rad, r.hit[1] + 0.05, r.hit[2] + Math.sin(ang) * rad);
        const start = vec3.scaleAndAdd(vec3.create(), tool.tip, tool.axis, 0.4 + Math.random() * 1.2);
        renderer.particles.arc(start, end);
      }
    }
    renderer.particles.update(frameDt, null);
    renderer.particles.upload(gpu.device);
    events.update(physics.simTime, r, gen, tool.type, frameDt);

    renderer.writeFrame(cam, scene);
    const enc = gpu.device.createCommandEncoder();
    const changed = physics.step(enc, tool, gen, frameDt);
    if (surface.dirty || changed) surface.encode(enc);
    renderer.render(enc);
    gpu.device.queue.submit([enc.finish()]);
    if (headless) { await gpu.device.queue.onSubmittedWorkDone(); await renderer.present2D(canvas); }
    physics.read();
    physics.readSlice().then((ok) => { if (ok) sliceView.draw(); });
    surface.readCount().then((c) => { vertexCount = c; });

    frames++; frameIndex++;
    if (now - lastFpsT > 500) { fps = frames * 1000 / (now - lastFpsT); frames = 0; lastFpsT = now; }
    const hv = r.hit ? `${TISSUE_NAMES[cellType(r.hitCell)]} (${STATE_NAMES[cellState(r.hitCell)]})  ${r.hit[0].toFixed(1)}, ${r.hit[1].toFixed(1)}, ${r.hit[2].toFixed(1)} mm` : '—';
    const cutM = FT10_MODES[gen.settings.cutMode], coagM = FT10_MODES[gen.settings.coagMode];
    const act = gen.activation === 'none' ? 'idle' : `<b>${gen.activation.toUpperCase()} ${gen.activeWatts} W</b>  Z ${isFinite(r.zOhm) && r.zOhm < 1e8 ? r.zOhm.toFixed(0) + ' Ω' : 'open'}  P ${r.powerW.toFixed(1)} W  V ${r.vRms.toFixed(0)} V  contact ${r.contactVoxels} vx`;
    hud.innerHTML = `<b>Bovie Sim</b> · ${scenario.name}\n` +
      `FT10  cut: ${cutM.label} ${gen.settings.cutWatts} W   coag: ${coagM.label} ${gen.settings.coagWatts} W   electrode: ${tool.type}\n` +
      `${act}\n` +
      `max T ${r.maxTempC.toFixed(0)} °C · char ${r.carbonVoxels} vx · vaporised ${physics.totals.vapVoxels.toFixed(0)} vx · sim t ${physics.simTime.toFixed(1)} s\n` +
      `under tip: ${hv}\n` +
      `volume ${volume.dims.nx}×${volume.dims.ny}×${volume.dims.nz} @ ${volume.dims.h} mm · tris ${(vertexCount / 3 | 0).toLocaleString()}${vertexCount >= surface.capacity ? ' (CAPPED)' : ''} · ${fps.toFixed(0)} fps\n` +
      `Z/X pedal cut/coag · LMB hand-switch cut, Ctrl+LMB coag · 1-4 cut mode · 5-6 coag mode · [ ] ; ' watts · B/N/O electrode · R roll · V overlay · RMB orbit`;
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
    window.__sim = { ready: true, frame: frameIndex, events: events.recent(12), vertexCount, fps, report, readout: { ...r, hit: r.hit ? Array.from(r.hit) : null }, totals: physics.totals, simTime: physics.simTime, autoDone, settings: gen.settings };
    if (!headless) requestAnimationFrame(loop); else setTimeout(loop, 10);
  };
  loop();
}

/** Demo carve for visual review: 1 = incision trench to fat, 2 = pit down to muscle, 3 = both. */
function carveDemo(volume: Volume, mode: number) {
  const { nx, ny, nz, h } = volume.dims;
  const surfK = volume.surfaceK;
  const carve = (sdf: (x: number, y: number, depth: number) => number) => {
    for (let k = 0; k < nz; k++) for (let j = 0; j < ny; j++) for (let i = 0; i < nx; i++) {
      const x = (i + 0.5 - nx / 2) * h, y = (j + 0.5 - ny / 2) * h, depth = (surfK - (k + 0.5)) * h;
      const d = sdf(x, y, depth);
      if (d < h) {
        const idx = volume.index(i, j, k);
        const keep = Math.min(1, Math.max(0, 0.5 + d / h));
        volume.occupancy[idx] = Math.min(volume.occupancy[idx], keep);
        if (volume.occupancy[idx] < 0.5) volume.cell[idx] = 0;
      }
    }
  };
  if (mode & 1) carve((x, y, d) => Math.max(Math.abs(x) - 15, Math.abs(y - 12) - (2.0 + d * 0.3), d - 9));
  if (mode & 2) carve((x, y, d) => Math.max(Math.hypot(x + 6, y + 10) - (15 - d * 0.42), d - 24));
}

main().catch((e) => fatal(String(e?.message ?? e)));
