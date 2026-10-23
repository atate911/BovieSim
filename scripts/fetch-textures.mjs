// Downloads CC0 texture sets for the browser build into public/textures/ (git-ignored) and writes a manifest
// the renderer reads at start-up. Run from the repo root on a PC with internet access:
//   node scripts/fetch-textures.mjs            # all slots
//   node scripts/fetch-textures.mjs steel      # one slot
// Sources: ambientCG (https://ambientcg.com, CC0 1.0) via its public API, Poly Haven (https://polyhaven.com,
// CC0 1.0) via api.polyhaven.com. Each slot lists search terms and known asset ids tried in order; the first
// set that downloads wins. Everything fetched is listed in public/textures/DOWNLOADED.md.
import { mkdirSync, writeFileSync, existsSync, readFileSync } from 'node:fs';
import { inflateRawSync } from 'node:zlib';
import { join } from 'node:path';

const OUT = 'public/textures';
/** slot -> what to look for. tileMm: physical size of one texture repeat on the drape / instrument. */
const SLOTS = {
  drape: { tileMm: 30, acgQuery: ['fabric nonwoven', 'fabric felt', 'fabric'], acgIds: ['Fabric030', 'Fabric026', 'Fabric004'], phCategory: 'fabric', phTags: ['cloth', 'fabric'] },
  steel: { tileMm: 12, acgQuery: ['brushed metal', 'brushed steel', 'metal'], acgIds: ['Metal007', 'Metal006', 'Metal032'], phCategory: 'metal', phTags: ['brushed', 'steel', 'metal'] },
  plastic: { tileMm: 20, acgQuery: ['plastic'], acgIds: ['Plastic010', 'Plastic006'], phCategory: 'plastic', phTags: ['plastic'] },
};

const wanted = process.argv.slice(2);
const slots = wanted.length ? wanted : Object.keys(SLOTS);
mkdirSync(OUT, { recursive: true });
const manifestPath = join(OUT, 'manifest.json');
const manifest = existsSync(manifestPath) ? JSON.parse(readFileSync(manifestPath, 'utf8')) : {};
const log = [];

async function get(url, as = 'buffer') {
  const r = await fetch(url, { headers: { 'User-Agent': 'BovieSim texture fetch (CC0 assets)' } });
  if (!r.ok) throw new Error(`${r.status} ${url}`);
  return as === 'json' ? r.json() : as === 'text' ? r.text() : Buffer.from(await r.arrayBuffer());
}

/** Minimal ZIP reader (stored + deflate), enough for ambientCG archives. */
function unzip(buf) {
  const files = {};
  let eocd = buf.length - 22;
  while (eocd >= 0 && buf.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  if (eocd < 0) throw new Error('not a zip');
  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);
  for (let i = 0; i < count; i++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error('bad central directory');
    const method = buf.readUInt16LE(p + 10), csize = buf.readUInt32LE(p + 20);
    const nlen = buf.readUInt16LE(p + 28), elen = buf.readUInt16LE(p + 30), clen = buf.readUInt16LE(p + 32);
    const lho = buf.readUInt32LE(p + 42);
    const name = buf.toString('utf8', p + 46, p + 46 + nlen);
    const lnlen = buf.readUInt16LE(lho + 26), lelen = buf.readUInt16LE(lho + 28);
    const data = buf.subarray(lho + 30 + lnlen + lelen, lho + 30 + lnlen + lelen + csize);
    if (!name.endsWith('/')) files[name] = method === 8 ? inflateRawSync(data) : method === 0 ? Buffer.from(data) : null;
    p += 46 + nlen + elen + clen;
  }
  return files;
}

function pickMaps(files, matchers) {
  const out = {};
  for (const [key, re] of Object.entries(matchers)) {
    const name = Object.keys(files).find((n) => re.test(n));
    if (name && files[name]) out[key] = files[name];
  }
  return out.albedo && out.normal ? out : null;
}

