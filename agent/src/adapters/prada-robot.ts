import jpeg from "jpeg-js";
import type { RobotMode, RobotState, SensorReadings, Snapshot } from "../types.js";
import type { ListenResult, MoveResult, RobotAdapter, RobotCapabilities, SweepResult } from "./robot.js";
import { PiSpeaker } from "./speaker.js";

/**
 * The real robot, prada, as it exists today:
 * - Camera: the MJPEG service's `GET /frame` (latest JPEG, already rotated), robot/picrawler_control/stream.py.
 * - Speaker + microphone: the speech service (`POST /say`, `POST /listen`), robot/picrawler_control/speech.py.
 * - Movement: NOT available to the agent. prada is joystick/head-steering only by design (one controlling
 *   connection, deadman timeout), and there is no pan-tilt mount yet. Those calls throw a clear error so
 *   the agent never believes the robot moved; the workflow asks the rescuer instead.
 * - No odometry, battery or sensor telemetry: state is a placeholder.
 */
export interface PradaRobotOptions {
  cameraUrl: string; // e.g. http://prada.local:8080
  speechUrl: string; // e.g. http://prada.local:8081
  token: string; // ROBOT_CONTROL_TOKEN
  id?: number;
  fetchImpl?: typeof fetch;
  /** NVIDIA's inline image limit is ~180 KB; frames above this are downscaled. */
  maxImageBytes?: number;
}

const NOT_DRIVABLE = "prada is joystick-only: the agent cannot drive it. Ask the rescuer to move the robot (push_headset_alert).";

export class PradaRobot implements RobotAdapter {
  readonly capabilities: RobotCapabilities = { move: false, aim: false, beacon: false };
  private readonly speaker: PiSpeaker;
  private readonly fetchImpl: typeof fetch;
  private mode: RobotMode = "searching";
  private seq = 0;

  constructor(private readonly o: PradaRobotOptions) {
    this.fetchImpl = o.fetchImpl ?? fetch;
    this.speaker = new PiSpeaker(o.speechUrl, o.token, 3000, this.fetchImpl);
  }

  private base(url: string): string {
    return url.replace(/\/$/, "");
  }

  async getState(): Promise<RobotState> {
    return { id: this.o.id ?? 1, name: "prada", mode: this.mode, x: 0, y: 0, heading: 0, battery: 1, pan: 0, tilt: 0, lastSeen: Date.now() };
  }

  async getSensors(): Promise<SensorReadings> {
    // No ultrasonic, drop or heat telemetry is exposed by prada yet.
    return { frontCm: null, dropCm: null, heat: null };
  }

  async move(): Promise<MoveResult> {
    throw new Error(NOT_DRIVABLE);
  }
  async turn(): Promise<RobotState> {
    throw new Error(NOT_DRIVABLE);
  }
  async navigateTo(): Promise<MoveResult> {
    throw new Error(NOT_DRIVABLE);
  }
  async sweep360(): Promise<SweepResult> {
    throw new Error(NOT_DRIVABLE);
  }
  async returnToStart(): Promise<MoveResult> {
    throw new Error(NOT_DRIVABLE);
  }
  async pointCamera(): Promise<RobotState> {
    throw new Error("prada has no pan-tilt camera mount yet: the camera points where the robot faces.");
  }
  async dropBeacon(): Promise<{ ok: boolean; reason?: string }> {
    return { ok: false, reason: "beacon not integrated on prada" };
  }

  async setMode(mode: RobotMode): Promise<RobotState> {
    this.mode = mode;
    return this.getState();
  }

  /** Latest camera frame as JPEG, downscaled if it is too big to send inline to Nemotron VL. */
  async takeSnapshot(): Promise<Snapshot> {
    const r = await this.fetchImpl(`${this.base(this.o.cameraUrl)}/frame`, { signal: AbortSignal.timeout(4000) });
    if (!r.ok) throw new Error(`camera /frame returned HTTP ${r.status}`);
    let bytes: Buffer = Buffer.from(await r.arrayBuffer());
    if (bytes[0] !== 0xff || bytes[1] !== 0xd8) throw new Error("camera /frame did not return a JPEG");
    bytes = shrinkJpeg(bytes, this.o.maxImageBytes ?? 170_000);
    const s = await this.getState();
    return {
      id: `prada-${Date.now()}-${++this.seq}`,
      robotId: s.id,
      takenAt: Date.now(),
      x: 0,
      y: 0,
      heading: 0,
      pan: 0,
      tilt: 0,
      imageBase64: bytes.toString("base64"),
      mimeType: "image/jpeg",
    };
  }

  async speak(text: string): Promise<{ ok: boolean }> {
    const r = await this.speaker.say(text);
    if (!r.ok) throw new Error(r.error ?? "robot speaker failed");
    return { ok: true };
  }

  /** Record from the robot's mic and transcribe on the Pi. Missing mic / speech-to-text is reported as unavailable, not silence. */
  async listen(seconds: number): Promise<ListenResult> {
    try {
      const r = await this.fetchImpl(`${this.base(this.o.speechUrl)}/listen`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${this.o.token}` },
        body: JSON.stringify({ seconds: Math.min(10, Math.max(1, Math.round(seconds))) }),
        // waits for playback to finish (≤15 s) + recording + speech-to-text (whisper timeout 60 s)
        signal: AbortSignal.timeout((seconds + 50) * 1000),
      });
      const j = (await r.json().catch(() => ({}))) as { ok?: boolean; transcript?: string | null; stt_unavailable?: boolean; error?: string; silent?: boolean };
      if (!r.ok || j.ok === false) return { transcript: null, unavailable: true, reason: `listen failed: HTTP ${r.status}${j.error ? ` ${j.error}` : ""}` };
      if (j.stt_unavailable) return { transcript: null, unavailable: true, reason: "speech-to-text not installed on the robot" };
      const t = (j.transcript ?? "").trim();
      return { transcript: t ? t : null };
    } catch (e) {
      return { transcript: null, unavailable: true, reason: `robot mic unreachable: ${(e as Error).message}` };
    }
  }
}

/** Re-encode a JPEG smaller (pure JS) until it fits: halve dimensions above 1024 px, then lower quality. */
export function shrinkJpeg(input: Buffer, maxBytes: number): Buffer {
  if (input.length <= maxBytes) return input;
  let img = jpeg.decode(input, { useTArray: true, maxMemoryUsageInMB: 256 });
  let { width, height, data } = img;
  while (Math.max(width, height) > 1024) {
    const w2 = Math.floor(width / 2);
    const h2 = Math.floor(height / 2);
    const out = new Uint8Array(w2 * h2 * 4);
    for (let y = 0; y < h2; y++) {
      for (let x = 0; x < w2; x++) {
        for (let c = 0; c < 4; c++) {
          const i = (sy: number, sx: number) => data[(sy * width + sx) * 4 + c]!;
          out[(y * w2 + x) * 4 + c] = (i(2 * y, 2 * x) + i(2 * y, 2 * x + 1) + i(2 * y + 1, 2 * x) + i(2 * y + 1, 2 * x + 1)) >> 2;
        }
      }
    }
    width = w2;
    height = h2;
    data = out;
  }
  for (const q of [80, 70, 60, 50, 40]) {
    const enc = Buffer.from(jpeg.encode({ width, height, data }, q).data);
    if (enc.length <= maxBytes) return enc;
    img = { ...img, width, height, data };
  }
  return Buffer.from(jpeg.encode({ width, height, data }, 35).data);
}
