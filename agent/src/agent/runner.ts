import type { Notifier } from "../adapters/notifier.js";
import type { RobotAdapter } from "../adapters/robot.js";
import type { StateStore } from "../adapters/state.js";
import type { VisionAnalyzer } from "../llm/vision.js";
import type { ToolContext } from "../tools/registry.js";
import type { Snapshot } from "../types.js";
import { EventQueue, type AgentEvent } from "./events.js";
import type { AgentLogger } from "./logger.js";
import { runEpisode, type EpisodeResult } from "./loop.js";
import type { Team } from "./team.js";
import { runDetectionWorkflow, type WorkflowDeps } from "./workflow.js";

export interface RunnerDeps {
  realRobotEvent?: (event: AgentEvent) => Promise<EpisodeResult>;
  /** Coordinator + specialists (or a solo agent). */
  team: Team;
  /** Set to run detection/recheck events through the fixed workflow instead of the coordinator. */
  workflow?: WorkflowDeps;
  logger: AgentLogger;
  robot: RobotAdapter;
  state: StateStore;
  vision: VisionAnalyzer;
  notifier: Notifier;
  robotId: number;
  responderPhones: string[];
  confirmTimeoutMs: number;
  sitrepIntervalMs: number;
  staleCaseMs: number;
  lowBattery?: number;
  now?: () => number;
}

/**
 * Turns state changes into events and runs them one at a time.
 * Watchers (tick) decide what wakes the agent; runEpisode decides what to do.
 */
export class AgentRunner {
  readonly queue = new EventQueue();
  readonly history: EpisodeResult[] = [];
  private readonly snapshots = new Map<string, Snapshot>();
  private readonly handledCases = new Set<number>();
  private readonly escalatedAt = new Map<number, number>();
  private lowBatteryFired = false;
  private lastSitrep: number;
  private missionActive = false;
  missionComplete = false;
  private stopped = false;

  constructor(private readonly d: RunnerDeps) {
    this.lastSitrep = this.now();
  }

  private now(): number {
    return this.d.now?.() ?? Date.now();
  }

  private context(event: AgentEvent): ToolContext {
    return {
      robot: this.d.robot,
      state: this.d.state,
      vision: this.d.vision,
      notifier: this.d.notifier,
      robotId: this.d.robotId,
      responderPhones: this.d.responderPhones,
      confirmTimeoutMs: this.d.confirmTimeoutMs,
      snapshots: this.snapshots,
      event,
      trace: [],
    };
  }

  startMission(instruction?: string): void {
    this.missionActive = true;
    this.missionComplete = false;
    this.queue.push({ kind: "mission_start", instruction });
  }

  push(e: AgentEvent): void {
    // A detection pushed from outside (the visor intake) must not be queued again by the watcher in tick().
    if (e.kind === "detection") this.handledCases.add(e.caseId);
    this.queue.push(e);
  }

  /** Look at state and enqueue whatever needs the agent's attention. */
  async tick(): Promise<void> {
    if (this.d.realRobotEvent) return; // No simulated telemetry, dispatch or periodic narration on Prada.
    const robot = await this.d.robot.getState();
    const cases = await this.d.state.getCases();
    const now = this.now();

    for (const c of cases) {
      if (this.handledCases.has(c.id)) continue;
      this.handledCases.add(c.id);
      if (c.triage === "unknown") this.queue.push({ kind: "detection", caseId: c.id });
    }

    for (const c of cases) {
      if (c.status === "responders_sent" || c.status === "resolved") continue;
      const waited = now - c.createdAt;
      const lastEsc = this.escalatedAt.get(c.id) ?? c.createdAt;
      if (waited >= this.d.staleCaseMs && now - lastEsc >= this.d.staleCaseMs) {
        this.escalatedAt.set(c.id, now);
        this.queue.push({ kind: "stale_case", caseId: c.id, waitingMin: waited / 60_000 });
      }
    }

    if (!this.lowBatteryFired && robot.battery < (this.d.lowBattery ?? 0.2)) {
      this.lowBatteryFired = true;
      this.queue.push({ kind: "low_battery", battery: robot.battery });
    }

    if (now - this.lastSitrep >= this.d.sitrepIntervalMs) {
      this.lastSitrep = now;
      this.queue.push({ kind: "sitrep_due" });
    }

    if (this.missionActive && !this.missionComplete && !this.lowBatteryFired && robot.mode === "searching" && this.queue.size === 0) {
      this.queue.push({ kind: "search_step" });
    }
  }

  /** A search step queued earlier is stale if the robot has since stopped searching (takeover, manual, returning). */
  private async isStale(e: AgentEvent): Promise<boolean> {
    if (e.kind !== "search_step") return false;
    const s = await this.d.robot.getState();
    return s.mode !== "searching" || this.missionComplete;
  }

  async runEvent(e: AgentEvent): Promise<EpisodeResult> {
    const result = this.d.realRobotEvent ? await this.d.realRobotEvent(e) :
      this.d.workflow && (e.kind === "detection" || e.kind === "recheck_case")
        ? await runDetectionWorkflow(e, this.context(e), this.d.workflow)
        : await runEpisode(e, this.d.team.lead, this.context(e), this.d.logger);
    this.history.push(result);
    if (result.toolCalls.some((t) => t.name === "return_to_start" && t.ok)) this.missionComplete = true;
    return result;
  }

  /** Run until nothing is left to do or the limit is hit. Used by the demo and tests. */
  async runUntilIdle(maxEpisodes = 50, between?: () => Promise<void>): Promise<EpisodeResult[]> {
    const out: EpisodeResult[] = [];
    for (let i = 0; i < maxEpisodes && !this.stopped; i++) {
      await this.tick();
      const e = this.queue.tryShift();
      if (!e) break;
      if (await this.isStale(e)) continue;
      out.push(await this.runEvent(e));
      await between?.();
    }
    return out;
  }

  /** Long-running mode for main.ts: tick on a timer, run events as they come. */
  async runForever(watchIntervalMs: number): Promise<void> {
    const timer = setInterval(() => {
      this.tick().catch((e) => this.d.logger.info(`watch error: ${(e as Error).message}`));
    }, watchIntervalMs);
    try {
      while (!this.stopped) {
        const e = await this.queue.next();
        if (this.stopped) break;
        try {
          if (await this.isStale(e)) continue;
          await this.runEvent(e);
        } catch (err) {
          this.d.logger.info(`episode ${e.kind} failed: ${(err as Error).message}`);
        }
      }
    } finally {
      clearInterval(timer);
    }
  }

  stop(): void {
    this.stopped = true;
    this.queue.push({ kind: "sitrep_due" }); // wake runForever so it can exit
  }
}
