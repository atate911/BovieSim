// Headless state probe: runs a scenario for N seconds with the given query and prints the readout, events and
// blood-column statistics. Usage: node scripts/probe.mjs name seconds "query"
import { chromium } from '/opt/node-tools/node_modules/playwright/index.mjs';
import { mkdirSync } from 'node:fs';
const name = process.argv[2] ?? 'probe';
const seconds = parseFloat(process.argv[3] ?? '3');
const extra = process.argv[4] ?? 'voxel=0.5';
mkdirSync('screenshots', { recursive: true });
const args = ['--enable-unsafe-webgpu', '--use-webgpu-adapter=swiftshader', '--enable-features=WebGPU,Vulkan', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--no-sandbox'];
const browser = await chromium.launch({ headless: true, executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome', args });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') { const t = m.text(); if (!t.includes('404')) console.log(`[${m.type()}] ${t.slice(0, 500)}`); } });
page.on('pageerror', (e) => console.log(`[pageerror] ${e.message}`));
const t0 = Date.now();
try {
  await page.goto(`http://localhost:${process.env.PORT ?? 5199}/?headless=1&${extra}`, { waitUntil: 'load' });
  await page.waitForFunction(() => window.__sim?.ready, null, { timeout: 300000 });
  await page.waitForFunction((s) => window.__sim.simTime >= s, seconds, { timeout: 900000 });
  const sim = await page.evaluate(() => ({ readout: window.__sim.readout, totals: window.__sim.totals, simTime: window.__sim.simTime, events: window.__sim.events.map((e) => `${e.t.toFixed(1)}s ${e.type}: ${e.detail}`), strands: window.__sim.strands, torn: window.__sim.tornLinks, yielded: window.__sim.yieldedLinks, gauzeUl: window.__sim.gauzeUl, hud: document.getElementById('hud')?.innerText }));
  const blood = await page.evaluate(() => window.__simControl.bloodStats());
  console.log(sim.hud.split('\n').slice(2, 6).join('\n'));
  console.log('events:', sim.events.join(' | '));
  console.log('mech:', JSON.stringify({ strands: sim.strands, torn: sim.torn, yielded: sim.yielded, gauzeUl: sim.gauzeUl, totals: sim.totals }));
  console.log('blood:', JSON.stringify(blood));
  await page.screenshot({ path: `screenshots/${name}.png` });
  console.log(`wrote screenshots/${name}.png in ${((Date.now() - t0) / 1000).toFixed(1)}s`);
} catch (e) {
  console.log('FAILED', e.message.split('\n')[0]);
  await page.screenshot({ path: `screenshots/${name}-failed.png` }).catch(() => {});
} finally { await browser.close(); }
