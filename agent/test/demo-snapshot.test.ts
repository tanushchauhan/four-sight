import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { exportDemoSnapshot, restoreDemoSnapshot } from '../src/shared/demo-snapshot.js';
import type { Incident } from '../src/damage/service.js';
import type { IncidentStore } from '../src/damage/storage.js';
const photo=Buffer.from([255,216,255,217]);
function fixture(): Incident & {demo:true; token:string} {
 return {id:'00000000-0000-4000-8000-000000000001',incident:true,title:'DEMO — test inspection',category:'other',severity:'unknown',confidence:.5,observations:['Synthetic test image'],uncertainties:['Demo only'],survey:'DEMO — test',location:'Staged scene',capturedAt:1,recordedAt:2,robotId:1,position:null,snapshotId:'test',imageSha256:createHash('sha256').update(photo).digest('hex'),model:'test',status:'needs_review',reviewNote:'',reviewedAt:null,demo:true,token:'must-never-be-exported'};
}
function memory(initial: Incident[]=[]):IncidentStore {
 const records=new Map(initial.map(r=>[r.id,structuredClone(r)]));
 return {list:async()=>[...records.values()].map(r=>structuredClone(r)),photo:async()=>photo,
 insert:async r=>{if(!records.has(r.id))records.set(r.id,structuredClone(r));},
 review:async(id,note)=>{const r=records.get(id)!;r.status='reviewed';r.reviewNote=note;r.reviewedAt=3;return r;}};
}
test('demo export strips unknown fields and restore preserves later reviews on repeat',async t=>{
 const dir=await mkdtemp(join(tmpdir(),'demo-snapshot-'));t.after(()=>rm(dir,{recursive:true,force:true}));
 const source=fixture();const manifest=await exportDemoSnapshot(memory([source]),dir,[source.id]);
 const text=await readFile(join(dir,manifest.incidents[0]!.record),'utf8');assert.ok(!text.includes(source.token));assert.ok(!text.includes('"token"'));
 const target=memory();assert.equal(await restoreDemoSnapshot(target,dir),1);await target.review(source.id,'Later human review');
 assert.equal(await restoreDemoSnapshot(target,dir),1);assert.equal((await target.list())[0]!.reviewNote,'Later human review');
});
test('demo restore rejects changed photos and unsafe manifest paths before any writes',async t=>{
 const dir=await mkdtemp(join(tmpdir(),'demo-snapshot-'));t.after(()=>rm(dir,{recursive:true,force:true}));
 const source=fixture();const manifest=await exportDemoSnapshot(memory([source]),dir,[source.id]);const target=memory();
 await writeFile(join(dir,manifest.incidents[0]!.photo),'tampered');await assert.rejects(restoreDemoSnapshot(target,dir),/integrity/);assert.equal((await target.list()).length,0);
 manifest.incidents[0]!.record='../../.env';await writeFile(join(dir,'manifest.json'),JSON.stringify(manifest));await assert.rejects(restoreDemoSnapshot(target,dir),/path/);
});
test('demo export refuses unselected or unlabeled production records',async t=>{
 const dir=await mkdtemp(join(tmpdir(),'demo-snapshot-'));t.after(()=>rm(dir,{recursive:true,force:true}));
 const source=fixture();await assert.rejects(exportDemoSnapshot(memory([source]),dir,[]),/explicit/);
 await assert.rejects(exportDemoSnapshot(memory([{...source,demo:false} as unknown as Incident]),dir,[source.id]),/demo records/);
});
