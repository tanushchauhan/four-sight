import OpenAI from "openai";
import type { AssistantTurn, ChatMessage, ChatModel, ToolCall, ToolSpec } from "./types.js";

export interface NemotronOptions {
  apiKey: string;
  baseUrl: string;
  model: string;
  /** native = OpenAI tools param; json = strict-JSON fallback we parse ourselves. */
  toolCallMode: "native" | "json";
  temperature?: number;
  maxTokens?: number;
  retries?: number;
  /** Override fetch (tests). */
  fetch?: typeof fetch;
}

/** Strip <think>…</think> blocks that Nemotron reasoning models may emit. */
export function stripThinking(text: string | null | undefined): string | null {
  if (!text) return text ?? null;
  const out = text.replace(/<think>[\s\S]*?<\/think>/g, "").trim();
  return out || null;
}

/** Find the first balanced {...} JSON object in a string. */
export function extractJsonObject(text: string): unknown {
  const fenced = text.match(/```(?:json)?\s*([\s\S]*?)```/);
  const src = fenced?.[1] ?? text;
  const start = src.indexOf("{");
  if (start < 0) throw new Error("no JSON object in model output");
  let depth = 0;
  let inStr = false;
  let esc = false;
  for (let i = start; i < src.length; i++) {
    const ch = src[i];
    if (inStr) {
      if (esc) esc = false;
      else if (ch === "\\") esc = true;
      else if (ch === '"') inStr = false;
      continue;
    }
    if (ch === '"') inStr = true;
    else if (ch === "{") depth++;
    else if (ch === "}") {
      depth--;
      if (depth === 0) return JSON.parse(src.slice(start, i + 1));
    }
  }
  throw new Error("unbalanced JSON object in model output");
}

function toOpenAI(messages: ChatMessage[]): OpenAI.Chat.ChatCompletionMessageParam[] {
  return messages.map((m): OpenAI.Chat.ChatCompletionMessageParam => {
    switch (m.role) {
      case "system":
      case "user":
        return { role: m.role, content: m.content };
      case "assistant":
        return {
          role: "assistant",
          content: m.content,
          ...(m.toolCalls?.length
            ? { tool_calls: m.toolCalls.map((c) => ({ id: c.id, type: "function" as const, function: { name: c.name, arguments: c.arguments } })) }
            : {}),
        };
      case "tool":
        return { role: "tool", tool_call_id: m.toolCallId, content: m.content };
    }
  });
}

/** Flatten tool traffic into plain text for models without native tool calling. */
function toJsonModeMessages(messages: ChatMessage[], tools: ToolSpec[]): OpenAI.Chat.ChatCompletionMessageParam[] {
  const toolList = tools.map((t) => `- ${t.name}: ${t.description}\n  args schema: ${JSON.stringify(t.parameters)}`).join("\n");
  const protocol = [
    "You call tools by replying with ONLY one JSON object, no prose outside it:",
    '{"thought": "<one short sentence>", "tool_calls": [{"name": "<tool>", "arguments": {...}}]}',
    'When you are finished, reply with {"thought": "...", "tool_calls": [], "final": "<short summary for the log>"}.',
    "Available tools:",
    toolList,
  ].join("\n");
  const out: OpenAI.Chat.ChatCompletionMessageParam[] = [];
  for (const m of messages) {
    if (m.role === "system") out.push({ role: "system", content: `${m.content}\n\n${protocol}` });
    else if (m.role === "user") out.push({ role: "user", content: m.content });
    else if (m.role === "assistant")
      out.push({
        role: "assistant",
        content: JSON.stringify({ thought: m.content ?? "", tool_calls: (m.toolCalls ?? []).map((c) => ({ name: c.name, arguments: safeParse(c.arguments) })) }),
      });
    else out.push({ role: "user", content: `Tool result for ${m.name}: ${m.content}` });
  }
  return out;
}

function safeParse(s: string): unknown {
  try {
    return JSON.parse(s);
  } catch {
    return s;
  }
}

let callSeq = 0;

/**
 * Retry NVIDIA API calls. 429 (rate limit) and 5xx back off 2 s, 4 s, 8 s, 16 s (or Retry-After);
 * other 4xx (bad request, model not available) fail at once; parse errors retry quickly.
 */
