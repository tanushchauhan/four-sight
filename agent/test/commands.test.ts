import assert from "node:assert/strict";
import { test } from "node:test";
import { commandRoute } from "../src/operator/commands.js";
import { startOperatorFeed } from "../src/operator/feed-server.js";
import { OperatorVoice } from "../src/operator/voice.js";

test("LAN visor commands accept no token and validate text before dispatch", async () => {
  const received: string[] = [];
  const server = await startOperatorFeed(new OperatorVoice({ setTimer: false }), {
    host: "127.0.0.1", port: 0,
    routes: commandRoute("test-agent-secret", (text) => received.push(text)),
  });
  try {
    const post = (token: string, text: unknown) => fetch(server.url + "/api/operator/command", {
      method: "POST", headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
      body: JSON.stringify({ text }),
    });
    assert.equal((await post("test-agent-secret", " ")).status, 400);
    assert.deepEqual(received, []);
    assert.equal((await post("", " status ")).status, 202);
    assert.deepEqual(received, ["status"]);
  } finally { await server.close(); }
});
