import type { Detection, RobotMode, RobotState, SensorReadings, Snapshot } from "../types.js";

export interface MoveResult {
  ok: boolean;
  /** Distance actually covered, meters. */
  movedM: number;
  /** Why it stopped early, if it did. */
  stoppedBy?: "obstacle" | "drop" | "battery" | "manual_mode";
  state: RobotState;
}

export interface SweepResult {
  detections: Detection[];
  /** Short note per 90-degree quadrant, e.g. obstacle distances. */
  notes: string[];
  state: RobotState;
}

/**
 * Everything the agent can make the robot do. Implemented by SimRobot (offline)
 * and PiRobot (WebSocket to the Pi). Units: meters, degrees.
 */
export interface ListenResult {
  transcript: string | null;
  unavailable?: boolean;
  reason?: string;
}

/** What the robot can physically do. Missing = everything (sim robots). */
export interface RobotCapabilities {
  /** The agent can drive it (move, turn, navigate, sweep, return). */
  move: boolean;
  /** It has a pan-tilt camera mount the agent can aim. */
  aim: boolean;
}

export interface RobotAdapter {
  readonly capabilities?: RobotCapabilities;
  getState(): Promise<RobotState>;
  getSensors(): Promise<SensorReadings>;
  move(meters: number): Promise<MoveResult>;
  turn(degrees: number): Promise<RobotState>;
  navigateTo(x: number, y: number): Promise<MoveResult>;
  sweep360(): Promise<SweepResult>;
  pointCamera(pan: number, tilt: number): Promise<RobotState>;
  takeSnapshot(): Promise<Snapshot>;
  speak(text: string): Promise<{ ok: boolean }>;
  /** Record from the robot mic and transcribe. transcript is null if nothing was heard. */
  /**
   * transcript null = silence. unavailable = the robot could not listen at all (no mic, no
   * speech-to-text): that is NOT evidence the survivor is unresponsive.
   */
  listen(seconds: number): Promise<ListenResult>;
  returnToStart(): Promise<MoveResult>;
  setMode(mode: RobotMode): Promise<RobotState>;
  close?(): Promise<void>;
}

export const PAN_LIMIT = 90;
export const TILT_MIN = -30;
export const TILT_MAX = 45;

export function clamp(v: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, v));
}

export function normalizeDeg(deg: number): number {
  let d = deg % 360;
  if (d > 180) d -= 360;
  if (d <= -180) d += 360;
  return d;
}
