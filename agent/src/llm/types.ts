/** Minimal chat types, shaped like the OpenAI chat-completions API that build.nvidia.com serves. */

export interface ToolCall {
  id: string;
  name: string;
  /** Raw JSON string from the model. */
  arguments: string;
}

export type ChatMessage =
  | { role: "system"; content: string }
  | { role: "user"; content: string }
  | { role: "assistant"; content: string | null; toolCalls?: ToolCall[] }
  | { role: "tool"; toolCallId: string; name: string; content: string };

export interface ToolSpec {
  name: string;
  description: string;
  /** JSON Schema object. */
  parameters: Record<string, unknown>;
}

export interface AssistantTurn {
  content: string | null;
  toolCalls: ToolCall[];
}

/** The reasoning model. NemotronChat for real runs, MockPlanner offline, ScriptedModel in tests. */
export interface ChatModel {
  readonly name: string;
  complete(messages: ChatMessage[], tools: ToolSpec[]): Promise<AssistantTurn>;
}
