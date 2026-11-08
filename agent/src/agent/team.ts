import type { ChatModel } from "../llm/types.js";
import { getCoverage } from "../tools/index.js";
import type { Tool, ToolRegistry } from "../tools/registry.js";
import type { AgentLogger } from "./logger.js";
import { runAgent, situation, type AgentSpec } from "./loop.js";
import { COORDINATOR_PROMPT, SEARCH_PROMPT, SOLO_PROMPT, TRIAGE_PROMPT } from "./prompts.js";

/** Which tools each agent may use. The registry enforces it: an agent never sees other tools. */
export const TOOLSETS = {
  coordinator: [
    "get_robot_state",
    "get_cases",
    "get_coverage",
    "get_responders",
    "request_takeover",
    "set_priority",
    "dispatch_responders",
    "notify_responders",
    "drop_beacon",
    "push_headset_alert",
    "return_to_start",
  ],
  triage: [
    "get_robot_state",
    "get_sensor_readings",
    "get_cases",
    "point_camera",
    "take_snapshot",
    "analyze_image",
    "speak",
    "listen",
    "check_movement",
    "move",
    "turn",
    "navigate_to",
    "set_triage",
    "mark_hazard",
  ],
  search: ["get_robot_state", "get_sensor_readings", "get_coverage", "navigate_to", "move", "turn", "sweep_360", "mark_hazard"],
} as const;

export interface TeamModels {
  coordinator: ChatModel;
  triage: ChatModel;
  search: ChatModel;
}

export interface Team {
  lead: AgentSpec;
  members: AgentSpec[];
}

/**
 * Coordinator + two specialists. The specialists are exposed to the coordinator as
 * tools (delegate_triage, delegate_search): calling one runs that agent's own
 * multi-step loop and returns its result. One robot, so delegations run one at a time.
 */
export function buildTeam(base: ToolRegistry, models: TeamModels, logger: AgentLogger, steps: { lead: number; member: number }): Team {
  const triage: AgentSpec = { name: "triage", prompt: TRIAGE_PROMPT, model: models.triage, registry: base.subset(TOOLSETS.triage), maxSteps: steps.member };
  // One sweep per delegation, enforced in code: small fast models otherwise keep going to the step limit.
  const search: AgentSpec = {
    name: "search",
    prompt: SEARCH_PROMPT,
    model: models.search,
    registry: base.subset(TOOLSETS.search),
    maxSteps: Math.min(steps.member, 10),
    stopAfter: ["sweep_360"],
  };

  const delegateTriage: Tool = {
    name: "delegate_triage",
    description:
      "Hand a case to the triage agent. It takes second looks until sure, talks to the survivor, and writes the triage card. Returns its summary and the updated case (triage color, description).",
    parameters: {
      type: "object",
      properties: { case_id: { type: "integer", minimum: 1 }, instructions: { type: "string", description: "Optional extra guidance." } },
      required: ["case_id"],
    },
    run: async (a, ctx) => {
      const caseId = a.case_id as number;
      const before = (await ctx.state.getCases()).find((c) => c.id === caseId);
      if (!before) throw new Error(`no case ${caseId}`);
      const r0 = await ctx.robot.getState();
      const dist = Math.hypot(before.x - r0.x, before.y - r0.y);
      let rel = (Math.atan2(before.y - r0.y, before.x - r0.x) * 180) / Math.PI - r0.heading;
      rel = ((((rel + 180) % 360) + 360) % 360) - 180;
      const task = [
        `TRIAGE TASK: case ${caseId} at (${before.x.toFixed(1)}, ${before.y.toFixed(1)}), detector confidence ${before.confidence.toFixed(2)}.`,
        `It is ${dist.toFixed(1)} m from the robot at ${rel.toFixed(0)}° relative to the robot's nose (positive = left). ${Math.abs(rel) > 80 ? "That is outside the camera's pan range: turn the body first." : "Pan the camera toward it."}`,
        "Never drive onto the case position; view it from 1-1.5 m away.",
        a.instructions ? `Coordinator says: ${a.instructions}` : "",
        await situation(ctx),
      ]
        .filter(Boolean)
        .join("\n\n");
      const r = await runAgent(triage, task, { ...ctx, depth: (ctx.depth ?? 0) + 1 }, logger);
      const after = (await ctx.state.getCases()).find((c) => c.id === caseId)!;
      return { agent: "triage", summary: r.summary, steps: r.steps, case: { case_id: after.id, triage: after.triage, description: after.description, x: after.x, y: after.y } };
    },
  };

  const delegateSearch: Tool = {
    name: "delegate_search",
    description:
      "Hand robot movement to the search agent: go to new ground (or the point/task you describe) and do one sweep, mapping hazards on the way. Returns new_case_ids, percent_searched, and area_complete (computed from the coverage grid; trust it over the summary).",
    parameters: { type: "object", properties: { instructions: { type: "string" } } },
    run: async (a, ctx) => {
      // One search per event: keeps episodes short so detections, low battery and voice commands get in between.
      const already = ctx.trace.slice(ctx.runStart ?? 0).some((t) => t.agent === ctx.agent && t.name === "delegate_search");
      if (already) throw new Error("Only one delegate_search per event. Finish now with a summary; you will be woken for the next search step.");
      const start = ctx.trace.length;
      const task = [`SEARCH TASK: ${a.instructions ?? "Continue the search: next unsearched spot, then sweep."}`, await situation(ctx)].join("\n\n");
      const r = await runAgent(search, task, { ...ctx, depth: (ctx.depth ?? 0) + 1 }, logger);
      const cov = (await getCoverage.run({ limit: 3 }, ctx)) as { percent_searched: number; area_complete: boolean; nearest_unsearched: unknown[] };
      const newCases = ctx.trace
        .slice(start)
        .filter((t) => t.name === "sweep_360" && t.ok)
        .flatMap((t) => ((t.result as { detections?: { case_id: number | null; new_case: boolean }[] }).detections ?? []).filter((d) => d.new_case).map((d) => d.case_id));
      return {
        agent: "search",
        summary: r.summary,
        steps: r.steps,
        new_case_ids: newCases,
        percent_searched: cov.percent_searched,
        area_complete: cov.area_complete,
        next_unsearched: cov.nearest_unsearched,
      };
    },
  };

  const lead: AgentSpec = {
    name: "coordinator",
    prompt: COORDINATOR_PROMPT,
    model: models.coordinator,
    registry: base.subset(TOOLSETS.coordinator, [delegateTriage, delegateSearch]),
    maxSteps: steps.lead,
  };
  return { lead, members: [triage, search] };
}

/** AGENT_MODE=single: one agent with every tool (the original design). */
export function buildSolo(base: ToolRegistry, model: ChatModel, maxSteps: number): Team {
  return { lead: { name: "solo", prompt: SOLO_PROMPT, model, registry: base, maxSteps }, members: [] };
}
