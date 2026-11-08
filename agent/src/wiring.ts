import { ConsoleNotifier, TwilioNotifier, type Notifier } from "./adapters/notifier.js";
import { MemoryState } from "./adapters/memory-state.js";
import { PiRobot } from "./adapters/pi-robot.js";
import type { RobotAdapter } from "./adapters/robot.js";
import { SimRobot, defaultWorld } from "./adapters/sim-robot.js";
import { SpacetimeState } from "./adapters/spacetime-state.js";
import type { StateStore } from "./adapters/state.js";
import { AgentLogger } from "./agent/logger.js";
import { AgentRunner } from "./agent/runner.js";
import { buildSolo, buildTeam, type Team } from "./agent/team.js";
import { buildTriageDecision, newMemo } from "./agent/workflow.js";
import type { Config } from "./config.js";
import { Health, ResilientChat, ResilientVision } from "./llm/fallback.js";
import { MockPlanner } from "./llm/mock-planner.js";
import { NemotronChat } from "./llm/nemotron.js";
import type { ChatModel } from "./llm/types.js";
import { AutoVision, NemotronVision } from "./llm/vision.js";
import { buildRegistry } from "./tools/index.js";

export interface App {
  config: Config;
  robot: RobotAdapter;
  state: StateStore;
  notifier: Notifier;
  model: ChatModel;
  logger: AgentLogger;
  runner: AgentRunner;
  team: Team;
}

/** Build every piece from config. Swap sim/memory/mock for pi/spacetime/nvidia with env vars only. */
export function buildApp(config: Config, opts: { print?: (l: string) => void; logDir?: string | null } = {}): App {
  const state: StateStore =
    config.stateMode === "spacetime"
      ? new SpacetimeState(config.spacetimeUrl, config.spacetimeDb, config.spacetimeToken, config.searchArea)
      : new MemoryState({ area: config.searchArea, autoConfirm: config.autoConfirm });

  const robot: RobotAdapter =
    config.robotMode === "pi"
      ? new PiRobot(config.piWsUrl)
      : new SimRobot({
          id: config.robotId,
          world: defaultWorld(config.searchArea),
          onCheckin: (s, r) => state.robotCheckin(s, r),
        });

  const notifier: Notifier =
    config.notifyMode === "twilio"
      ? new TwilioNotifier(config.twilioAccountSid, config.twilioAuthToken, config.twilioFrom)
      : new ConsoleNotifier(opts.print);

  // One client per distinct model ID; the mock planner reads each agent's ROLE from its prompt.
  const mock = new MockPlanner();
  // Offline / degraded mode: every Nemotron call falls back to rule-based behaviour if the API is unreachable.
  const health = new Health();
  health.onChange((mode, source, reason) => console.log(`[nemotron] ${mode} (${source}): ${reason}`));
  const clients = new Map<string, ChatModel>();
  const modelFor = (id: string): ChatModel => {
    if (config.llmMode !== "nvidia") return mock;
    let m = clients.get(id);
    if (!m) {
      m = new ResilientChat(new NemotronChat({ apiKey: config.nvidiaApiKey, baseUrl: config.nvidiaBaseUrl, model: id, toolCallMode: config.toolCallMode }), mock, { health, healthSource: `chat:${id}` });
      clients.set(id, m);
    }
    return m;
  };
  const model = modelFor(config.coordinatorModel);

  const vision = new AutoVision(
    config.nvidiaApiKey ? new ResilientVision(new NemotronVision(config.nvidiaApiKey, config.nvidiaBaseUrl, config.visionModel, config.visionThinking), { health }) : null,
  );
  const logger = new AgentLogger({ state, dir: opts.logDir === undefined ? config.logDir : opts.logDir, print: opts.print });

  const base = buildRegistry();
  const team =
    config.agentMode === "single"
      ? buildSolo(base, modelFor(config.reasoningModel), config.maxSteps)
      : buildTeam(
          base,
          { coordinator: modelFor(config.coordinatorModel), triage: modelFor(config.triageModel), search: modelFor(config.searchModel) },
          logger,
          { lead: config.maxSteps, member: config.subMaxSteps },
        );

  const workflow =
    config.detectionMode === "workflow" ? { registry: base, logger, decision: buildTriageDecision(base, modelFor(config.triageModel)), memo: newMemo() } : undefined;

  const runner = new AgentRunner({
    team,
    workflow,
    logger,
    robot,
    state,
    vision,
    notifier,
    robotId: config.robotId,
    responderPhones: config.responderPhones,
    confirmTimeoutMs: config.confirmTimeoutMs,

    sitrepIntervalMs: config.sitrepIntervalMs,
    staleCaseMs: config.staleCaseMs,
  });

  return { config, robot, state, notifier, model, logger, runner, team };
}
