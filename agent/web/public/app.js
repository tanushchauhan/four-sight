/* Rescue Agent Test Bench — browser UI. Vanilla JS, no build step, no network beyond the local server.
 * All inserted text goes through textContent (see h()); nothing from the server is ever parsed as HTML. */
"use strict";

(() => {
  // ---------------------------------------------------------------- helpers

  const $ = (id) => document.getElementById(id);

  /** Element builder. attrs: class, text, on<event>, dataset, hidden, or any attribute. Children: nodes, strings, arrays, null. */
  function h(tag, attrs, ...children) {
    const el = document.createElement(tag);
    if (attrs) {
      for (const [k, v] of Object.entries(attrs)) {
        if (v === undefined || v === null || v === false) continue;
        if (k === "class") el.className = v;
        else if (k === "text") el.textContent = String(v);
        else if (k === "dataset") Object.assign(el.dataset, v);
        else if (k.startsWith("on") && typeof v === "function") el.addEventListener(k.slice(2), v);
        else if (k === "hidden") el.hidden = true;
        else if (k === "value") el.value = v;
        else el.setAttribute(k, v === true ? "" : String(v));
      }
    }
    append(el, children);
    return el;
  }
  function append(el, children) {
    for (const c of children) {
      if (c === null || c === undefined || c === false) continue;
      if (Array.isArray(c)) append(el, c);
      else if (c instanceof Node) el.appendChild(c);
      else el.appendChild(document.createTextNode(String(c)));
    }
  }
  function clear(el) {
    while (el.firstChild) el.removeChild(el.firstChild);
    return el;
  }

  const store = {
    get(key, fallback) {
      try {
        const raw = window.localStorage.getItem(key);
        return raw ? JSON.parse(raw) : fallback;
      } catch {
        return fallback;
      }
    },
    set(key, value) {
      try {
        window.localStorage.setItem(key, JSON.stringify(value));
      } catch {
        /* storage blocked or full: history just won't persist */
      }
    },
  };

  /** Only let image sources through that can't run script: same-origin paths, http(s), data:image, blob. */
  function safeImg(u) {
    if (typeof u !== "string" || !u) return null;
    if (u.startsWith("data:")) return /^data:image\/[a-z0-9.+-]+[;,]/i.test(u) ? u : null;
    try {
      const url = new URL(u, window.location.href);
      return ["http:", "https:", "blob:"].includes(url.protocol) ? url.href : null;
    } catch {
      return null;
    }
  }
  function safeHttp(u) {
    if (typeof u !== "string") return null;
    try {
      const url = new URL(u, window.location.href);
      return url.protocol === "http:" || url.protocol === "https:" ? url.href : null;
    } catch {
      return null;
    }
  }

  const TRIAGE = ["red", "yellow", "green", "black", "unknown", "unclear"];
  const AGENTS = ["coordinator", "triage", "search", "solo"];
  const agentClass = (a) => (AGENTS.includes(a) ? a : "other");
  const triageKey = (t) => (typeof t === "string" && TRIAGE.includes(t.toLowerCase()) ? t.toLowerCase() : "unknown");

  function triChip(t, big) {
    const label = t === undefined || t === null || t === "" ? "unknown" : String(t);
    return h("span", { class: `triage triage-${triageKey(label)}${big ? " triage-lg" : ""}`, text: label });
  }

  /** Render an "expected"/"actual" value: triage strings as chips, arrays as several, booleans as yes/no. */
  function renderVal(v) {
    if (Array.isArray(v)) return v.length ? v.map((x, i) => [i ? h("span", { class: "muted", text: "or" }) : null, renderVal(x)]) : h("span", { text: "—" });
    if (typeof v === "boolean") return h("span", { text: v ? "yes" : "no" });
    if (typeof v === "string" && TRIAGE.includes(v.toLowerCase())) return triChip(v);
    if (v === null || v === undefined) return h("span", { text: "—" });
    return h("span", { text: typeof v === "object" ? JSON.stringify(v) : String(v) });
  }

  /** Deep copy for display with long strings (base64 frames) shortened. */
  function forDisplay(value, limit = 600, depth = 0) {
    if (typeof value === "string") {
      if (value.length <= limit) return value;
      const kind = /^data:|^[A-Za-z0-9+/=]{200,}$/.test(value.slice(0, 400)) ? "binary/base64" : "text";
      return `${value.slice(0, Math.min(80, limit))}… <${value.length.toLocaleString()} chars ${kind}>`;
    }
    if (value === null || typeof value !== "object") return value;
    if (depth > 12) return "…";
    if (Array.isArray(value)) {
      const out = value.slice(0, 200).map((v) => forDisplay(v, limit, depth + 1));
      if (value.length > 200) out.push(`… ${value.length - 200} more`);
      return out;
    }
    const out = {};
    for (const [k, v] of Object.entries(value)) out[k] = forDisplay(v, limit, depth + 1);
    return out;
  }
  function pretty(value) {
    if (value === undefined) return "undefined";
    try {
      return JSON.stringify(forDisplay(value, 4000), null, 2);
    } catch {
      return String(value);
    }
  }
  function compact(value, max) {
    let s;
    try {
      s = JSON.stringify(forDisplay(value, 60));
    } catch {
      s = String(value);
    }
    if (s === undefined) s = "";
    return s.length > max ? `${s.slice(0, max - 1)}…` : s;
  }
  function compactArgs(args, max = 140) {
    if (args === undefined || args === null) return "";
    if (typeof args !== "object" || Array.isArray(args)) return compact(args, max);
    const parts = Object.entries(args).map(([k, v]) => `${k}: ${compact(v, 60)}`);
    const s = parts.join(", ");
    return s.length > max ? `${s.slice(0, max - 1)}…` : s;
  }
  function errorText(result) {
    if (result && typeof result === "object") {
      if (typeof result.error === "string") return result.error;
      if (result.error) return compact(result.error, 400);
      if (typeof result.message === "string") return result.message;
      if (typeof result.status === "string") return `status: ${result.status}`;
    }
    return typeof result === "string" ? result : compact(result, 400);
  }
  const fmtSec = (ms) => (typeof ms === "number" && isFinite(ms) ? (ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(1)} s`) : "—");
  const fmtClock = (t) => {
    if (typeof t !== "number") return "";
    try {
      return new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
    } catch {
      return "";
    }
  };
  function verdictClass(passed, total) {
    if (!total) return "noscore";
    if (passed >= total) return "pass";
    return passed / total >= 0.5 ? "partial" : "fail";
  }

  async function api(path, opts = {}, timeoutMs = 15000) {
    const ctl = new AbortController();
    const timer = setTimeout(() => ctl.abort(), timeoutMs);
    try {
      const res = await fetch(path, { ...opts, signal: ctl.signal, headers: { "content-type": "application/json", ...(opts.headers || {}) } });
      let body = null;
      const text = await res.text();
      try {
        body = text ? JSON.parse(text) : null;
      } catch {
        body = { error: text.slice(0, 300) };
      }
      if (!res.ok) {
        const msg = (body && (body.error || body.message)) || `${res.status} ${res.statusText}`;
        const err = new Error(typeof msg === "string" ? msg : JSON.stringify(msg));
        err.status = res.status;
        throw err;
      }
      return body;
    } catch (e) {
      if (e.name === "AbortError") throw new Error("The server took too long to answer.");
      throw e;
    } finally {
      clearTimeout(timer);
    }
  }

  // ---------------------------------------------------------------- app state

  const HISTORY_KEY = "rescueBench.history.v1";
  const UPLOAD_ID = "__upload__";

  const app = {
    scenes: [],
    llmAvailable: false,
    filter: "all",
    selected: null, // scene id or UPLOAD_ID
    upload: null, // { dataUrl, name, width, height }
    run: null,
    pickerCollapsed: false,
    pauseScroll: false,
  };

  // ---------------------------------------------------------------- spoken updates
  // Same rewrite rules as the operator earpiece (speech.js ↔ agent/src/operator/voice.ts).

  const VOICE_KEY = "rescueBench.voice.v1";
  const voice = {
    speakUpdates: false,
    robotVoice: false,
    speaker: null,
    /** normalized text -> time spoken, for the 30 s repeat filter */
    said: new Map(),
  };

  function voiceInit() {
    const saved = store.get(VOICE_KEY, {}) || {};
    voice.speakUpdates = saved.speakUpdates === true;
    voice.robotVoice = saved.robotVoice === true;
    const RS = window.RescueSpeech;
    voice.speaker = RS ? new RS.Speaker({ rate: 1.05 }) : null;
    const ok = !!(voice.speaker && voice.speaker.supported);
    for (const [id, keyName] of [["speakUpdates", "speakUpdates"], ["robotVoice", "robotVoice"]]) {
      const box = $(id);
      if (!box) continue;
      box.checked = ok && voice[keyName];
      box.disabled = !ok;
      box.addEventListener("change", () => {
        voice[keyName] = box.checked;
        store.set(VOICE_KEY, { speakUpdates: voice.speakUpdates, robotVoice: voice.robotVoice });
        // The click is the user gesture browsers want before speech is allowed.
        if (box.checked) voice.speaker.unlock(keyName === "robotVoice" ? "Robot voice on." : "Spoken updates on.");
        else if (!voice.speakUpdates && !voice.robotVoice) voice.speaker.stop();
      });
    }
    if (!ok && $("voiceHint")) $("voiceHint").hidden = false;
  }

  /** Speak headset alerts that are new in this state event (diffed by text + level, kept across reconnect replays). */
  function voiceAlerts(d, run) {
    if (!Array.isArray(d.alerts)) return;
    for (const a of d.alerts) {
      if (!a || typeof a.text !== "string") continue;
      const lvl = ["info", "warning", "critical"].includes(a.level) ? a.level : "info";
      const key = `${lvl}|${a.text}`;
      if (run.spokenAlerts.has(key)) continue;
      run.spokenAlerts.add(key);
      if (!voice.speakUpdates || !voice.speaker) continue;
      const r = window.RescueSpeech.rewrite(a.text, lvl);
      if (!r.text) continue;
      const now = Date.now();
      const nk = window.RescueSpeech.normalizeKey(r.text);
      if (now - (voice.said.get(nk) || 0) < 30_000) continue;
      voice.said.set(nk, now);
      voice.speaker.say(r.text, { interrupt: r.level === "critical" });
    }
  }

  /** Speak the robot's own lines (the server replays events on reconnect, so count them). Silence says nothing. */
  function voiceRobot(d, run) {
    const idx = run.speechIdx++;
    if (idx < run.speechSpoken) return;
    run.speechSpoken = idx + 1;
    if (!voice.robotVoice || !voice.speaker || d.who !== "robot") return;
    if (typeof d.text !== "string" || !d.text.trim()) return;
    voice.speaker.say(window.RescueSpeech.clean(d.text), { kind: "robot" });
  }

  // ---------------------------------------------------------------- scenes + picker

  async function loadScenes() {
    setPill($("serverPill"), "muted", "connecting…");
    $("offline").hidden = true;
    try {
      if (window.location.protocol === "file:") throw new Error("This page was opened from the file system.");
      const data = await api("/api/scenes", {}, 8000);
      app.scenes = Array.isArray(data && data.scenes) ? data.scenes.filter((s) => s && typeof s.id === "string") : [];
      app.llmAvailable = !!(data && data.llmAvailable);
      applyDefaults((data && data.defaults) || {});
      setPill($("serverPill"), "ok", app.llmAvailable ? "server online · Nemotron ready" : "server online · mock only");
      $("picker").hidden = false;
      $("options").hidden = false;
      renderFilters();
      renderGrid();
    } catch (e) {
      setPill($("serverPill"), "bad", "server offline");
      $("offline").hidden = false;
      $("offlineMsg").textContent =
        window.location.protocol === "file:"
          ? "This page was opened as a local file, so it can't talk to the API."
          : `The page loaded, but /api/scenes didn't answer (${e.message || "network error"}).`;
      clear($("grid")).appendChild(h("div", { class: "empty muted", text: "Scenes come from the server. Start it and press Retry." }));
      $("filters").textContent = "";
      updateRunButton();
    }
  }

  function applyDefaults(defaults) {
    const nv = document.querySelector('input[name="llm"][value="nvidia"]');
    nv.disabled = !app.llmAvailable;
    $("llmHint").hidden = app.llmAvailable;
    $("llmNvidiaLabel").title = app.llmAvailable ? "" : "Set NVIDIA_API_KEY in agent/.env and restart the server";
    if (defaults.agentMode === "team" || defaults.agentMode === "single") setRadio("agentMode", defaults.agentMode);
    const llm = defaults.llm === "nvidia" && app.llmAvailable ? "nvidia" : defaults.llm === "mock" || !app.llmAvailable ? "mock" : null;
    if (llm) setRadio("llm", llm);
    if (!app.llmAvailable) setRadio("llm", "mock");
  }
  function setRadio(name, value) {
    const el = document.querySelector(`input[name="${name}"][value="${value}"]`);
    if (el && !el.disabled) el.checked = true;
  }
  function getRadio(name) {
    const el = document.querySelector(`input[name="${name}"]:checked`);
    return el ? el.value : undefined;
  }
  function setPill(el, kind, text) {
    el.className = `pill pill-${kind}`;
    el.textContent = text;
  }

  function categories() {
    const counts = new Map();
    for (const s of app.scenes) {
      const c = typeof s.category === "string" && s.category ? s.category : "other";
      counts.set(c, (counts.get(c) || 0) + 1);
    }
    return [...counts.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }
  const catOf = (s) => (typeof s.category === "string" && s.category ? s.category : "other");
  const filteredScenes = () => (app.filter === "all" ? app.scenes : app.scenes.filter((s) => catOf(s) === app.filter));

  function renderFilters() {
    const box = clear($("filters"));
    const cats = categories();
    if (!cats.find(([c]) => c === app.filter)) app.filter = "all";
    const mk = (id, label, n) =>
      h(
        "button",
        {
          class: "chip",
          type: "button",
          "aria-pressed": String(app.filter === id),
          onclick: () => {
            app.filter = id;
            renderFilters();
            renderGrid();
          },
        },
        label,
        h("span", { class: "n", text: String(n) }),
      );
    box.appendChild(mk("all", "All", app.scenes.length));
    for (const [c, n] of cats) box.appendChild(mk(c, c.replace(/[_-]+/g, " "), n));
  }

  function sceneThumb(s) {
    const f = Array.isArray(s.frames) && s.frames.length ? s.frames[0] : null;
    return f ? safeImg(f.image) : null;
  }

  function renderGrid() {
    const grid = clear($("grid"));
    grid.appendChild(uploadCard());
    const list = filteredScenes();
    if (!app.scenes.length) grid.appendChild(h("div", { class: "empty muted", text: "No scenes on the server yet (agent/scenes). Upload your own photo to test." }));
    for (const s of list) grid.appendChild(sceneCard(s));
    syncSelection();
  }

  function sceneCard(s) {
    const frames = Array.isArray(s.frames) ? s.frames.length : 0;
    const src = sceneThumb(s);
    const generated = s.source && s.source.kind === "generated";
    const img = src ? h("img", { src, alt: "", loading: "lazy", decoding: "async" }) : null;
    const noimg = h("div", { class: "noimg", text: frames ? "image unavailable" : "no frames", hidden: !!src });
    if (img) img.addEventListener("error", () => { img.hidden = true; noimg.hidden = false; });
    return h(
      "button",
      {
        class: "card",
        type: "button",
        "aria-pressed": "false",
        dataset: { id: s.id },
        title: s.title || s.id,
        onclick: () => select(s.id),
        ondblclick: () => { select(s.id); startRun(); },
      },
      h(
        "div",
        { class: "card-thumb" },
        img,
        noimg,
        h(
          "div",
          { class: "card-badges" },
          h("span", { class: `tag${generated ? " tag-gen" : ""}`, text: generated ? "AI-generated" : "photo" }),
          frames > 1 ? h("span", { class: "tag tag-evolve", text: `evolving · ${frames} frames` }) : null,
        ),
        h("span", { class: "card-check", "aria-hidden": "true", text: "✓" }),
      ),
      h(
        "div",
        { class: "card-body" },
        h("div", { class: "card-title", text: s.title || s.id }),
        s.description ? h("div", { class: "card-desc", text: s.description }) : null,
        h("div", { class: "card-meta" }, h("span", { class: "tag tag-cat", text: catOf(s) })),
      ),
    );
  }

  // Upload card is built once so its inputs keep their values across filter changes.
  let uploadCardEl = null;
  function uploadCard() {
    if (uploadCardEl) return uploadCardEl;
    const fileInput = h("input", { type: "file", accept: "image/*", class: "sr-only", id: "uploadFile", "aria-label": "Choose an image" });
    const preview = h("img", { alt: "Uploaded image preview", hidden: true });
    const drop = h("div", { class: "upload-drop" }, h("b", { text: "+" }), h("span", { text: "Upload your own" }), h("span", { class: "muted", text: "click or drop an image" }));
    const thumb = h("div", { class: "card-thumb", role: "button", tabindex: "0", "aria-label": "Choose an image to upload" }, drop, preview, h("span", { class: "card-check", "aria-hidden": "true", text: "✓" }));
    const info = h("div", { class: "upload-info", text: "Resized to ≤1024 px, JPEG, under NVIDIA's inline limit." });
    const replies = h("textarea", { id: "uploadReplies", rows: "3", placeholder: "Help, I'm under here\nMy leg is stuck" });
    const conf = h("input", { type: "range", id: "uploadConf", min: "0", max: "1", step: "0.05", value: "0.5" });
    const confOut = h("output", { for: "uploadConf", text: "0.50" });
    conf.addEventListener("input", () => { confOut.textContent = Number(conf.value).toFixed(2); });

    const card = h(
      "div",
      { class: "card card-upload", dataset: { id: UPLOAD_ID }, "aria-pressed": "false" },
      thumb,
      h(
        "div",
        { class: "card-body upload-opts" },
        info,
        h("label", null, "Survivor replies, one per line", h("span", { class: "muted", text: "blank line = no answer that turn · empty = silent survivor" }), replies),
        h("label", null, "Detector confidence", h("div", { class: "range-row" }, conf, confOut)),
        fileInput,
      ),
    );

    const pick = () => fileInput.click();
    thumb.addEventListener("click", pick);
    thumb.addEventListener("keydown", (e) => {
      if (e.key === "Enter" || e.key === " ") { e.preventDefault(); pick(); }
    });
    fileInput.addEventListener("change", () => {
      if (fileInput.files && fileInput.files[0]) handleFile(fileInput.files[0]);
      fileInput.value = "";
    });
    card.addEventListener("dragover", (e) => { e.preventDefault(); card.classList.add("drag"); });
    card.addEventListener("dragleave", (e) => { if (!card.contains(e.relatedTarget)) card.classList.remove("drag"); });
    card.addEventListener("drop", (e) => {
      e.preventDefault();
      card.classList.remove("drag");
      const f = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
      if (f) handleFile(f);
    });
    // Clicking the options area also selects the upload, if one is loaded.
    card.addEventListener("click", (e) => {
      if (app.upload && !thumb.contains(e.target)) select(UPLOAD_ID);
    });

    async function handleFile(file) {
      info.className = "upload-info";
      info.textContent = `Processing ${file.name}…`;
      try {
        const out = await resizeImage(file);
        app.upload = { dataUrl: out.dataUrl, name: file.name, width: out.width, height: out.height };
        preview.src = out.dataUrl;
        preview.hidden = false;
        drop.hidden = true;
        info.textContent = `${file.name} · ${out.width}×${out.height} · ${Math.round(out.dataUrl.length / 1024)} K chars · q${out.quality.toFixed(2)}`;
        select(UPLOAD_ID);
      } catch (e) {
        info.className = "upload-info err";
        info.textContent = e.message || "Couldn't read that image.";
      }
    }

    uploadCardEl = card;
    return card;
  }

  const MAX_SIDE = 1024;
  const MAX_DATAURL = 230000;
  function loadImage(src) {
    return new Promise((resolve, reject) => {
      const img = new Image();
      img.onload = () => resolve(img);
      img.onerror = () => reject(new Error("The browser couldn't decode that image (try JPEG or PNG)."));
      img.src = src;
    });
  }
  async function resizeImage(file) {
    if (file.type && !file.type.startsWith("image/")) throw new Error("That file isn't an image.");
    if (file.size > 40 * 1024 * 1024) throw new Error("That image is over 40 MB.");
    const objUrl = URL.createObjectURL(file);
    try {
      const img = await loadImage(objUrl);
      const w0 = img.naturalWidth || img.width;
      const h0 = img.naturalHeight || img.height;
      if (!w0 || !h0) throw new Error("That image has no size.");
      let scale = Math.min(1, MAX_SIDE / Math.max(w0, h0));
      const canvas = document.createElement("canvas");
      const ctx = canvas.getContext("2d");
      for (let pass = 0; pass < 6; pass++) {
        canvas.width = Math.max(1, Math.round(w0 * scale));
        canvas.height = Math.max(1, Math.round(h0 * scale));
        ctx.fillStyle = "#000";
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
        let q = 0.8;
        let url = canvas.toDataURL("image/jpeg", q);
        while (url.length > MAX_DATAURL && q > 0.35) {
          q = Math.round((q - 0.1) * 100) / 100;
          url = canvas.toDataURL("image/jpeg", q);
        }
        if (url.length <= MAX_DATAURL) return { dataUrl: url, width: canvas.width, height: canvas.height, quality: q };
        scale *= 0.75; // still too big at low quality: shrink and try again
      }
      throw new Error("Couldn't get that image under the size limit.");
    } finally {
      URL.revokeObjectURL(objUrl);
    }
  }

  function select(id) {
    app.selected = id;
    syncSelection();
  }
  function selectedScene() {
    if (app.selected === UPLOAD_ID) return app.upload ? { id: UPLOAD_ID, title: `Upload: ${app.upload.name}` } : null;
    return app.scenes.find((s) => s.id === app.selected) || null;
  }
  function syncSelection() {
    for (const card of $("grid").querySelectorAll(".card")) card.setAttribute("aria-pressed", String(card.dataset.id === app.selected));
    const s = selectedScene();
    $("selectedLabel").textContent = s ? s.title || s.id : "No scene selected";
    $("selectedLabel").title = s ? s.title || s.id : "";
    updateRunButton();
    renderPickerSummary();
  }
  function updateRunButton() {
    $("runBtn").disabled = !selectedScene();
    $("randomBtn").disabled = !app.scenes.length;
  }

  function setPickerCollapsed(collapsed) {
    app.pickerCollapsed = collapsed;
    $("pickerBody").hidden = collapsed;
    $("collapsePicker").textContent = collapsed ? "Change scene" : "Hide";
    $("collapsePicker").setAttribute("aria-expanded", String(!collapsed));
    renderPickerSummary();
  }
  function renderPickerSummary() {
    const box = $("pickerSummary");
    box.hidden = !app.pickerCollapsed;
    if (!app.pickerCollapsed) return;
    clear(box);
    const s = selectedScene();
    if (!s) {
      box.appendChild(h("span", { class: "muted", text: "No scene selected." }));
      return;
    }
    const src = s.id === UPLOAD_ID ? app.upload && app.upload.dataUrl : sceneThumb(s);
    if (safeImg(src)) box.appendChild(h("img", { src: safeImg(src), alt: "" }));
    box.appendChild(h("b", { text: s.title || s.id }));
    if (s.category) box.appendChild(h("span", { class: "tag tag-cat", text: catOf(s) }));
  }

  // ---------------------------------------------------------------- run lifecycle

  function runOptions() {
    return { agentMode: getRadio("agentMode") || "team", llm: getRadio("llm") || "mock", approvals: getRadio("approvals") || "auto" };
  }

  function parseReplies(text) {
    if (!text || !text.trim()) return [];
    const lines = text.split(/\r?\n/).map((l) => l.trim());
    while (lines.length && !lines[lines.length - 1]) lines.pop();
    return lines;
  }

  async function startRun() {
    const s = selectedScene();
    if (!s) return;
    const opts = runOptions();
    const body = { ...opts };
    if (s.id === UPLOAD_ID) {
      body.upload = {
        dataUrl: app.upload.dataUrl,
        name: app.upload.name,
        replies: parseReplies($("uploadReplies").value),
        detectorConfidence: Number($("uploadConf").value),
      };
    } else body.sceneId = s.id;

    const btn = $("runBtn");
    btn.disabled = true;
    const prevLabel = btn.textContent;
    btn.textContent = "Starting…";
    try {
      const res = await api("/api/runs", { method: "POST", body: JSON.stringify(body) }, 30000);
      if (!res || typeof res.runId !== "string") throw new Error("The server didn't return a run id.");
      const scene = res.scene && typeof res.scene === "object" ? res.scene : { id: s.id, title: s.title, frames: [] };
      beginRun(res.runId, scene, opts, s.id);
    } catch (e) {
      showStartError(e);
    } finally {
      btn.textContent = prevLabel;
      updateRunButton();
    }
  }

  function showStartError(e) {
    $("run").hidden = false;
    const err = $("runError");
    err.hidden = false;
    clear(err).appendChild(h("span", { text: `Couldn't start the run: ${e.message || e}` }));
    if (e instanceof TypeError || /fetch|network/i.test(String(e.message))) {
      err.appendChild(h("span", { text: " — is the server still running?" }));
      setPill($("serverPill"), "bad", "server unreachable");
    }
    err.scrollIntoView({ behavior: "smooth", block: "center" });
  }

  function randomize() {
    let pool = filteredScenes();
    if (!pool.length) pool = app.scenes;
    if (!pool.length) return;
    if (pool.length > 1) pool = pool.filter((s) => s.id !== app.selected);
    const s = pool[Math.floor(Math.random() * pool.length)];
    select(s.id);
    startRun();
  }

  function beginRun(runId, scene, opts, selectedId) {
    if (app.run) stopRun(app.run);
    const run = {
      id: runId,
      scene,
      opts,
      selectedId,
      es: null,
      finished: false,
      retries: 0,
      startedAt: Date.now(),
      timer: null,
      resolvedConfirms: new Set(),
      recorded: false,
      // spoken-update bookkeeping survives resetRunPanels so replays aren't spoken twice
      spokenAlerts: new Set(),
      speechIdx: 0,
      speechSpoken: 0,
    };
    if (voice.speaker) voice.speaker.stop();
    app.run = run;
    $("run").hidden = false;
    $("runSceneTitle").textContent = scene.title || scene.id || "Uploaded image";
    $("runMeta").textContent = `${opts.agentMode === "single" ? "single agent" : "agent team"} · ${opts.llm === "nvidia" ? "Nemotron" : "offline mock"} · ${opts.approvals === "ask" ? "asks for approval" : "auto-approve"}`;
    resetRunPanels(run);
    setPickerCollapsed(true);
    run.timer = setInterval(() => {
      if (!run.finished) $("statElapsed").textContent = fmtSec(Date.now() - run.startedAt);
    }, 200);
    connect(run);
    $("run").scrollIntoView({ behavior: "smooth", block: "start" });
  }

  function stopRun(run) {
    run.finished = true;
    if (run.es) run.es.close();
    run.es = null;
    if (run.timer) clearInterval(run.timer);
  }

  /** Clear every live panel. Also used before a reconnect, since the server replays all events. */
  function resetRunPanels(run) {
    run.frames = [];
    run.viewing = null;
    run.toolCalls = 0;
    run.trace = { stack: [] };
    run.lastState = null;
    run.speechIdx = 0;
    setPill($("phasePill"), "muted", "starting");
    $("runMsg").textContent = "";
    $("statTools").textContent = "0";
    $("statElapsed").textContent = fmtSec(Date.now() - run.startedAt);
    $("runError").hidden = true;
    clear($("approvals"));
    $("resultCard").hidden = true;
    clear($("resultCard"));
    clear($("trace")).appendChild(h("div", { class: "empty muted", text: "Waiting for the agents…" }));
    clear($("conversation")).appendChild(h("div", { class: "empty muted", text: "Nothing said yet." }));
    clear($("situation")).appendChild(h("div", { class: "empty muted", text: "No state yet." }));
    renderCamera(run);
    renderSource(run.scene);
  }

  const EVENT_NAMES = ["status", "log", "frame", "speech", "confirm", "state", "result"];

  function connect(run) {
    let es;
    try {
      es = new EventSource(`/api/runs/${encodeURIComponent(run.id)}/events`);
    } catch (e) {
      fatal(run, `Couldn't open the event stream: ${e.message}`);
      return;
    }
    run.es = es;
    const handlers = { status: onStatus, log: onLog, frame: onFrame, speech: onSpeech, confirm: onConfirm, state: onState, result: onResult };
    for (const name of EVENT_NAMES) {
      es.addEventListener(name, (ev) => {
        if (app.run !== run || run.es !== es) return;
        let data;
        try {
          data = JSON.parse(ev.data);
        } catch {
          return;
        }
        if (!data || typeof data !== "object") return;
        try {
          handlers[name](data, run);
        } catch (err) {
          console.error(`bad "${name}" event`, err, data);
        }
      });
    }
    es.addEventListener("open", () => {
      if (app.run === run && run.retries) $("runMsg").textContent = "reconnected";
    });
    // "error" is both the server's named event (has data) and EventSource's own connection error (no data).
    es.addEventListener("error", (ev) => {
      if (app.run !== run || run.es !== es) return;
      if (typeof ev.data === "string" && ev.data) {
        let msg = ev.data;
        try {
          const d = JSON.parse(ev.data);
          msg = (d && (d.message || d.error)) || msg;
        } catch { /* plain text */ }
        fatal(run, String(msg));
        return;
      }
      es.close();
      if (run.finished) return;
      if (run.serverDone) {
        // The server closes the stream after its final status; that's not a failure.
        finish(run);
        return;
      }
      if (run.retries < 3) {
        run.retries += 1;
        setPill($("phasePill"), "warn", "reconnecting");
        $("runMsg").textContent = `event stream dropped · retry ${run.retries}/3`;
        setTimeout(() => {
          if (app.run !== run || run.finished) return;
          resetRunPanels(run);
          connect(run);
        }, 800 * run.retries);
      } else {
        run.es = null;
        setPill($("phasePill"), "bad", "disconnected");
        const err = clear($("runError"));
        err.hidden = false;
        err.appendChild(h("span", { text: "Lost the live event stream. The server may have stopped or restarted. " }));
        err.appendChild(
          h("button", {
            class: "btn btn-sm",
            type: "button",
            text: "Reconnect",
            onclick: () => {
              run.retries = 0;
              resetRunPanels(run);
              connect(run);
            },
          }),
        );
      }
    });
  }

  function fatal(run, message) {
    const err = clear($("runError"));
    err.hidden = false;
    err.appendChild(h("b", { text: "Run error: " }));
    err.appendChild(h("span", { text: message }));
    setPill($("phasePill"), "bad", "error");
    finish(run);
  }

  function finish(run) {
    run.finished = true;
    if (run.timer) clearInterval(run.timer);
    run.timer = null;
    if (run.es) run.es.close();
    run.es = null;
    for (const a of $("approvals").querySelectorAll(".approval:not(.resolved)")) a.remove();
    if (!run.frames.length) {
      $("camEmpty").textContent = "This run produced no camera frames.";
    }
  }

  // ---------------------------------------------------------------- event handlers

  function onStatus(d, run) {
    const phase = String(d.phase || "");
    const map = { starting: ["muted", "starting"], running: ["live", "running"], done: ["ok", "done"], error: ["bad", "error"] };
    const [kind, label] = map[phase] || ["muted", phase || "…"];
    setPill($("phasePill"), kind, label);
    if (typeof d.message === "string" && phase !== "running" && phase !== "starting") $("runMsg").textContent = d.message;
    if (phase === "error") fatal(run, d.message || "The run failed.");
    else if (phase === "done") {
      run.serverDone = true;
      // The result usually arrives first; if not, give it a moment before closing the stream.
      setTimeout(() => {
        if (app.run === run && !run.finished) finish(run);
      }, 2500);
    }
  }

  function onResult(d, run) {
    renderResult(d, run);
    if (typeof d.durationMs === "number") $("statElapsed").textContent = fmtSec(d.durationMs);
    if (typeof d.toolCalls === "number") $("statTools").textContent = String(d.toolCalls);
    setPill($("phasePill"), "ok", "done");
    recordHistory(d, run);
    finish(run);
  }

  // ---- trace

  const traceEl = () => $("trace");
  function traceDepth(d) {
    if (typeof d.depth === "number" && d.depth >= 0) return Math.min(Math.floor(d.depth), 8);
    return d.agent === "triage" || d.agent === "search" ? 1 : 0;
  }
  function traceContainer(run) {
    const st = run.trace.stack;
    return st.length ? st[st.length - 1].body : traceEl();
  }
  function level(run) {
    const st = run.trace.stack;
    return st.length ? st[st.length - 1].depth + 1 : 0;
  }
  function openGroup(run, depth, target, label) {
    const t = agentClass(target);
    const head = h("div", { class: "t-group-head" }, h("span", { class: "arrow", text: "⇢" }), h("span", { class: "pending", text: label || `delegating to ${target || "sub-agent"}…` }));
    const body = h("div", { class: "t-group-body" });
    const toggle = h("button", {
      class: "t-group-toggle",
      type: "button",
      text: "collapse",
      onclick: (e) => {
        e.stopPropagation();
        const g = toggle.closest(".t-group");
        g.classList.toggle("collapsed");
        toggle.textContent = g.classList.contains("collapsed") ? "expand" : "collapse";
      },
    });
    head.insertBefore(toggle, head.firstChild);
    const el = h("div", { class: `t-group to-${t}` }, head, body);
    traceContainer(run).appendChild(el);
    run.trace.stack.push({ depth, el, head, body, filled: false });
  }
  /** Make the current container the right one for a row at `depth`: close deeper groups, open inferred ones. */
  function placeAt(run, depth, agent) {
    const st = run.trace.stack;
    while (st.length && st[st.length - 1].depth >= depth) st.pop();
    while (level(run) < depth) openGroup(run, level(run), agent);
  }

  function traceAppend(run, node) {
    const t = traceEl();
    const empty = t.querySelector(":scope > .empty");
    if (empty) empty.remove();
    traceContainer(run).appendChild(node);
    scheduleScroll();
  }
  let scrollQueued = false;
  function scheduleScroll() {
    if (app.pauseScroll || scrollQueued) return;
    scrollQueued = true;
    requestAnimationFrame(() => {
      scrollQueued = false;
      const t = traceEl();
      t.scrollTop = t.scrollHeight;
    });
  }

  function whoLabel(agent) {
    return h("span", { class: "t-who", text: agent || "agent" });
  }

  function toolRow(d) {
    const agent = agentClass(d.agent);
    const ok = d.ok !== false;
    const name = typeof d.tool === "string" ? d.tool : "tool";
    const details = h("details", { class: `t-row t-tool agent-${agent}${ok ? "" : " fail"}` });
    const summary = h(
      "summary",
      { title: "Click for full arguments and result" },
      h("span", { class: "mark", text: ok ? "✓" : "✗" }),
      whoLabel(d.agent),
      h("span", { class: "name", text: name }),
      h("span", { class: "args", text: `(${compactArgs(d.args)})` }),
      d.result !== undefined ? h("span", { class: "res", text: compact(d.result, 160) }) : null,
    );
    details.appendChild(summary);
    if (!ok) details.appendChild(h("div", { class: "err", text: errorText(d.result) }));
    let built = false;
    details.addEventListener("toggle", () => {
      if (!details.open || built) return;
      built = true;
      details.appendChild(
        h(
          "div",
          { class: "t-detail" },
          h("div", null, h("span", { class: "lbl", text: "args" }), h("pre", { text: pretty(d.args) })),
          h("div", null, h("span", { class: "lbl", text: ok ? "result" : "error / result" }), h("pre", { text: pretty(d.result) })),
          d.event ? h("div", { class: "muted", text: `${d.event} · ${fmtClock(d.time)}` }) : null,
        ),
      );
    });
    return details;
  }

  function onLog(d, run) {
    const type = d.type;
    if (type === "event") {
      run.trace.stack = [];
      const t = traceEl();
      const empty = t.querySelector(":scope > .empty");
      if (empty) empty.remove();
      t.appendChild(
        h(
          "div",
          { class: "t-event" },
          h("span", { class: "ev", text: `▶ ${d.event || "event"}` }),
          d.description ? h("span", { class: "desc", text: d.description }) : null,
          h("span", { class: "time", text: fmtClock(d.time) }),
        ),
      );
      scheduleScroll();
      return;
    }
    const depth = traceDepth(d);
    const agent = agentClass(d.agent);

    if (type === "thought") {
      placeAt(run, depth, d.agent);
      traceAppend(run, h("div", { class: `t-row t-thought agent-${agent}` }, whoLabel(d.agent), String(d.text ?? "")));
      return;
    }
    if (type === "done") {
      placeAt(run, depth, d.agent);
      traceAppend(
        run,
        h(
          "div",
          { class: `t-row t-done agent-${agent}` },
          h("span", { class: "sq", text: "■ " }),
          whoLabel(d.agent),
          String(d.summary ?? ""),
          typeof d.steps === "number" ? h("span", { class: "steps", text: `  (${d.steps} steps)` }) : null,
        ),
      );
      return;
    }
    if ((type === "start" || type === "tool_start") && typeof d.tool === "string" && d.tool.startsWith("delegate_")) {
      placeAt(run, depth, d.agent);
      const target = d.tool.slice("delegate_".length);
      openGroup(run, depth, target, `${d.tool} → ${target} agent working…`);
      return;
    }
    if (type === "tool") {
      run.toolCalls += 1;
      $("statTools").textContent = String(run.toolCalls);
      const row = toolRow(d);
      const isDelegate = typeof d.tool === "string" && d.tool.startsWith("delegate_");
      if (isDelegate) {
        const st = run.trace.stack;
        while (st.length && st[st.length - 1].depth > depth) st.pop();
        const top = st[st.length - 1];
        if (top && top.depth === depth && !top.filled) {
          // The delegate_* row is logged after its sub-agent finishes: it becomes the group's header.
          const pending = top.head.querySelector(".pending");
          if (pending) pending.remove();
          row.classList.remove("t-row");
          top.head.appendChild(row);
          top.filled = true;
          st.pop();
          if (d.ok === false) top.el.classList.add("fail");
          scheduleScroll();
          return;
        }
      }
      placeAt(run, depth, d.agent);
      traceAppend(run, row);
    }
    // Unknown log types are ignored on purpose.
  }

  function traceNote(run, text, cls) {
    traceAppend(run, h("div", { class: `t-note${cls ? ` ${cls}` : ""}`, text }));
  }

  // ---- camera

  function frameNumber(f) {
    // Server indices are 0-based; show them 1-based.
    return (typeof f.index === "number" ? f.index : 0) + 1;
  }

  function onFrame(d, run) {
    const f = {
      index: typeof d.index === "number" ? d.index : run.frames.length,
      total: typeof d.total === "number" ? d.total : null,
      image: safeImg(d.image),
      trigger: typeof d.trigger === "string" ? d.trigger : "",
      reason: typeof d.reason === "string" ? d.reason : "",
    };
    const changed = run.frames.length > 0;
    const existing = run.frames.findIndex((x) => x.index === f.index);
    if (existing >= 0) run.frames[existing] = f;
    else run.frames.push(f);
    run.viewing = f.index;
    renderCamera(run, changed ? f : null);
    const total = f.total ? `/${f.total}` : "";
    if (changed) traceNote(run, `📷 Camera view changed → frame ${frameNumber(f)}${total}${f.trigger ? ` (${f.trigger})` : ""}${f.reason ? `: ${f.reason}` : ""}`);
    else traceNote(run, `📷 Camera on · frame ${frameNumber(f)}${total}${f.reason ? ` · ${f.reason}` : ""}`, "say");
  }

  function renderCamera(run, changedFrame) {
    const img = $("camImg");
    const empty = $("camEmpty");
    const change = $("camChange");
    const frames = run.frames || [];
    const cur = frames.find((f) => f.index === run.viewing) || frames[frames.length - 1];
    if (!cur) {
      img.hidden = true;
      img.removeAttribute("src");
      empty.hidden = false;
      const n = Array.isArray(run.scene.frames) ? run.scene.frames.length : 0;
      empty.textContent = run.finished ? "This run produced no camera frames." : n === 0 && run.scene.frames ? "This scene has no frames — the agents will run blind." : "Waiting for the first camera frame…";
      change.hidden = true;
      $("frameCount").textContent = "no frames yet";
      clear($("strip"));
      return;
    }
    if (cur.image) {
      img.src = cur.image;
      img.hidden = false;
      empty.hidden = true;
      img.alt = `Robot camera, frame ${frameNumber(cur)}`;
    } else {
      img.hidden = true;
      empty.hidden = false;
      empty.textContent = "This frame has no viewable image.";
    }
    const total = cur.total || (Array.isArray(run.scene.frames) ? run.scene.frames.length : 0);
    $("frameCount").textContent = `frame ${frameNumber(cur)}${total ? ` of ${total}` : ""} · ${frames.length} seen`;
    if (changedFrame) {
      clear(change);
      change.appendChild(h("b", { text: `View changed${changedFrame.trigger ? ` · ${changedFrame.trigger}` : ""}` }));
      if (changedFrame.reason) change.appendChild(h("div", { text: changedFrame.reason }));
      change.hidden = false;
      // Re-trigger the slide-in animation.
      change.style.animation = "none";
      void change.offsetWidth;
      change.style.animation = "";
    } else if (cur.index !== frames[frames.length - 1].index) {
      change.hidden = true;
    }
    const strip = clear($("strip"));
    for (const f of frames) {
      const b = h(
        "button",
        {
          type: "button",
          "aria-current": String(f.index === cur.index),
          title: `Frame ${frameNumber(f)}${f.trigger ? ` · ${f.trigger}` : ""}${f.reason ? ` · ${f.reason}` : ""}`,
          onclick: () => {
            run.viewing = f.index;
            renderCamera(run, null);
          },
        },
        f.image ? h("img", { src: f.image, alt: "" }) : null,
        f.trigger ? h("span", { class: "trig", text: f.trigger }) : null,
        h("span", { class: "n", text: String(frameNumber(f)) }),
      );
      strip.appendChild(b);
    }
  }

  function renderSource(scene) {
    const box = clear($("camSource"));
    const src = scene && scene.source;
    if (!src || typeof src !== "object") {
      if (scene && scene.id && String(scene.id).startsWith("upload")) box.appendChild(h("span", { text: "Your uploaded image" }));
      return;
    }
    if (src.kind === "generated") {
      box.appendChild(h("span", { class: "tag tag-gen", text: "AI-generated" }));
      if (src.model) box.appendChild(h("span", { text: ` · ${src.model}` }));
      if (src.license) box.appendChild(h("span", { text: ` · ${src.license}` }));
      return;
    }
    box.appendChild(h("span", { class: "tag", text: "photo" }));
    if (src.author) box.appendChild(h("span", { text: ` · ${src.author}` }));
    if (src.license) box.appendChild(h("span", { text: ` · ${src.license}` }));
    const link = safeHttp(src.url);
    if (link) {
      box.appendChild(h("span", { text: " · " }));
      box.appendChild(h("a", { href: link, target: "_blank", rel: "noopener noreferrer", text: "source" }));
    }
  }

  // ---- conversation

  function onSpeech(d, run) {
    voiceRobot(d, run);
    const box = $("conversation");
    const empty = box.querySelector(":scope > .empty");
    if (empty) empty.remove();
    const who = d.who === "survivor" ? "survivor" : "robot";
    const silent = d.text === null || d.text === undefined || (typeof d.text === "string" && !d.text.trim());
    box.appendChild(
      h(
        "div",
        { class: `bubble bubble-${who}${silent ? " bubble-silent" : ""}` },
        h("span", { class: "who", text: who === "robot" ? "🤖 robot" : "survivor" }),
        silent ? "… no response" : String(d.text),
      ),
    );
    box.scrollTop = box.scrollHeight;
    traceNote(run, `${who === "robot" ? "🔊 robot" : "👂 survivor"}: ${silent ? "… no response" : String(d.text)}`, "say");
  }

  // ---- approvals

  function onConfirm(d, run) {
    if (typeof d.id !== "number" && typeof d.id !== "string") return;
    if (run.resolvedConfirms.has(d.id) || run.finished) return;
    const box = $("approvals");
    if (box.querySelector(`[data-cid="${CSS.escape(String(d.id))}"]`)) return;
    const status = h("span", { class: "muted" });
    const approve = h("button", { class: "btn btn-ok btn-lg", type: "button", text: "✓ Approve" });
    const deny = h("button", { class: "btn btn-bad btn-lg", type: "button", text: "✗ Deny" });
    const card = h(
      "div",
      { class: "approval", role: "alertdialog", "aria-label": "Approval needed", dataset: { cid: String(d.id) } },
      h(
        "div",
        { class: "approval-text" },
        h("b", { text: "Rescuer approval needed" }),
        h("div", { class: "act", text: d.action || "action" }),
        d.summary ? h("div", { class: "sum", text: d.summary }) : null,
        status,
      ),
      h("div", { class: "approval-btns" }, approve, deny),
    );
    async function answer(approved) {
      approve.disabled = deny.disabled = true;
      status.textContent = " sending…";
      try {
        await api(`/api/runs/${encodeURIComponent(run.id)}/confirm`, { method: "POST", body: JSON.stringify({ id: d.id, approved }) }, 10000);
        run.resolvedConfirms.add(d.id);
        card.classList.add("resolved");
        status.textContent = approved ? " approved" : " denied";
        traceNote(run, `${approved ? "✓ You approved" : "✗ You denied"} ${d.action || "the action"}`);
        setTimeout(() => card.remove(), 1500);
      } catch (e) {
        status.textContent = ` failed: ${e.message}`;
        approve.disabled = deny.disabled = false;
      }
    }
    approve.addEventListener("click", () => answer(true));
    deny.addEventListener("click", () => answer(false));
    box.appendChild(card);
    traceNote(run, `⏸ Waiting for your approval: ${d.action || "action"}${d.summary ? ` — ${d.summary}` : ""}`);
    card.scrollIntoView({ behavior: "smooth", block: "center" });
    approve.focus({ preventScroll: true });
  }

  // ---- situation

  function onState(d, run) {
    run.lastState = d;
    voiceAlerts(d, run);
    const box = clear($("situation"));
    const sec = (title, content) => h("div", { class: "sit-sec" }, h("h4", { text: title }), content);
    const arr = (x) => (Array.isArray(x) ? x : []);

    const robot = d.robot && typeof d.robot === "object" ? d.robot : null;
    if (robot) {
      const bat = typeof robot.battery === "number" ? Math.max(0, Math.min(1, robot.battery > 1 ? robot.battery / 100 : robot.battery)) : null;
      const bar = h("span", { class: `battery-bar${bat !== null && bat < 0.2 ? " low" : bat !== null && bat < 0.4 ? " mid" : ""}` }, h("span", { style: `width:${bat === null ? 0 : Math.round(bat * 100)}%` }));
      const num = (v, dgt = 1) => (typeof v === "number" ? v.toFixed(dgt) : "?");
      box.appendChild(
        sec(
          "Robot",
          h(
            "div",
            { class: "robot-row" },
            h("span", { class: "battery" }, bar, h("b", { class: "mono", text: bat === null ? "—" : `${Math.round(bat * 100)}%` })),
            h("span", { class: "tag", text: robot.mode || "?" }),
            h("span", { class: "muted mono", text: `(${num(robot.x)}, ${num(robot.y)}) m · ${num(robot.heading, 0)}°` }),
          ),
        ),
      );
    }

    const cases = arr(d.cases);
    box.appendChild(
      sec(
        `Cases (${cases.length})`,
        cases.length
          ? h(
              "ul",
              { class: "sit-list" },
              cases.map((c) =>
                h(
                  "li",
                  null,
                  triChip(c.triage),
                  h(
                    "div",
                    null,
                    h("b", { text: `#${c.id ?? "?"}` }),
                    c.status ? h("span", { class: "muted", text: ` · ${c.status}` }) : null,
                    typeof c.x === "number" && typeof c.y === "number" ? h("span", { class: "muted mono", text: ` · (${c.x.toFixed(1)}, ${c.y.toFixed(1)})` }) : null,
                    c.description ? h("div", { class: "sub", text: c.description }) : null,
                  ),
                ),
              ),
            )
          : h("div", { class: "muted", text: "None found yet." }),
      ),
    );

    const alerts = arr(d.alerts);
    if (alerts.length)
      box.appendChild(
        sec(
          `Headset alerts (${alerts.length})`,
          h(
            "ul",
            { class: "sit-list" },
            alerts
              .slice(-8)
              .reverse()
              .map((a) => {
                const lvl = ["info", "warning", "critical"].includes(a.level) ? a.level : "info";
                return h("li", null, h("span", { class: `lvl lvl-${lvl}`, text: lvl }), h("div", { text: a.text ?? "" }));
              }),
          ),
        ),
      );

    const sms = arr(d.sms);
    if (sms.length)
      box.appendChild(
        sec(
          `SMS sent (${sms.length})`,
          h(
            "ul",
            { class: "sit-list" },
            sms.map((m) =>
              h("li", null, h("span", { text: "✉" }), h("div", null, h("div", { class: "sub mono", text: `to ${arr(m.to).join(", ") || "?"}` }), h("div", { text: m.body ?? "" }))),
            ),
          ),
        ),
      );

    const hazards = arr(d.hazards);
    if (hazards.length)
      box.appendChild(
        sec(
          `Hazards (${hazards.length})`,
          h(
            "ul",
            { class: "sit-list" },
            hazards.map((z) => h("li", null, h("span", { class: "lvl lvl-warning", text: z.kind || "hazard" }), h("div", { text: z.note ?? "" }))),
          ),
        ),
      );
  }

  // ---- result

  const TRIAGE_ORDER = ["red", "yellow", "green", "black"];
  function finalTriage(d) {
    if (d.score && d.score.triage && d.score.triage.actual !== undefined && d.score.triage.actual !== null) return d.score.triage.actual;
    const cases = Array.isArray(d.cases) ? d.cases : [];
    for (const t of TRIAGE_ORDER) if (cases.some((c) => c && c.triage === t)) return t;
    return "unknown";
  }

  function renderResult(d, run) {
    const card = clear($("resultCard"));
    const score = d.score && typeof d.score === "object" ? d.score : null;
    const passed = score && typeof score.passed === "number" ? score.passed : 0;
    const total = score && typeof score.total === "number" ? score.total : 0;
    const verdict = score ? verdictClass(passed, total) : "noscore";
    card.className = `panel result ${verdict}`;
    card.hidden = false;

    const actions = Array.isArray(d.actions) ? d.actions : [];
    const cases = Array.isArray(d.cases) ? d.cases : [];
    const main = h(
      "div",
      { class: "result-main" },
      h(
        "div",
        { class: "result-heading" },
        h("h2", { text: "Decision" }),
        triChip(finalTriage(d), true),
        score ? h("span", { class: `pill pill-${verdict === "pass" ? "ok" : verdict === "partial" ? "warn" : "bad"}`, text: `${verdict === "pass" ? "PASS" : verdict === "partial" ? "PARTIAL" : "FAIL"} · ${passed}/${total}` }) : h("span", { class: "pill pill-muted", text: "not scored" }),
      ),
      h("p", { class: "result-summary", text: d.summary || "(no summary)" }),
      actions.length ? h("div", { class: "result-actions" }, actions.map((a) => h("span", { class: "tag tag-action", text: String(a) }))) : h("div", { class: "muted", text: "No actions taken." }),
      h(
        "div",
        { class: "result-meta" },
        h("span", { text: `${typeof d.toolCalls === "number" ? d.toolCalls : run.toolCalls} tool calls` }),
        h("span", { text: fmtSec(d.durationMs) }),
        h("span", { text: `${cases.length} case${cases.length === 1 ? "" : "s"}` }),
        h("span", { text: `${run.frames.length} frame${run.frames.length === 1 ? "" : "s"} seen` }),
      ),
    );

    const top = h("div", { class: "result-top" }, main);
    if (score) top.appendChild(scoreBox(score, passed, total, verdict));
    card.appendChild(top);

    const truth = truthBox(run.scene);
    const truthBtn = h("button", { class: "btn", type: "button", "aria-pressed": "false", text: "Show ground truth" });
    truthBtn.addEventListener("click", () => {
      truth.hidden = !truth.hidden;
      truthBtn.setAttribute("aria-pressed", String(!truth.hidden));
      truthBtn.textContent = truth.hidden ? "Show ground truth" : "Hide ground truth";
    });
    card.appendChild(
      h(
        "div",
        { class: "result-btns" },
        truthBtn,
        h("button", { class: "btn btn-primary", type: "button", text: "↻ Run again", onclick: () => rerun(run) }),
        app.scenes.length ? h("button", { class: "btn", type: "button", text: "⚄ Next random", onclick: randomize }) : null,
      ),
    );
    card.appendChild(truth);
    card.scrollIntoView({ behavior: "smooth", block: "start" });
  }

  function scoreBox(score, passed, total, verdict) {
    const list = h("ul", { class: "checklist" });
    const item = (pass, what, val) =>
      h("li", null, h("span", { class: `ck ${pass ? "y" : "n"}`, text: pass ? "✓" : "✗" }), h("span", { class: "what" }, what), val ? h("span", { class: "val" }, val) : null);
    if (score.triage)
      list.appendChild(item(!!score.triage.pass, "Triage", [h("span", { text: "expected" }), renderVal(score.triage.expected), h("span", { text: "got" }), renderVal(score.triage.actual)]));
    if (score.personPresent)
      list.appendChild(
        item(!!score.personPresent.pass, "Person present", [h("span", { text: "expected" }), renderVal(score.personPresent.expected), h("span", { text: "got" }), renderVal(score.personPresent.actual)]),
      );
    const must = Array.isArray(score.mustDo) ? score.mustDo : [];
    if (must.length) {
      list.appendChild(h("li", { class: "grp", text: "Must do" }));
      for (const m of must) list.appendChild(item(!!m.pass, h("code", { text: m.tool || "?" }), h("span", { text: m.pass ? "called" : "not called" })));
    }
    const mustNot = Array.isArray(score.mustNotDo) ? score.mustNotDo : [];
    if (mustNot.length) {
      list.appendChild(h("li", { class: "grp", text: "Must not do" }));
      for (const m of mustNot) list.appendChild(item(!!m.pass, h("code", { text: m.tool || "?" }), h("span", { text: m.pass ? "avoided" : "called" })));
    }
    const pct = total ? Math.round((passed / total) * 100) : 0;
    return h(
      "div",
      { class: "result-score" },
      h("div", { class: "score-total" }, h("span", { class: "muted", text: "Score" }), h("span", { class: `big ${verdict}`, text: `${passed}/${total}` })),
      h("div", { class: "score-bar" }, h("span", { style: `width:${pct}%;background:var(--${verdict === "pass" ? "ok" : verdict === "partial" ? "warn" : "bad"})` })),
      list,
    );
  }

  function truthBox(scene) {
    const box = h("div", { class: "truth", hidden: true });
    const frames = Array.isArray(scene.frames) ? scene.frames : [];
    box.appendChild(h("h4", { text: "Ground truth" }));
    if (frames.length) {
      box.appendChild(
        h(
          "ol",
          null,
          frames.map((f) => h("li", null, f.trigger ? h("span", { class: "tag", text: f.trigger }) : null, " ", f.caption || h("span", { class: "muted", text: "(no caption)" }))),
        ),
      );
    } else box.appendChild(h("div", { class: "muted", text: "No frame captions for this scene." }));
    const ex = scene.expected && typeof scene.expected === "object" ? scene.expected : null;
    if (ex) {
      box.appendChild(h("div", { class: "kv" }, h("b", { text: "Person present:" }), renderVal(ex.personPresent)));
      box.appendChild(h("div", { class: "kv" }, h("b", { text: "Expected triage:" }), renderVal(ex.triage)));
      if (Array.isArray(ex.mustDo) && ex.mustDo.length) box.appendChild(h("div", { class: "kv" }, h("b", { text: "Must do:" }), ex.mustDo.map((t) => h("code", { text: t }))));
      if (Array.isArray(ex.mustNotDo) && ex.mustNotDo.length) box.appendChild(h("div", { class: "kv" }, h("b", { text: "Must not do:" }), ex.mustNotDo.map((t) => h("code", { text: t }))));
      if (ex.notes) box.appendChild(h("p", { text: ex.notes }));
    } else box.appendChild(h("div", { class: "muted", text: "No expected outcome (uploaded image) — nothing to score against." }));
    if (typeof scene.detectorConfidence === "number") box.appendChild(h("div", { class: "muted", text: `Detector confidence: ${scene.detectorConfidence}` }));
    return box;
  }

  function rerun(run) {
    if (run.selectedId) {
      if (run.selectedId === UPLOAD_ID && !app.upload) return;
      app.selected = run.selectedId;
      syncSelection();
    }
    startRun();
  }

  // ---------------------------------------------------------------- history

  function recordHistory(d, run) {
    if (run.recorded) return;
    run.recorded = true;
    const list = store.get(HISTORY_KEY, []);
    const hist = Array.isArray(list) ? list.filter((x) => x && x.runId !== run.id) : [];
    hist.unshift({
      runId: run.id,
      at: Date.now(),
      title: String(run.scene.title || run.scene.id || "upload").slice(0, 120),
      mode: run.opts.agentMode,
      llm: run.opts.llm,
      passed: d.score && typeof d.score.passed === "number" ? d.score.passed : null,
      total: d.score && typeof d.score.total === "number" ? d.score.total : null,
      triage: String(finalTriage(d)),
      durationMs: typeof d.durationMs === "number" ? d.durationMs : Date.now() - run.startedAt,
      toolCalls: typeof d.toolCalls === "number" ? d.toolCalls : run.toolCalls,
    });
    store.set(HISTORY_KEY, hist.slice(0, 50));
    renderHistory();
  }

  function renderHistory() {
    const body = clear($("historyBody"));
    const list = store.get(HISTORY_KEY, []);
    const hist = Array.isArray(list) ? list.filter((x) => x && typeof x === "object") : [];
    $("clearHistory").hidden = !hist.length;
    if (!hist.length) {
      body.appendChild(h("div", { class: "empty muted", text: "No runs yet." }));
      return;
    }
    const totals = hist.filter((r) => typeof r.total === "number" && r.total > 0);
    const sumP = totals.reduce((a, r) => a + r.passed, 0);
    const sumT = totals.reduce((a, r) => a + r.total, 0);
    const rows = hist.map((r) => {
      const scored = typeof r.total === "number" && r.total > 0;
      const v = scored ? verdictClass(r.passed, r.total) : "noscore";
      return h(
        "tr",
        null,
        h("td", { class: "num muted", text: fmtClock(r.at) }),
        h("td", { class: "scene", text: r.title }),
        h("td", { text: `${r.mode === "single" ? "single" : "team"} · ${r.llm === "nvidia" ? "Nemotron" : "mock"}` }),
        h("td", null, triChip(r.triage)),
        h("td", { class: `num ${scored ? `score-${v}` : "muted"}`, text: scored ? `${r.passed}/${r.total}` : "—" }),
        h("td", { class: "num", text: String(r.toolCalls ?? "—") }),
        h("td", { class: "num", text: fmtSec(r.durationMs) }),
      );
    });
    body.appendChild(
      h(
        "div",
        { class: "table-wrap" },
        h(
          "table",
          { class: "hist" },
          h("thead", null, h("tr", null, ["Time", "Scene", "Mode", "Triage", "Score", "Tools", "Duration"].map((t) => h("th", { text: t })))),
          h("tbody", null, rows),
        ),
      ),
    );
    if (sumT) body.appendChild(h("div", { class: "muted", style: "margin-top:8px;font-size:12px", text: `${totals.length} scored run${totals.length === 1 ? "" : "s"} · ${sumP}/${sumT} checks passed (${Math.round((sumP / sumT) * 100)}%)` }));
  }

  // ---------------------------------------------------------------- wiring

  function init() {
    $("retryBtn").addEventListener("click", loadScenes);
    $("runBtn").addEventListener("click", startRun);
    $("randomBtn").addEventListener("click", randomize);
    $("collapsePicker").addEventListener("click", () => setPickerCollapsed(!app.pickerCollapsed));
    $("scrollToggle").addEventListener("click", () => {
      app.pauseScroll = !app.pauseScroll;
      $("scrollToggle").setAttribute("aria-pressed", String(app.pauseScroll));
      $("scrollToggle").textContent = app.pauseScroll ? "Resume scroll" : "Pause scroll";
      if (!app.pauseScroll) scheduleScroll();
    });
    $("clearHistory").addEventListener("click", () => {
      store.set(HISTORY_KEY, []);
      renderHistory();
    });
    voiceInit();
    renderHistory();
    loadScenes();
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
