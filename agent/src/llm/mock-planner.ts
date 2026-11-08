import type { AssistantTurn, ChatMessage, ChatModel, ToolCall } from "./types.js";

/**
 * Offline stand-in for Nemotron: a fixed policy that issues the same kind of
 * tool calls the real model should. Lets the whole pipeline (events → tool loop →
 * adapters → logs) run with no API key and no hardware. It is NOT the product;
 * with LLM_MODE=nvidia the real model makes every decision.
 */

interface Step {
  name: string;
  args: Record<string, unknown>;
  result: any;
}

let seq = 0;
const call = (name: string, args: Record<string, unknown> = {}): ToolCall => ({ id: `mock_${++seq}`, name, arguments: JSON.stringify(args) });
const say = (content: string, ...calls: ToolCall[]): AssistantTurn => ({ content, toolCalls: calls });
const finish = (content: string): AssistantTurn => ({ content, toolCalls: [] });

function history(messages: ChatMessage[]): Step[] {
  const results = new Map<string, any>();
  for (const m of messages) {
    if (m.role === "tool") {
      try {
        results.set(m.toolCallId, JSON.parse(m.content));
      } catch {
        results.set(m.toolCallId, m.content);
      }
    }
  }
  const steps: Step[] = [];
  for (const m of messages) {
    if (m.role !== "assistant") continue;
    for (const c of m.toolCalls ?? []) steps.push({ name: c.name, args: JSON.parse(c.arguments || "{}"), result: results.get(c.id) });
  }
  return steps;
}

const last = (h: Step[], name: string) => [...h].reverse().find((s) => s.name === name);
const count = (h: Step[], name: string) => h.filter((s) => s.name === name).length;

function norm(deg: number): number {
  let d = deg % 360;
  if (d > 180) d -= 360;
  if (d <= -180) d += 360;
  return d;
}

/** Bearing of a point relative to the robot's nose, degrees (positive = left). */
function relBearing(robot: { x: number; y: number; heading: number }, tx: number, ty: number): number {
  const b = (Math.atan2(ty - robot.y, tx - robot.x) * 180) / Math.PI;
  return Math.round(norm(b - robot.heading));
}

export class MockPlanner implements ChatModel {
  readonly name = "mock-planner";

  async complete(messages: ChatMessage[]): Promise<AssistantTurn> {
    const sys = messages.find((m) => m.role === "system");
    const role = (sys && "content" in sys ? sys.content : "").match(/ROLE: (\w+)/)?.[1] ?? "solo";
    const first = messages.find((m) => m.role === "user");
    const text = first && "content" in first ? (first.content ?? "") : "";
    const h = history(messages);
    if (role === "triage") return this.detection(text, h, false);
    if (role === "triage_decision") return this.triageDecision(text, h);
    if (role === "search") return this.searchAgent(text, h);
    if (role === "coordinator") return this.coordinator(text, h);
    // solo: one agent does everything
    if (text.startsWith("DETECTION")) return this.detection(text, h, true);
    if (text.startsWith("RE-CHECK CASE")) return this.detection(text.replace("RE-CHECK CASE", "RE-CHECK case"), h, true);
    if (text.startsWith("MISSION START") || text.startsWith("SEARCH STEP") || text.startsWith("The rescuer released")) return this.search(h);
    if (text.startsWith("LOW BATTERY")) return this.lowBattery(h);
    if (text.startsWith("VOICE COMMAND")) return this.voice(text, h);
    if (text.startsWith("SITREP")) return this.sitrep(h);
    if (text.startsWith("STALE CASE")) return this.stale(text, h);
    if (text.startsWith("ROBOT STUCK")) return this.stuck(h);
    return finish("No policy for this event in mock mode.");
  }

  // ---------------- coordinator (team mode) ----------------

