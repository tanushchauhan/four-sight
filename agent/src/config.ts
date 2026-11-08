import type { SearchArea } from "./types.js";

export interface Config {
  llmMode: "mock" | "nvidia";
  nvidiaApiKey: string;
  nvidiaBaseUrl: string;
  reasoningModel: string;
  visionModel: string;
  /** Vision model hidden reasoning: off = ~5x faster per frame. */
  visionThinking: boolean;
  toolCallMode: "native" | "json";
  maxSteps: number;
  /** team = coordinator + triage + search agents; single = one agent with every tool. */
  agentMode: "team" | "single";
  /** workflow = code runs detection, Nemotron reads frames + decides triage; agent = coordinator decides every step. */
  detectionMode: "workflow" | "agent";
  /** Per-role models (team mode). reasoningModel is used in single mode. */
  coordinatorModel: string;
  triageModel: string;
  searchModel: string;
  /** Step budget for each delegated agent run. */
  subMaxSteps: number;

  robotMode: "sim" | "pi";
  robotId: number;
  piWsUrl: string;

  stateMode: "memory" | "spacetime";
  spacetimeUrl: string;
  spacetimeDb: string;
  spacetimeToken: string;
  searchArea: SearchArea;

  autoConfirm: "approve" | "deny" | "none";
  confirmTimeoutMs: number;

  notifyMode: "console" | "twilio";
  twilioAccountSid: string;
  twilioAuthToken: string;
  twilioFrom: string;
  responderPhones: string[];

  sitrepIntervalMs: number;
  staleCaseMs: number;
  watchIntervalMs: number;
  logDir: string;
}

function pick<T extends string>(value: string | undefined, allowed: readonly T[], fallback: T, name: string): T {
  if (value === undefined || value === "") return fallback;
  if ((allowed as readonly string[]).includes(value)) return value as T;
  throw new Error(`${name} must be one of ${allowed.join(", ")} (got "${value}")`);
}

function num(value: string | undefined, fallback: number, name: string): number {
  if (value === undefined || value === "") return fallback;
  const n = Number(value);
  if (!Number.isFinite(n)) throw new Error(`${name} must be a number (got "${value}")`);
  return n;
}

function parseArea(value: string | undefined): SearchArea {
  const parts = (value ?? "0,0,8,6").split(",").map(Number);
  if (parts.length !== 4 || parts.some((p) => !Number.isFinite(p))) {
    throw new Error(`SEARCH_AREA must be minX,minY,maxX,maxY (got "${value}")`);
  }
  const [minX, minY, maxX, maxY] = parts as [number, number, number, number];
  return { minX, minY, maxX, maxY };
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): Config {
  const cfg: Config = {
    llmMode: pick(env.LLM_MODE, ["mock", "nvidia"], "mock", "LLM_MODE"),
    nvidiaApiKey: env.NVIDIA_API_KEY ?? "",
    nvidiaBaseUrl: env.NVIDIA_BASE_URL || "https://integrate.api.nvidia.com/v1",
    reasoningModel: env.REASONING_MODEL || "nvidia/nemotron-3-ultra-550b-a55b",
    visionModel: env.VISION_MODEL || "nvidia/nemotron-3.5-super-vl-120b-a12b",
    visionThinking: pick(env.VISION_THINKING, ["on", "off"], "off", "VISION_THINKING") === "on",
    toolCallMode: pick(env.TOOL_CALL_MODE, ["native", "json"], "native", "TOOL_CALL_MODE"),
    maxSteps: num(env.MAX_STEPS, 16, "MAX_STEPS"),
    agentMode: pick(env.AGENT_MODE, ["team", "single"], "team", "AGENT_MODE"),
    detectionMode: pick(env.DETECTION_MODE, ["workflow", "agent"], "workflow", "DETECTION_MODE"),
    coordinatorModel: env.COORDINATOR_MODEL || "nvidia/nemotron-3-ultra-550b-a55b",
    triageModel: env.TRIAGE_MODEL || "nvidia/nemotron-3.5-lightning-30b-a3b",
    searchModel: env.SEARCH_MODEL || "nvidia/nemotron-3.5-bolt-e4b",
    subMaxSteps: num(env.SUB_MAX_STEPS, 16, "SUB_MAX_STEPS"),

    robotMode: pick(env.ROBOT_MODE, ["sim", "pi"], "sim", "ROBOT_MODE"),
    robotId: num(env.ROBOT_ID, 1, "ROBOT_ID"),
    piWsUrl: env.PI_WS_URL || "ws://raspberrypi.local:8765",

    stateMode: pick(env.STATE_MODE, ["memory", "spacetime"], "memory", "STATE_MODE"),
    spacetimeUrl: env.SPACETIME_URL || "https://maincloud.spacetimedb.com",
    spacetimeDb: env.SPACETIME_DB || "rescue-robot",
    spacetimeToken: env.SPACETIME_TOKEN ?? "",
    searchArea: parseArea(env.SEARCH_AREA),

    autoConfirm: pick(env.AUTO_CONFIRM, ["approve", "deny", "none"], "approve", "AUTO_CONFIRM"),
    confirmTimeoutMs: num(env.CONFIRM_TIMEOUT_MS, 30_000, "CONFIRM_TIMEOUT_MS"),

    notifyMode: pick(env.NOTIFY_MODE, ["console", "twilio"], "console", "NOTIFY_MODE"),
    twilioAccountSid: env.TWILIO_ACCOUNT_SID ?? "",
    twilioAuthToken: env.TWILIO_AUTH_TOKEN ?? "",
    twilioFrom: env.TWILIO_FROM ?? "",
    responderPhones: (env.RESPONDER_PHONES ?? "").split(",").map((s) => s.trim()).filter(Boolean),

    sitrepIntervalMs: num(env.SITREP_INTERVAL_MS, 120_000, "SITREP_INTERVAL_MS"),
    staleCaseMs: num(env.STALE_CASE_MS, 300_000, "STALE_CASE_MS"),
    watchIntervalMs: num(env.WATCH_INTERVAL_MS, 1_000, "WATCH_INTERVAL_MS"),
    logDir: env.LOG_DIR || "logs",
  };

  if (cfg.llmMode === "nvidia" && !cfg.nvidiaApiKey) {
    throw new Error("LLM_MODE=nvidia needs NVIDIA_API_KEY (get one at build.nvidia.com). Use LLM_MODE=mock to run offline.");
  }
  if (cfg.notifyMode === "twilio" && (!cfg.twilioAccountSid || !cfg.twilioAuthToken || !cfg.twilioFrom)) {
    throw new Error("NOTIFY_MODE=twilio needs TWILIO_ACCOUNT_SID, TWILIO_AUTH_TOKEN and TWILIO_FROM.");
  }
  return cfg;
}

/** Load agent/.env if present (Node 22 built-in, no dotenv needed). */
export function loadEnvFile(path = ".env"): void {
  try {
    process.loadEnvFile(path);
  } catch {
    // No .env file: defaults apply.
  }
}
