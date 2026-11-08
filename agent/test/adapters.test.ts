import assert from "node:assert/strict";
import { test } from "node:test";
import { TwilioNotifier } from "../src/adapters/notifier.js";
import { SpacetimeState, decodeSqlResponse } from "../src/adapters/spacetime-state.js";
import { NemotronChat, extractJsonObject, stripThinking } from "../src/llm/nemotron.js";

function fakeFetch(handler: (url: string, init: RequestInit) => unknown, status = 200) {
  const calls: { url: string; init: RequestInit }[] = [];
  const f = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    calls.push({ url, init: init ?? {} });
    return new Response(JSON.stringify(handler(url, init ?? {})), { status, headers: { "content-type": "application/json" } });
  }) as typeof fetch;
  return { f, calls };
}

test("decodes SpacetimeDB SQL rows with option/timestamp wrappers", () => {
  const body = [
    {
      schema: { elements: [{ name: { some: "id" } }, { name: { some: "triage" } }, { name: { some: "created_at" } }, { name: { some: "operator_identity" } }] },
      rows: [[3, "red", { __timestamp_micros_since_unix_epoch__: 1_700_000_000_000_000 }, { none: [] }]],
    },
  ];
  assert.deepEqual(decodeSqlResponse(body), [{ id: 3, triage: "red", created_at: 1_700_000_000_000, operator_identity: null }]);
});

test("SpacetimeState calls reducers with positional args and bearer token", async () => {
  const { f, calls } = fakeFetch(() => ({}));
  const st = new SpacetimeState("http://localhost:3000", "rescue-robot", "tok", { minX: 0, minY: 0, maxX: 8, maxY: 6 }, f);
  await st.setTriage(2, "yellow", "trapped");
  assert.equal(calls[0]!.url, "http://localhost:3000/v1/database/rescue-robot/call/set_triage");
  assert.equal(calls[0]!.init.body, JSON.stringify([2, "yellow", "trapped"]));
  assert.equal((calls[0]!.init.headers as Record<string, string>).Authorization, "Bearer tok");
});

test("Twilio notifier posts a form to the Messages API", async () => {
  const { f, calls } = fakeFetch(() => ({ sid: "SM123" }), 201);
  const n = new TwilioNotifier("AC1", "secret", "+15550001111", f);
  const res = await n.send(["+15550002222"], "hello");
  assert.equal(res[0]!.ok, true);
  assert.equal(res[0]!.id, "SM123");
  assert.match(calls[0]!.url, /Accounts\/AC1\/Messages\.json$/);
  const form = new URLSearchParams(String(calls[0]!.init.body));
  assert.equal(form.get("To"), "+15550002222");
  assert.equal(form.get("Body"), "hello");
});

test("JSON fallback helpers handle fences and thinking blocks", () => {
  assert.equal(stripThinking("<think>hmm</think> done"), "done");
  assert.deepEqual(extractJsonObject('Sure:\n```json\n{"tool_calls":[{"name":"move","arguments":{"meters":1}}]}\n```'), {
    tool_calls: [{ name: "move", arguments: { meters: 1 } }],
  });
  assert.deepEqual(extractJsonObject('x {"a":"}{"} y'), { a: "}{" });
});

test("NemotronChat (native) sends tools and parses tool_calls", async () => {
  const { f, calls } = fakeFetch(() => ({
    id: "1",
    object: "chat.completion",
    created: 0,
    model: "m",
    choices: [
      {
        index: 0,
        finish_reason: "tool_calls",
        message: { role: "assistant", content: "<think>x</think>Looking again.", tool_calls: [{ id: "c1", type: "function", function: { name: "take_snapshot", arguments: "{}" } }] },
      },
    ],
  }));
  const m = new NemotronChat({ apiKey: "k", baseUrl: "http://nim.test/v1", model: "nvidia/test", toolCallMode: "native", fetch: f, retries: 0 });
  const turn = await m.complete([{ role: "user", content: "hi" }], [{ name: "take_snapshot", description: "d", parameters: { type: "object", properties: {} } }]);
  assert.equal(turn.content, "Looking again.");
  assert.deepEqual(turn.toolCalls, [{ id: "c1", name: "take_snapshot", arguments: "{}" }]);
  const sent = JSON.parse(String(calls[0]!.init.body));
  assert.equal(sent.model, "nvidia/test");
  assert.equal(sent.tools[0].function.name, "take_snapshot");
  assert.match(calls[0]!.url, /chat\/completions$/);
});

test("NemotronChat (json mode) turns a JSON reply into tool calls", async () => {
  const { f } = fakeFetch(() => ({
    id: "1",
    object: "chat.completion",
    created: 0,
    model: "m",
    choices: [{ index: 0, finish_reason: "stop", message: { role: "assistant", content: '{"thought":"go","tool_calls":[{"name":"turn","arguments":{"degrees":45}}]}' } }],
  }));
  const m = new NemotronChat({ apiKey: "k", baseUrl: "http://nim.test/v1", model: "m", toolCallMode: "json", fetch: f, retries: 0 });
  const turn = await m.complete([{ role: "system", content: "s" }, { role: "user", content: "hi" }], [{ name: "turn", description: "d", parameters: { type: "object" } }]);
  assert.equal(turn.toolCalls[0]!.name, "turn");
  assert.deepEqual(JSON.parse(turn.toolCalls[0]!.arguments), { degrees: 45 });
});