/** ambientCG: the API lists assets with their download links; the 1K JPG zip holds Color, NormalGL, Roughness (+ Metalness, AO). */
async function fromAmbientCG(slot, spec) {
  const candidates = [];
  for (const q of spec.acgQuery) {
    try {
      const j = await get(`https://ambientcg.com/api/v2/full_json?type=Material&q=${encodeURIComponent(q)}&limit=12&include=downloadData,tagData`, 'json');
      for (const a of j.foundAssets ?? []) {
        const link = JSON.stringify(a).match(/https:\/\/ambientcg\.com\/get\?file=([A-Za-z0-9]+)_1K-JPG\.zip/);
        if (link) candidates.push({ id: link[1], url: link[0] });
      }
    } catch (e) { log.push(`ambientCG search "${q}": ${e.message}`); }
  }
  for (const id of spec.acgIds) candidates.push({ id, url: `https://ambientcg.com/get?file=${id}_1K-JPG.zip` });
  const seen = new Set();
  for (const c of candidates) {
    if (seen.has(c.id)) continue; seen.add(c.id);
    try {
      const zip = unzip(await get(c.url));
      const maps = pickMaps(zip, { albedo: /_Color\.jpg$/i, normal: /_NormalGL\.jpg$/i, roughness: /_Roughness\.jpg$/i, metalness: /_Metalness\.jpg$/i });
      if (!maps) { log.push(`${c.id}: archive has no Color + NormalGL`); continue; }
      return { id: c.id, source: `https://ambientcg.com/view?id=${c.id}`, author: 'Lennart Demes / ambientCG', maps };
    } catch (e) { log.push(`${c.id}: ${e.message}`); }
  }
  return null;
}

/** Poly Haven: assets?t=textures lists ids with categories/tags; files/<id> gives per-map URLs (Diffuse, nor_gl, Rough). */
async function fromPolyHaven(slot, spec) {
  try {
    const all = await get(`https://api.polyhaven.com/assets?t=textures&c=${encodeURIComponent(spec.phCategory)}`, 'json');
    const ids = Object.entries(all)
      .map(([id, a]) => ({ id, score: spec.phTags.reduce((s, t, i) => s + ((a.tags ?? []).includes(t) || id.includes(t) ? spec.phTags.length - i : 0), 0) }))
      .filter((x) => x.score > 0).sort((a, b) => b.score - a.score).slice(0, 6);
    for (const { id } of ids) {
      try {
        const f = await get(`https://api.polyhaven.com/files/${id}`, 'json');
        const url = (k) => f?.[k]?.['1k']?.jpg?.url;
        if (!url('Diffuse') || !url('nor_gl')) continue;
        const maps = { albedo: await get(url('Diffuse')), normal: await get(url('nor_gl')) };
        if (url('Rough')) maps.roughness = await get(url('Rough'));
        return { id, source: `https://polyhaven.com/a/${id}`, author: 'Poly Haven contributors (see asset page)', maps };
      } catch (e) { log.push(`polyhaven ${id}: ${e.message}`); }
    }
  } catch (e) { log.push(`polyhaven list: ${e.message}`); }
  return null;
}

for (const slot of slots) {
  const spec = SLOTS[slot];
  if (!spec) { console.log(`unknown slot ${slot}; known: ${Object.keys(SLOTS).join(', ')}`); continue; }
  console.log(`${slot}: looking for a CC0 set…`);
  const set = (await fromAmbientCG(slot, spec)) ?? (await fromPolyHaven(slot, spec));
  if (!set) { console.log(`${slot}: nothing could be downloaded (see messages below); the procedural material stays.`); continue; }
  const dir = join(OUT, slot); mkdirSync(dir, { recursive: true });
  const entry = { tileMm: spec.tileMm, id: set.id, source: set.source };
  for (const [k, data] of Object.entries(set.maps)) { writeFileSync(join(dir, k + '.jpg'), data); entry[k] = `${slot}/${k}.jpg`; }
  manifest[slot] = entry;
  console.log(`${slot}: ${set.id} (${set.source}) -> ${dir}/`);
}
writeFileSync(manifestPath, JSON.stringify(manifest, null, 2));
writeFileSync(join(OUT, 'DOWNLOADED.md'), '# CC0 textures fetched by scripts/fetch-textures.mjs\n\n| Slot | Asset | Source | Licence |\n|---|---|---|---|\n' +
  Object.entries(manifest).map(([s, e]) => `| ${s} | ${e.id} | ${e.source} | CC0 1.0 |`).join('\n') + '\n');
if (log.length) console.log('\nnotes:\n  ' + log.join('\n  '));
console.log(`\nmanifest: ${manifestPath}`);
