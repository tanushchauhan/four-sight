import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import vm from "node:vm";
import { startOperatorFeed } from "../src/operator/feed-server.js";
import { MAX_CHARS, MAX_WORDS, OperatorVoice, rewriteForSpeech, type Utterance } from "../src/operator/voice.js";

const PUBLIC = (f: string) => fileURLToPath(new URL(`../web/public/${f}`, import.meta.url));
const words = (s: string) => s.split(/\s+/).filter(Boolean).length;

function fakeClock(start = 1_000_000) {
  let t = start;
  return { now: () => t, advance: (ms: number) => (t += ms) };
}
const voiceAt = (clock: { now: () => number }) => new OperatorVoice({ now: clock.now, setTimer: false });

const SAMPLES: [string, "info" | "warning" | "critical"][] = [
  ["Person seen, case 1 at (2.345, -1.06): Adult lying behind the couch, partially covered by debris, conscious and waving one arm toward the camera slowly.", "warning"],
  ["Case 1: RED. Unresponsive adult, no visible breathing, heavy bleeding from the leg (left thigh).", "critical"],
  ["Takeover requested (priority 95): Survivor pinned, needs manual approach", "critical"],
  ["Approve? Dispatch responders to case 1 (RED) at (3.2, 4.0)", "warning"],
  ["🚨 **Battery low** — 18% left, returning", "info"],
  ["Robot heading 92.57° moved 1.83 m", "info"],
  ["## Sitrep\n- Case 2: `YELLOW`, talking. [map](http://x/y)", "info"],
  ["Search 40% done. Next: kitchen. Then hallway. Then garage and the back bedroom near the stairs where the smoke is thickest today.", "info"],
  ["", "info"],
];

test("rewrite: coordinates, triage words, parentheses, numbers", () => {
  assert.equal(rewriteForSpeech("Case 1: RED. Unresponsive adult (left thigh bleeding).", "critical").text, "Case 1: red. Unresponsive adult, left thigh bleeding.");
  assert.equal(rewriteForSpeech("Person seen, case 1 at (2.345, -1.06): behind the couch", "warning").text, "Person seen, case 1 at 2.3, minus 1.1: behind the couch.");
  assert.equal(rewriteForSpeech("Hazard (3, 4.0)", "info").text, "Hazard at 3, 4.");
  assert.equal(rewriteForSpeech("Takeover requested (priority 95): pinned", "critical").text, "Takeover requested, priority 95: pinned.");
  assert.equal(rewriteForSpeech("Turned 92.57° and moved 1.83 m, 18% battery", "info").text, "Turned 92.6 degrees and moved 1.8 meters, 18 percent battery.");
  assert.equal(rewriteForSpeech("I'm here", "info").text, "I'm here.");
});

test("rewrite: markdown and emoji removed, nothing invented", () => {
  const r = rewriteForSpeech("🚨 **Battery low** — `18%` left ✅", "info").text;
  assert.equal(r, "Battery low — 18 percent left.");
  const md = rewriteForSpeech("## Sitrep\n- Case 2: _YELLOW_, talking. [map](http://x/y)", "info").text;
  assert.equal(md, "Sitrep Case 2: yellow, talking. map.");
  assert.equal(rewriteForSpeech("   ", "info").text, "");
});

test("rewrite: long text is cut at a sentence, then clause, then word boundary", () => {
  for (const [t, l] of SAMPLES) {
    const r = rewriteForSpeech(t, l).text;
    assert.ok(words(r) <= MAX_WORDS, `${words(r)} words: ${r}`);
    assert.ok(r.length <= MAX_CHARS, `${r.length} chars: ${r}`);
  }
  assert.equal(
    rewriteForSpeech("Search 40% done. Next: kitchen. Then hallway. Then garage and the back bedroom near the stairs where the smoke is thickest and the floor is weak.", "info").text,
    "Search 40 percent done. Next: kitchen. Then hallway.",
  );
  const one = rewriteForSpeech("Adult lying behind the couch, partially covered by debris, conscious and waving one arm toward the camera slowly while calling out for help again and again", "warning").text;
  // the last clause boundary that fits keeps <60% of the words, so it is a word cut, minus the dangling "for"
  assert.equal(one, "Adult lying behind the couch, partially covered by debris, conscious and waving one arm toward the camera slowly while calling out.");
  // a clause cut that would drop the description is not used
  assert.equal(
    rewriteForSpeech("Person seen, case 1 at (2.8, 3.0): A man trapped under a collapsed wooden beam in a dusty room, his left arm visible and raised", "warning").text,
    "Person seen, case 1 at 2.8, 3: A man trapped under a collapsed wooden beam in a dusty room.",
  );
  assert.equal(
    rewriteForSpeech("Survivor talking, conscious: says the pain in the left leg is getting worse and asks for water and for the family to be called right away now", "info").text,
    "Survivor talking, conscious: says the pain in the left leg is getting worse and asks for water and for the family.",
  );
  // every output word comes from the input (no invented content), apart from the speakable expansions
  const src = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon phi chi psi omega";
  const cut = rewriteForSpeech(src, "info").text;
  assert.ok(src.startsWith(cut.replace(/\.$/, "")));
});

