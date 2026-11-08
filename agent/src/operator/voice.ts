/**
 * Spoken operator updates.
 *
 * The rescuer wears a phone VR visor and drives the robot, so they cannot read a screen.
 * OperatorVoice turns agent alerts (the same text that goes to `pushAlert`) into short lines
 * for the ear, removes repeats, spaces out non-critical lines, and lets listeners (the SSE feed in
 * ./feed-server.ts, a TTS adapter, a test) subscribe to what should be said.
 *
 * Pure logic, no I/O. The clock and the timer are injectable.
 * The browser port of `rewriteForSpeech` lives in agent/web/public/speech.js (tests check they agree).
 */

export type VoiceLevel = "info" | "warning" | "critical";

export interface VoiceInput {
  text: string;
  level: VoiceLevel;
  source?: string;
  caseId?: number;
  /** When the alert happened (ms epoch). Default: the clock's now. */
  time?: number;
}

export interface Utterance {
  id: number;
  text: string;
  level: VoiceLevel;
  /** true = stop whatever is being said and say this now (critical lines). */
  interrupt: boolean;
  time: number;
  source?: string;
  caseId?: number;
}

export interface VoiceOptions {
  /** Clock in ms. Default Date.now. */
  now?: () => number;
  /** Timer used to release queued lines. `false` = no timer; call `flush()` yourself (tests). */
  setTimer?: ((fn: () => void, ms: number) => { cancel(): void }) | false;
  /** Same normalized text within this window is dropped. Default 30 s. */
  dedupeMs?: number;
  /** Minimum gap between non-critical lines. Default 4 s. */
  minGapMs?: number;
  /** Max non-critical lines waiting for their turn. Default 3. */
  maxQueue?: number;
  /** History kept for `recent()`. Default 50. */
  historySize?: number;
}

export const MAX_WORDS = 22;
export const MAX_CHARS = 140;

const TRIAGE_WORDS = /\b(RED|YELLOW|GREEN|BLACK)\b/g;
const EMOJI = /[\p{Extended_Pictographic}\u{1F1E6}-\u{1F1FF}\u{FE0F}\u{200D}\u{20E3}]/gu;
const APPROVAL = /^\s*approve\?\s*/i;
const DANGLING = /(\s+(a|an|the|and|or|but|of|for|to|in|on|at|by|with|from|while|his|her|its|their|is|are|was|near))+$/i;

const fmtNum = (s: string): string => {
  const n = Number(s);
  if (!Number.isFinite(n)) return s;
  const r = Math.round(n * 10) / 10;
  return (Object.is(r, -0) ? 0 : r).toString();
};

