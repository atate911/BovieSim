// Copies the shared scenario and teaching JSON (source of truth: /scenarios) into the Unity project.
// Run after editing any scenario: node scripts/sync-unity-scenarios.mjs
import { readdirSync, copyFileSync, mkdirSync } from 'node:fs';
const src = 'scenarios', dst = 'unity/BovieSim/Assets/BovieSim/Resources/BovieSim/Scenarios';
mkdirSync(dst, { recursive: true });
for (const f of readdirSync(src).filter((f) => f.endsWith('.json'))) { copyFileSync(`${src}/${f}`, `${dst}/${f}`); console.log('synced', f); }
