import type { Snapshot } from "../types.js";
import type { AssistantTurn, ChatMessage, ChatModel, ToolSpec } from "./types.js";
import type { MovementResult, VisionAnalyzer, VisionResult } from "./vision.js";

/**
 * Offline / degraded mode. The NVIDIA hosted API is a single point of failure in a
 * disaster zone (connectivity drops) and under load (429s, 5xx, very slow calls).
 * These wrappers put a per-call timeout and a circuit breaker in front of Nemotron and
 * answer from a safe rule-based fallback while it is unreachable, then probe Nemotron
 * again after a cooldown and switch back on the first success.
 */

export type HealthMode = "healthy" | "degraded";
export type ModeListener = (mode: HealthMode, reason: string) => void;

/** One shared status the app can show ("Nemotron: healthy/degraded"). Degraded if any reporter is degraded. */
export class Health {
  private readonly sources = new Map<string, { mode: HealthMode; reason: string }>();
  private readonly listeners = new Set<(mode: HealthMode, source: string, reason: string) => void>();
  private overall: HealthMode = "healthy";

  get mode(): HealthMode {
    return this.overall;
  }

  /** Per-source detail, e.g. { chat: {mode, reason}, vision: {...} }. */
  snapshot(): Record<string, { mode: HealthMode; reason: string }> {
    return Object.fromEntries(this.sources);
  }

  report(source: string, mode: HealthMode, reason: string): void {
    this.sources.set(source, { mode, reason });
    const next: HealthMode = [...this.sources.values()].some((s) => s.mode === "degraded") ? "degraded" : "healthy";
    if (next === this.overall) return;
    this.overall = next;
    for (const l of this.listeners) l(next, source, reason);
  }

  /** Fires when the overall mode changes. Returns an unsubscribe function. */
  onChange(listener: (mode: HealthMode, source: string, reason: string) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }
}

export interface ResilienceOptions {
  /** Per-call timeout for the primary, ms. A timeout counts as a failure. */
  timeoutMs?: number;
  /** Consecutive primary failures before going degraded. Default 2. */
  failureThreshold?: number;
  /** How long to skip the primary once degraded, ms. Default 60 000. */
  cooldownMs?: number;
  /** Injectable clock for tests. Default Date.now. */
  now?: () => number;
  /** Called once per transition healthy <-> degraded. */
  onModeChange?: ModeListener;
  /** Shared status to report into. */
  health?: Health;
  /** Name used when reporting to `health`. Default "chat" / "vision". */
  healthSource?: string;
}

export interface ResilienceStats {
  calls: number;
  primaryOk: number;
  fallbackUsed: number;
  failures: number;
}

export class TimeoutError extends Error {
  constructor(ms: number) {
    super(`timed out after ${ms} ms`);
    this.name = "TimeoutError";
  }
}

/** Race `p` against a timer that is always cleared. */
export async function withTimeout<T>(p: Promise<T>, ms: number): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const t = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new TimeoutError(ms)), ms);
  });
  try {
    return await Promise.race([p, t]);
  } finally {
    if (timer) clearTimeout(timer);
  }
}

function shortReason(err: unknown): string {
  const e = err as { status?: number; message?: string } | undefined;
  const msg = (e?.message ?? String(err)).replace(/\s+/g, " ").trim();
  const head = e?.status ? `HTTP ${e.status}: ` : "";
  const s = head + msg;
  return s.length > 120 ? `${s.slice(0, 117)}...` : s;
}

/** Shared timeout + circuit breaker. closed (healthy) -> open (degraded) -> half-open probe -> closed. */
class Breaker {
  readonly stats: ResilienceStats = { calls: 0, primaryOk: 0, fallbackUsed: 0, failures: 0 };
  mode: HealthMode = "healthy";
  lastError: string | null = null;
  /** True when the most recent call was answered by the primary. */
  lastWasPrimary = false;
  private consecutive = 0;
  private openedAt = 0;
  private readonly timeoutMs: number;
  private readonly threshold: number;
  private readonly cooldownMs: number;
  private readonly now: () => number;

  constructor(private readonly opts: ResilienceOptions, defaultTimeout: number, private readonly source: string) {
    this.timeoutMs = opts.timeoutMs ?? defaultTimeout;
    this.threshold = Math.max(1, opts.failureThreshold ?? 2);
    this.cooldownMs = opts.cooldownMs ?? 60_000;
    this.now = opts.now ?? Date.now;
    opts.health?.report(opts.healthSource ?? source, "healthy", "start");
  }

  /** ms left before the next probe of the primary (0 when healthy or probe due). */
  get cooldownRemainingMs(): number {
    if (this.mode === "healthy") return 0;
    return Math.max(0, this.openedAt + this.cooldownMs - this.now());
  }

