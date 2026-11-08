import { randomUUID } from "node:crypto";
import { existsSync, readFileSync, statSync } from "node:fs";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { extname, join, normalize, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import type { MemoryState } from "../adapters/memory-state.js";
import { loadConfig, loadEnvFile } from "../config.js";
import { runScenario, type RunEvent } from "../scenes/harness.js";
import { SCENES_DIR, loadScenes } from "../scenes/load.js";
import type { Scene } from "../scenes/types.js";

/**
 * Local test bench: pick or upload a scene image, watch the Nemotron agents decide.
 *   cd agent && npm run web   →  http://localhost:8787
 * API contract: docs/scene-bench.md.
 */

const AGENT_DIR = fileURLToPath(new URL("../..", import.meta.url));
const PUBLIC_DIR = join(AGENT_DIR, "web", "public");
loadEnvFile(join(AGENT_DIR, ".env"));
// Per-run choice of mock/nvidia, so load with mock here (it never throws for a missing key).
const config = loadConfig({ ...process.env, LLM_MODE: "mock" });
const PORT = Number(process.env.WEB_PORT ?? 8787);
const HOST = process.env.WEB_HOST ?? "127.0.0.1";

interface Run {
  id: string;
  scene: Scene;
  events: { type: string; data: unknown }[];
  clients: Set<ServerResponse>;
  state?: MemoryState;
  done: boolean;
  createdAt: number;
}
const runs = new Map<string, Run>();

const MIME: Record<string, string> = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg",
  ".webp": "image/webp",
  ".ico": "image/x-icon",
};

function send(res: ServerResponse, status: number, body: unknown): void {
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
  res.end(JSON.stringify(body));
}

/** Serve a file from root, refusing anything outside it. */
function serveFile(res: ServerResponse, root: string, rel: string): void {
  const base = resolve(root);
  const path = resolve(base, normalize(decodeURIComponent(rel)).replace(/^([/\\])+/, ""));
  if (path !== base && !path.startsWith(base + sep)) return send(res, 403, { error: "forbidden" });
  if (!existsSync(path) || !statSync(path).isFile()) return send(res, 404, { error: "not found" });
  res.writeHead(200, { "Content-Type": MIME[extname(path).toLowerCase()] ?? "application/octet-stream", "Cache-Control": "no-cache" });
  res.end(readFileSync(path));
}

async function readBody(req: IncomingMessage, limit = 8 * 1024 * 1024): Promise<unknown> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const c of req) {
    size += (c as Buffer).length;
    if (size > limit) throw new Error("request too large");
    chunks.push(c as Buffer);
  }
  const text = Buffer.concat(chunks).toString("utf8");
  return text ? JSON.parse(text) : {};
}

/** Frame images in events/listings become URLs the page can load. */
function imageUrl(image: string): string {
  return image.startsWith("data:") ? image : `/scenes/${image.split(/[\\/]/).map(encodeURIComponent).join("/")}`;
}

function publicScene(s: Scene): Scene {
  return { ...s, frames: s.frames.map((f) => ({ ...f, image: imageUrl(f.image) })) };
}

function push(run: Run, e: RunEvent): void {
  let data = e.data as Record<string, unknown>;
  if (e.type === "frame") data = { ...data, image: imageUrl(String(data.image)) };
  const ev = { type: e.type, data };
  run.events.push(ev);
  const line = `event: ${ev.type}\ndata: ${JSON.stringify(ev.data)}\n\n`;
  for (const c of run.clients) c.write(line);
  if (e.type === "status" && (data.phase === "done" || data.phase === "error")) {
    run.done = true;
    for (const c of run.clients) c.end();
    run.clients.clear();
  }
}

function uploadScene(u: { dataUrl?: string; name?: string; replies?: string[]; detectorConfidence?: number }): Scene {
  if (!u.dataUrl || !/^data:image\/(jpeg|png|webp);base64,/.test(u.dataUrl)) throw new Error("upload.dataUrl must be a base64 JPEG/PNG/WebP data URL");
  const conf = Number(u.detectorConfidence ?? 0.5);
  return {
    id: `upload-${Date.now()}`,
    title: u.name?.slice(0, 80) || "Your image",
    category: "upload",
    description: "Uploaded by the tester",
    frames: [{ image: u.dataUrl, caption: "(uploaded image: no ground truth)", trigger: "start" }],
    detectorConfidence: Number.isFinite(conf) ? Math.min(1, Math.max(0, conf)) : 0.5,
    replies: (u.replies ?? []).map(String).filter((r) => r.trim()),
    expected: null,
    source: { kind: "upload", license: "uploaded by tester" },
  };
}