/** Strip markdown, emoji and symbols the ear can't use; make coordinates and numbers speakable. */
export function cleanForSpeech(input: string): string {
  let t = String(input ?? "");
  t = t.replace(/```[\s\S]*?```/g, " ");
  t = t.replace(/!?\[([^\]]*)\]\([^)]*\)/g, "$1"); // [text](url) -> text
  t = t.replace(/https?:\/\/\S+/g, " ");
  t = t.replace(/^\s{0,3}(#{1,6}|>|[-*+]|\d+[.)])\s+/gm, ""); // headings, quotes, bullets
  t = t.replace(/(\*\*|__|\*|_|`|~~)(?=\S)([^*_`~]*?\S)\1/g, "$2"); // **bold**, _it_, `code`
  t = t.replace(/[*`~|]/g, " ");
  t = t.replace(EMOJI, " ");
  t = t.replace(/\s+/g, " ");
  // "(x, y)" -> "at x, y" (no doubled "at")
  t = t.replace(/(\bat\s+)?\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)/gi, (_m, _at, x: string, y: string) => `at ${fmtNum(x)}, ${fmtNum(y)}`);
  // Other parentheses: keep the words, drop the brackets.
  t = t.replace(/\s*\(([^()]*)\)/g, (_m, inner: string) => (inner.trim() ? `, ${inner.trim()},` : ""));
  t = t.replace(/[()[\]{}]/g, " ");
  t = t.replace(TRIAGE_WORDS, (w) => w.toLowerCase());
  t = t.replace(/#(\d)/g, "$1");
  t = t.replace(/(\d)\s*°/g, "$1 degrees");
  t = t.replace(/(\d)\s*%/g, "$1 percent");
  t = t.replace(/(\d)\s?m\b(?![\w'])/g, "$1 meters");
  t = t.replace(/(^|[\s,(])-(\d)/g, "$1minus $2");
  t = t.replace(/\b\d+\.\d+\b/g, fmtNum); // beyond one decimal, and 2.0 -> 2
  t = t.replace(/\s*->\s*|\s*=>\s*/g, " to ");
  t = t.replace(/\s+/g, " ").trim();
  // tidy punctuation left behind by removals
  t = t.replace(/\s+([,.;:!?])/g, "$1");
  t = t.replace(/,\s*([,.;:!?])/g, "$1");
  t = t.replace(/([.;:!?])[.,]+/g, "$1");
  t = t.replace(/^[,.;:\s]+/, "");
  return t.trim();
}

const words = (s: string) => s.split(/\s+/).filter(Boolean).length;
const fits = (s: string, maxWords: number, maxChars: number) => words(s) <= maxWords && s.length <= maxChars;
const endSentence = (s: string) => {
  const t = s.replace(/[,;:\s-]+$/, "");
  return t && !/[.!?]$/.test(t) ? `${t}.` : t;
};

/** Cut to the limits: whole sentences first, then a clause boundary, then a word boundary. Never adds words. */
export function shorten(text: string, maxWords = MAX_WORDS, maxChars = MAX_CHARS): string {
  const t = text.trim();
  if (!t) return "";
  if (fits(t, maxWords, maxChars)) return endSentence(t);
  const sentences = t.split(/(?<=[.!?])\s+/);
  let out = "";
  for (const s of sentences) {
    const next = out ? `${out} ${s}` : s;
    if (!fits(next, maxWords, maxChars - 1)) break;
    out = next;
  }
  if (out) return endSentence(out);
  // First sentence alone is too long: cut at the last clause boundary that fits, else at a word.
  const first = sentences[0]!;
  const toks = first.split(/\s+/);
  let best = "";
  let clause = "";
  for (const w of toks) {
    const next = best ? `${best} ${w}` : w;
    if (!fits(next, maxWords, maxChars - 1)) break;
    best = next;
    if (/[,;:]$/.test(w) && words(best) >= 4) clause = best;
  }
  // A clause end sounds better, but not if it throws away most of the line (e.g. the description after "at x, y:").
  const useClause = clause && words(clause) >= 0.6 * words(best);
  // Don't end a word cut on a dangling little word ("... calling out for.").
  const trimmed = best.replace(DANGLING, "");
  return endSentence((useClause ? clause : trimmed || best) || first.slice(0, maxChars - 1));
}

export interface Rewritten {
  text: string;
  level: VoiceLevel;
  approval: boolean;
}

/** Alert text -> one short spoken line. Approval prompts ("Approve? ...") become a warning that says where to answer. */
export function rewriteForSpeech(text: string, level: VoiceLevel): Rewritten {
  const raw = String(text ?? "");
  if (APPROVAL.test(raw)) {
    const summary = shorten(cleanForSpeech(raw.replace(APPROVAL, "")), MAX_WORDS - 7, MAX_CHARS - 40).replace(/[.!?]+$/, "");
    return { text: summary ? `Approval needed: ${summary}. Answer on the console.` : "Approval needed. Answer on the console.", level: level === "critical" ? "critical" : "warning", approval: true };
  }
  return { text: shorten(cleanForSpeech(raw)), level, approval: false };
}

/** Key used for dedupe: case, punctuation and spacing don't matter. */
export function normalizeKey(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^\p{L}\p{N}]+/gu, " ")
    .trim();
}

type Pending = Omit<Utterance, "id" | "interrupt">;

export class OperatorVoice {
  private readonly now: () => number;
  private readonly setTimer: VoiceOptions["setTimer"];
  private readonly dedupeMs: number;
  private readonly minGapMs: number;
  private readonly maxQueue: number;
  private readonly historySize: number;

  private nextId = 0;
  private lastSpokenAt = Number.NEGATIVE_INFINITY;
  private readonly seen = new Map<string, number>();
  private readonly queue: Pending[] = [];
  private readonly history: Utterance[] = [];
  private readonly listeners = new Set<(u: Utterance) => void>();
  private timer: { cancel(): void } | null = null;
  private dropped = 0;

  constructor(o: VoiceOptions = {}) {
    this.now = o.now ?? Date.now;
    this.setTimer =
      o.setTimer ??
      ((fn, ms) => {
        const h = setTimeout(fn, ms);
        h.unref?.();
        return { cancel: () => clearTimeout(h) };
      });
    this.dedupeMs = o.dedupeMs ?? 30_000;
    this.minGapMs = o.minGapMs ?? 4_000;
    this.maxQueue = o.maxQueue ?? 3;
    this.historySize = o.historySize ?? 50;
  }

  /**
   * Offer one alert. Returns the utterance if it is said now; null if it was empty, a repeat
   * within the dedupe window, or queued behind the rate limit (it is then delivered to subscribers
   * when its turn comes, unless a newer line pushes it out of the queue).
   */
  push(input: VoiceInput): Utterance | null {
    const now = this.now();
    this.release(now);
    const r = input.source === 'conversation' ? { text: shorten(cleanForSpeech(input.text), 60, 400), level: input.level, approval: false } : rewriteForSpeech(input.text, input.level);
    if (!r.text) return null;
    const key = normalizeKey(r.text);
    for (const [k, t] of this.seen) if (now - t >= this.dedupeMs) this.seen.delete(k);
    if (input.source !== 'conversation' && input.source !== 'handoff' && this.seen.has(key)) return null;
    this.seen.set(key, now);

    const p: Pending = { text: r.text, level: r.level, time: input.time ?? now };
    if (input.source !== undefined) p.source = input.source;
    if (input.caseId !== undefined) p.caseId = input.caseId;

    if (input.source === "handoff") return this.emit(p, true, now);
    if (input.source === "conversation") return this.emit(p, false, now);
    if (p.level === "critical") return this.emit(p, true, now);
    if (this.queue.length === 0 && now - this.lastSpokenAt >= this.minGapMs) return this.emit(p, false, now);

    this.queue.push(p);
    while (this.queue.length > this.maxQueue) {
      const i = this.queue.findIndex((q) => q.level === "info");
      this.queue.splice(i >= 0 ? i : 0, 1);
      this.dropped++;
    }
    this.arm(now);
    return null;
  }

  /** Release queued lines whose turn has come (the built-in timer calls this; call it yourself with setTimer:false). */
  flush(): Utterance[] {
    return this.release(this.now());
  }

  subscribe(fn: (u: Utterance) => void): () => void {
    this.listeners.add(fn);
    return () => this.unsubscribe(fn);
  }

  unsubscribe(fn: (u: Utterance) => void): void {
    this.listeners.delete(fn);
  }

  /** Last n utterances said, oldest first (max 50 kept). */
  recent(n = 20): Utterance[] {
    const k = Math.max(0, Math.floor(n));
    return k ? this.history.slice(-k) : [];
  }

  /** Lines waiting for their turn. */
  pending(): number {
    return this.queue.length;
  }

  /** How many queued lines were pushed out by newer ones. */
  droppedCount(): number {
    return this.dropped;
  }

  /** Stop the release timer (queued lines stay; flush() still works). */
  dispose(): void {
    this.timer?.cancel();
    this.timer = null;
  }

  private release(now: number): Utterance[] {
    const out: Utterance[] = [];
    while (this.queue.length && now - this.lastSpokenAt >= this.minGapMs) out.push(this.emit(this.queue.shift()!, false, now));
    this.arm(now);
    return out;
  }

  private arm(now: number): void {
    this.timer?.cancel();
    this.timer = null;
    if (!this.queue.length || !this.setTimer) return;
    const wait = Math.max(0, this.lastSpokenAt + this.minGapMs - now);
    this.timer = this.setTimer(() => {
      this.timer = null;
      this.flush();
    }, wait);
  }

  private emit(p: Pending, interrupt: boolean, now: number): Utterance {
    const u: Utterance = { id: ++this.nextId, text: p.text, level: p.level, interrupt, time: p.time };
    if (p.source !== undefined) u.source = p.source;
    if (p.caseId !== undefined) u.caseId = p.caseId;
    this.lastSpokenAt = now;
    this.history.push(u);
    if (this.history.length > this.historySize) this.history.splice(0, this.history.length - this.historySize);
    for (const fn of [...this.listeners]) {
      try {
        fn(u);
      } catch {
        // a broken listener must not stop the others
      }
    }
    return u;
  }
}
