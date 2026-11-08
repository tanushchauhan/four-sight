import type { MoveResult, RobotAdapter, SweepResult } from "../adapters/robot.js";
import { SimRobot } from "../adapters/sim-robot.js";
import type { RobotMode, RobotState, SensorReadings, SimScene, Snapshot } from "../types.js";
import { readFrameImage } from "./load.js";
import type { Scene } from "./types.js";

export interface FrameChange {
  index: number;
  total: number;
  image: string;
  trigger: string;
  reason: string;
}

export interface SceneRobotOptions {
  scene: Scene;
  scenesDir?: string;
  /** Where the case sits in the map frame (the robot starts 1.8 m in front of it). */
  casePos?: { x: number; y: number };
  onFrame?: (f: FrameChange) => void;
  onSpeech?: (who: "robot" | "survivor", text: string | null) => void;
}

/** Below these the camera view doesn't count as changed. */
const MIN_PAN = 10;
const MIN_TURN = 10;
const MIN_MOVE = 0.15;

/**
 * A robot whose camera shows a scene's images. Movement and pose come from a
 * SimRobot in an open room; frames advance when the agent repositions (moves,
 * turns or pans) and snapshots again, or when the harness advances time.
 */
export class SceneRobot implements RobotAdapter {
  readonly inner: SimRobot;
  private frame = 0;
  private viewChanged = false;
  private replyIdx = 0;
  private snapSeq = 0;
  readonly spoken: string[] = [];
  readonly heard: (string | null)[] = [];
  readonly casePos: { x: number; y: number };

  constructor(private readonly o: SceneRobotOptions) {
    this.casePos = o.casePos ?? { x: 2.8, y: 3 };
    this.inner = new SimRobot({
      world: {
        area: { minX: 0, minY: 0, maxX: 8, maxY: 6 },
        start: { x: this.casePos.x - 1.8, y: this.casePos.y, heading: 0 },
        obstacles: [],
        drops: [],
        survivors: [],
      },
    });
  }

  get frameIndex(): number {
    return this.frame;
  }

  private emitFrame(reason: string): void {
    const f = this.o.scene.frames[this.frame]!;
    this.o.onFrame?.({ index: this.frame, total: this.o.scene.frames.length, image: f.image, trigger: f.trigger, reason });
  }

  /** Show the first frame (call once when the run starts). */
  start(): void {
    this.emitFrame("first view");
  }

  /** The scenario clock moves: switch to the next "time" frame if there is one. Returns true if the view changed. */
  advanceTime(): boolean {
    const next = this.o.scene.frames[this.frame + 1];
    if (!next || next.trigger !== "time") return false;
    this.frame++;
    this.replyIdx = 0;
    this.emitFrame("time passed");
    return true;
  }

  /** Are there more "time" frames ahead? */
  hasTimeFrames(): boolean {
    return this.o.scene.frames.slice(this.frame + 1).some((f) => f.trigger === "time");
  }

  private markMoved(changed: boolean): void {
    if (changed) this.viewChanged = true;
  }

  getState(): Promise<RobotState> {
    return this.inner.getState();
  }

  async getSensors(): Promise<SensorReadings> {
    const s = await this.inner.getSensors();
    return { ...s, heat: null };
  }

  async move(meters: number): Promise<MoveResult> {
    const r = await this.inner.move(meters);
    this.markMoved(r.movedM >= MIN_MOVE);
    return r;
  }

  async turn(degrees: number): Promise<RobotState> {
    this.markMoved(Math.abs(degrees) >= MIN_TURN);
    return this.inner.turn(degrees);
  }

  async navigateTo(x: number, y: number): Promise<MoveResult> {
    const r = await this.inner.navigateTo(x, y);
    this.markMoved(r.movedM >= MIN_MOVE);
    return r;
  }

  async pointCamera(pan: number, tilt: number): Promise<RobotState> {
    const before = await this.inner.getState();
    const s = await this.inner.pointCamera(pan, tilt);
    this.markMoved(Math.abs(s.pan - before.pan) >= MIN_PAN || Math.abs(s.tilt - before.tilt) >= MIN_PAN);
    return s;
  }

  async sweep360(): Promise<SweepResult> {
    const s = await this.inner.getState();
    const d = Math.hypot(this.casePos.x - s.x, this.casePos.y - s.y);
    const b = (Math.atan2(this.casePos.y - s.y, this.casePos.x - s.x) * 180) / Math.PI - s.heading;
    return {
      detections: [{ x: this.casePos.x, y: this.casePos.y, bearingDeg: Math.round(b), distanceM: Number(d.toFixed(2)), confidence: this.o.scene.detectorConfidence }],
      notes: ["scene test: open room"],
      state: s,
    };
  }

  /** Ground-truth hint for the offline mock vision only (real runs send pixels to Nemotron). */
  private simScene(): SimScene {
    const sc = this.o.scene;
    const f = sc.frames[this.frame]!;
    const present = sc.expected?.personPresent;
    const moreReposition = sc.frames[this.frame + 1]?.trigger === "reposition";
    const visibility: SimScene["visibility"] =
      present === false ? "none" : present === "unclear" || sc.category === "partial_view" || moreReposition ? "partial" : "clear";
    return {
      visibility,
      description: f.caption,
      truthTriage: sc.expected?.triage,
      distanceM: 1.8,
      bearingDeg: 0,
      ...(f.moving !== undefined ? { moving: f.moving } : {}),
    };
  }

  async takeSnapshot(): Promise<Snapshot> {
    const next = this.o.scene.frames[this.frame + 1];
    if (this.viewChanged && next?.trigger === "reposition") {
      this.frame++;
      this.replyIdx = 0;
      this.emitFrame("robot changed its viewpoint");
    }
    this.viewChanged = false;
    const s = await this.inner.getState();
    const f = this.o.scene.frames[this.frame]!;
    const img = readFrameImage(f.image, this.o.scenesDir);
    return {
      id: `scene-${this.o.scene.id}-${this.frame}-${Date.now()}-${++this.snapSeq}`,
      robotId: s.id,
      takenAt: Date.now(),
      x: s.x,
      y: s.y,
      heading: s.heading,
      pan: s.pan,
      tilt: s.tilt,
      imageBase64: img.base64,
      mimeType: img.mimeType,
      simScene: this.simScene(),
    };
  }

  async speak(text: string): Promise<{ ok: boolean }> {
    this.spoken.push(text);
    this.o.onSpeech?.("robot", text);
    return { ok: true };
  }

  async listen(_seconds: number): Promise<{ transcript: string | null }> {
    const sc = this.o.scene;
    const replies = sc.frameReplies?.[this.frame] ?? sc.replies;
    const t = replies[this.replyIdx] ?? null;
    if (t !== null) this.replyIdx++;
    this.heard.push(t);
    this.o.onSpeech?.("survivor", t);
    return { transcript: t };
  }

  returnToStart() {
    return this.inner.returnToStart();
  }

  setMode(mode: RobotMode) {
    return this.inner.setMode(mode);
  }
}
