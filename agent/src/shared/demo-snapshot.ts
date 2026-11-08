/** Selected demo evidence only; credentials and runtime/control state never enter the snapshot. */
import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { Incident } from '../damage/service.js';
import type { IncidentStore } from '../damage/storage.js';
import { SpacetimeIncidentStore } from '../damage/storage.js';
import { SpacetimeState } from '../adapters/spacetime-state.js';
import { loadConfig, loadEnvFile } from '../config.js';
const FORMAT = 'four-sights-demo-snapshot';
const uuid = /^[0-9a-f-]{36}$/;
const hash = (photo: Buffer) => createHash('sha256').update(photo).digest('hex');
const fields = ['id','incident','title','category','severity','confidence','observations','uncertainties','survey','location','capturedAt','recordedAt','robotId','position','snapshotId','imageSha256','model','status','reviewNote','reviewedAt'] as const;
interface Manifest { format: string; version: number; demo: boolean; exportedAt: string; incidents: { id: string; record: string; photo: string; sha256: string }[]; }
function checkedRecord(raw: Incident & { demo?: boolean }) {
  if (!raw || !uuid.test(raw.id) || raw.demo !== true || !raw.title.startsWith('DEMO') || !raw.survey.startsWith('DEMO')) throw new Error('Only explicitly labeled demo records can be included');
  return { ...Object.fromEntries(fields.map(k => [k, raw[k]])), demo: true } as unknown as Incident & { demo: true };
}
export async function exportDemoSnapshot(store: IncidentStore, directory: string, ids: string[]) {
  if (!ids.length || ids.some(id => !uuid.test(id)) || new Set(ids).size !== ids.length) throw new Error('Select explicit, unique incident IDs');
  const available = await store.list();
  const selected = ids.map(id => { const r = available.find(r => r.id === id); if (!r) throw new Error('Selected incident not found: '+id); return checkedRecord(r); });
  const manifest: Manifest = { format: FORMAT, version: 1, demo: true, exportedAt: new Date().toISOString(), incidents: [] };
  await mkdir(join(directory,'incidents'),{recursive:true});
  for (const record of selected) {
    const photo = await store.photo(record.id);
    if (hash(photo) !== record.imageSha256) throw new Error('Photo hash mismatch: '+record.id);
    const entry = { id: record.id, record: `incidents/${record.id}.json`, photo: `incidents/${record.id}.jpg`, sha256: record.imageSha256 };
    await writeFile(join(directory,entry.record),JSON.stringify(record,null,2)+'\n');
    await writeFile(join(directory,entry.photo),photo);manifest.incidents.push(entry);
  }
  await writeFile(join(directory,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');return manifest;
}
export async function restoreDemoSnapshot(store: IncidentStore, directory: string) {
  const manifest = JSON.parse(await readFile(join(directory,'manifest.json'),'utf8')) as Manifest;
  if (manifest.format !== FORMAT || manifest.version !== 1 || manifest.demo !== true || !Array.isArray(manifest.incidents)) throw new Error('Unsupported demo snapshot');
  // Check every file before writes. Exact relative paths prevent manifest path traversal.
  const records = await Promise.all(manifest.incidents.map(async entry => {
    if (!uuid.test(entry.id) || entry.record !== `incidents/${entry.id}.json` || entry.photo !== `incidents/${entry.id}.jpg`) throw new Error('Invalid snapshot path');
    const record = checkedRecord(JSON.parse(await readFile(join(directory,entry.record),'utf8')));
    const photo = await readFile(join(directory,entry.photo));
    if (record.id !== entry.id || hash(photo) !== entry.sha256 || record.imageSha256 !== entry.sha256) throw new Error('Photo integrity check failed: '+entry.id);
    return {record,photo};
  }));
  for (const {record,photo} of records) {
    await store.insert(record,photo);
    const saved = (await store.list()).find(r => r.id === record.id || (r.survey === record.survey && r.location === record.location && r.imageSha256 === record.imageSha256));
    if (!saved || hash(await store.photo(saved.id)) !== record.imageSha256) throw new Error('Restore verification failed: '+record.id);
  }
  return records.length;
}
async function main() {
  const [action,dir,...ids] = process.argv.slice(2);
  if (!dir || !['export','restore'].includes(action ?? '')) throw new Error('Usage: demo-snapshot.ts export|restore DIRECTORY [INCIDENT_ID ...]');
  loadEnvFile();const c = loadConfig();if (c.stateMode !== 'spacetime') throw new Error('Configure STATE_MODE=spacetime first');
  const store = new SpacetimeIncidentStore(new SpacetimeState(c.spacetimeUrl,c.spacetimeDb,c.spacetimeToken,c.searchArea));
  if (action === 'export') console.log(`Exported ${(await exportDemoSnapshot(store,resolve(dir),ids)).incidents.length} selected demo records with verified photos.`);
  else console.log(`Restored and verified ${await restoreDemoSnapshot(store,resolve(dir))} demo records. Existing reviews preserved; no robot control state restored.`);
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main().catch(e=>{console.error((e as Error).message);process.exitCode=1;});
