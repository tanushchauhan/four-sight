import type { IncomingMessage, ServerResponse } from "node:http";

/** Authenticated text input from the visor's speech recognizer. */
export function commandRoute(_token: string, push: (text: string) => void) {
  return async (req: IncomingMessage, res: ServerResponse, url: URL): Promise<boolean> => {
    if (url.pathname !== "/api/operator/command") return false;
    const reply = (status: number, body: unknown) => {
      res.writeHead(status, { "Content-Type": "application/json", "Cache-Control": "no-store" });
      res.end(JSON.stringify(body));
    };
    if (req.method !== "POST") { reply(405, { error: "use POST" }); return true; }
    try {
      const chunks: Buffer[] = [];
      let size = 0;
      for await (const chunk of req) {
        size += (chunk as Buffer).length;
        if (size > 8192) { reply(413, { error: "command too large" }); return true; }
        chunks.push(chunk as Buffer);
      }
      const body = JSON.parse(Buffer.concat(chunks).toString());
      if (typeof body?.text !== "string" || !body.text.trim() || body.text.length > 2000) {
        reply(400, { error: "text must contain 1–2000 characters" }); return true;
      }
      push(body.text.trim());
      reply(202, { ok: true });
    } catch { reply(400, { error: "invalid command" }); }
    return true;
  };
}
