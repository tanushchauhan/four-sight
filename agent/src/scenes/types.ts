import type { Triage } from "../types.js";

/** One camera view in a scene. */
export interface SceneFrame {
  /** Path relative to agent/scenes/ (manifest), or a data: URL (uploads). */
  image: string;
  /** Ground truth of what the image shows. Never shown to the agent. */
  caption: string;
  /** start = first view; reposition = after the robot moves/turns/pans and snapshots again; time = when the scenario clock advances. */
  trigger: "start" | "reposition" | "time";
  /** Ground truth for the movement check (offline mock vision only): does the person visibly move in this view? Absent = unknown. */
  moving?: boolean;
}

export interface SceneExpected {
  personPresent: boolean | "unclear";
  triage: Triage;
  mustDo: string[];
  mustNotDo: string[];
  notes: string;
}

export interface Scene {
  id: string;
  title: string;
  category: string;
  description: string;
  frames: SceneFrame[];
  detectorConfidence: number;
  replies: string[];
  /** Optional per-frame replies (null = use `replies`). */
  frameReplies?: (string[] | null)[];
  expected: SceneExpected | null;
  source: { kind: "photo" | "generated" | "upload"; url?: string; author?: string; license: string; model?: string; prompts?: string[] };
}

export interface SceneFile {
  scenes: Scene[];
}