async function startRun(req: IncomingMessage, res: ServerResponse): Promise<void> {
  const body = (await readBody(req)) as {
    sceneId?: string;
    upload?: Parameters<typeof uploadScene>[0];
    agentMode?: "team" | "single";
    llm?: "nvidia" | "mock";
    approvals?: "auto" | "ask";
  };
  let scene: Scene | undefined;
  if (body.upload) scene = uploadScene(body.upload);
  else scene = loadScenes(SCENES_DIR, () => {}).find((s) => s.id === body.sceneId);
  if (!scene) return send(res, 404, { error: `unknown scene ${body.sceneId}` });

  const llm = body.llm === "mock" ? "mock" : config.nvidiaApiKey ? "nvidia" : "mock";
  const run: Run = { id: randomUUID(), scene, events: [], clients: new Set(), done: false, createdAt: Date.now() };
  runs.set(run.id, run);
  send(res, 200, { runId: run.id, scene: publicScene(scene) });

  push(run, { type: "status", data: { phase: "starting", message: `llm=${llm}` } });
  runScenario(scene, {
    config,
    llm,
    agentMode: body.agentMode === "single" ? "single" : "team",
    approvals: body.approvals === "ask" ? "ask" : "auto",
    emit: (e) => push(run, e),
    onState: (s) => (run.state = s),
  }).catch((e: Error) => {
    console.error(`run ${run.id} failed:`, e);
    push(run, { type: "error", data: { message: e.message } });
    push(run, { type: "status", data: { phase: "error", message: e.message } });
  });
}

function streamEvents(run: Run, req: IncomingMessage, res: ServerResponse): void {
  res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache", Connection: "keep-alive" });
  res.write(": connected\n\n");
  for (const ev of run.events) res.write(`event: ${ev.type}\ndata: ${JSON.stringify(ev.data)}\n\n`);
  if (run.done) return void res.end();
  run.clients.add(res);
  const ping = setInterval(() => res.write(": ping\n\n"), 15_000);
  req.on("close", () => {
    clearInterval(ping);
    run.clients.delete(res);
  });
}

const server = createServer(async (req, res) => {
  try {
    const url = new URL(req.url ?? "/", `http://${req.headers.host ?? "localhost"}`);
    const p = url.pathname;
    if (req.method === "GET" && p === "/api/scenes") {
      return send(res, 200, {
        scenes: loadScenes(SCENES_DIR).map(publicScene),
        llmAvailable: !!config.nvidiaApiKey,
        defaults: { agentMode: config.agentMode, llm: config.nvidiaApiKey ? "nvidia" : "mock" },
        models: { coordinator: config.coordinatorModel, triage: config.triageModel, search: config.searchModel, vision: config.visionModel, single: config.reasoningModel },
      });
    }
    if (req.method === "POST" && p === "/api/runs") return await startRun(req, res);
    const m = p.match(/^\/api\/runs\/([\w-]+)\/(events|confirm)$/);
    if (m) {
      const run = runs.get(m[1]!);
      if (!run) return send(res, 404, { error: "unknown run" });
      if (req.method === "GET" && m[2] === "events") return streamEvents(run, req, res);
      if (req.method === "POST" && m[2] === "confirm") {
        const b = (await readBody(req)) as { id?: number; approved?: boolean };
        if (!run.state) return send(res, 409, { error: "run has no state yet" });
        run.state.resolveConfirmation(Number(b.id), b.approved === true);
        return send(res, 200, { ok: true });
      }
    }
    if (req.method === "GET" && p.startsWith("/scenes/")) return serveFile(res, SCENES_DIR, p.slice("/scenes/".length));
    if (req.method === "GET") return serveFile(res, PUBLIC_DIR, p === "/" ? "index.html" : p);
    send(res, 405, { error: "method not allowed" });
  } catch (e) {
    send(res, 400, { error: (e as Error).message });
  }
});

// Drop finished runs after 30 minutes.
setInterval(() => {
  for (const [id, r] of runs) if (r.done && Date.now() - r.createdAt > 30 * 60_000) runs.delete(id);
}, 60_000).unref();

server.listen(PORT, HOST, () => {
  const n = loadScenes(SCENES_DIR, () => {}).length;
  console.log(`Scene test bench: http://localhost:${PORT}  (${n} scenes, Nemotron ${config.nvidiaApiKey ? "available" : "not configured: offline mock only"})`);
});
