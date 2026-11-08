import OpenAI from "openai";
import type { Snapshot, Triage } from "../types.js";
import { TRIAGE_COLORS } from "../types.js";
import { extractJsonObject, stripThinking, withRetry } from "./nemotron.js";

export interface VisionResult {
  personVisible: "yes" | "no" | "unsure";
  /** 0..1, how sure the model is about what it sees. */
  confidence: number;
  description: string;
  injuries: string[];
  hazards: string[];
  suggestedTriage: Triage;
  /** What would make the view better, e.g. "pan left 30" or "move closer". */
  nextLook: string | null;
  /** fallback = vision was unreachable and a safe default was returned. */
  source: "nemotron" | "sim" | "fallback";
}

/** Did the person visibly move between two frames (breathing, limbs, head, eyes)? */
export interface MovementResult {
  movement: "yes" | "no" | "unsure";
  detail: string;
  /** 0..1 */
  confidence: number;
  source: "nemotron" | "sim" | "fallback";
}

export interface VisionAnalyzer {
  analyze(snapshot: Snapshot, question?: string): Promise<VisionResult>;
  /** Optional: compare two frames of the same view taken a few seconds apart, for signs of life. */
  compareFrames?(a: Snapshot, b: Snapshot): Promise<MovementResult>;
}

const MOVEMENT_PROMPT = `You are the motion-check step of a search-and-rescue robot. Image 1 is frame A and image 2 is frame B, taken a few seconds apart from the same low camera position.
Did the person visibly move between frame A and frame B? Look for chest or back rising/falling (breathing), limb or hand movement, head turning, eyes opening or closing.
Ignore differences caused only by camera noise, lighting, dust or smoke. If there is no person, or the frames are too different or unclear to compare, answer "unsure".
Reply with ONLY a JSON object: {"movement":"yes|no|unsure","detail":"what changed or stayed the same, one sentence","confidence":0.0-1.0}`;

function imageUrlOf(snapshot: Snapshot): string | null {
  return snapshot.imageBase64
    ? `data:${snapshot.mimeType ?? "image/jpeg"};base64,${snapshot.imageBase64}`
    : snapshot.url && /^https?:/.test(snapshot.url)
      ? snapshot.url
      : null;
}

function hasPixels(snapshot: Snapshot): boolean {
  return imageUrlOf(snapshot) !== null;
}

function normalizeMovement(raw: Record<string, unknown>, source: MovementResult["source"]): MovementResult {
  const m = String(raw.movement ?? "unsure").toLowerCase();
  return {
    movement: m === "yes" || m === "no" ? m : "unsure",
    detail: String(raw.detail ?? ""),
    confidence: Math.min(1, Math.max(0, Number(raw.confidence ?? 0.5) || 0)),
    source,
  };
}

const VISION_PROMPT = `You are the camera-analysis step of a search-and-rescue robot. Look at this frame from a low ground robot in a collapsed building.
Reply with ONLY a JSON object:
{"person_visible":"yes|no|unsure","confidence":0.0-1.0,"description":"what you see, one or two sentences",
 "injuries":["visible signs only"],"hazards":["debris, fire, water, unstable structure..."],
 "suggested_triage":"red|yellow|green|black|unknown","next_look":"how to get a better view, or null"}
Triage (START, provisional): red = immediate (not breathing normally, heavy bleeding, unresponsive), yellow = delayed (serious but stable, trapped),
green = minor/walking, black = deceased/expectant, unknown = cannot tell. Describe only what is visible; say "unsure" rather than guess.
person_visible means a REAL, living-looking human. Mannequins, training dummies, manikins, dolls, statues, piles of clothing or bedding are NOT people: answer "no" and say what the object is.
Rescuers/responders (helmets, uniforms) ARE people: answer "yes", and say in the description whether anyone is a casualty or they are only responders at work.`;

