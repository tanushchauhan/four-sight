import { existsSync, readFileSync, readdirSync } from "node:fs";
import { extname, join } from "node:path";
import { fileURLToPath } from "node:url";
import type { Scene, SceneFile } from "./types.js";

/** agent/scenes, resolved from this file so it works from any cwd. */
export const SCENES_DIR = fileURLToPath(new URL("../../scenes", import.meta.url));

/** Load and merge every manifest-*.json in the scenes dir. Bad or missing files are skipped with a warning. */
export function loadScenes(dir = SCENES_DIR, warn: (m: string) => void = console.warn): Scene[] {
  if (!existsSync(dir)) return [];
  const out: Scene[] = [];
  const seen = new Set<string>();
  for (const f of readdirSync(dir).filter((n) => /^manifest-.*\.json$/.test(n)).sort()) {
    try {
      const file = JSON.parse(readFileSync(join(dir, f), "utf8")) as SceneFile;
      for (const s of file.scenes ?? []) {
        if (!s?.id || !Array.isArray(s.frames) || s.frames.length === 0) {
          warn(`${f}: skipping scene without id/frames`);
          continue;
        }
        if (seen.has(s.id)) {
          warn(`${f}: duplicate scene id ${s.id}`);
          continue;
        }
        const missing = s.frames.filter((fr) => !fr.image.startsWith("data:") && !existsSync(join(dir, fr.image)));
        if (missing.length) {
          warn(`${f}: scene ${s.id} is missing ${missing.map((m) => m.image).join(", ")}`);
          continue;
        }
        seen.add(s.id);
        out.push({ ...s, expected: s.expected ?? null, replies: s.replies ?? [] });
      }
    } catch (e) {
      warn(`${f}: ${(e as Error).message}`);
    }
  }
  return out;
}

const MIME: Record<string, string> = { ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".png": "image/png", ".webp": "image/webp" };

/** Read a frame's image as base64 + mime type (handles data: URLs from uploads). */
export function readFrameImage(image: string, dir = SCENES_DIR): { base64: string; mimeType: string } {
  const m = image.match(/^data:([^;]+);base64,(.*)$/);
  if (m) return { mimeType: m[1]!, base64: m[2]! };
  const path = join(dir, image);
  return { base64: readFileSync(path).toString("base64"), mimeType: MIME[extname(path).toLowerCase()] ?? "image/jpeg" };
}
