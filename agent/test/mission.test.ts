import test from 'node:test';
import assert from 'node:assert/strict';
import { MissionLink } from '../src/adapters/mission.js';

test('mission admission is fail closed and uses the authenticated stop endpoint', async () => {
  const requests: [string, RequestInit | undefined][] = [];
  const fetcher = (async (url, init) => { requests.push([String(url), init]); return new Response('{"accepted":true}'); }) as typeof fetch;
  const link = new MissionLink('http://localhost:8090', 'test-token', fetcher);
  assert.equal(await link.admit({count:1, confidence:.9}), true);
  assert.equal(requests[0]![0], 'http://localhost:8090/api/agent/person');
  assert.equal((requests[0]![1]!.headers as Record<string,string>).Authorization, 'Bearer test-token');
  const unavailable = new MissionLink('http://localhost', 'token', (async () => {throw Error('offline');}) as typeof fetch);
  assert.equal(await unavailable.admit({}), false);
  const disabled = new MissionLink('http://localhost', 'token', (async () => new Response('{"accepted":false}')) as typeof fetch);
  assert.equal(await disabled.admit({}), false);
});

import { DetectionIntake } from '../src/agent/intake.js';
import { MemoryState } from '../src/adapters/memory-state.js';
test('disabled mission never opens cases; stop admission precedes detection event', async () => {
  const state = new MemoryState();
  const order: string[] = [];
  let enabled = false;
  const intake = new DetectionIntake(state, () => order.push('case'), {
    token: 'test', robotId: 1, admit: async () => { order.push('stop'); return enabled; },
  });
  const sight = {source:'visor',confidence:.9,count:1,box:null,frameTime:Date.now()};
  assert.equal((await intake.sight(sight)).body.ignored, 'mission_disabled');
  assert.equal((await state.getCases()).length, 0);
  enabled = true;
  assert.equal((await intake.sight(sight)).status, 202);
  assert.deepEqual(order, ['stop','stop','case']);
});