export async function withRetry<T>(fn: () => Promise<T>, retries = 4, sleep = (ms: number) => new Promise((r) => setTimeout(r, ms))): Promise<T> {
  let lastErr: unknown;
  for (let attempt = 0; attempt <= retries; attempt++) {
    try {
      return await fn();
    } catch (e) {
      lastErr = e;
      const err = e as { status?: number; headers?: { get?: (k: string) => string | null } | Record<string, string> };
      const status = err.status;
      if (status && status >= 400 && status < 500 && status !== 429 && status !== 408) throw e;
      if (attempt === retries) break;
      const h = err.headers as { get?: (k: string) => string | null } | undefined;
      const retryAfter = Number(typeof h?.get === "function" ? h.get("retry-after") : (err.headers as Record<string, string> | undefined)?.["retry-after"]);
      const wait = status === 429 || (status && status >= 500) ? (Number.isFinite(retryAfter) && retryAfter > 0 ? retryAfter * 1000 : 2000 * 2 ** attempt) : 300 * (attempt + 1);
      await sleep(wait);
    }
  }
  throw lastErr;
}

/** Pull token counts (incl. hidden reasoning tokens) out of a completion. */
export function usageOf(res: { usage?: unknown }): { promptTokens?: number; completionTokens?: number; reasoningTokens?: number } {
  const u = res.usage as { prompt_tokens?: number; completion_tokens?: number; completion_tokens_details?: { reasoning_tokens?: number | null } } | undefined;
  return { promptTokens: u?.prompt_tokens, completionTokens: u?.completion_tokens, reasoningTokens: u?.completion_tokens_details?.reasoning_tokens ?? undefined };
}

export class NemotronChat implements ChatModel {
  readonly name: string;
  /** Token usage of the most recent call (for latency/cost reports). */
  lastUsage?: { promptTokens?: number; completionTokens?: number; reasoningTokens?: number };
  private readonly client: OpenAI;

  constructor(private readonly opts: NemotronOptions) {
    this.name = opts.model;
    this.client = new OpenAI({ apiKey: opts.apiKey, baseURL: opts.baseUrl, ...(opts.fetch ? { fetch: opts.fetch } : {}) });
  }

  async complete(messages: ChatMessage[], tools: ToolSpec[]): Promise<AssistantTurn> {
    return withRetry(() => (this.opts.toolCallMode === "native" ? this.native(messages, tools) : this.jsonMode(messages, tools)), this.opts.retries ?? 4);
  }

  private async native(messages: ChatMessage[], tools: ToolSpec[]): Promise<AssistantTurn> {
    const res = await this.client.chat.completions.create({
      model: this.opts.model,
      messages: toOpenAI(messages),
      tools: tools.map((t) => ({ type: "function" as const, function: { name: t.name, description: t.description, parameters: t.parameters } })),
      tool_choice: "auto",
      temperature: this.opts.temperature ?? 0.2,
      max_tokens: this.opts.maxTokens ?? 4096,
    });
    const msg = res.choices[0]?.message;
    this.lastUsage = usageOf(res);
    if (!msg) throw new Error("empty completion");
    const toolCalls: ToolCall[] = (msg.tool_calls ?? [])
      .filter((c) => c.type === "function")
      .map((c) => {
        const fn = (c as { id: string; function: { name: string; arguments: string } });
        return { id: fn.id || `call_${++callSeq}`, name: fn.function.name, arguments: fn.function.arguments || "{}" };
      });
    return { content: stripThinking(msg.content), toolCalls };
  }

  private async jsonMode(messages: ChatMessage[], tools: ToolSpec[]): Promise<AssistantTurn> {
    const res = await this.client.chat.completions.create({
      model: this.opts.model,
      messages: toJsonModeMessages(messages, tools),
      temperature: this.opts.temperature ?? 0.2,
      max_tokens: this.opts.maxTokens ?? 4096,
    });
    this.lastUsage = usageOf(res);
    const text = stripThinking(res.choices[0]?.message?.content) ?? "";
    const parsed = extractJsonObject(text) as { thought?: string; final?: string; tool_calls?: { name: string; arguments?: unknown }[] };
    const toolCalls = (parsed.tool_calls ?? []).map((c) => ({ id: `call_${++callSeq}`, name: c.name, arguments: JSON.stringify(c.arguments ?? {}) }));
    return { content: parsed.final ?? parsed.thought ?? null, toolCalls };
  }
}
