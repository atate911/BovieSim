// Attach to a running dev server (port 5199) and screenshot. Usage: node scripts/shot.mjs name "query" [timeoutMs]
import { chromium } from '/opt/node-tools/node_modules/playwright/index.mjs';
import { mkdirSync } from 'node:fs';
const name = process.argv[2] ?? 'frame';
const extra = process.argv[3] ?? 'voxel=0.5';
const timeout = parseInt(process.argv[4] ?? '120000', 10);
/** frames to wait after the app reports ready (the software GPU can take seconds per frame) */
const settleFrames = parseInt(process.argv[5] ?? '0', 10);
mkdirSync('screenshots', { recursive: true });
const args = ['--enable-unsafe-webgpu', '--use-webgpu-adapter=swiftshader', '--enable-features=WebGPU,Vulkan', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--no-sandbox'];
const browser = await chromium.launch({ headless: true, executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome', args });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('console', (m) => console.log(`[${m.type()}] ${m.text().slice(0, 600)}`));
page.on('pageerror', (e) => console.log(`[pageerror] ${e.message}`));
const t0 = Date.now();
try {
  await page.goto(`http://localhost:${process.env.PORT ?? 5199}/?headless=1&${extra}`, { waitUntil: 'load' });
  await page.waitForFunction(() => window.__sim?.ready, null, { timeout });
  if (settleFrames > 0) { const f0 = await page.evaluate(() => window.__sim.frame); await page.waitForFunction((n) => window.__sim.frame >= n, f0 + settleFrames, { timeout }); }
  await page.waitForTimeout(1500);
  const sim = await page.evaluate(() => ({ vertexCount: window.__sim.vertexCount, fps: window.__sim.fps, hud: document.getElementById('hud')?.innerText }));
  console.log(JSON.stringify(sim));
  await page.screenshot({ path: `screenshots/${name}.png` });
  console.log(`wrote screenshots/${name}.png in ${((Date.now() - t0) / 1000).toFixed(1)}s`);
} catch (e) {
  console.log('FAILED', e.message.split('\n')[0]);
  await page.screenshot({ path: `screenshots/${name}-failed.png` }).catch(() => {});
} finally { await browser.close(); }
