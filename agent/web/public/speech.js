/* Shared speech helpers for the operator earpiece (operator.html) and the scene bench (index.html).
 * Vanilla, offline. Exposes window.RescueSpeech = { rewrite, normalizeKey, Speaker }.
 * rewrite() is a port of rewriteForSpeech in agent/src/operator/voice.ts; agent/test/operator.test.ts
 * checks that both give the same output, so change them together. */
"use strict";

(function (root) {
  const MAX_WORDS = 22;
  const MAX_CHARS = 140;
  const TRIAGE_WORDS = /\b(RED|YELLOW|GREEN|BLACK)\b/g;
  const EMOJI = /[\p{Extended_Pictographic}\u{1F1E6}-\u{1F1FF}\u{FE0F}\u{200D}\u{20E3}]/gu;
  const APPROVAL = /^\s*approve\?\s*/i;
  const DANGLING = /(\s+(a|an|the|and|or|but|of|for|to|in|on|at|by|with|from|while|his|her|its|their|is|are|was|near))+$/i;

  function fmtNum(s) {
    const n = Number(s);
    if (!Number.isFinite(n)) return s;
    const r = Math.round(n * 10) / 10;
    return (Object.is(r, -0) ? 0 : r).toString();
  }

  function clean(input) {
    let t = String(input == null ? "" : input);
    t = t.replace(/```[\s\S]*?```/g, " ");
    t = t.replace(/!?\[([^\]]*)\]\([^)]*\)/g, "$1");
    t = t.replace(/https?:\/\/\S+/g, " ");
    t = t.replace(/^\s{0,3}(#{1,6}|>|[-*+]|\d+[.)])\s+/gm, "");
    t = t.replace(/(\*\*|__|\*|_|`|~~)(?=\S)([^*_`~]*?\S)\1/g, "$2");
    t = t.replace(/[*`~|]/g, " ");
    t = t.replace(EMOJI, " ");
    t = t.replace(/\s+/g, " ");
    t = t.replace(/(\bat\s+)?\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)/gi, (_m, _at, x, y) => `at ${fmtNum(x)}, ${fmtNum(y)}`);
    t = t.replace(/\s*\(([^()]*)\)/g, (_m, inner) => (inner.trim() ? `, ${inner.trim()},` : ""));
    t = t.replace(/[()[\]{}]/g, " ");
    t = t.replace(TRIAGE_WORDS, (w) => w.toLowerCase());
    t = t.replace(/#(\d)/g, "$1");
    t = t.replace(/(\d)\s*°/g, "$1 degrees");
    t = t.replace(/(\d)\s*%/g, "$1 percent");
    t = t.replace(/(\d)\s?m\b(?![\w'])/g, "$1 meters");
    t = t.replace(/(^|[\s,(])-(\d)/g, "$1minus $2");
    t = t.replace(/\b\d+\.\d+\b/g, fmtNum);
    t = t.replace(/\s*->\s*|\s*=>\s*/g, " to ");
    t = t.replace(/\s+/g, " ").trim();
    t = t.replace(/\s+([,.;:!?])/g, "$1");
    t = t.replace(/,\s*([,.;:!?])/g, "$1");
    t = t.replace(/([.;:!?])[.,]+/g, "$1");
    t = t.replace(/^[,.;:\s]+/, "");
    return t.trim();
  }

  const words = (s) => s.split(/\s+/).filter(Boolean).length;
  const fits = (s, mw, mc) => words(s) <= mw && s.length <= mc;
  function endSentence(s) {
    const t = s.replace(/[,;:\s-]+$/, "");
    return t && !/[.!?]$/.test(t) ? `${t}.` : t;
  }

  function shorten(text, maxWords = MAX_WORDS, maxChars = MAX_CHARS) {
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
    const first = sentences[0];
    let best = "";
    let clause = "";
    for (const w of first.split(/\s+/)) {
      const next = best ? `${best} ${w}` : w;
      if (!fits(next, maxWords, maxChars - 1)) break;
      best = next;
      if (/[,;:]$/.test(w) && words(best) >= 4) clause = best;
    }
    const useClause = clause && words(clause) >= 0.6 * words(best);
    const trimmed = best.replace(DANGLING, "");
    return endSentence((useClause ? clause : trimmed || best) || first.slice(0, maxChars - 1));
  }

  /** Alert text -> { text, level, approval } for the ear. */
  function rewrite(text, level) {
    const lvl = level === "critical" || level === "warning" ? level : "info";
    const raw = String(text == null ? "" : text);
    if (APPROVAL.test(raw)) {
      const summary = shorten(clean(raw.replace(APPROVAL, "")), MAX_WORDS - 7, MAX_CHARS - 40).replace(/[.!?]+$/, "");
      return { text: summary ? `Approval needed: ${summary}. Answer on the console.` : "Approval needed. Answer on the console.", level: lvl === "critical" ? "critical" : "warning", approval: true };
    }
    return { text: shorten(clean(raw)), level: lvl, approval: false };
  }

  function normalizeKey(text) {
    return String(text).toLowerCase().replace(/[^\p{L}\p{N}]+/gu, " ").trim();
  }

  /**
   * Thin wrapper over the Web Speech API.
   * - Browsers only allow speech after a user gesture: call unlock() from a click handler.
   * - interrupt:true cancels whatever is being said first (critical lines).
   * - kind "robot" uses a different English voice (if there is one) and a lower pitch.
   */
  class Speaker {
    constructor(opts) {
      const o = opts || {};
      this.rate = o.rate || 1.05;
      this.volume = o.volume == null ? 1 : o.volume;
      this.muted = !!o.muted;
      this.unlocked = false;
      this.voices = [];
      this.synth = typeof window !== "undefined" && window.speechSynthesis ? window.speechSynthesis : null;
      if (this.synth) {
        const load = () => {
          try {
            this.voices = this.synth.getVoices() || [];
          } catch {
            this.voices = [];
          }
        };
        load();
        try {
          this.synth.addEventListener("voiceschanged", load);
        } catch {
          /* old Safari: no addEventListener on speechSynthesis */
          this.synth.onvoiceschanged = load;
        }
      }
    }
    get supported() {
      return !!this.synth && typeof window.SpeechSynthesisUtterance === "function";
    }
    /** Preferred English voices: local en-US first, then any en-*. */
    englishVoices() {
      const en = this.voices.filter((v) => /^en([-_]|$)/i.test(v.lang || ""));
      const score = (v) => (/^en[-_]US/i.test(v.lang) ? 0 : 2) + (v.localService ? 0 : 1) + (v.default ? -0.5 : 0);
      return en.slice().sort((a, b) => score(a) - score(b));
    }
    voiceFor(kind) {
      const en = this.englishVoices();
      if (!en.length) return null;
      if (kind === "robot") return en.length > 1 ? en[1] : en[0];
      return en[0];
    }
    /** Call from a user gesture. Says a short confirmation so iOS/Chrome treat speech as allowed. */
    unlock(confirmText) {
      if (!this.supported) return false;
      this.unlocked = true;
      try {
        this.synth.cancel();
        const u = new window.SpeechSynthesisUtterance(confirmText || "Voice on.");
        u.volume = this.muted ? 0 : this.volume;
        u.rate = this.rate;
        const v = this.voiceFor("operator");
        if (v) u.voice = v;
        u.lang = v ? v.lang : "en-US";
        this.synth.speak(u);
      } catch {
        return false;
      }
      return true;
    }
    say(text, opts) {
      const o = opts || {};
      if (!this.supported || this.muted || !text) return false;
      try {
        if (o.interrupt) this.synth.cancel();
        // Chrome sometimes leaves the queue paused after a tab switch.
        if (this.synth.paused) this.synth.resume();
        const u = new window.SpeechSynthesisUtterance(String(text));
        const v = this.voiceFor(o.kind);
        if (v) u.voice = v;
        u.lang = v ? v.lang : "en-US";
        u.rate = o.kind === "robot" ? Math.max(0.5, this.rate - 0.1) : this.rate;
        u.pitch = o.kind === "robot" ? 0.75 : 1;
        u.volume = this.volume;
        if (o.onend) u.onend = o.onend;
        this.synth.speak(u);
        return true;
      } catch {
        return false;
      }
    }
    stop() {
      try {
        if (this.synth) this.synth.cancel();
      } catch {
        /* ignore */
      }
    }
  }

  const api = { rewrite, clean, shorten, normalizeKey, Speaker, MAX_WORDS, MAX_CHARS };
  root.RescueSpeech = api;
})(typeof window !== "undefined" ? window : globalThis);
