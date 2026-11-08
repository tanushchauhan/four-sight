import type { Detection, RobotMode, RobotState, SearchArea, SensorReadings, SimScene, Snapshot, Triage } from "../types.js";
import { PAN_LIMIT, TILT_MAX, TILT_MIN, clamp, normalizeDeg, type MoveResult, type RobotAdapter, type SweepResult } from "./robot.js";

/** Axis-aligned box in meters. */
export interface Box {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export interface SimSurvivor {
  x: number;
  y: number;
  truthTriage: Triage;
  /** What a clear camera view would show. */
  description: string;
  /** What a partial view shows (e.g. only a hand). */
  partialDescription: string;
  /** Bearing (world degrees) from which the person is hidden, ±60°. Undefined = visible from anywhere. */
  occludedFromDeg?: number;
  /** Replies to the robot's questions, in order. Empty = unresponsive. */
  replies: string[];
  /** Ground truth for the movement check: visible breathing / limb / head motion. Undefined = unknown. */
  moving?: boolean;
}

export interface SimWorld {
  area: SearchArea;
  start: { x: number; y: number; heading: number };
  obstacles: Box[];
  drops: Box[];
  survivors: SimSurvivor[];
}

/** A small collapsed room: couch, fallen shelf, a hole in the floor, two people. */
export function defaultWorld(area: SearchArea = { minX: 0, minY: 0, maxX: 8, maxY: 6 }): SimWorld {
  return {
    area,
    start: { x: 0.5, y: 0.5, heading: 0 },
    obstacles: [
      { minX: 3, minY: 2.5, maxX: 4.8, maxY: 3.3 }, // couch
      { minX: 6, minY: 0, maxX: 6.4, maxY: 2 }, // fallen shelf
    ],
    drops: [{ minX: 1.5, minY: 4.5, maxX: 2.3, maxY: 5.3 }],
    survivors: [
      {
        x: 4.2,
        y: 4.1,
        truthTriage: "yellow",
        description:
          "Adult lying on their side behind a couch, eyes open, left leg pinned under debris, small cut on forehead, appears conscious.",
        partialDescription: "A hand and part of an arm visible past the edge of a couch. Rest of the body hidden.",
        occludedFromDeg: -100, // hidden when viewed from the south-west side of the couch
        replies: ["Yes... I can hear you.", "My leg is stuck, it hurts a lot.", "I can move my arms."],
        moving: true,
      },
      {
        x: 7.2,
        y: 1.0,
        truthTriage: "red",
        description:
          "Person lying face down next to a fallen shelf, not moving, dust on clothing, possible bleeding near the head.",
        partialDescription: "Legs visible on the floor beside a fallen shelf, not moving.",
        replies: [],
        moving: false,
      },
    ],
  };
}

const CAMERA_FOV = 62; // Camera Module 3 horizontal FOV, roughly
const DETECT_RANGE = 3.0;
const STEP = 0.1;
const SIGHT_RADIUS = 1.0;

function inBox(x: number, y: number, b: Box): boolean {
  return x >= b.minX && x <= b.maxX && y >= b.minY && y <= b.maxY;
}

function bearing(fromX: number, fromY: number, toX: number, toY: number): number {
  return (Math.atan2(toY - fromY, toX - fromX) * 180) / Math.PI;
}

export interface SimRobotOptions {
  id?: number;
  world?: SimWorld;
  /** Called after every pose change so coverage can be recorded (mirrors robot_checkin). */
  onCheckin?: (s: RobotState, sightRadius: number) => void | Promise<void>;
  /** Battery fraction used per meter walked. */
  drainPerMeter?: number;
  log?: (msg: string) => void;
}

export class SimRobot implements RobotAdapter {
  readonly world: SimWorld;
  private s: RobotState;
  private snapshotSeq = 0;
  private replyIndex = new Map<SimSurvivor, number>();
  private beaconsLeft = 1;
  /** Positions after each walk, for retracing the path home. */
  private readonly breadcrumbs: { x: number; y: number }[] = [];
  readonly spoken: string[] = [];
  private readonly onCheckin?: SimRobotOptions["onCheckin"];
  private readonly drain: number;
  private readonly log: (msg: string) => void;