  private coordinator(text: string, h: Step[]): AssistantTurn {
    const lastStep = h.at(-1);
    if (text.startsWith("DETECTION")) {
      const caseId = Number(text.match(/case (\d+)/)?.[1] ?? 0);
      if (h.length === 0) return say(`New detection: hand case ${caseId} to the triage agent.`, call("delegate_triage", { case_id: caseId }));
      return this.afterTriage(caseId, h);
    }
    if (text.startsWith("RE-CHECK CASE")) {
      const caseId = Number(text.match(/CASE (\d+)/)?.[1] ?? 0);
      if (h.length === 0) return say(`Time passed: have triage look at case ${caseId} again.`, call("delegate_triage", { case_id: caseId, instructions: "RE-CHECK: the scene may have changed." }));
      return this.afterTriage(caseId, h, true);
    }
    if (text.startsWith("MISSION START") || text.startsWith("SEARCH STEP") || text.startsWith("The rescuer released")) {
      if (h.length === 0) return say("Nothing urgent: send the search agent to new ground.", call("delegate_search", { instructions: "Next unsearched spot, then sweep." }));
      if (lastStep?.name === "delegate_search" && lastStep.result?.new_case_ids?.length) {
        return say(`Search found case ${lastStep.result.new_case_ids[0]}; triage it now.`, call("delegate_triage", { case_id: lastStep.result.new_case_ids[0] }));
      }
      if (lastStep?.name === "delegate_triage") return this.afterTriage(lastStep.args.case_id as number, h);
      if (h.some((s) => s.name === "delegate_triage")) return this.afterTriage(last(h, "delegate_triage")!.args.case_id as number, h);
      if (lastStep?.name === "delegate_search" && lastStep.result?.area_complete && count(h, "return_to_start") === 0) {
        return say("Search agent reports the area is complete. Tell the rescuer and bring the robot back.", call("push_headset_alert", { text: "Search complete. Returning to entry.", level: "info" }), call("return_to_start"));
      }
      const r = last(h, "delegate_search")?.result;
      return finish(r?.new_case_ids?.length ? `Search found case ${r.new_case_ids.join(", ")}; triage next.` : `Search: ${r?.summary ?? "done"}`);
    }
    if (text.startsWith("ROBOT STUCK")) {
      if (h.length === 0) return say("Robot stuck: ask the search agent to recover.", call("delegate_search", { instructions: "RECOVER: back off and turn, then stop." }));
      return finish("Recovery delegated.");
    }
    if (text.startsWith("VOICE COMMAND")) {
      const cmd = text.toLowerCase();
      const nums = cmd.match(/(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)/);
      if (h.length === 0 && nums && !/(status|sitrep|report|come back|return|retreat)/.test(cmd)) {
        return say(`Rescuer wants (${nums[1]}, ${nums[2]}) checked: delegate to search.`, call("delegate_search", { instructions: `Go to ${nums[1]}, ${nums[2]} and sweep.` }));
      }
      if (h.length > 0 && h[0]!.name === "delegate_search") return finish(`Checked the spot: ${last(h, "delegate_search")?.result?.summary ?? "done"}`);
      return this.voice(text, h);
    }
    if (text.startsWith("LOW BATTERY")) return this.lowBattery(h);
    if (text.startsWith("SITREP")) return this.sitrep(h);
    if (text.startsWith("STALE CASE")) return this.stale(text, h);
    return finish("No policy for this event in mock mode.");
  }

  /** People-facing follow-up after the triage agent reports. */
  private afterTriage(caseId: number, h: Step[], recheck = false): AssistantTurn {
    const res = last(h, "delegate_triage")?.result;
    const color: string = res?.case?.triage ?? "unknown";
    // On a re-check only act again if the case got worse (red).
    if (recheck && color !== "red" && count(h, "request_takeover") === 0) return finish(`Re-checked case ${caseId}: still ${color}.`);
    if (count(h, "request_takeover") === 0) {
      const priority = color === "red" ? 95 : color === "yellow" ? 75 : color === "green" ? 40 : 55;
      const reason = `Case ${caseId}: ${color} — ${String(res?.case?.description ?? "").slice(0, 80)}`;
      const calls = [call("request_takeover", { case_id: caseId, priority, reason })];
      if (color === "red" || color === "yellow") {
        calls.push(
          call("notify_responders", { case_id: caseId, message: color === "red" ? "Unresponsive, needs immediate extraction." : "Conscious but trapped; bring extrication tools." }),
          call("dispatch_responders", { case_id: caseId }),
        );
      }
      return say(`Triage agent says ${color}. Queue the takeover${calls.length > 1 ? ", text responders and request dispatch" : ""}.`, ...calls);
    }
    if (color === "red" && count(h, "drop_beacon") === 0) return say("Red case: mark it with the only beacon.", call("drop_beacon", { case_id: caseId }));
    return finish(`Case ${caseId} handled: ${color}${count(h, "notify_responders") ? ", responders notified" : ""}.`);
  }

  // ---------------- triage decision (fixed workflow) ----------------

