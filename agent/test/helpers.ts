import { MemoryState } from "../src/adapters/memory-state.js";
import { ConsoleNotifier } from "../src/adapters/notifier.js";
import { SimRobot } from "../src/adapters/sim-robot.js";
import { AgentLogger } from "../src/agent/logger.js";
import type { AgentSpec } from "../src/agent/loop.js";
import { AgentRunner } from "../src/agent/runner.js";
import { buildSolo, buildTeam, type TeamModels } from "../src/agent/team.js";
import type { ChatModel } from "../src/llm/types.js";
import { SimVision } from "../src/llm/vision.js";
import { buildRegistry } from "../src/tools/index.js";
import type { ToolContext } from "../src/tools/registry.js";

export interface SetupOpts {
  autoConfirm?: "approve" | "deny" | "none";
  confirmTimeoutMs?: number;
  maxSteps?: number;
  /** team (default) or single. With team, `model` is used for every role unless `models` is given. */
  mode?: "team" | "single";
  models?: Partial<TeamModels>;
}

export function setup(model: ChatModel, opts: SetupOpts = {}) {
  const state = new MemoryState({ autoConfirm: opts.autoConfirm ?? "approve" });
  const robot = new SimRobot({ onCheckin: (s, r) => state.robotCheckin(s, r) });
  const notifier = new ConsoleNotifier(() => {});
  const logger = new AgentLogger({ state, dir: null, print: () => {}, color: false });
  const registry = buildRegistry();
  const maxSteps = opts.maxSteps ?? 16;
  const team =
    opts.mode === "single"
      ? buildSolo(registry, model, maxSteps)
      : buildTeam(
          registry,
          { coordinator: opts.models?.coordinator ?? model, triage: opts.models?.triage ?? model, search: opts.models?.search ?? model },
          logger,
          { lead: maxSteps, member: maxSteps },
        );
  const runner = new AgentRunner({
    team,
    logger,
    robot,
    state,
    vision: new SimVision(),
    notifier,
    robotId: 1,
    responderPhones: ["+15555550100"],
    confirmTimeoutMs: opts.confirmTimeoutMs ?? 1000,
    sitrepIntervalMs: 10 ** 9,
    staleCaseMs: 10 ** 9,
  });
  const ctx: ToolContext = {
    robot,
    state,
    vision: new SimVision(),
    notifier,
    robotId: 1,
    responderPhones: ["+15555550100"],
    confirmTimeoutMs: opts.confirmTimeoutMs ?? 1000,
    snapshots: new Map(),
    event: { kind: "search_step" },
    trace: [],
  };
  /** A solo agent spec with every tool, for loop tests. */
  const solo = (m: ChatModel, steps = maxSteps): AgentSpec => ({ name: "solo", prompt: "ROLE: solo", model: m, registry, maxSteps: steps });
  return { state, robot, notifier, logger, registry, runner, ctx, team, solo };
}

/** Simulated rescuer: clears triaged takeover requests and hands control back. */
export function simulatedRescuer(state: MemoryState, robot: SimRobot) {
  return async () => {
    const triaged = new Set(state.cases.filter((c) => c.triage !== "unknown" || c.description).map((c) => c.id));
    const waiting = state.requests.filter((r) => r.status === "waiting" && (r.caseId === 0 || triaged.has(r.caseId)));
    if (!waiting.length) return;
    waiting.forEach((r) => (r.status = "done"));
    await robot.setMode("searching");
  };
}
