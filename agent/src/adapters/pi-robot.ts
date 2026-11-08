import WebSocket from "ws";
import type { RobotMode, RobotState, SensorReadings, Snapshot } from "../types.js";
import type { MoveResult, RobotAdapter, SweepResult } from "./robot.js";

/**
 * Talks to the Pi's control WebSocket (docs/plan.md "Robot ↔ headset").
 * NOT YET TESTED AGAINST HARDWARE. The agent command messages are a proposal,
 * documented in docs/agent.md ("Pi command protocol"):
 *
 *   agent → Pi: {"type":"agent_cmd","id":7,"cmd":"move","args":{"meters":0.5}}
 *   Pi → agent: {"type":"agent_result","id":7,"ok":true,"result":{...}}
 *               {"type":"agent_result","id":7,"ok":false,"error":"..."}
 *
 * Results must match the shapes in robot.ts (MoveResult, SweepResult, Snapshot...).
 */
export class PiRobot implements RobotAdapter {
  private ws: WebSocket | null = null;
  private seq = 0;
  private pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void; timer: NodeJS.Timeout }>();

  constructor(
    private readonly url: string,
    private readonly timeoutMs = 20_000,
  ) {}

  private async connect(): Promise<WebSocket> {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) return this.ws;
    const ws = new WebSocket(this.url);
    await new Promise<void>((resolve, reject) => {
      ws.once("open", () => resolve());
      ws.once("error", (e) => reject(new Error(`Pi WebSocket ${this.url}: ${e.message}`)));
    });
    ws.on("message", (raw) => this.onMessage(raw.toString()));
    ws.on("close", () => {
      for (const [, p] of this.pending) p.reject(new Error("Pi connection closed"));
      this.pending.clear();
      this.ws = null;
    });
    this.ws = ws;
    return ws;
  }

  private onMessage(text: string): void {
    let msg: { type?: string; id?: number; ok?: boolean; result?: unknown; error?: string };
    try {
      msg = JSON.parse(text);
    } catch {
      return;
    }
    // Status/person messages meant for the headset are ignored here.
    if (msg.type !== "agent_result" || typeof msg.id !== "number") return;
    const p = this.pending.get(msg.id);
    if (!p) return;
    clearTimeout(p.timer);
    this.pending.delete(msg.id);
    if (msg.ok) p.resolve(msg.result);
    else p.reject(new Error(msg.error ?? "Pi command failed"));
  }

  private async call<T>(cmd: string, args: Record<string, unknown> = {}, timeoutMs = this.timeoutMs): Promise<T> {
    const ws = await this.connect();
    const id = ++this.seq;
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Pi command ${cmd} timed out after ${timeoutMs} ms`));
      }, timeoutMs);
      this.pending.set(id, { resolve: resolve as (v: unknown) => void, reject, timer });
      ws.send(JSON.stringify({ type: "agent_cmd", id, cmd, args }));
    });
  }

  getState() { return this.call<RobotState>("get_state"); }
  getSensors() { return this.call<SensorReadings>("get_sensors"); }
  move(meters: number) { return this.call<MoveResult>("move", { meters }, 60_000); }
  turn(degrees: number) { return this.call<RobotState>("turn", { degrees }); }
  navigateTo(x: number, y: number) { return this.call<MoveResult>("navigate_to", { x, y }, 120_000); }
  sweep360() { return this.call<SweepResult>("sweep_360", {}, 60_000); }
  pointCamera(pan: number, tilt: number) { return this.call<RobotState>("point_camera", { pan, tilt }); }
  takeSnapshot() { return this.call<Snapshot>("take_snapshot"); }
  speak(text: string) { return this.call<{ ok: boolean }>("speak", { text }, 30_000); }
  listen(seconds: number) { return this.call<{ transcript: string | null }>("listen", { seconds }, (seconds + 15) * 1000); }
  returnToStart() { return this.call<MoveResult>("return_to_start", {}, 180_000); }
  setMode(mode: RobotMode) { return this.call<RobotState>("set_mode", { mode }); }

  async close(): Promise<void> {
    this.ws?.close();
    this.ws = null;
  }
}
