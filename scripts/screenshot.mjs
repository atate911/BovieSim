// Headless render check: starts the Vite dev server, opens the app with ?headless=1 in Chromium with
// the SwiftShader WebGPU adapter, waits for frames, and writes screenshots/<name>.png.
// Usage: node scripts/screenshot.mjs [name] [extra query, e.g. "voxel=0.5&debug=3"]
import { chromium } from '/opt/node-tools/node_modules/playwright/index.mjs';
import { spawn } from 'node:child_process';
import { mkdirSync } from 'node:fs';

const name = process.argv[2] ?? 'frame';
const extra = process.argv[3] ?? 'voxel=0.5';
const port = 5199;
mkdirSync('screenshots', { recursive: true });
const vite = spawn('npx', ['vite', '--port', String(port), '--strictPort'], { stdio: ['ignore', 'pipe', 'pipe'] });
let viteOut = '';
vite.stdout.on('data', (d) => { viteOut += d; });
vite.stderr.on('data', (d) => { viteOut += d; });
await new Promise((res, rej) => { const t = setInterval(() => { if (viteOut.includes('Local:')) { clearInterval(t); res(); } }, 100); setTimeout(() => { clearInterval(t); rej(new Error('vite did not start\n' + viteOut)); }, 30000); });

const args = ['--enable-unsafe-webgpu', '--use-webgpu-adapter=swiftshader', '--enable-features=WebGPU,Vulkan', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--no-sandbox'];
const browser = await chromium.launch({ headless: true, executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome', args });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
const logs = [];
page.on('console', (m) => logs.push(`[${m.type()}] ${m.text()}`));
page.on('pageerror', (e) => logs.push(`[pageerror] ${e.message}`));
try {
  await page.goto(`http://localhost:${port}/?headless=1&${extra}`, { waitUntil: 'load' });
  await page.waitForFunction(() => window.__sim?.ready, null, { timeout: 180000 });
  await page.waitForTimeout(2500);
  const sim = await page.evaluate(() => ({ vertexCount: window.__sim.vertexCount, report: window.__sim.report, hud: document.getElementById('hud')?.innerText }));
  console.log(JSON.stringify(sim, null, 1));
  await page.screenshot({ path: `screenshots/${name}.png` });
  console.log(`wrote screenshots/${name}.png`);
} catch (e) {
  console.error('FAILED', e.message);
} finally {
  console.log(logs.slice(0, 40).join('\n'));
  await browser.close();
  vite.kill();
}
