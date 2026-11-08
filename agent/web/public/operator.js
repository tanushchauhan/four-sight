/* Operator earpiece: listens to /api/operator/feed (SSE, event "say") and speaks each line.
 * Vanilla JS, offline. All text goes in through textContent.
 *
 * Browser limits worth knowing:
 * - Speech needs a user gesture first, hence "Tap to enable voice" (it says "Voice on." to unlock audio).
 * - iOS Safari suspends the page (and the SSE connection and speech) when the tab is in the background
 *   or the screen locks. Keep this page in front with the screen on; we ask for a screen wake lock where
 *   the browser supports it. When the page comes back, EventSource reconnects with Last-Event-ID and only
 *   recent missed lines are spoken. For a real background voice, the iOS visor app should poll the feed
 *   and use AVSpeechSynthesizer (see docs/agent.md, "Operator voice updates").
 * - Desktop Chrome keeps SSE and speech running in a background tab.
 * - ?feed=http://<host>:8788 points the page at another feed server (the feed sends CORS headers). */
"use strict";

(() => {
  const $ = (id) => document.getElementById(id);
  const RS = window.RescueSpeech;
  const KEY = "operator-earpiece-settings";
  /** Replayed / missed lines older than this are shown but not spoken. */
  const SPEAK_MAX_AGE_MS = 20_000;

  const store = {
    get() {
      try {
        return JSON.parse(window.localStorage.getItem(KEY) || "{}") || {};
      } catch {
        return {};
      }
    },
    set(v) {
      try {
        window.localStorage.setItem(KEY, JSON.stringify(v));
      } catch {
        /* storage blocked: settings just won't persist */
      }
    },
  };

  const saved = store.get();
  const settings = {
    muted: saved.muted === true,
    volume: typeof saved.volume === "number" ? saved.volume : 1,
    rate: typeof saved.rate === "number" ? saved.rate : 1.05,
  };
  const speaker = new RS.Speaker(settings);
  let enabled = false;
  const seen = new Set();
  const history = [];
  let wakeLock = null;

  function save() {
    store.set(settings);
  }

  function feedBase() {
    try {
      const f = new URLSearchParams(location.search).get("feed");
      if (f && /^https?:\/\//i.test(f)) return f.replace(/\/+$/, "");
    } catch {
      /* ignore */
    }
    return "";
  }

  function lvlOf(u) {
    return ["info", "warning", "critical"].includes(u.level) ? u.level : "info";
  }
  function clock(t) {
    try {
      return new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
    } catch {
      return "";
    }
  }

  function render(u) {
    const lvl = lvlOf(u);
    $("latest").className = `latest lvl-${lvl}`;
    $("latestText").textContent = u.text;
    $("latestMeta").textContent = [lvl, typeof u.caseId === "number" ? `case ${u.caseId}` : null, u.source, clock(u.time)].filter(Boolean).join(" · ");
    history.unshift(u);
    if (history.length > 50) history.pop();
    const ol = $("history");
    while (ol.firstChild) ol.removeChild(ol.firstChild);
    for (const h of history) {
      const li = document.createElement("li");
      li.className = `lvl-${lvlOf(h)}`;
      const t = document.createElement("span");
      t.className = "t";
      t.textContent = clock(h.time);
      li.appendChild(t);
      li.appendChild(document.createTextNode(h.text));
      ol.appendChild(li);
    }
  }

  function onSay(u) {
    if (!u || typeof u.text !== "string" || !u.text.trim()) return;
    const key = `${u.id}|${u.time}|${u.text}`;
    if (seen.has(key)) return;
    seen.add(key);
    render(u);
    const fresh = typeof u.time !== "number" || Date.now() - u.time < SPEAK_MAX_AGE_MS;
    if (enabled && fresh) speaker.say(u.text, { interrupt: u.interrupt === true || u.level === "critical" });
  }

  function setConn(kind, text) {
    const el = $("conn");
    el.className = `pill${kind ? ` ${kind}` : ""}`;
    el.textContent = text;
  }

  function connect() {
    let es;
    try {
      es = new EventSource(`${feedBase()}/api/operator/feed`);
    } catch (e) {
      setConn("bad", "no feed");
      return;
    }
    es.addEventListener("open", () => setConn("ok", "live"));
    es.addEventListener("error", () => setConn("bad", es.readyState === EventSource.CLOSED ? "disconnected" : "reconnecting…"));
    es.addEventListener("say", (ev) => {
      try {
        onSay(JSON.parse(ev.data));
      } catch {
        /* bad event */
      }
    });
    es.addEventListener("ping", () => setConn("ok", "live"));
  }

  async function keepAwake() {
    try {
      if ("wakeLock" in navigator && document.visibilityState === "visible") {
        wakeLock = await navigator.wakeLock.request("screen");
        wakeLock.addEventListener("release", () => (wakeLock = null));
      }
    } catch {
      wakeLock = null;
    }
  }

  function syncControls() {
    $("muteBtn").setAttribute("aria-pressed", String(settings.muted));
    $("muteBtn").textContent = settings.muted ? "Unmute" : "Mute";
    $("vol").value = String(settings.volume);
    $("volOut").textContent = `${Math.round(settings.volume * 100)}%`;
    $("rate").value = String(settings.rate);
    $("rateOut").textContent = `${settings.rate.toFixed(2)}×`;
    speaker.muted = settings.muted;
    speaker.volume = settings.volume;
    speaker.rate = settings.rate;
  }

  function init() {
    if (!speaker.supported) {
      $("noSpeech").hidden = false;
      $("enableBtn").hidden = true;
    }
    $("enableBtn").addEventListener("click", () => {
      enabled = speaker.unlock("Voice on.");
      $("enableBtn").textContent = enabled ? "Voice on" : "Voice unavailable";
      $("enableBtn").disabled = enabled;
      keepAwake();
    });
    $("muteBtn").addEventListener("click", () => {
      settings.muted = !settings.muted;
      if (settings.muted) speaker.stop();
      syncControls();
      save();
    });
    $("testBtn").addEventListener("click", () => {
      if (!enabled) enabled = speaker.unlock("Voice on.");
      speaker.say("Case 1 is red. Responders notified.", {});
    });
    $("vol").addEventListener("input", () => {
      settings.volume = Number($("vol").value);
      syncControls();
      save();
    });
    $("rate").addEventListener("input", () => {
      settings.rate = Number($("rate").value);
      syncControls();
      save();
    });
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible" && enabled && !wakeLock) keepAwake();
    });
    syncControls();
    connect();
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
