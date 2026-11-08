import { appendFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import type { StateStore } from "../adapters/state.js";
import type { AgentLogEntry } from "../types.js";
import type { LlmTiming } from "./loop.js";

const C = { dim: "\x1b[2m", cyan: "\x1b[36m", green: "\x1b[32m", red: "\x1b[31m", yellow: "\x1b[33m", magenta: "\x1b[35m", bold: "\x1b[1m", reset: "\x1b[0m" };

export interface AgentLoggerOptions {
  state?: StateStore;
  dir?: string | null;
  print?: (line: string) => void;
  color?: boolean;
  /** Structured copy of every record (used by the web test bench to stream the trace). */
  onRecord?: (rec: Record<string, unknown>) => void;
}

/** Which agent is speaking and how deep in the delegation chain. */
export interface Who {
  agent: string;
  depth: number;
}

const ROOT: Who = { agent: "agent", depth: 0 };

/** Every tool call goes to: console (readable, indented per agent), JSONL file, and the agent_log table. */
export class AgentLogger {
  private readonly file: string | null;
  private readonly print: (line: string) => void;
  private readonly color: boolean;

  constructor(private readonly opts: AgentLoggerOptions = {}) {
    this.print = opts.print ?? ((l) => console.log(l));
    this.color = opts.color ?? process.stdout.isTTY ?? false;
    if (opts.dir) {
      mkdirSync(opts.dir, { recursive: true });
      this.file = join(opts.dir, `agent-${new Date().toISOString().slice(0, 10)}.jsonl`);
    } else this.file = null;
  }

  private c(code: keyof typeof C, s: string): string {
    return this.color ? `${C[code]}${s}${C.reset}` : s;
  }

  private pad(who: Who): string {
    return `${"    ".repeat(who.depth)}  ${this.c("magenta", `[${who.agent}]`)} `;
  }

  event(name: string, description: string): void {
    this.print(`\n${this.c("bold", `▶ ${name}`)} ${this.c("dim", description)}`);
    this.write({ type: "event", time: Date.now(), event: name, description });
  }

  thought(text: string, who: Who = ROOT): void {
    this.print(`${this.pad(who)}${this.c("cyan", "💭")} ${text}`);
    this.write({ type: "thought", time: Date.now(), agent: who.agent, depth: who.depth, text });
  }

  /** Printed before a delegation runs so the sub-agent's lines appear under it. */
  toolStart(name: string, who: Who = ROOT): void {
    if (name.startsWith("delegate_")) this.print(`${this.pad(who)}${this.c("yellow", "⇢")} ${this.c("bold", name)}`);
  }

  /** A model call finished. Console stays quiet; the record carries the latency. */
  llm(t: LlmTiming, who: Who = ROOT): void {
    this.write({ type: "llm", time: Date.now(), depth: who.depth, ...t });
  }

  async tool(entry: AgentLogEntry, ok: boolean, who: Who = ROOT, ms?: number): Promise<void> {
    const args = JSON.stringify(entry.args);
    const res = JSON.stringify(entry.result) ?? "null";
    this.print(
      `${this.pad(who)}${this.c(ok ? "green" : "red", ok ? "✓" : "✗")} ${this.c("bold", entry.tool)}(${args}) ${this.c("dim", `→ ${res.length > 200 ? `${res.slice(0, 200)}…` : res}${ms !== undefined ? ` (${ms} ms)` : ""}`)}`,
    );
    this.write({ type: "tool", ok, agent: who.agent, depth: who.depth, ms, ...entry });
    try {
      await this.opts.state?.logAgentAction(entry);
    } catch (e) {
      this.print(`  ${this.c("yellow", "!")} agent_log write failed: ${(e as Error).message}`);
    }
  }

  done(summary: string, steps: number, who: Who = ROOT): void {
    this.print(`${this.pad(who)}${this.c("yellow", "■")} ${summary} ${this.c("dim", `(${steps} steps)`)}`);
    this.write({ type: "done", time: Date.now(), agent: who.agent, depth: who.depth, summary, steps });
  }

  info(text: string): void {
    this.print(this.c("dim", text));
  }

  private write(obj: Record<string, unknown>): void {
    this.opts.onRecord?.(obj);
    if (this.file) appendFileSync(this.file, `${JSON.stringify(obj)}\n`);
  }
}
