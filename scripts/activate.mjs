// Headless activation check: runs a scripted activation (auto=mode,watts,seconds) and screenshots
// the result in normal view and temperature overlay. Usage: node scripts/activate.mjs name "auto=cut,30,1" [extra]
import { chromium } from '/opt/node-tools/node_modules/playwright/index.mjs';
import { mkdirSync } from 'node:fs';
const name = process.argv[2] ?? 'act';
const auto = process.argv[3] ?? 'auto=cut,30,1';
const extra = process.argv[4] ?? 'voxel=0.5&cam=-0.35,0.95,70';
const settle = parseFloat(process.argv[5] ?? '0');
mkdirSync('screenshots', { recursive: true });
const args = ['--enable-unsafe-webgpu', '--use-webgpu-adapter=swiftshader', '--enable-features=WebGPU,Vulkan', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--no-sandbox'];
const browser = await chromium.launch({ headless: true, executablePath: '/opt/pw-browsers/chromium-1194/chrome-linux/chrome', args });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') console.log(`[${m.type()}] ${m.text().slice(0, 400)}`); });
page.on('pageerror', (e) => console.log(`[pageerror] ${e.message}`));
const t0 = Date.now();
try {
  await page.goto(`http://localhost:${process.env.PORT ?? 5199}/?headless=1&${auto}&${extra}`, { waitUntil: 'load' });
  // poll the readout while the activation runs
  const samples = [];
  let midShot = false;
  let settleUntil;
  for (let n = 0; n < 100000; n++) {
    if (!midShot) {
      const st = await page.evaluate(() => window.__sim ? { t: window.__sim.simTime, p: window.__sim.readout?.powerW } : null).catch(() => null);
      if (st && st.p > 0 && st.t > 0.25) {
        midShot = true;
        const wait = async (k) => { const f0 = await page.evaluate(() => window.__sim.frame); await page.waitForFunction((t) => window.__sim.frame >= t, f0 + k, { timeout: 600000 }); };
        await page.evaluate(() => window.__simControl.setSlice(4)); await wait(2); await page.screenshot({ path: `screenshots/${name}-mid-potential.png` });
        await page.evaluate(() => window.__simControl.setSlice(5)); await wait(2); await page.screenshot({ path: `screenshots/${name}-mid-heating.png` });
        await page.evaluate(() => window.__simControl.setSlice(1)); await wait(2); await page.screenshot({ path: `screenshots/${name}-mid-temp.png` });
        await page.evaluate(() => window.__simControl.setSlice(0));
      }
    }
    const s = await page.evaluate(() => window.__sim ? { t: window.__sim.simTime, done: window.__sim.autoDone, r: window.__sim.readout } : null).catch(() => null);
    if (s && s.r && (s.r.powerW > 0 || s.r.exposedArterial + s.r.exposedVenous > 0 || s.r.bloodLostMl > 0)) samples.push(`t=${s.t.toFixed(2)} Z=${s.r.zOhm.toFixed(0)} P=${s.r.powerW.toFixed(1)} V=${s.r.vRms.toFixed(0)} contact=${s.r.contactVoxels} maxT=${s.r.maxTempC.toFixed(0)} char=${s.r.carbonVoxels} | blood=${s.r.bloodLostMl.toFixed(3)}ml pool=${s.r.poolMl.toFixed(3)}ml art=${s.r.exposedArterial} ven=${s.r.exposedVenous} sealed=${s.r.sealedVoxels} plug=${s.r.charPlugVoxels} rebleed=${s.r.rebleeds}`);
    if (s && s.done && settleUntil === undefined) settleUntil = s.t + settle;
    if (s && s.done && s.t >= settleUntil) break;
    await page.waitForTimeout(500);
  }
  console.log(samples.filter((_, i, a) => i % Math.max(1, Math.floor(a.length / 8)) === 0).join('\n'));
  const sim = await page.evaluate(() => ({ readout: window.__sim.readout, totals: window.__sim.totals, simTime: window.__sim.simTime, hud: document.getElementById('hud')?.innerText, events: window.__sim.events }));
  console.log(sim.hud);
  console.log('events:', sim.events.map((e) => `${e.t.toFixed(1)}s ${e.type}`).join(' | '));
  console.log('mechanics:', JSON.stringify(await page.evaluate(() => ({ gapMm: window.__sim.gapMm, lipomaFree: window.__sim.lipomaFree, capsule: window.__sim.capsule, clusterRiseMm: window.__sim.clusterRiseMm }))));
  console.log(JSON.stringify({ totals: sim.totals, simTime: sim.simTime }));
  await page.screenshot({ path: `screenshots/${name}.png` });
  const waitFrames = async (n) => { const f0 = await page.evaluate(() => window.__sim.frame); await page.waitForFunction((t) => window.__sim.frame >= t, f0 + n, { timeout: 600000 }); };
  await page.evaluate(() => { window.__simControl.setDebug(1); window.__simControl.setSlice(1); }); await waitFrames(3);
  await page.screenshot({ path: `screenshots/${name}-temp.png` });
  await page.evaluate(() => { window.__simControl.setDebug(2); window.__simControl.setSlice(2); }); await waitFrames(3);
  await page.screenshot({ path: `screenshots/${name}-omega.png` });
  console.log('damage zone in section:', JSON.stringify(await page.evaluate(() => window.__simControl.measureSlice())));
  console.log(`wrote screenshots/${name}*.png in ${((Date.now() - t0) / 1000).toFixed(1)}s`);
} catch (e) {
  console.log('FAILED', e.message.split('\n')[0]);
  await page.screenshot({ path: `screenshots/${name}-failed.png` }).catch(() => {});
} finally { await browser.close(); }