function normalize(raw: Record<string, unknown>, source: VisionResult["source"]): VisionResult {
  const pv = String(raw.person_visible ?? "unsure");
  const tri = String(raw.suggested_triage ?? "unknown") as Triage;
  return {
    personVisible: pv === "yes" || pv === "no" ? pv : "unsure",
    confidence: Math.min(1, Math.max(0, Number(raw.confidence ?? 0.5) || 0)),
    description: String(raw.description ?? ""),
    injuries: Array.isArray(raw.injuries) ? raw.injuries.map(String) : [],
    hazards: Array.isArray(raw.hazards) ? raw.hazards.map(String) : [],
    suggestedTriage: TRIAGE_COLORS.includes(tri) ? tri : "unknown",
    nextLook: raw.next_look ? String(raw.next_look) : null,
    source,
  };
}

/** Nemotron vision-language model on build.nvidia.com. Needs a real frame (imageBase64 or url). */
export class NemotronVision implements VisionAnalyzer {
  private readonly client: OpenAI;
  /**
   * thinking=false turns off the model's hidden reasoning (chat_template_kwargs.enable_thinking).
   * Measured Nov 8: ~1-3 s per frame instead of ~5-15 s, same descriptions.
   */
  constructor(apiKey: string, baseUrl: string, private readonly model: string, private readonly thinking = false) {
    this.client = new OpenAI({ apiKey, baseURL: baseUrl });
  }

  async analyze(snapshot: Snapshot, question?: string): Promise<VisionResult> {
    const imageUrl = imageUrlOf(snapshot);
    if (!imageUrl) throw new Error(`snapshot ${snapshot.id} has no image data`);
    const params = {
      model: this.model,
      temperature: 0.1,
      // With thinking on, Nemotron VL reasons before answering (500-2500 hidden tokens), so leave room.
      max_tokens: this.thinking ? 4000 : 800,
      // NVIDIA NIM extension: disable the hidden reasoning pass for speed.
      ...(this.thinking ? {} : { chat_template_kwargs: { enable_thinking: false } }),
      messages: [
        {
          role: "user",
          content: [
            { type: "text", text: question ? `${VISION_PROMPT}\nAlso answer: ${question}` : VISION_PROMPT },
            { type: "image_url", image_url: { url: imageUrl } },
          ],
        },
      ],
    };
    return withRetry(async () => {
    const res = await this.client.chat.completions.create(params as unknown as OpenAI.Chat.ChatCompletionCreateParamsNonStreaming);
    const choice = res.choices[0];
    const text = stripThinking(choice?.message?.content);
    if (!text) throw new Error(`vision model returned no answer (finish_reason=${choice?.finish_reason ?? "?"}); try again`);
    const raw = extractJsonObject(text) as Record<string, unknown>;
    if (!raw.person_visible && !raw.description) throw new Error(`vision model answer had no fields: ${text.slice(0, 200)}`);
    return normalize(raw, "nemotron");
    }, 4);
  }

  /** One call with both frames as two image parts (verified Nov 8 that the VL model accepts two images). */
  async compareFrames(a: Snapshot, b: Snapshot): Promise<MovementResult> {
    const urlA = imageUrlOf(a);
    const urlB = imageUrlOf(b);
    if (!urlA || !urlB) throw new Error(`snapshot ${!urlA ? a.id : b.id} has no image data`);
    const params = {
      model: this.model,
      temperature: 0.1,
      max_tokens: this.thinking ? 4000 : 400,
      ...(this.thinking ? {} : { chat_template_kwargs: { enable_thinking: false } }),
      messages: [
        {
          role: "user",
          content: [
            { type: "text", text: MOVEMENT_PROMPT },
            { type: "text", text: "Frame A:" },
            { type: "image_url", image_url: { url: urlA } },
            { type: "text", text: "Frame B:" },
            { type: "image_url", image_url: { url: urlB } },
          ],
        },
      ],
    };
    return withRetry(async () => {
      const res = await this.client.chat.completions.create(params as unknown as OpenAI.Chat.ChatCompletionCreateParamsNonStreaming);
      const choice = res.choices[0];
      const text = stripThinking(choice?.message?.content);
      if (!text) throw new Error(`vision model returned no answer (finish_reason=${choice?.finish_reason ?? "?"}); try again`);
      const raw = extractJsonObject(text) as Record<string, unknown>;
      if (!raw.movement) throw new Error(`vision model answer had no movement field: ${text.slice(0, 200)}`);
      return normalizeMovement(raw, "nemotron");
    }, 4);
  }
}