test("approval prompts become a warning that says where to answer", () => {
  const r = rewriteForSpeech("Approve? Dispatch responders to case 1 (RED) at (3.2, 4.0)", "info");
  assert.equal(r.text, "Approval needed: Dispatch responders to case 1, red, at 3.2, 4. Answer on the console.");
  assert.equal(r.level, "warning");
  assert.equal(r.approval, true);
  const v = voiceAt(fakeClock());
  const u = v.push({ text: "Approve? Dispatch responders to case 2", level: "info" });
  assert.equal(u?.level, "warning");
  assert.match(u!.text, /^Approval needed: Dispatch responders to case 2\. Answer on the console\.$/);
});

test("browser port (speech.js) gives the same rewrite", () => {
  const ctx: Record<string, unknown> = {};
  ctx.window = ctx;
  vm.runInNewContext(readFileSync(PUBLIC("speech.js"), "utf8"), ctx);
  const RS = ctx.RescueSpeech as { rewrite(t: string, l: string): { text: string; level: string; approval: boolean } };
  for (const [t, l] of SAMPLES) assert.deepEqual({ ...RS.rewrite(t, l) }, rewriteForSpeech(t, l), t);
});

test("dedupe: same normalized text within 30 s is dropped", () => {
  const c = fakeClock();
  const v = voiceAt(c);
  assert.ok(v.push({ text: "Case 1: RED.", level: "critical" }));
  c.advance(10_000);
  assert.equal(v.push({ text: "case 1 red", level: "critical" }), null);
  c.advance(20_001);
  assert.ok(v.push({ text: "Case 1: RED!", level: "critical" }));
});

test("rate limit: non-critical lines 4 s apart, queue of 3 drops oldest info first", () => {
  const c = fakeClock();
  const v = voiceAt(c);
  const said: string[] = [];
  v.subscribe((u) => said.push(u.text));
  assert.ok(v.push({ text: "One", level: "info" }));
  assert.equal(v.push({ text: "Two", level: "info" }), null);
  assert.equal(v.push({ text: "Three", level: "warning" }), null);
  assert.equal(v.push({ text: "Four", level: "info" }), null);
  assert.equal(v.pending(), 3);
  assert.equal(v.push({ text: "Five", level: "warning" }), null); // queue full: "two" (oldest info) goes
  assert.equal(v.pending(), 3);
  assert.equal(v.droppedCount(), 1);
  c.advance(3_999);
  assert.deepEqual(v.flush(), []);
  c.advance(1);
  assert.deepEqual(v.flush().map((u) => u.text), ["Three."]);
  c.advance(4_000);
  v.flush();
  c.advance(4_000);
  v.flush();
  assert.deepEqual(said, ["One.", "Three.", "Four.", "Five."]);
  // with no info left, the oldest line goes
  const v2 = voiceAt(c);
  v2.push({ text: "A", level: "info" });
  for (const t of ["W1", "W2", "W3", "W4"]) v2.push({ text: t, level: "warning" });
  c.advance(4_000);
  assert.deepEqual(v2.flush().map((u) => u.text), ["W2."]);
});

test("critical always passes now, with interrupt", () => {
  const c = fakeClock();
  const v = voiceAt(c);
  v.push({ text: "Searching the kitchen", level: "info" });
  v.push({ text: "Battery 40 percent", level: "info" });
  const u = v.push({ text: "Case 3: RED. Not breathing.", level: "critical", caseId: 3, source: "set_triage" });
  assert.ok(u);
  assert.equal(u.interrupt, true);
  assert.equal(u.caseId, 3);
  assert.equal(u.source, "set_triage");
  assert.equal(v.push({ text: "Case 4: RED.", level: "critical" })?.interrupt, true);
  assert.equal(v.pending(), 1);
  c.advance(4_000);
  const [q] = v.flush();
  assert.equal(q?.interrupt, false);
});

test("queued lines are released by the built-in timer", async () => {
  const v = new OperatorVoice({ minGapMs: 30 });
  const got: Utterance[] = [];
  v.subscribe((u) => got.push(u));
  v.push({ text: "First", level: "info" });
  v.push({ text: "Second", level: "info" });
  assert.equal(got.length, 1);
  await new Promise((r) => setTimeout(r, 80));
  assert.deepEqual(got.map((u) => u.text), ["First.", "Second."]);
  v.dispose();
});

