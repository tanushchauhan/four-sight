import type { RobotAdapter } from "./robot.js";

/**
 * Robot loudspeaker. The Pi runs an offline text-to-speech service (robot/picrawler_control/speech.py,
 * port 8081) that plays through the Robot HAT speaker. SPEAKER_MODE=pi turns it on; off by default.
 */
export interface SpeakResult {
  ok: boolean;
  engine?: string;
  cached?: boolean;
  error?: string;
}

export interface Speaker {
  say(text: string, opts?: { interrupt?: boolean }): Promise<SpeakResult>;
  stop?(): Promise<void>;
}

export class NullSpeaker implements Speaker {
  async say(): Promise<SpeakResult> {
    return { ok: true, engine: "off" };
  }
}

/** HTTP client for the Pi speech service: POST /say {text, interrupt} with the robot control token. */
export class PiSpeaker implements Speaker {
  constructor(
    private readonly baseUrl: string,
    private readonly token: string,
    private readonly timeoutMs = 3000,
    private readonly fetchImpl: typeof fetch = fetch,
  ) {}

  private async post(path: string, body: unknown): Promise<Response> {
    return this.fetchImpl(`${this.baseUrl.replace(/\/$/, "")}${path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${this.token}` },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(this.timeoutMs),
    });
  }

  async say(text: string, opts: { interrupt?: boolean } = {}): Promise<SpeakResult> {
    // The service accepts 1..300 chars; keep spoken lines short anyway.
    const t = text.replace(/\s+/g, " ").trim().slice(0, 300);
    if (!t) return { ok: false, error: "empty text" };
    try {
      const r = await this.post("/say", { text: t, interrupt: opts.interrupt === true });
      const j = (await r.json().catch(() => ({}))) as { ok?: boolean; engine?: string; cached?: boolean; error?: string };
      if (!r.ok) return { ok: false, error: `speaker HTTP ${r.status}${j.error ? `: ${j.error}` : ""}` };
      return { ok: true, engine: j.engine, cached: j.cached };
    } catch (e) {
      return { ok: false, error: `robot speaker unreachable: ${(e as Error).message}` };
    }
  }

  async stop(): Promise<void> {
    try {
      await this.post("/stop", {});
    } catch {
      // best effort
    }
  }
}

/**
 * Route a robot's speak() to a real speaker while keeping the inner robot's behaviour (the sim
 * and scene robots record what was said; the scene bench shows it as conversation bubbles).
 * The tool result says whether the survivor could actually have heard it.
 */
export function withSpeaker<R extends RobotAdapter>(robot: R, speaker: Speaker): R {
  const inner = robot.speak.bind(robot);
  return new Proxy(robot, {
    get(target, prop, receiver) {
      if (prop === "speak") {
        return async (text: string) => {
          const [real, sim] = await Promise.all([speaker.say(text), inner(text)]);
          return { ...sim, ok: real.ok && sim.ok, speaker: real.ok ? (real.engine ?? "played") : real.error };
        };
      }
      const v = Reflect.get(target, prop, receiver);
      return typeof v === "function" ? v.bind(target) : v;
    },
  });
}
