import { mkdir, readFile, readdir, rename, writeFile } from 'node:fs/promises';
import { createHash, randomUUID } from 'node:crypto';
import { join } from 'node:path';
import type { Incident } from './service.js';
import type { SpacetimeState } from '../adapters/spacetime-state.js';
export interface IncidentStore {
  list(): Promise<Incident[]>;
  insert(record: Incident, photo: Buffer): Promise<void>;
  photo(id: string): Promise<Buffer>;
  review(id: string, note: string): Promise<Incident>;
}
/** Retained for importing legacy records and isolated offline tests. */
export class FileIncidentStore implements IncidentStore {
  constructor(private dir: string) {}
  async list(): Promise<Incident[]> {
    await mkdir(this.dir, { recursive: true });
    const names = (await readdir(this.dir)).filter(n => /^[0-9a-f-]{36}\.json$/.test(n));
    return (await Promise.all(names.map(async n => JSON.parse(await readFile(join(this.dir,n),'utf8')) as Incident)))
      .sort((a,b) => b.recordedAt - a.recordedAt);
  }
  private async save(record: Incident) {
    const temp = join(this.dir, `${record.id}.${randomUUID()}.tmp`);
    await writeFile(temp, JSON.stringify(record,null,2)); await rename(temp,join(this.dir,record.id+'.json'));
  }
  async insert(record: Incident, photo: Buffer) {
    await mkdir(this.dir,{recursive:true}); await writeFile(join(this.dir,record.id+'.jpg'),photo,{flag:'wx'}); await this.save(record);
  }
  photo(id: string) { return readFile(join(this.dir,id+'.jpg')); }
  async review(id: string, note: string) {
    const r = (await this.list()).find(r => r.id===id); if (!r) throw new Error('Incident not found');
    r.status='reviewed';r.reviewNote=note;r.reviewedAt=Date.now();await this.save(r);return r;
  }
}
const quote = (s: string) => "'"+s.replace(/'/g,"''")+"'";
export class SpacetimeIncidentStore implements IncidentStore {
  constructor(private db: SpacetimeState) {}
  async list(): Promise<Incident[]> {
    return (await this.db.sql('SELECT record_json FROM damage_incident')).map(r=>JSON.parse(String(r.record_json)) as Incident)
      .sort((a,b)=>b.recordedAt-a.recordedAt);
  }
  async insert(record: Incident, photo: Buffer) {
    if (createHash('sha256').update(photo).digest('hex') !== record.imageSha256) throw new Error('Evidence photo hash does not match record');
    await this.db.call('record_damage',[JSON.stringify(record),photo.toString('base64')]);
  }
  async photo(id: string): Promise<Buffer> {
    if (!/^[0-9a-f-]{36}$/.test(id)) throw new Error('Invalid incident ID');
    const rows=await this.db.sql(`SELECT image_base64 FROM damage_photo WHERE id = ${quote(id)}`);
    if (!rows[0]) throw Object.assign(new Error('Photo not found'),{code:'ENOENT'});
    return Buffer.from(String(rows[0].image_base64),'base64');
  }
  async review(id: string,note: string) {
    await this.db.call('review_damage',[id,note]);
    const record=(await this.list()).find(r=>r.id===id);if(!record)throw new Error('Incident not found');return record;
  }
}
