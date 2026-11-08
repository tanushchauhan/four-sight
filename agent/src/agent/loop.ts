import type { ChatMessage, ChatModel } from "../llm/types.js";
import type { ToolContext, ToolRegistry } from "../tools/registry.js";
import { describeEvent, type AgentEvent } from "./events.js";
import type { AgentLogger } from "./logger.js";
import { contextBlock } from "./prompts.js";

/** One agent on the team: its own prompt, model, tool subset and step budget. */
export interface AgentSpec {
  name: string;
  prompt: string;
  model: ChatModel;
  registry: ToolRegistry;
  maxSteps: number;
  /** End the run as soon as one of these tools succeeds (e.g. search stops after one sweep). */
  stopAfter?: readonly string[];
}

export interface TraceEntry {
  agent: string;
  name: string;
  args: unknown;
  ok: boolean;
  result: unknown;
  /** Wall time of the tool call (includes nested agents for delegate_*). */
  ms?: number;
  /** Epoch ms when the call finished. */
  at?: number;
}

export interface LlmUsage {
  promptTokens?: number;
  completionTokens?: number;
  reasoningTokens?: number;
}

/** One model call: who made it, how long it took, how many tool calls it asked for. */
export interface LlmTiming extends LlmUsage {
  agent: string;
  model: string;
  ms: number;
  toolCalls: number;
}

export interface AgentRunResult {
  agent: string;
  summary: string;
  steps: number;
  hitStepLimit: boolean;
}

export interface EpisodeResult {
  event: AgentEvent;
  summary: string;
  steps: number;
  /** Every tool call in the episode, including those made by delegated agents. */
  toolCalls: TraceEntry[];
  hitStepLimit: boolean;
}

const MAX_TOOL_RESULT_CHARS = 3000;

export async function situation(ctx: ToolContext): Promise<string> {
  try {
    const s = await ctx.robot.getState();
    const cases = await ctx.state.getCases();
    const open = cases.filter((c) => c.status !== "resolved");
    return contextBlock([
      `Robot ${s.id} at (${s.x.toFixed(1)}, ${s.y.toFixed(1)}) heading ${s.heading.toFixed(0)}°, camera pan ${s.pan}° tilt ${s.tilt}°, battery ${(s.battery * 100).toFixed(0)}%, mode ${s.mode}`,
      `Open cases: ${open.length ? open.map((c) => `#${c.id} ${c.triage} ${c.status} at (${c.x.toFixed(1)}, ${c.y.toFixed(1)})`).join("; ") : "none"}`,
      `Time: ${new Date().toISOString()}`,
    ]);
  } catch (e) {
    return contextBlock([`State unavailable: ${(e as Error).message}`]);
  }
}

/**
 * Run one agent on one task: ask model → run requested tools → feed results back →
 * repeat until it stops calling tools or hits its step budget.
 * Delegation tools call this recursively for sub-agents (ctx.depth + 1).
 */
export async function runAgent(spec: AgentSpec, task: string, ctx: ToolContext, logger: AgentLogger): Promise<AgentRunResult> {
  const depth = ctx.depth ?? 0;
  const who = { agent: spec.name, depth };
  const messages: ChatMessage[] = [
    { role: "system", content: spec.prompt },
    { role: "user", content: task },
  ];
  const specs = spec.registry.specs();
  let summary = "";
  let steps = 0;
  let hitStepLimit = false;
  const runStart = ctx.trace.length;

  for (;;) {
    if (steps >= spec.maxSteps) {
      hitStepLimit = true;
      summary = `Stopped after ${spec.maxSteps} steps (step limit).`;
      break;
    }
    steps++;
    const t0 = performance.now();
    const turn = await spec.model.complete(messages, specs);
    const llmMs = Math.round(performance.now() - t0);
    const usage = (spec.model as { lastUsage?: LlmUsage }).lastUsage;
    const timing: LlmTiming = { agent: spec.name, model: spec.model.name, ms: llmMs, toolCalls: turn.toolCalls.length, ...usage };
    ctx.llmTimings?.push(timing);
    logger.llm(timing, who);
    messages.push({ role: "assistant", content: turn.content, toolCalls: turn.toolCalls });
    if (turn.toolCalls.length === 0) {
      summary = turn.content?.trim() || "Done.";
      break;
    }
    if (turn.content) logger.thought(turn.content, who);

    let stopped: string | null = null;
    for (const call of turn.toolCalls) {
      logger.toolStart(call.name, who);
      const tt = performance.now();
      const outcome = await spec.registry.execute(call.name, call.arguments, { ...ctx, agent: spec.name, depth, runStart });
      const toolMs = Math.round(performance.now() - tt);
      if (outcome.ok && spec.stopAfter?.includes(call.name)) stopped = call.name;
      let args: unknown = call.arguments;
      try {
        args = JSON.parse(call.arguments || "{}");
      } catch {
        // keep raw string
      }
      ctx.trace.push({ agent: spec.name, name: call.name, args, ok: outcome.ok, result: outcome.result, ms: toolMs, at: Date.now() });
      await logger.tool({ time: Date.now(), event: `${ctx.event.kind}>${spec.name}`, tool: call.name, args, result: outcome.result }, outcome.ok, who, toolMs);
      let content = JSON.stringify(outcome.result ?? null);
      if (content.length > MAX_TOOL_RESULT_CHARS) content = `${content.slice(0, MAX_TOOL_RESULT_CHARS)}…(truncated)`;
      messages.push({ role: "tool", toolCallId: call.id, name: call.name, content });
    }
    if (stopped) {
      summary = turn.content?.trim() || `Finished after ${stopped}.`;
      break;
    }
  }

  logger.done(summary, steps, who);
  return { agent: spec.name, summary, steps, hitStepLimit };
}

/**
 * Run one tool from code (no model), recorded exactly like an agent's call: trace, timing, logs.
 * Used by the fixed detection workflow so its steps show up in the trace and the web page.
 */
export async function callTool(
  registry: ToolRegistry,
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
  logger: AgentLogger,
  agent = "workflow",
): Promise<{ ok: boolean; result: unknown }> {
  const depth = ctx.depth ?? 0;
  logger.toolStart(name, { agent, depth });
  const tt = performance.now();
  const outcome = await registry.execute(name, JSON.stringify(args), { ...ctx, agent, depth, runStart: ctx.runStart ?? 0 });
  const ms = Math.round(performance.now() - tt);
  ctx.trace.push({ agent, name, args, ok: outcome.ok, result: outcome.result, ms, at: Date.now() });
  await logger.tool({ time: Date.now(), event: `${ctx.event.kind}>${agent}`, tool: name, args, result: outcome.result }, outcome.ok, { agent, depth }, ms);
  return outcome;
}

/** Top-level: an event goes to the lead agent (the coordinator, or the solo agent). */
export async function runEpisode(event: AgentEvent, lead: AgentSpec, ctx: ToolContext, logger: AgentLogger): Promise<EpisodeResult> {
  const description = describeEvent(event);
  logger.event(event.kind, description);
  const r = await runAgent(lead, `${description}\n\n${await situation(ctx)}`, ctx, logger);
  return { event, summary: r.summary, steps: r.steps, toolCalls: ctx.trace, hitStepLimit: r.hitStepLimit };
}