  async run<T>(primary: () => Promise<T>, fallback: (reason: string) => Promise<T>): Promise<T> {
    this.stats.calls++;
    if (this.mode === "degraded" && this.cooldownRemainingMs > 0) {
      return this.useFallback(fallback, `degraded: ${this.lastError ?? "primary unavailable"}`);
    }
    // healthy, or degraded with the cooldown over (half-open probe)
    try {
      const out = await withTimeout(primary(), this.timeoutMs);
      this.stats.primaryOk++;
      this.consecutive = 0;
      this.lastWasPrimary = true;
      if (this.mode === "degraded") this.setMode("healthy", "primary answered again");
      return out;
    } catch (err) {
      const reason = shortReason(err);
      this.stats.failures++;
      this.consecutive++;
      this.lastError = reason;
      if (this.mode === "degraded") {
        this.openedAt = this.now(); // probe failed: stay degraded, restart cooldown
      } else if (this.consecutive >= this.threshold) {
        this.openedAt = this.now();
        this.setMode("degraded", reason);
      }
      return this.useFallback(fallback, reason);
    }
  }

  private async useFallback<T>(fallback: (reason: string) => Promise<T>, reason: string): Promise<T> {
    this.stats.fallbackUsed++;
    this.lastWasPrimary = false;
    return fallback(reason);
  }

  private setMode(mode: HealthMode, reason: string): void {
    if (mode === this.mode) return;
    this.mode = mode;
    this.opts.health?.report(this.opts.healthSource ?? this.source, mode, reason);
    try {
      this.opts.onModeChange?.(mode, reason);
    } catch {
      /* a status callback must never break the agent */
    }
  }
}

type Usage = { promptTokens?: number; completionTokens?: number; reasoningTokens?: number };

/**
 * ChatModel that uses `primary` (NemotronChat) and falls back to `fallback`
 * (MockPlanner, rule-based) on error/timeout, with a circuit breaker.
 * Mixing the two inside one agent run is fine: both read plain ChatMessage[] history.
 */
export class ResilientChat implements ChatModel {
  private readonly breaker: Breaker;
  /** Token usage of the last call, only when the primary answered it. */
  lastUsage?: Usage;

  constructor(readonly primary: ChatModel, readonly fallback: ChatModel, opts: ResilienceOptions = {}) {
    this.breaker = new Breaker(opts, 25_000, "chat");
  }

  /** Active path: primary name when healthy, "<primary>→fallback" when degraded or the last call fell back. */
  get name(): string {
    const fellBack = this.breaker.mode === "degraded" || (this.breaker.stats.calls > 0 && !this.breaker.lastWasPrimary);
    return fellBack ? `${this.primary.name}→fallback` : this.primary.name;
  }
  get mode(): HealthMode {
    return this.breaker.mode;
  }
  get stats(): Readonly<ResilienceStats> {
    return this.breaker.stats;
  }
  get lastError(): string | null {
    return this.breaker.lastError;
  }
  get cooldownRemainingMs(): number {
    return this.breaker.cooldownRemainingMs;
  }

  complete(messages: ChatMessage[], tools: ToolSpec[]): Promise<AssistantTurn> {
    this.lastUsage = undefined;
    return this.breaker.run(
      async () => {
        const turn = await this.primary.complete(messages, tools);
        this.lastUsage = (this.primary as { lastUsage?: Usage }).lastUsage;
        return turn;
      },
      () => this.fallback.complete(messages, tools),
    );
  }
}

/** The safe answer when vision is unreachable: never claims a person is visible. */
export function offlineVisionResult(reason: string): VisionResult {
  return {
    personVisible: "unsure",
    confidence: 0,
    description: `Vision unavailable (offline or rate-limited): ${reason}. Treat as unconfirmed.`,
    injuries: [],
    hazards: [],
    suggestedTriage: "unknown",
    nextLook: null,
    source: "fallback",
  };
}

/** VisionAnalyzer with the same timeout + breaker; returns `offlineVisionResult` on failure. */
export class ResilientVision implements VisionAnalyzer {
  private readonly breaker: Breaker;

  constructor(readonly primary: VisionAnalyzer, opts: ResilienceOptions = {}) {
    this.breaker = new Breaker(opts, 20_000, "vision");
  }

  get mode(): HealthMode {
    return this.breaker.mode;
  }
  get stats(): Readonly<ResilienceStats> {
    return this.breaker.stats;
  }
  get lastError(): string | null {
    return this.breaker.lastError;
  }
  get cooldownRemainingMs(): number {
    return this.breaker.cooldownRemainingMs;
  }

  analyze(snapshot: Snapshot, question?: string): Promise<VisionResult> {
    return this.breaker.run(
      () => this.primary.analyze(snapshot, question),
      async (reason) => offlineVisionResult(reason),
    );
  }

  /** Movement check through the same breaker; offline it says "unsure", never "no movement". */
  compareFrames(a: Snapshot, b: Snapshot): Promise<MovementResult> {
    const primary = this.primary;
    if (!primary.compareFrames) return Promise.resolve({ movement: "unsure", detail: "movement check not supported", confidence: 0, source: "fallback" });
    return this.breaker.run(
      () => primary.compareFrames!(a, b),
      async (reason) => ({ movement: "unsure", detail: `Movement check unavailable (offline or rate-limited): ${reason}`, confidence: 0, source: "fallback" }),
    );
  }
}