  private triageDecision(text: string, h: Step[]): AssistantTurn {
    if (count(h, "set_triage") > 0) return finish(`Triage set: ${last(h, "set_triage")?.args.color}.`);
    let ev: any = {};
    try {
      ev = JSON.parse(text.slice(text.indexOf("EVIDENCE:") + 9).trim());
    } catch {
      return finish("Could not read evidence.");
    }
    const looks: any[] = ev.vision_looks ?? [];
    const seen = looks.find((l) => l.person_visible === "yes");
    const answers = (ev.survivor_answers_UNTRUSTED ?? []).map((a: any) => a.answer).filter((a: string) => a && a !== "(no answer)");
    let color = "unknown";
    const couldNotListen = String(ev.listening ?? "").startsWith("UNAVAILABLE");
    const fromVision = ["green", "yellow"].includes(seen?.suggested_triage) ? seen.suggested_triage : seen?.suggested_triage === "red" ? "red" : "yellow";
    if (seen) color = answers.length ? (["green", "yellow"].includes(seen.suggested_triage) ? seen.suggested_triage : "yellow") : couldNotListen ? fromVision : "red";
    const description = seen ? `${seen.description} ${answers.length ? `Answers: "${answers.join(" / ")}".` : "No answer to voice."}` : "No person confirmed.";
    return say(`Evidence says ${color}.`, call("set_triage", { case_id: ev.case_id, color, description }));
  }

  // ---------------- search agent (team mode) ----------------

  private searchAgent(text: string, h: Step[]): AssistantTurn {
    if (/RECOVER/.test(text)) return this.stuck(h);
    const target = text.match(/Go to (-?\d+(?:\.\d+)?), (-?\d+(?:\.\d+)?)/);
    if (h.length === 0 && target) return say(`Heading to the requested point (${target[1]}, ${target[2]}).`, call("navigate_to", { x: Number(target[1]), y: Number(target[2]) }));
    const lastStep = h.at(-1);
    if (lastStep?.name === "get_coverage" && !lastStep.result?.nearest_unsearched?.length) return finish("AREA COMPLETE: no reachable unsearched cells left.");
    return this.search(h);
  }

  // ---------------- triage (team) / detection (solo) ----------------