test("subscribe / unsubscribe / recent", () => {
  const c = fakeClock();
  const v = new OperatorVoice({ now: c.now, setTimer: false, minGapMs: 0 });
  const a: number[] = [];
  const off = v.subscribe((u) => a.push(u.id));
  v.subscribe(() => {
    throw new Error("broken listener");
  });
  v.push({ text: "x1", level: "info", time: 42 });
  off();
  v.push({ text: "x2", level: "info" });
  assert.deepEqual(a, [1]);
  for (let i = 0; i < 60; i++) v.push({ text: `line ${i}`, level: "info" });
  assert.equal(v.recent(100).length, 50);
  assert.deepEqual(v.recent(2).map((u) => u.text), ["line 58.", "line 59."]);
  assert.equal(v.recent(0).length, 0);
  const v2 = voiceAt(c);
  assert.equal(v2.push({ text: "t", level: "info", time: 42 })?.time, 42);
});

test("feed server: SSE replays last 5, streams new lines, recent JSON, page served, CORS", async () => {
  const c = fakeClock();
  const v = new OperatorVoice({ now: c.now, setTimer: false, minGapMs: 0 });
  for (let i = 1; i <= 7; i++) v.push({ text: `update ${i}`, level: "info" });
  const feed = await startOperatorFeed(v, { port: 0 });
  try {
    const recent = await fetch(`${feed.url}/api/operator/recent?n=3`);
    assert.equal(recent.headers.get("access-control-allow-origin"), "*");
    const list = (await recent.json()) as Utterance[];
    assert.deepEqual(list.map((u) => u.id), [5, 6, 7]);
    const since = (await (await fetch(`${feed.url}/api/operator/recent?since=6`)).json()) as Utterance[];
    assert.deepEqual(since.map((u) => u.id), [7]);

    const ac = new AbortController();
    const res = await fetch(`${feed.url}/api/operator/feed`, { signal: ac.signal });
    assert.match(res.headers.get("content-type") ?? "", /text\/event-stream/);
    assert.equal(res.headers.get("access-control-allow-origin"), "*");
    const reader = res.body!.getReader();
    const dec = new TextDecoder();
    let buf = "";
    const readUntil = async (pred: (s: string) => boolean) => {
      while (!pred(buf)) {
        const { value, done } = await reader.read();
        if (done) break;
        buf += dec.decode(value, { stream: true });
      }
    };
    const says = () => [...buf.matchAll(/event: say\ndata: (.*)\n/g)].map((m) => JSON.parse(m[1]!) as Utterance);
    await readUntil(() => says().length >= 5);
    assert.deepEqual(says().map((u) => u.id), [3, 4, 5, 6, 7]);
    v.push({ text: "Case 1 is RED, responders notified", level: "critical" });
    await readUntil(() => says().length >= 6);
    const last = says().at(-1)!;
    assert.equal(last.text, "Case 1 is red, responders notified.");
    assert.equal(last.interrupt, true);
    assert.match(buf, /id: 8\n/);
    ac.abort();
    await reader.cancel().catch(() => {});

    const page = await fetch(`${feed.url}/operator`);
    assert.equal(page.status, 200);
    assert.match(page.headers.get("content-type") ?? "", /text\/html/);
    const html = await page.text();
    assert.match(html, /\/api\/operator\/feed|operator\.js/);
    assert.equal((await fetch(`${feed.url}/speech.js`)).status, 200);
    assert.equal((await fetch(`${feed.url}/operator.js`)).status, 200);
    assert.equal((await fetch(`${feed.url}/app.js`)).status, 404);
    assert.equal((await fetch(`${feed.url}/../package.json`)).status, 404);
    assert.equal((await fetch(`${feed.url}/api/operator/recent`, { method: "POST" })).status, 405);
  } finally {
    await feed.close();
  }
});

test("feed server: ping events", async () => {
  const v = new OperatorVoice({ setTimer: false });
  const feed = await startOperatorFeed(v, { port: 0, pingMs: 30 });
  try {
    const ac = new AbortController();
    const res = await fetch(`${feed.url}/api/operator/feed`, { signal: ac.signal });
    const reader = res.body!.getReader();
    let buf = "";
    while (!buf.includes("event: ping")) buf += new TextDecoder().decode((await reader.read()).value);
    ac.abort();
    await reader.cancel().catch(() => {});
  } finally {
    await feed.close();
  }
});

test("browser JS parses (node --check)", () => {
  for (const f of ["speech.js", "operator.js", "app.js"]) execFileSync(process.execPath, ["--check", PUBLIC(f)]);
});
