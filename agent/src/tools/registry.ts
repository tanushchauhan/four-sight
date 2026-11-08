import type { NotifyResult, Notifier } from "../adapters/notifier.js";
import type { RobotAdapter } from "../adapters/robot.js";
import type { StateStore } from "../adapters/state.js";
import type { AgentEvent } from "../agent/events.js";
import type { TraceEntry } from "../agent/loop.js";
import type { ToolSpec } from "../llm/types.js";
import type { VisionAnalyzer } from "../llm/vision.js";
import type { Snapshot } from "../types.js";

export interface ToolContext {
  robot: RobotAdapter;
  state: StateStore;
  vision: VisionAnalyzer;
  notifier: Notifier;
  robotId: number;
  responderPhones: string[];
  confirmTimeoutMs: number;
  /** Snapshots taken this run, by id, so analyze_image can refer to them. */
  snapshots: Map<string, Snapshot>;
  event: AgentEvent;
  /** Every tool call this episode, across all agents (shared by reference). */
  trace: TraceEntry[];
  /** Which agent is calling, and how deep in the delegation chain (0 = lead). */
  agent?: string;
  depth?: number;
  /** Every model call this episode (for latency reports). */
  llmTimings?: import("../agent/loop.js").LlmTiming[];
  /** Index into trace where the current agent run began. */
  runStart?: number;
  /** Last SMS results, for tests and the log. */
  lastNotify?: NotifyResult[];
}

type JsonSchema = {
  type?: "object" | "number" | "integer" | "string" | "boolean";
  description?: string;
  properties?: Record<string, JsonSchema>;
  required?: string[];
  enum?: readonly (string | number)[];
  minimum?: number;
  maximum?: number;
};

export interface Tool {
  name: string;
  description: string;
  parameters: JsonSchema & { type: "object" };
  /** If set, a human must approve in the headset before the tool runs. Returns a one-line summary for the prompt. */
  confirm?: (args: Record<string, unknown>, ctx: ToolContext) => Promise<string> | string;
  run(args: Record<string, unknown>, ctx: ToolContext): Promise<unknown>;
}

export class ToolArgError extends Error {}

/** Check args against the schema, coercing numeric strings and booleans the way models often send them. */
export function validateArgs(schema: JsonSchema, raw: unknown): Record<string, unknown> {
  if (raw === null || raw === undefined || raw === "") raw = {};
  if (typeof raw !== "object" || Array.isArray(raw)) throw new ToolArgError("arguments must be a JSON object");
  const input = raw as Record<string, unknown>;
  const out: Record<string, unknown> = {};
  const props = schema.properties ?? {};
  for (const req of schema.required ?? []) {
    if (input[req] === undefined || input[req] === null) throw new ToolArgError(`missing required argument "${req}"`);
  }
  for (const [key, value] of Object.entries(input)) {
    const p = props[key];
    if (!p) continue; // ignore unknown args rather than failing the call
    if (value === null || value === undefined) continue;
    let v: unknown = value;
    if (p.type === "number" || p.type === "integer") {
      v = typeof value === "string" ? Number(value) : value;
      if (typeof v !== "number" || !Number.isFinite(v)) throw new ToolArgError(`"${key}" must be a number`);
      if (p.type === "integer") v = Math.round(v as number);
      if (p.minimum !== undefined && (v as number) < p.minimum) throw new ToolArgError(`"${key}" must be >= ${p.minimum}`);
      if (p.maximum !== undefined && (v as number) > p.maximum) throw new ToolArgError(`"${key}" must be <= ${p.maximum}`);
    } else if (p.type === "string") {
      if (typeof value !== "string") v = String(value);
    } else if (p.type === "boolean") {
      v = value === true || value === "true";
    }
    if (p.enum && !p.enum.includes(v as string | number)) throw new ToolArgError(`"${key}" must be one of ${p.enum.join(", ")}`);
    out[key] = v;
  }
  return out;
}

export interface ToolOutcome {
  ok: boolean;
  result: unknown;
  /** True when the operator denied or did not answer in time. */
  blocked?: boolean;
}

export class ToolRegistry {
  private readonly tools = new Map<string, Tool>();

  constructor(tools: Tool[]) {
    for (const t of tools) {
      if (this.tools.has(t.name)) throw new Error(`duplicate tool ${t.name}`);
      this.tools.set(t.name, t);
    }
  }

  names(): string[] {
    return [...this.tools.keys()];
  }

  /** A registry with only the named tools (plus extras), e.g. one agent's toolbox. */
  subset(names: readonly string[], extra: Tool[] = []): ToolRegistry {
    const picked = names.map((n) => {
      const t = this.tools.get(n);
      if (!t) throw new Error(`unknown tool ${n}`);
      return t;
    });
    return new ToolRegistry([...picked, ...extra]);
  }

  specs(): ToolSpec[] {
    return [...this.tools.values()].map((t) => ({
      name: t.name,
      description: t.confirm ? `${t.description} (Needs rescuer approval in the headset; may be denied.)` : t.description,
      parameters: t.parameters as Record<string, unknown>,
    }));
  }

  /** Run one tool call. Never throws: errors come back as results the model can read and react to. */
  async execute(name: string, argsJson: string, ctx: ToolContext): Promise<ToolOutcome> {
    const tool = this.tools.get(name);
    if (!tool) return { ok: false, result: { error: `unknown tool "${name}". Available: ${this.names().join(", ")}` } };
    let args: Record<string, unknown>;
    try {
      args = validateArgs(tool.parameters, argsJson ? JSON.parse(argsJson) : {});
    } catch (e) {
      return { ok: false, result: { error: `bad arguments for ${name}: ${(e as Error).message}` } };
    }
    try {
      if (tool.confirm) {
        const summary = await tool.confirm(args, ctx);
        const decision = await awaitConfirmation(ctx, name, summary, args);
        if (decision !== "approved") {
          return { ok: false, blocked: true, result: { status: decision, note: `Rescuer ${decision === "denied" ? "denied" : "did not answer"}: ${summary}. Do not retry right away.` } };
        }
      }
      return { ok: true, result: await tool.run(args, ctx) };
    } catch (e) {
      return { ok: false, result: { error: (e as Error).message } };
    }
  }
}

async function awaitConfirmation(ctx: ToolContext, action: string, summary: string, args: Record<string, unknown>): Promise<"approved" | "denied" | "expired"> {
  const id = await ctx.state.requestConfirmation(action, JSON.stringify({ summary, args }));
  await ctx.state.pushAlert(`Approve? ${summary}`, "warning");
  const deadline = Date.now() + ctx.confirmTimeoutMs;
  for (;;) {
    const c = await ctx.state.getConfirmation(id);
    if (c && c.status !== "pending") return c.status === "approved" ? "approved" : c.status === "denied" ? "denied" : "expired";
    if (Date.now() >= deadline) return "expired";
    await new Promise((r) => setTimeout(r, 500));
  }
}
