/** Things that wake the agent up. Each one runs one episode of the tool loop. */
export type AgentEvent =
  | { kind: "mission_start"; instruction?: string }
  | { kind: "search_step" }
  | { kind: "detection"; caseId: number; visor?: Record<string, unknown> }
  | { kind: "voice_command"; text: string }
  | { kind: "operator_released"; robotId: number }
  | { kind: "low_battery"; battery: number }
  | { kind: "robot_stuck"; detail: string }
  | { kind: "stale_case"; caseId: number; waitingMin: number }
  | { kind: "recheck_case"; caseId: number; reason: string }
  | { kind: "sitrep_due" };

/** Higher runs first. */
const PRIORITY: Record<AgentEvent["kind"], number> = {
  detection: 90,
  voice_command: 85,
  low_battery: 80,
  robot_stuck: 70,
  stale_case: 60,
  recheck_case: 88,
  operator_released: 50,
  mission_start: 40,
  sitrep_due: 30,
  search_step: 10,
};

export function describeEvent(e: AgentEvent): string {
  switch (e.kind) {
    case "mission_start":
      return `MISSION START. ${e.instruction ?? "Search the whole area for survivors."}`;
    case "search_step":
      return "SEARCH STEP. Nothing urgent is pending. Continue the search: pick the next unsearched spot, go there, and sweep.";
    case "detection":
      return `DETECTION. The robot's on-board detector flagged a possible person: case ${e.caseId}. Confirm and triage it. Structured visor observations (estimates, not medical findings): ${JSON.stringify(e.visor ?? {})}`;
    case "voice_command":
      return `VOICE COMMAND from the rescuer: "${e.text}"`;
    case "operator_released":
      return `The rescuer released manual control of robot ${e.robotId}. Resume the mission where it left off.`;
    case "low_battery":
      return `LOW BATTERY: ${(e.battery * 100).toFixed(0)}%. Decide whether to finish the current task or return to start.`;
    case "robot_stuck":
      return `ROBOT STUCK: ${e.detail}. Try to recover; ask for a takeover if you cannot.`;
    case "stale_case":
      return `STALE CASE: case ${e.caseId} has waited ${e.waitingMin.toFixed(1)} minutes without responders. Re-rank or escalate.`;
    case "recheck_case":
      return `RE-CHECK CASE ${e.caseId}: ${e.reason} The scene may have changed since the last look. Look again, re-triage if needed, and act on any change.`;
    case "sitrep_due":
      return "SITREP DUE. Give the rescuer a short spoken situation report through push_headset_alert.";
  }
}

export function eventKey(e: AgentEvent): string {
  switch (e.kind) {
    case "detection":
    case "stale_case":
    case "recheck_case":
      return `${e.kind}:${e.caseId}`;
    case "voice_command":
      return `${e.kind}:${e.text}`;
    default:
      return e.kind;
  }
}

/** Priority queue that drops duplicates of events already waiting. */
export class EventQueue {
  private items: AgentEvent[] = [];
  private waiters: ((e: AgentEvent) => void)[] = [];

  push(e: AgentEvent): void {
    if (this.items.some((x) => eventKey(x) === eventKey(e))) return;
    const w = this.waiters.shift();
    if (w) return w(e);
    this.items.push(e);
    this.items.sort((a, b) => PRIORITY[b.kind] - PRIORITY[a.kind]);
  }

  has(kind: AgentEvent["kind"]): boolean {
    return this.items.some((x) => x.kind === kind);
  }

  get size(): number {
    return this.items.length;
  }

  tryShift(): AgentEvent | undefined {
    return this.items.shift();
  }

  next(): Promise<AgentEvent> {
    const e = this.items.shift();
    if (e) return Promise.resolve(e);
    return new Promise((resolve) => this.waiters.push(resolve));
  }
}
