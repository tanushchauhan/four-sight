/** Idempotent import: originals stay on disk as a backup, retries do not overwrite reviews. */
import { createHash } from 'node:crypto';
import { readFile, readdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { loadConfig, loadEnvFile } from '../config.js';
import { SpacetimeState } from '../adapters/spacetime-state.js';
import { FileIncidentStore, SpacetimeIncidentStore } from '../damage/storage.js';
loadEnvFile();
const config=loadConfig();
if(config.stateMode!=='spacetime')throw new Error('Configure STATE_MODE=spacetime before importing.');
const db=new SpacetimeState(config.spacetimeUrl,config.spacetimeDb,config.spacetimeToken,config.searchArea);
await db.sql('SELECT id FROM robot');
const source=new FileIncidentStore(resolve(process.env.DAMAGE_DATA_DIR ?? 'data/incidents'));
const target=new SpacetimeIncidentStore(db);
let incidents=0, events=0;
for(const record of await source.list()){
  const photo=await source.photo(record.id);await target.insert(record,photo);
  const saved=(await target.list()).find(r=>r.id===record.id || (r.survey===record.survey&&r.location===record.location&&r.imageSha256===record.imageSha256));
  if(!saved || createHash('sha256').update(await target.photo(saved.id)).digest('hex')!==record.imageSha256)throw new Error('Photo verification failed for '+record.id);
  incidents++;
}
let names:string[]=[];
try{names=(await readdir('logs')).filter(n=>n.endsWith('.jsonl'));}catch(e){if((e as NodeJS.ErrnoException).code!=='ENOENT')throw e;}
for(const name of names){
  for(const line of (await readFile('logs/'+name,'utf8')).split('\n').filter(s=>s.trim())){
    const value=JSON.stringify(JSON.parse(line));
    await db.call('import_app_event',[createHash('sha256').update(value).digest('hex'),value]);events++;
  }
}
console.log(`Verified ${incidents} legacy incident(s) with photos and imported ${events} audit event(s). Originals retained; import is safe to rerun.`);