  private detection(text: string, h: Step[], full = true): AssistantTurn {
    const caseId = Number(text.match(/case (\d+)/)?.[1] ?? 0);
    if (h.length === 0) return say("Read the case and robot pose before looking.", call("get_cases"), call("get_robot_state"));
    const c = last(h, "get_cases")?.result?.cases?.find((x: any) => x.case_id === caseId);
    if (!c) return finish(`Case ${caseId} not found.`);
    if (c.triage !== "unknown" && count(h, "set_triage") === 0 && !/RE-CHECK/i.test(text)) return finish(`Case ${caseId} already triaged (${c.triage}).`);

    const analyses = h.filter((s) => s.name === "analyze_image");
    const lastAnalysis = analyses.at(-1)?.result;
    const lastStep = h.at(-1)!;

    // After a reposition, re-aim and look again.
    if (lastStep.name === "navigate_to" || lastStep.name === "get_robot_state") {
      const pose = lastStep.name === "navigate_to" ? lastStep.result?.state : lastStep.result;
      if (pose) {
        const rel = relBearing(pose, c.x, c.y);
        // Pan-tilt only covers ±90°: turn the body when the spot is off to the side or behind.
        if (Math.abs(rel) > 60) return say(`Spot is ${rel}° off the nose; turn to face it.`, call("turn", { degrees: rel }), call("point_camera", { pan: 0, tilt: 0 }), call("take_snapshot"));
        return say("Aim the camera at the detection and capture a frame.", call("point_camera", { pan: rel, tilt: 0 }), call("take_snapshot"));
      }
    }
    if (lastStep.name === "take_snapshot") return say("Check the frame with the vision model.", call("analyze_image", { snapshot_id: lastStep.result.snapshot_id }));

    if (lastAnalysis && lastAnalysis.personVisible !== "yes" && analyses.length < 3) {
      // Second look: view the spot from a side we have not tried yet.
      const tried = h.filter((s) => s.name === "navigate_to").map((s) => s.args);
      const options = [0, 90, -90, 180]
        .map((a) => ({ x: +(c.x + 1.4 * Math.cos((a * Math.PI) / 180)).toFixed(1), y: +(c.y + 1.4 * Math.sin((a * Math.PI) / 180)).toFixed(1) }))
        .filter((p) => p.x > 0.2 && p.y > 0.2 && p.x < 7.8 && p.y < 5.8)
        .filter((p) => !tried.some((t) => t.x === p.x && t.y === p.y));
      const vp = options[0];
      if (vp) return say(`View unclear (${lastAnalysis.description}). Moving to (${vp.x}, ${vp.y}) for a second look.`, call("navigate_to", vp));
    }

    if (lastAnalysis?.personVisible === "yes" && count(h, "speak") === 0) {
      return say("Person confirmed. Reassure them and ask if they can hear.", call("speak", { text: "This is a rescue robot. Help is on the way. Can you hear me?" }));
    }
    if (lastStep.name === "speak") return say("Listen for a reply.", call("listen", { seconds: 6 }));
    if (lastStep.name === "listen" && count(h, "speak") < 2 && lastStep.result?.transcript) {
      return say("They responded. Ask about injuries.", call("speak", { text: "Are you hurt? Can you move?" }));
    }

    if (count(h, "set_triage") === 0) {
      const replies = h.filter((s) => s.name === "listen").map((s) => s.result?.transcript).filter(Boolean);
      let color = lastAnalysis?.suggestedTriage ?? "unknown";
      if (lastAnalysis?.personVisible === "yes" && replies.length === 0 && count(h, "listen") > 0) color = "red";
      const description =
        lastAnalysis?.personVisible === "yes"
          ? `${lastAnalysis.description} ${replies.length ? `Responds to voice: "${replies.join(" / ")}"` : "No response to voice."}`
          : `Unconfirmed after ${analyses.length} looks: ${lastAnalysis?.description ?? "no view"}`;
      const priority = color === "red" ? 95 : color === "yellow" ? 75 : color === "green" ? 40 : 55;
      if (!full) return say(`Triage ${color}; writing the card for the coordinator.`, call("set_triage", { case_id: caseId, color, description }));
      return say(
        `Triage ${color}. Queue a takeover so the rescuer can inspect.`,
        call("set_triage", { case_id: caseId, color, description }),
        call("request_takeover", { case_id: caseId, priority, reason: `Case ${caseId}: ${color} — ${description.slice(0, 80)}` }),
      );
    }
    const color = last(h, "set_triage")?.args.color;
    if (!full) return finish(`Case ${caseId}: ${color} after ${analyses.length} look(s) and ${count(h, "listen")} listen(s).`);
    if ((color === "red" || color === "yellow") && count(h, "notify_responders") === 0) {
      return say(
        "Urgent or trapped: alert responders and ask to dispatch the nearest team.",
        call("notify_responders", { case_id: caseId, message: color === "red" ? "Unresponsive, needs immediate extraction." : "Conscious but trapped; bring extrication tools." }),
        call("dispatch_responders", { case_id: caseId }),
      );
    }
    if (color === "red" && count(h, "drop_beacon") === 0) return say("Mark the red case with the beacon.", call("drop_beacon", { case_id: caseId }));
    return finish(`Case ${caseId} triaged ${color}, takeover queued${count(h, "notify_responders") ? ", responders notified" : ""}.`);
  }

  private search(h: Step[]): AssistantTurn {
    if (h.length === 0) return say("Check coverage for the next spot.", call("get_coverage"));
    const lastStep = h.at(-1)!;
    if (lastStep.name === "get_coverage") {
      const next = lastStep.result?.nearest_unsearched?.[0];
      if (!next) return say("Area fully searched. Tell the rescuer and head back.", call("push_headset_alert", { text: "Search complete. Returning to entry.", level: "info" }), call("return_to_start"));
      return say(`${lastStep.result.percent_searched}% searched. Going to (${next.x}, ${next.y}).`, call("navigate_to", { x: next.x, y: next.y }));
    }
    if (lastStep.name === "navigate_to") {
      const r = lastStep.result;
      const navs = h.filter((s) => s.name === "navigate_to");
      const target = navs[0]!.args as { x: number; y: number };
      if (r && !r.ok && navs.length === 1 && r.state) {
        // Detour once: step 1 m to the side, then try the target again.
        const rad = ((r.state.heading + 90) * Math.PI) / 180;
        const side = { x: +(r.state.x + Math.cos(rad)).toFixed(1), y: +(r.state.y + Math.sin(rad)).toFixed(1) };
        const alt = { x: +(r.state.x - Math.cos(rad)).toFixed(1), y: +(r.state.y - Math.sin(rad)).toFixed(1) };
        const inside = (p: { x: number; y: number }) => p.x > 0.3 && p.y > 0.3 && p.x < 7.7 && p.y < 5.7;
        const detour = inside(side) ? side : alt;
        return say(`Blocked by ${r.stoppedBy}. Detouring via (${detour.x}, ${detour.y}).`, call("navigate_to", detour));
      }
      if (navs.length === 2) return say("Retry the target from the detour point.", call("navigate_to", target));
      if (r && !r.ok && r.movedM < 0.3) {
        return say(
          `Blocked by ${r.stoppedBy}. Marking it so the planner skips that cell.`,
          call("mark_hazard", { kind: r.stoppedBy === "drop" ? "drop" : "obstacle", note: `could not reach (${target.x}, ${target.y})`, x: target.x, y: target.y }),
        );
      }
      return say("Sweep for people here.", call("sweep_360"));
    }
    if (lastStep.name === "mark_hazard") return say("Sweep from where we stopped.", call("sweep_360"));
    if (lastStep.name === "sweep_360") {
      const d = lastStep.result?.detections ?? [];
      const fresh = d.filter((x: any) => x.new_case);
      return finish(fresh.length ? `Sweep found ${fresh.length} possible person(s): case ${fresh.map((x: any) => x.case_id).join(", ")}.` : "Sweep clear; continuing.");
    }
    return finish("Search step done.");
  }

