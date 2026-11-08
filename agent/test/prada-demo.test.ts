import test from 'node:test';
import assert from 'node:assert/strict';
import { PradaDemo, GREETING } from '../src/agent/prada-demo.js';
import { MemoryState } from '../src/adapters/memory-state.js';
import { SimRobot } from '../src/adapters/sim-robot.js';
import { loadConfig } from '../src/config.js';
import { DetectionIntake } from '../src/agent/intake.js';

async function setup() {
  const state=new MemoryState();
  const robot=new SimRobot();
  const spoken:string[]=[];
  robot.speak=async text=>{spoken.push(text);return {ok:true}};
  const mission={mode:'person_found',person_detection:true,session_id:'demo-1'};
  const stages:string[]=[];
  const demo=new PradaDemo({state,robot,voice:null,config:loadConfig({}),fetchImpl:(async(url,init)=>{
    if(String(url).endsWith('/api/mission'))return Response.json(mission);
    if(String(url).endsWith('/health'))return Response.json({speaking:false,queue:0,last_error:null});
    stages.push(JSON.parse(String(init?.body)).stage);
    return Response.json({accepted:mission.mode==='person_found'});
  }) as typeof fetch});
  const c=await state.reportDetection(1,3,0,.9,'test');
  return {state,robot,spoken,mission,stages,demo,event:{kind:'detection' as const,caseId:c!.id,sessionId:'demo-1'}};
}
test('real demo alerts, greets once, waits, and never dispatches or diagnoses',async()=>{
  const d=await setup();await d.demo.run(d.event);await d.demo.run(d.event);
  assert.deepEqual(d.spoken,[GREETING]);
  assert.deepEqual(d.stages,['alerted','greeting','waiting']);
  assert.equal((await d.state.getTakeoverRequests()).length,1);
  const c=(await d.state.getCases())[0]!;
  assert.equal(c.triage,'unknown');assert.notEqual(c.status,'responders_sent');
  assert.equal((await d.state.getAlerts()).length,1);
  await d.demo.run({kind:'sitrep_due'});await d.demo.run({kind:'stale_case',caseId:c.id,waitingMin:10});
  assert.equal((await d.state.getAlerts()).length,1);
});
test('manual takeover or obsolete session suppresses queued greeting',async()=>{
  const d=await setup();d.mission.mode='manual';await d.demo.run(d.event);
  d.mission.mode='person_found';d.mission.session_id='new-demo';await d.demo.run(d.event);
  assert.deepEqual(d.spoken,[]);assert.deepEqual(d.stages,[]);
});
test('speaker failure leaves actionable alert without false greeting success',async()=>{
  const d=await setup();d.robot.speak=async()=>{throw Error('offline')};
  await d.demo.run(d.event);
  assert.deepEqual(d.stages,['alerted','greeting','error']);
});
test('parallel visor/local sightings create one case; rearming allows a new greeting',async()=>{
  const state=new MemoryState();const events:any[]=[];let session='first';
  const intake=new DetectionIntake(state,e=>events.push(e),{robotId:1,token:'test',admit:async s=>{s.sessionId=session;return true}});
  const sight=()=>({source:'local-yolo',confidence:.9,count:1,box:null,frameTime:Date.now()});
  await Promise.all([intake.sight(sight()),intake.sight(sight())]);
  assert.equal(events.length,1);assert.equal((await state.getCases()).length,1);
  session='second';await intake.sight(sight());
  assert.equal(events.length,2);assert.equal(events[1].sessionId,'second');
});

import { OperatorVoice } from '../src/operator/voice.js';
test('Nemotron replies use measured facts and no action tools or synthetic telemetry',async()=>{
  let payload:any;
  const voice=new OperatorVoice({setTimer:false});
  const state=new MemoryState();const robot=new SimRobot();
  const demo=new PradaDemo({state,robot,voice,config:loadConfig({LLM_MODE:'nvidia',NVIDIA_API_KEY:'test-key'}),fetchImpl:(async(url,init)=>{
    if(String(url).includes('/chat/completions')){
      payload=JSON.parse(String(init?.body));
      return Response.json({choices:[{message:{role:'assistant',content:'The battery reads 7.5 volts; its charge percentage is unknown.'}}]});
    }
    if(String(url).endsWith('/api/status'))return Response.json({reachable:true,checked_at:Date.now()/1000,battery_voltage:7.5,mission:{mode:'manual'}});
    return Response.json({ready:false});
  }) as typeof fetch});
  await demo.run({kind:'voice_command',text:'How much battery is left?'});
  assert.equal(payload.tools,undefined);
  assert.match(payload.messages.at(-1).content,/"battery_percent":null/);
  assert.match(payload.messages.at(-1).content,/"position":null/);
  assert.equal(voice.recent()[0]?.source,'conversation');
  assert.match(voice.recent()[0]!.text,/7.5 volts/);
  voice.dispose();
});

test('new handoff alerts bypass conversational throttling and dedupe',()=>{
  const voice=new OperatorVoice({setTimer:false,minGapMs:12000});
  voice.push({text:'Person detected. Take control.',level:'warning',source:'handoff'});
  voice.push({text:'Person detected. Take control.',level:'warning',source:'handoff'});
  assert.equal(voice.recent().length,2);assert.ok(voice.recent().every(x=>x.interrupt));
  voice.dispose();
});
