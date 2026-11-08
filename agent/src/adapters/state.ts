import type {
  AgentLogEntry,
  Alert,
  AlertLevel,
  Confirmation,
  CoverageCell,
  Hazard,
  HazardKind,
  ResponderTeam,
  RobotState,
  SearchArea,
  SurvivorCase,
  TakeoverRequest,
  Triage,
} from "../types.js";

/**
 * Shared operational state. Each method maps to one SpacetimeDB reducer or SQL read
 * (docs/spacetime.md). MemoryState implements it in-process for offline runs and tests.
 */
export interface StateStore {
  readonly area: SearchArea;

  // Robots + coverage (robot_checkin)
  robotCheckin(s: RobotState, sightRadius?: number): Promise<void>;
  getRobots(): Promise<RobotState[]>;
  getCoverage(): Promise<CoverageCell[]>;

  // Cases (report_detection, set_triage)
  reportDetection(robotId: number, x: number, y: number, confidence: number, snapshotUrl: string): Promise<SurvivorCase | null>;
  getCases(): Promise<SurvivorCase[]>;
  setTriage(caseId: number, color: Triage, description: string): Promise<void>;

  // Takeover queue
  requestTakeover(robotId: number, caseId: number, priority: number, reason: string): Promise<void>;
  setPriority(requestId: number, priority: number, reason: string): Promise<void>;
  getTakeoverRequests(): Promise<TakeoverRequest[]>;

  // Responders
  getResponderTeams(): Promise<ResponderTeam[]>;
  dispatchResponders(caseId: number, teamId: number): Promise<void>;

  // Hazards + headset
  markHazard(x: number, y: number, kind: HazardKind, note: string): Promise<void>;
  getHazards(): Promise<Hazard[]>;
  pushAlert(text: string, level: AlertLevel): Promise<void>;
  getAlerts(): Promise<Alert[]>;

  // Operator confirmations (headset approves/denies)
  requestConfirmation(action: string, detailsJson: string): Promise<number>;
  getConfirmation(id: number): Promise<Confirmation | null>;

  // Audit trail
  logAgentAction(entry: AgentLogEntry): Promise<void>;
}

export class StateError extends Error {}