  private lowBattery(h: Step[]): AssistantTurn {
    if (h.length === 0) return say("Battery low: warn the rescuer and head back.", call("push_headset_alert", { text: "Battery low. Robot returning to entry.", level: "warning" }), call("return_to_start"));
    return finish("Returning to start on low battery.");
  }

  private voice(text: string, h: Step[]): AssistantTurn {
    const cmd = text.toLowerCase();
    const nums = cmd.match(/(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)/);
    if (h.length === 0) {
      if (/(status|sitrep|report)/.test(cmd)) return say("Rescuer asked for status.", call("get_coverage"), call("get_cases"));
      if (/(come back|return|retreat)/.test(cmd)) return say("Rescuer recalled the robot.", call("return_to_start"));
      if (nums) return say(`Going to (${nums[1]}, ${nums[2]}) as asked.`, call("navigate_to", { x: Number(nums[1]), y: Number(nums[2]) }));
      return say("Command not understood in mock mode.", call("push_headset_alert", { text: "Mock mode: try 'status', 'return', or 'go to x, y'.", level: "info" }));
    }
    const lastStep = h.at(-1)!;
    if (lastStep.name === "navigate_to") return say("Sweep at the requested spot.", call("sweep_360"));
    if (lastStep.name === "get_cases") return this.sitrepAlert(h);
    return finish("Voice command handled.");
  }

  private sitrepAlert(h: Step[]): AssistantTurn {
    if (count(h, "push_headset_alert") > 0) return finish("Sitrep sent.");
    const cov = last(h, "get_coverage")?.result;
    const cases: any[] = last(h, "get_cases")?.result?.cases ?? [];
    const red = cases.filter((c) => c.triage === "red").length;
    return say(
      "Summarize for the headset.",
      call("push_headset_alert", { text: `${cov?.percent_searched ?? "?"}% searched, ${cases.length} case(s), ${red} red.`, level: red ? "warning" : "info" }),
    );
  }

  private sitrep(h: Step[]): AssistantTurn {
    if (h.length === 0) return say("Gather status.", call("get_coverage"), call("get_cases"));
    return this.sitrepAlert(h);
  }

  private stale(text: string, h: Step[]): AssistantTurn {
    const caseId = Number(text.match(/case (\d+)/)?.[1] ?? 0);
    if (h.length === 0) return say("Look at the queue.", call("get_cases"));
    if (count(h, "set_priority") === 0) {
      const req = last(h, "get_cases")?.result?.takeover_queue?.find((r: any) => r.case_id === caseId);
      if (req) {
        return say(
          "Case waiting too long: bump it and alert.",
          call("set_priority", { request_id: req.request_id, priority: Math.min(100, req.priority + 15), reason: "waiting too long" }),
          call("push_headset_alert", { text: `Case ${caseId} still waiting. Please review.`, level: "warning" }),
        );
      }
    }
    return finish(`Escalated case ${caseId}.`);
  }

  private stuck(h: Step[]): AssistantTurn {
    if (h.length === 0) return say("Back off and turn to free the legs.", call("move", { meters: -0.3 }), call("turn", { degrees: 90 }));
    return finish("Recovery attempted.");
  }
}

/** Test helper: replays fixed turns in order. */
export class ScriptedModel implements ChatModel {
  readonly name = "scripted";
  readonly seen: ChatMessage[][] = [];
  constructor(private readonly turns: AssistantTurn[]) {}
  async complete(messages: ChatMessage[]): Promise<AssistantTurn> {
    this.seen.push([...messages]);
    return this.turns.shift() ?? finish("script exhausted");
  }
}

export { call as mockCall, finish as mockFinish };
