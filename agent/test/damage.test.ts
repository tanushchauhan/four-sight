import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, rm, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import jpeg from 'jpeg-js';
import { DamageService, assessment, type Assessment } from '../src/damage/service.js';
import { loadConfig } from '../src/config.js';
import { SimRobot } from '../src/adapters/sim-robot.js';
import { startOperatorFeed } from '../src/operator/feed-server.js';
import { OperatorVoice } from '../src/operator/voice.js';

const finding: Assessment = { incident: true, title: 'Water across floor', category: 'water', severity: 'moderate', confidence: .85,
  observations: ['A reflective pool covers part of the visible floor.'], uncertainties: ['Source and depth are unknown.'] };
const photo = jpeg.encode({ width: 2, height: 2, data: Buffer.alloc(16, 180) }, 70).data.toString('base64');
async function setup(t: any, analyze: (image: string) => Promise<Assessment> = async () => finding) {
  const dir = await mkdtemp(join(tmpdir(), 'damage-test-')); t.after(() => rm(dir, { recursive: true, force: true }));
  const robot = new SimRobot(); const original = robot.takeSnapshot.bind(robot);
  robot.takeSnapshot = async () => ({ ...await original(), takenAt: Date.now(), imageBase64: photo, mimeType: 'image/jpeg' });
  const config = loadConfig({ LLM_MODE: 'mock', ROBOT_MODE: 'prada', AGENT_TOKEN: 'damage-test-secret', ROBOT_CONTROL_TOKEN: 'test-robot-secret' });
  const service = new DamageService(robot, config, { dir, analyze }); t.after(() => service.stop());
  return { service, robot, dir, config };
}
test('damage records keep the actual photo, hash, unknown Prada position, and survive restart', async t => {
  const { service, robot, config, dir } = await setup(t, async image => { assert.match(image, /^data:image\/jpeg;base64,/); return finding; });
  service.configure({ survey: 'Oak Street', location: 'Kitchen', enabled: false });
  const result = await service.capture(); const r = result.record!;
  assert.equal(r.survey, 'Oak Street'); assert.equal(r.location, 'Kitchen'); assert.equal(r.position, null); assert.equal(r.status, 'needs_review');
  assert.match(r.imageSha256, /^[a-f0-9]{64}$/); assert.equal((await readFile(join(dir, r.id + '.jpg'))).toString('base64'), photo);
  const restarted = new DamageService(robot, config, { dir });
  assert.deepEqual(await restarted.list(), [r]);
  assert.equal((await service.capture()).duplicate, true); assert.equal((await service.list()).length, 1);
  service.configure({ survey: 'Oak Street', location: 'Hallway', enabled: false });
  assert.equal((await service.capture()).duplicate, false); assert.equal((await service.list()).length, 2);
});
test('damage clear views do not create incident records', async t => {
  const { service } = await setup(t, async () => ({ ...finding, incident: false, observations: [] }));
  assert.equal((await service.capture()).record, null); assert.deepEqual(await service.list(), []);
});
test('damage unavailable model, simulated pixels, stale frames, and malformed results never create evidence', async t => {
  const { service, robot } = await setup(t, async () => { throw Error('Model unavailable'); });
  await assert.rejects(service.capture(), /Model unavailable/); assert.equal(service.status().busy, false);
  robot.takeSnapshot = async () => ({ id: 'fake', robotId: 1, takenAt: Date.now(), x: 0, y: 0, heading: 0, pan: 0, tilt: 0 });
  await assert.rejects(service.capture(), /real JPEG/);
  const noImage = await robot.takeSnapshot(); robot.takeSnapshot = async () => ({ ...noImage, takenAt: Date.now() - 20000, imageBase64: photo });
  await assert.rejects(service.capture(), /stale/); assert.deepEqual(await service.list(), []);
  assert.throws(() => assessment({ ...finding, incident: 'true' }), /invalid/);
  assert.throws(() => assessment({ ...finding, confidence: NaN }), /invalid/);
  assert.throws(() => assessment({ ...finding, observations: [] }), /invalid/);
});
test('damage overlapping captures are refused and stopping an in-flight survey prevents scheduling', async t => {
  let finish!: (value: Assessment) => void;
  const { service } = await setup(t, () => new Promise(resolve => { finish = resolve; }));
  const pending = service.capture();
  await assert.rejects(service.capture(), /already/);
  service.configure({ enabled: false }); assert.equal(service.status().enabled, false);
  while (!finish) await new Promise(resolve => setTimeout(resolve, 1));
  finish(finding); await pending; assert.equal(service.status().busy, false);
});
test('damage reports contain portable photos, escaped evidence, review notes, and provenance', async t => {
  const { service } = await setup(t, async () => ({ ...finding, title: '<script>unsafe</script>' }));
  const r = (await service.capture()).record!;
  const reviewed = await service.review(r.id, 'Checked floor <wet>');
  assert.equal(reviewed.status, 'reviewed'); assert.ok(reviewed.reviewedAt);
  const html = await service.report(await service.list());
  assert.ok(html.includes('data:image/jpeg;base64,' + photo)); assert.ok(html.includes('&lt;script&gt;')); assert.ok(!html.includes('<script>unsafe'));
  assert.ok(html.includes('Checked floor &lt;wet&gt;')); assert.ok(html.includes(r.imageSha256)); assert.ok(html.includes('policy coverage'));
});
test('damage HTTP protects changes and exports records together with the evidence image', async t => {
  const { service } = await setup(t);
  const feed = await startOperatorFeed(new OperatorVoice(), { port: 0, routes: (req,res,url) => service.routes(req,res,url) }); t.after(() => feed.close());
  const url = feed.url;
  const denied = await fetch(url + '/api/damage/analyze', { method: 'POST', body: '{}' }); assert.equal(denied.status, 401);
  const saved = await fetch(url + '/api/damage/analyze', { method: 'POST', headers: { Authorization: 'Bearer damage-test-secret' }, body: '{}' });
  assert.equal(saved.status, 200); const r = (await saved.json() as any).record;
  const exported = await fetch(url + '/api/damage/export.json'); assert.match(exported.headers.get('content-disposition')!, /attachment/);
  const data = await exported.json() as any; assert.equal(data.incidents[0].imageBase64, photo);
  assert.equal((await fetch(url + '/api/damage/' + r.id + '/photo')).status, 200);
  assert.equal((await fetch(url + '/api/damage/report?id=missing')).status, 404);
});