  constructor(opts: SimRobotOptions = {}) {
    this.world = opts.world ?? defaultWorld();
    this.onCheckin = opts.onCheckin;
    this.drain = opts.drainPerMeter ?? 0.01;
    this.log = opts.log ?? (() => {});
    this.s = {
      id: opts.id ?? 1,
      name: "PiCrawler",
      mode: "searching",
      x: this.world.start.x,
      y: this.world.start.y,
      heading: this.world.start.heading,
      battery: 1,
      pan: 0,
      tilt: 0,
      lastSeen: Date.now(),
    };
  }

  private snap(): RobotState {
    this.s.lastSeen = Date.now();
    return { ...this.s };
  }

  private async checkin(): Promise<void> {
    await this.onCheckin?.(this.snap(), SIGHT_RADIUS);
  }

  /** Test hook: set battery level. */
  setBattery(b: number): void {
    this.s.battery = clamp(b, 0, 1);
  }

  async getState(): Promise<RobotState> {
    return this.snap();
  }

  private blockedAt(x: number, y: number): "obstacle" | "drop" | null {
    const a = this.world.area;
    if (x < a.minX || x > a.maxX || y < a.minY || y > a.maxY) return "obstacle";
    if (this.world.obstacles.some((b) => inBox(x, y, b))) return "obstacle";
    if (this.world.drops.some((b) => inBox(x, y, b))) return "drop";
    return null;
  }

  private distanceAhead(kind: "obstacle" | "drop", maxM: number): number | null {
    const rad = (this.s.heading * Math.PI) / 180;
    for (let d = STEP; d <= maxM; d += STEP) {
      const hit = this.blockedAt(this.s.x + Math.cos(rad) * d, this.s.y + Math.sin(rad) * d);
      if (hit === kind) return d;
    }
    return null;
  }

  async getSensors(): Promise<SensorReadings> {
    const front = this.distanceAhead("obstacle", 2.5);
    const drop = this.distanceAhead("drop", 0.25);
    const nearest = Math.min(...this.world.survivors.map((p) => Math.hypot(p.x - this.s.x, p.y - this.s.y)));
    return {
      frontCm: front === null ? null : Math.round(front * 100),
      dropCm: drop === null ? 4 : 60, // IR reads ~4 cm to the floor, much more over a hole
      heat: Number(clamp(1 - nearest / 4, 0, 1).toFixed(2)),
    };
  }

  async move(meters: number): Promise<MoveResult> {
    if (this.s.mode === "manual") return { ok: false, movedM: 0, stoppedBy: "manual_mode", state: this.snap() };
    const dir = Math.sign(meters);
    const rad = (this.s.heading * Math.PI) / 180;
    let moved = 0;
    let stoppedBy: MoveResult["stoppedBy"];
    while (moved + STEP <= Math.abs(meters) + 1e-9) {
      if (this.s.battery <= 0.02) {
        stoppedBy = "battery";
        break;
      }
      // Look one step plus a safety margin ahead, like the ultrasonic + drop sensor would.
      const nx = this.s.x + Math.cos(rad) * STEP * dir;
      const ny = this.s.y + Math.sin(rad) * STEP * dir;
      const probe = this.blockedAt(nx + Math.cos(rad) * 0.15 * dir, ny + Math.sin(rad) * 0.15 * dir);
      if (probe) {
        stoppedBy = probe;
        break;
      }
      this.s.x = nx;
      this.s.y = ny;
      moved += STEP;
      this.s.battery = clamp(this.s.battery - this.drain * STEP, 0, 1);
      if (Math.round(moved * 10) % 5 === 0) await this.checkin();
    }
    this.s.x = Number(this.s.x.toFixed(2));
    this.s.y = Number(this.s.y.toFixed(2));
    if (moved > 0 && this.s.mode !== "returning") this.breadcrumbs.push({ x: this.s.x, y: this.s.y });
    await this.checkin();
    this.log(`sim: moved ${moved.toFixed(1)} m${stoppedBy ? ` (stopped: ${stoppedBy})` : ""}`);
    return { ok: !stoppedBy, movedM: Number(moved.toFixed(2)), stoppedBy, state: this.snap() };
  }

