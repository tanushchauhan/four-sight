/**
 * Operator feed: a tiny HTTP server (node:http, no deps) that streams OperatorVoice utterances.
 *
 * Contract (also in docs/agent.md, "Operator voice updates"):
 *   GET /api/operator/feed       Server-Sent Events. Event `say`, data = Utterance JSON, `id:` = utterance id.
 *                                On connect the last 5 utterances are replayed (or, with a Last-Event-ID
 *                                header / ?since=<id>, only the ones after that id). Event `ping` every 15 s
 *                                with {"time": ms}.
 *   GET /api/operator/recent?n=20  JSON array of the last n utterances (1-50), oldest first.
 *                                Poll this with ?since=<id> to get only newer ones (visor without SSE).
 *   GET /operator                 The earpiece page (agent/web/public/operator.html), which speaks each line.
 * All GETs send Access-Control-Allow-Origin: * so a visor/phone on the LAN can read them.
 * Nothing secret is served: only the utterance text the rescuer would hear anyway.
 *
 * Binds to 127.0.0.1 by default; pass host "0.0.0.0" to reach it from the phone on the LAN.
 */
import { readFile } from "node:fs/promises";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import type { OperatorVoice, Utterance } from "./voice.js";

export interface FeedOptions {
  host?: string;
  /** 0 = pick a free port. Default 8788. */
  port?: number;
  /** Folder with operator.html / operator.js / speech.js. Default agent/web/public. */
  publicDir?: string;
  /** Ping interval. Default 15 s. */
  pingMs?: number;
  /** Utterances replayed to a new SSE client. Default 5. */
  replay?: number;
}

export interface OperatorFeed {
  url: string;
  close(): Promise<void>;
}

const DEFAULT_PUBLIC = fileURLToPath(new URL("../../web/public/", import.meta.url));
/** Only these files are served, nothing else from disk. */
const STATIC: Record<string, { file: string; type: string }> = {
  "/operator": { file: "operator.html", type: "text/html; charset=utf-8" },
  "/operator.html": { file: "operator.html", type: "text/html; charset=utf-8" },
  "/operator.js": { file: "operator.js", type: "text/javascript; charset=utf-8" },
  "/speech.js": { file: "speech.js", type: "text/javascript; charset=utf-8" },
};

const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, OPTIONS",
  "Access-Control-Allow-Headers": "Last-Event-ID, Cache-Control",
};

function sendJson(res: ServerResponse, status: number, body: unknown): void {
  res.writeHead(status, { ...CORS, "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
  res.end(JSON.stringify(body));
}

const sseSay = (u: Utterance) => `id: ${u.id}\nevent: say\ndata: ${JSON.stringify(u)}\n\n`;

export async function startOperatorFeed(voice: OperatorVoice, o: FeedOptions = {}): Promise<OperatorFeed> {
  const host = o.host ?? "127.0.0.1";
  const publicDir = o.publicDir ?? DEFAULT_PUBLIC;
  const pingMs = o.pingMs ?? 15_000;
  const replay = o.replay ?? 5;
  const clients = new Set<ServerResponse>();

  const off = voice.subscribe((u) => {
    const chunk = sseSay(u);
    for (const res of clients) res.write(chunk);
  });
  const ping = setInterval(() => {
    const chunk = `event: ping\ndata: ${JSON.stringify({ time: Date.now() })}\n\n`;
    for (const res of clients) res.write(chunk);
  }, pingMs);
  ping.unref();

  const sinceOf = (req: IncomingMessage, url: URL): number | null => {
    const raw = (req.headers["last-event-id"] as string | undefined) ?? url.searchParams.get("since");
    const n = raw == null || raw === "" ? NaN : Number(raw);
    return Number.isFinite(n) ? n : null;
  };

  const feed = (req: IncomingMessage, res: ServerResponse, url: URL) => {
    res.writeHead(200, { ...CORS, "Content-Type": "text/event-stream; charset=utf-8", "Cache-Control": "no-cache, no-transform", Connection: "keep-alive", "X-Accel-Buffering": "no" });
    res.write("retry: 3000\n\n");
    const since = sinceOf(req, url);
    const backlog = since === null ? voice.recent(replay) : voice.recent(50).filter((u) => u.id > since);
    for (const u of backlog) res.write(sseSay(u));
    clients.add(res);
    const drop = () => clients.delete(res);
    req.on("close", drop);
    res.on("error", drop);
  };

  const server = createServer((req, res) => {
    void (async () => {
      const url = new URL(req.url ?? "/", "http://local");
      const p = url.pathname;
      if (req.method === "OPTIONS") {
        res.writeHead(204, { ...CORS, "Access-Control-Max-Age": "600" });
        return res.end();
      }
      if (req.method !== "GET" && req.method !== "HEAD") return sendJson(res, 405, { error: "method not allowed" });
      if (p === "/api/operator/feed") return feed(req, res, url);
      if (p === "/api/operator/recent") {
        const n = Math.min(50, Math.max(1, Math.floor(Number(url.searchParams.get("n") ?? 20)) || 20));
        const since = sinceOf(req, url);
        const list = since === null ? voice.recent(n) : voice.recent(50).filter((u) => u.id > since).slice(-n);
        return sendJson(res, 200, list);
      }
      if (p === "/") {
        res.writeHead(302, { ...CORS, Location: "/operator" });
        return res.end();
      }
      const s = STATIC[p];
      if (s) {
        try {
          const body = await readFile(join(publicDir, s.file));
          res.writeHead(200, { ...CORS, "Content-Type": s.type, "Cache-Control": "no-cache" });
          return res.end(req.method === "HEAD" ? undefined : body);
        } catch {
          return sendJson(res, 404, { error: `${s.file} missing` });
        }
      }
      sendJson(res, 404, { error: "not found" });
    })().catch((e: unknown) => {
      if (!res.headersSent) sendJson(res, 500, { error: (e as Error).message });
      else res.end();
    });
  });

  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(o.port ?? 8788, host, () => {
      server.off("error", reject);
      resolve();
    });
  });
  const addr = server.address() as AddressInfo;
  const shownHost = host === "0.0.0.0" || host === "::" ? "localhost" : host.includes(":") ? `[${host}]` : host;

  return {
    url: `http://${shownHost}:${addr.port}`,
    close: () =>
      new Promise<void>((resolve) => {
        off();
        clearInterval(ping);
        for (const res of clients) res.end();
        clients.clear();
        server.close(() => resolve());
        server.closeAllConnections?.();
      }),
  };
}
