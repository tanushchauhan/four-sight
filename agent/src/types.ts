// Shared domain types. Field names mirror the SpacetimeDB tables in server/ (see docs/spacetime.md).

export type RobotMode = "searching" | "waiting" | "manual" | "returning" | "offline";
export type Triage = "unknown" | "red" | "yellow" | "green" | "black";
export type CaseStatus = "found" | "viewing" | "responders_sent" | "resolved";
export type RequestStatus = "waiting" | "claimed" | "done";
export type HazardKind = "drop" | "obstacle" | "heat" | "other";
export type AlertLevel = "info" | "warning" | "critical";
export type ConfirmationStatus = "pending" | "approved" | "denied" | "expired";

export const TRIAGE_COLORS: readonly Triage[] = ["unknown", "red", "yellow", "green", "black"];
export const HAZARD_KINDS: readonly HazardKind[] = ["drop", "obstacle", "heat", "other"];
export const ALERT_LEVELS: readonly AlertLevel[] = ["info", "warning", "critical"];

export interface RobotState {
  id: number;
  name: string;
  mode: RobotMode;
  x: number;
  y: number;
  /** Degrees, 0 = +x, counter-clockwise. */
  heading: number;
  /** 0..1 */
  battery: number;
  pan: number;
  tilt: number;
  lastSeen: number;
}

export interface SensorReadings {
  /** Ultrasonic distance straight ahead, cm. null if no echo. */
  frontCm: number | null;
  /** Downward IR distance at the front, cm. Large = drop ahead. */
  dropCm: number | null;
  /** Relative heat reading 0..1 if a heat sensor exists. */
  heat: number | null;
}

export interface SimScene {
  visibility: "none" | "partial" | "clear";
  description: string;
  distanceM?: number;
  bearingDeg?: number;
  /** Ground-truth triage, used only by the simulated vision model. */
  truthTriage?: Triage;
  /** Ground truth: does the person visibly move (breathing, limbs, head)? Undefined = unknown. Sim vision only. */
  moving?: boolean;
}

export interface Snapshot {
  id: string;
  robotId: number;
  takenAt: number;
  x: number;
  y: number;
  heading: number;
  pan: number;
  tilt: number;
  /** Real camera frame (JPEG), base64. Present when the Pi sends one. */
  imageBase64?: string;
  mimeType?: string;
  /** Public URL for the frame, if uploaded somewhere. */
  url?: string;
  /** Present only for simulated snapshots. */
  simScene?: SimScene;
}

export interface Detection {
  x: number;
  y: number;
  bearingDeg: number;
  distanceM: number;
  confidence: number;
}

export interface SurvivorCase {
  id: number;
  robotId: number;
  x: number;
  y: number;
  confidence: number;
  snapshotUrl: string;
  triage: Triage;
  description: string;
  status: CaseStatus;
  responderTeamId: number;
  createdAt: number;
  updatedAt: number;
}

export interface TakeoverRequest {
  id: number;
  caseId: number;
  robotId: number;
  priority: number;
  reason: string;
  status: RequestStatus;
  createdAt: number;
}

export interface ResponderTeam {
  id: number;
  name: string;
  x: number;
  y: number;
  status: "available" | "en_route" | "on_scene";
}

export interface Hazard {
  id: number;
  x: number;
  y: number;
  kind: HazardKind;
  note: string;
  createdAt: number;
}

export interface Alert {
  id: number;
  text: string;
  level: AlertLevel;
  createdAt: number;
}

export interface Confirmation {
  id: number;
  action: string;
  detailsJson: string;
  status: ConfirmationStatus;
  createdAt: number;
}

export interface CoverageCell {
  x: number;
  y: number;
  searchedBy: number;
  searchedAt: number;
}

export interface AgentLogEntry {
  time: number;
  event: string;
  tool: string;
  args: unknown;
  result: unknown;
}

export interface SearchArea {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}