  async turn(degrees: number): Promise<RobotState> {
    this.s.heading = normalizeDeg(this.s.heading + degrees);
    this.s.battery = clamp(this.s.battery - 0.001, 0, 1);
    await this.checkin();
    return this.snap();
  }

  async navigateTo(x: number, y: number): Promise<MoveResult> {
    const a = this.world.area;
    const tx = clamp(x, a.minX, a.maxX);
    const ty = clamp(y, a.minY, a.maxY);
    const target = normalizeDeg(bearing(this.s.x, this.s.y, tx, ty));
    await this.turn(normalizeDeg(target - this.s.heading));
    const dist = Math.hypot(tx - this.s.x, ty - this.s.y);
    // Straight-line only: the agent is expected to route around obstacles using hazards + retries.
    return this.move(Math.round(dist * 10) / 10);
  }

  private visible(p: SimSurvivor, centerDeg: number, halfFov: number): { scene: SimScene; inView: boolean } {
    const dist = Math.hypot(p.x - this.s.x, p.y - this.s.y);
    const b = bearing(this.s.x, this.s.y, p.x, p.y);
    const off = Math.abs(normalizeDeg(b - centerDeg));
    const inView = dist <= DETECT_RANGE && off <= halfFov;
    // Viewing direction from the person back to the robot, to test occlusion.
    const fromPerson = bearing(p.x, p.y, this.s.x, this.s.y);
    const occluded = p.occludedFromDeg !== undefined && Math.abs(normalizeDeg(fromPerson - p.occludedFromDeg)) <= 60;
    const centered = off <= 15 && dist <= 2.5;
    const visibility: SimScene["visibility"] = !inView ? "none" : occluded || !centered ? "partial" : "clear";
    return {
      inView,
      scene: {
        visibility,
        description: visibility === "clear" ? p.description : visibility === "partial" ? p.partialDescription : "",
        distanceM: Number(dist.toFixed(2)),
        bearingDeg: Number(normalizeDeg(b - this.s.heading).toFixed(0)),
        truthTriage: p.truthTriage,
        ...(visibility !== "none" && p.moving !== undefined ? { moving: p.moving } : {}),
      },
    };
  }

  async sweep360(): Promise<SweepResult> {
    const detections: Detection[] = [];
    for (const p of this.world.survivors) {
      const { scene, inView } = this.visible(p, this.s.heading, 180);
      if (!inView) continue;
      detections.push({
        x: Number(p.x.toFixed(1)),
        y: Number(p.y.toFixed(1)),
        bearingDeg: scene.bearingDeg ?? 0,
        distanceM: scene.distanceM ?? 0,
        // The on-robot detector is weak: a partial view gives low confidence.
        confidence: p.occludedFromDeg !== undefined && scene.visibility !== "clear" ? 0.45 : 0.78,
      });
    }
    const notes: string[] = [];
    const saved = this.s.heading;
    for (const q of [0, 90, 180, 270]) {
      this.s.heading = normalizeDeg(saved + q);
      const front = this.distanceAhead("obstacle", 2.5);
      notes.push(`${q}°: ${front === null ? "clear >2.5 m" : `obstacle at ${front.toFixed(1)} m`}`);
    }
    this.s.heading = saved;
    this.s.battery = clamp(this.s.battery - 0.004, 0, 1);
    await this.checkin();
    return { detections, notes, state: this.snap() };
  }