/** Stand-in for simulated snapshots: turns the sim's ground-truth scene into a vision result. */
export class SimVision implements VisionAnalyzer {
  async analyze(snapshot: Snapshot): Promise<VisionResult> {
    const sc = snapshot.simScene;
    if (!sc || sc.visibility === "none") {
      return normalize({ person_visible: "no", confidence: 0.8, description: sc?.description || "No person visible.", suggested_triage: "unknown" }, "sim");
    }
    if (sc.visibility === "partial") {
      return normalize(
        {
          person_visible: "unsure",
          confidence: 0.4,
          description: sc.description,
          suggested_triage: "unknown",
          next_look: `Person may be at bearing ${sc.bearingDeg}° (${sc.distanceM} m). Pan toward it or move to view from another side.`,
        },
        "sim",
      );
    }
    return normalize(
      {
        person_visible: "yes",
        confidence: 0.85,
        description: sc.description,
        injuries: /cut|bleed|pinned|blood/i.test(sc.description) ? [sc.description.match(/[^,.]*(cut|bleed|pinned|blood)[^,.]*/i)?.[0]?.trim() ?? ""] : [],
        suggested_triage: sc.truthTriage ?? "unknown",
      },
      "sim",
    );
  }

  /** Ground truth: SimScene.moving on the second frame (falls back to the first). Undefined = unsure. */
  async compareFrames(a: Snapshot, b: Snapshot): Promise<MovementResult> {
    const sc = b.simScene ?? a.simScene;
    if (!sc || sc.visibility === "none") {
      return normalizeMovement({ movement: "unsure", detail: "No person in view to check for movement.", confidence: 0.3 }, "sim");
    }
    const moving = b.simScene?.moving ?? a.simScene?.moving;
    if (moving === undefined) {
      return normalizeMovement({ movement: "unsure", detail: "Cannot tell from these frames whether the person moved.", confidence: 0.3 }, "sim");
    }
    const partial = sc.visibility === "partial";
    return normalizeMovement(
      moving
        ? { movement: "yes", detail: "Visible movement between frames (chest, limbs or head).", confidence: partial ? 0.55 : 0.8 }
        : { movement: "no", detail: "No visible movement between frames: chest, limbs and head look the same.", confidence: partial ? 0.5 : 0.75 },
      "sim",
    );
  }
}

/** Uses Nemotron when the snapshot has a real image, otherwise the sim stand-in. */
export class AutoVision implements VisionAnalyzer {
  constructor(private readonly real: VisionAnalyzer | null, private readonly sim: VisionAnalyzer = new SimVision()) {}

  analyze(snapshot: Snapshot, question?: string): Promise<VisionResult> {
    const hasImage = !!snapshot.imageBase64 || (!!snapshot.url && /^https?:/.test(snapshot.url));
    if (hasImage && this.real) return this.real.analyze(snapshot, question);
    return this.sim.analyze(snapshot, question);
  }

  async compareFrames(a: Snapshot, b: Snapshot): Promise<MovementResult> {
    const target = hasPixels(a) && hasPixels(b) && this.real ? this.real : this.sim;
    if (!target.compareFrames) return unsupportedMovement();
    return target.compareFrames(a, b);
  }
}

/** What check_movement returns when the analyzer cannot compare frames. */
export function unsupportedMovement(): MovementResult {
  return { movement: "unsure", detail: "movement check not supported", confidence: 0, source: "fallback" };
}