  async pointCamera(pan: number, tilt: number): Promise<RobotState> {
    this.s.pan = clamp(pan, -PAN_LIMIT, PAN_LIMIT);
    this.s.tilt = clamp(tilt, TILT_MIN, TILT_MAX);
    return this.snap();
  }

  async takeSnapshot(): Promise<Snapshot> {
    const center = normalizeDeg(this.s.heading + this.s.pan);
    let best: SimScene = { visibility: "none", description: "Rubble, dust and broken furniture. No person visible." };
    const rank = { none: 0, partial: 1, clear: 2 } as const;
    for (const p of this.world.survivors) {
      const { scene } = this.visible(p, center, CAMERA_FOV / 2);
      if (rank[scene.visibility] > rank[best.visibility]) best = scene;
    }
    this.snapshotSeq += 1;
    return {
      id: `sim-${this.s.id}-${this.snapshotSeq}`,
      robotId: this.s.id,
      takenAt: Date.now(),
      x: this.s.x,
      y: this.s.y,
      heading: this.s.heading,
      pan: this.s.pan,
      tilt: this.s.tilt,
      url: `sim://snapshot/${this.snapshotSeq}`,
      simScene: best,
    };
  }

  async speak(text: string): Promise<{ ok: boolean }> {
    this.spoken.push(text);
    this.log(`sim speaker: "${text}"`);
    return { ok: true };
  }

  async listen(_seconds: number): Promise<{ transcript: string | null }> {
    // Prefer the person the camera is pointed at; otherwise the closest one in earshot.
    const center = normalizeDeg(this.s.heading + this.s.pan);
    const near = this.world.survivors
      .map((p) => {
        const d = Math.hypot(p.x - this.s.x, p.y - this.s.y);
        const off = Math.abs(normalizeDeg(bearing(this.s.x, this.s.y, p.x, p.y) - center));
        return { p, d, score: d + (off <= CAMERA_FOV / 2 ? 0 : 10) };
      })
      .filter(({ d }) => d <= DETECT_RANGE)
      .sort((a, b) => a.score - b.score)[0];
    if (!near || near.p.replies.length === 0) return { transcript: null };
    const i = this.replyIndex.get(near.p) ?? 0;
    this.replyIndex.set(near.p, i + 1);
    return { transcript: near.p.replies[Math.min(i, near.p.replies.length - 1)] ?? null };
  }

  async dropBeacon(): Promise<{ ok: boolean; reason?: string }> {
    if (this.beaconsLeft <= 0) return { ok: false, reason: "no beacons left" };
    this.beaconsLeft -= 1;
    return { ok: true };
  }

  /** Walk back along the recorded path (like the real robot's "retrace on link loss"), then to the start. */
  async returnToStart(): Promise<MoveResult> {
    this.s.mode = "returning";
    // Direct route first; retrace the breadcrumbs only if that is blocked.
    const direct = await this.navigateTo(this.world.start.x, this.world.start.y);
    if (direct.ok) {
      this.breadcrumbs.length = 0;
      this.s.mode = "returning";
      return { ...direct, state: this.snap() };
    }
    const waypoints = [...this.breadcrumbs].reverse();
    waypoints.push({ x: this.world.start.x, y: this.world.start.y });
    let moved = direct.movedM;
    let last: MoveResult = direct;
    for (const w of waypoints) {
      if (Math.hypot(w.x - this.s.x, w.y - this.s.y) < 0.15) continue;
      last = await this.navigateTo(w.x, w.y);
      moved += last.movedM;
      if (last.stoppedBy === "battery") break;
    }
    this.breadcrumbs.length = 0;
    this.s.mode = "returning";
    const home = Math.hypot(this.s.x - this.world.start.x, this.s.y - this.world.start.y) < 0.3;
    return { ok: home, movedM: Number(moved.toFixed(2)), stoppedBy: home ? undefined : last.stoppedBy, state: this.snap() };
  }

  async setMode(mode: RobotMode): Promise<RobotState> {
    this.s.mode = mode;
    await this.checkin();
    return this.snap();
  }
}
