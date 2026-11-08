import * as THREE from './vendor/three.module.js';
import { OrbitControls } from './vendor/OrbitControls.js';
import { FIXED, LIMITS, check } from './door.js';

// ------------------------------------------------------------------ presets (tested doors)
const PRESETS = {
  'iphone-15-pro': { name: 'iPhone 15 Pro', length: 146.6, width: 70.6, thickness: 8.25, cornerRadius: 10.2,
    camera: { top: 2.05, left: 2.05, width: 42.5, height: 43.5, radius: 6.5 }, cameraClearance: 1.5 },
  'pixel-9-pro': { name: 'Pixel 9 Pro', length: 152.8, width: 72.0, thickness: 8.5, cornerRadius: 10.0,
    camera: { top: 14, left: 3.5, width: 65, height: 21.5, radius: 10.75 }, cameraClearance: 1.5 },
};

const FIELDS = ['name', 'length', 'width', 'thickness', 'cornerRadius', 'camera.top', 'camera.left', 'camera.width', 'camera.height', 'camera.radius', 'cameraClearance'];
const $ = (s) => document.querySelector(s);
const field = (k) => document.getElementById('f-' + k.replace('.', '-'));

function readForm() {
  const s = { camera: {} };
  for (const k of FIELDS) {
    const el = field(k); const raw = el.value.trim();
    const v = k === 'name' ? raw : (raw === '' ? NaN : Number(raw));
    if (k.startsWith('camera.')) s.camera[k.slice(7)] = v; else s[k] = v;
  }
  if (!isFinite(s.camera.radius)) s.camera.radius = 0;
  if (!isFinite(s.cameraClearance)) s.cameraClearance = 1.5;
  return s;
}
function writeForm(s) {
  for (const k of FIELDS) {
    const v = k.startsWith('camera.') ? s.camera?.[k.slice(7)] : s[k];
    field(k).value = v ?? (k === 'cameraClearance' ? 1.5 : '');
  }
}

// ------------------------------------------------------------------ AI agent prompt + JSON import
const PROMPT = `Read the attached dimensional drawing of a phone (for iPhones: the device's drawing from Apple's accessory dimensional drawings) and reply with ONLY this JSON, every value in millimetres:

{
  "name": "<model name>",
  "length": <overall height of the phone body>,
  "width": <overall width of the phone body>,
  "thickness": <body thickness, NOT including the camera bump>,
  "cornerRadius": <radius of the phone's corners seen from the back>,
  "camera": {
    "top": <distance from the top edge of the phone to the top edge of the raised camera area>,
    "left": <distance from the left edge of the phone to the left edge of the raised camera area>,
    "width": <width of the raised camera area>,
    "height": <height of the raised camera area>,
    "radius": <corner radius of the raised camera area>
  },
  "notes": "<anything you estimated or could not read>"
}

Rules:
- Measure on the BACK view of the phone, held upright with the camera at the top. "left" means the left side of that back view.
- "camera" is the whole raised camera plateau or bar (the part that sticks out), not the individual lenses.
- If a value is not dimensioned on the drawing, estimate it from the drawing's scale and say so in "notes". Never leave a value out.`;

function importJSON(text) {
  const m = text.match(/\{[\s\S]*\}/);
  if (!m) throw new Error('No JSON object found.');
  const j = JSON.parse(m[0]);
  const num = (v) => (v === undefined || v === null || v === '' ? undefined : Number(v));
  const s = { name: j.name ?? '', length: num(j.length), width: num(j.width), thickness: num(j.thickness), cornerRadius: num(j.cornerRadius ?? j.corner_radius),
    camera: { top: num(j.camera?.top), left: num(j.camera?.left), width: num(j.camera?.width), height: num(j.camera?.height), radius: num(j.camera?.radius) ?? 0 },
    cameraClearance: num(j.cameraClearance) ?? 1.5 };
  return { spec: s, notes: j.notes };
}

// ------------------------------------------------------------------ 3D preview
const viewEl = $('#view');
const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
viewEl.appendChild(renderer.domElement);
const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(32, 1, 1, 2000);
camera.up.set(0, 0, 1);
const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
scene.add(new THREE.HemisphereLight(0xffffff, 0x8890a0, 1.6));
const key = new THREE.DirectionalLight(0xffffff, 1.6); key.position.set(-120, -160, 260); scene.add(key);
const fill = new THREE.DirectionalLight(0xffffff, 0.6); fill.position.set(150, 120, -200); scene.add(fill);
const model = new THREE.Group(); scene.add(model);   // hidden while the values are invalid
const group = new THREE.Group(); model.add(group);
const phoneGroup = new THREE.Group(); model.add(phoneGroup);
const MAT = {
  door: new THREE.MeshStandardMaterial({ color: 0x9aa3ad, roughness: 0.7, metalness: 0.0 }),
  tpu: new THREE.MeshStandardMaterial({ color: 0xe8803a, roughness: 0.8 }),
  phone: new THREE.MeshStandardMaterial({ color: 0x3b6fd8, transparent: true, opacity: 0.22, roughness: 0.5, depthWrite: false }),
  cam: new THREE.MeshStandardMaterial({ color: 0x1d3f8f, transparent: true, opacity: 0.55, depthWrite: false }),
};
function geom({ pos, idx }) {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  g.setIndex(new THREE.BufferAttribute(idx, 1));
  const ng = g.toNonIndexed(); ng.computeVertexNormals(); return ng;
}
function setView(which) {
  const d = 330;
  if (which === 'outer') camera.position.set(-d * 0.35, -d * 0.55, -d * 0.8);
  else camera.position.set(-d * 0.35, -d * 0.55, d * 0.8);
  controls.target.set(0, 0, 2); controls.update();
}
function resize() {
  const w = viewEl.clientWidth, h = viewEl.clientHeight;
  renderer.setSize(w, h, false); camera.aspect = w / h; camera.updateProjectionMatrix();
}
new ResizeObserver(resize).observe(viewEl);
setView('inner');
(function loop() { controls.update(); renderer.render(scene, camera); requestAnimationFrame(loop); })();

function roundedShape(x0, x1, y0, y1, r) {
  const s = new THREE.Shape(); r = Math.max(0.01, Math.min(r, (x1 - x0) / 2, (y1 - y0) / 2));
  s.moveTo(x0 + r, y0); s.lineTo(x1 - r, y0); s.absarc(x1 - r, y0 + r, r, -Math.PI / 2, 0);
  s.lineTo(x1, y1 - r); s.absarc(x1 - r, y1 - r, r, 0, Math.PI / 2);
  s.lineTo(x0 + r, y1); s.absarc(x0 + r, y1 - r, r, Math.PI / 2, Math.PI);
  s.lineTo(x0, y0 + r); s.absarc(x0 + r, y0 + r, r, Math.PI, 1.5 * Math.PI);
  return s;
}
function showPhone(spec, derived) {
  phoneGroup.clear();
  if (!derived) return;
  const zb = derived.padTop - FIXED.z0;                              // phone back, print coordinates
  const { W, L, R } = derived;
  const body = new THREE.Mesh(new THREE.ExtrudeGeometry(roundedShape(-W, W, -L, L, R), { depth: spec.thickness, bevelEnabled: false, curveSegments: 10 }), MAT.phone);
  body.position.z = zb; phoneGroup.add(body);
  const c = spec.camera; const x1 = W - c.left, x0 = x1 - c.width, y1 = L - c.top, y0 = y1 - c.height;
  const cam = new THREE.Mesh(new THREE.ExtrudeGeometry(roundedShape(x0, x1, y0, y1, c.radius || 0), { depth: 2.5, bevelEnabled: false, curveSegments: 10 }), MAT.cam);
  cam.position.z = zb - 2.5; phoneGroup.add(cam);
  phoneGroup.visible = $('#show-phone').checked;
}

// ------------------------------------------------------------------ generation
const worker = new Worker(new URL('./worker.js', import.meta.url), { type: 'module' });
let seq = 0, lastFiles = null, lastSpec = null;
const statusEl = $('#status'), msgEl = $('#messages');
function setStatus(t, cls = '') { statusEl.textContent = t; statusEl.className = 'status ' + cls; }
function showMessages(errors, warnings, extra = []) {
  msgEl.innerHTML = '';
  for (const [cls, list] of [['err', errors], ['warn', warnings], ['note', extra]])
    for (const m of list) { const li = document.createElement('li'); li.className = cls; li.textContent = m; msgEl.appendChild(li); }
}
function enableDownloads(on) { for (const b of document.querySelectorAll('.dl')) b.disabled = !on; }

let notes = [];
// The door is only rebuilt when the user clicks Generate models. Until then, after any change to the numbers,
// the preview shows the last door and its downloads are off, so nobody downloads a door that doesn't match the form.
let changed = false;
const showChanged = () => setStatus(model.visible ? 'The numbers have changed. Click Generate models to update the preview and downloads.'
  : 'Click Generate models to build the door.', 'warn');
function markChanged() { changed = true; enableDownloads(false); showChanged(); }
// Editing a field, or filling the form from an agent's reply, selects a greyed-out "Your own numbers" entry that is
// only in the menu while it's selected. So every real entry, "Custom phone" included, still fires a change when picked.
const presetEl = $('#preset');
function showEdited() {
  if (!presetEl.querySelector('option[value=edited]')) { const o = new Option('Your own numbers', 'edited'); o.disabled = true; presetEl.add(o); }
  presetEl.value = 'edited';
}
// "Custom phone" starts again from empty fields with no door.
function clearAll() {
  ++seq; changed = false; notes = []; lastFiles = null;            // ++seq drops a door that is still being built
  writeForm({ camera: {} });                                       // clearance goes back to its 1.5 mm default
  model.visible = false; group.clear(); phoneGroup.clear();
  enableDownloads(false); showMessages([], [], []);
  setStatus('Enter your phone\'s numbers, then click Generate models.');
  $('#json-status').textContent = '';
}
function generate() {
  changed = false;
  const spec = readForm(); lastSpec = spec;
  const chk = check(spec);
  showMessages(chk.errors, chk.warnings, notes);
  if (chk.errors.length) { ++seq; model.visible = false; setStatus('Fix the values above to generate a door.', 'bad'); enableDownloads(false); return; }
  setStatus('Generating…', 'busy'); enableDownloads(false);
  worker.postMessage({ id: ++seq, spec });
}
worker.onmessage = (e) => {
  const r = e.data; if (r.id !== seq) return;
  if (r.error) { model.visible = false; setStatus('Something went wrong while building the door.', 'bad'); showMessages([r.error], [], []); return; }
  if (!r.door) { model.visible = false; showMessages(r.check.errors, r.check.warnings, notes); setStatus('Fix the values above to generate a door.', 'bad'); return; }
  group.clear();
  group.add(new THREE.Mesh(geom(r.door.arrays), MAT.door));
  for (const l of r.locs) group.add(new THREE.Mesh(geom(l.arrays), MAT.tpu));
  showPhone(lastSpec, r.stats.derived);
  model.visible = true;
  // A door or locator in more than one piece can't be printed as it is, so it stays in the preview to look at
  // but can't be downloaded.
  if (r.stats.pieces !== 1 || r.stats.locatorPieces.some((n) => n !== 1)) {
    showMessages(['Part of the door or a corner locator came out in more than one piece, so this door can\'t be printed as it is. Check the camera numbers against the preview.'], r.check.warnings, notes);
    setStatus('This door would print in pieces.', 'bad');
    return;
  }
  lastFiles = r.files;
  showMessages(r.check.errors, r.check.warnings, notes);
  if (changed) { showChanged(); return; }             // the numbers changed while this door was being built
  setStatus(`Door ready: ${r.stats.holes} vent holes, built in ${(r.stats.ms / 1000).toFixed(1)} s.`, 'ok');
  enableDownloads(true);
};
function download(f) {
  const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob([f.data])); a.download = f.name; a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 2000);
}

// ------------------------------------------------------------------ wiring
$('#form').addEventListener('submit', (e) => { e.preventDefault(); generate(); });   // the button, or Enter in a field
document.querySelectorAll('#form input').forEach((el) => el.addEventListener('input', () => { showEdited(); markChanged(); }));
presetEl.addEventListener('change', (e) => {
  presetEl.querySelector('option[value=edited]')?.remove();
  if (e.target.value === 'custom') { clearAll(); return; }
  const p = PRESETS[e.target.value]; if (p) { notes = []; writeForm(p); markChanged(); }
});
$('#prompt-text').value = PROMPT;
$('#copy-prompt').addEventListener('click', async () => {
  // Without clipboard access, select the prompt so the user can copy it by hand.
  try { await navigator.clipboard.writeText(PROMPT); $('#copy-prompt').textContent = 'Copied'; } catch { $('#prompt-text').select(); }
  setTimeout(() => ($('#copy-prompt').textContent = 'Copy the prompt'), 1800);
});
$('#apply-json').addEventListener('click', () => {
  try {
    const { spec, notes: n } = importJSON($('#json-in').value);
    writeForm(spec); showEdited();
    notes = n ? [`Agent notes: ${n}`] : [];
    $('#json-status').textContent = 'Filled in the form. Check every value against the drawing, then click Generate models.';
    $('#json-status').className = 'hint ok';
    markChanged();
  } catch (err) { $('#json-status').textContent = 'Could not read that: ' + err.message; $('#json-status').className = 'hint bad'; }
});
$('#dl-3mf').addEventListener('click', () => lastFiles && download(lastFiles.threeMF));
$('#dl-stl').addEventListener('click', () => lastFiles && download(lastFiles.stlZip));
$('#view-inner').addEventListener('click', () => setView('inner'));
$('#view-outer').addEventListener('click', () => setView('outer'));
$('#show-phone').addEventListener('change', (e) => (phoneGroup.visible = e.target.checked));
$('#limits').textContent = `Fits phones up to ${LIMITS.maxWidth} × ${LIMITS.maxLength} mm and ${LIMITS.maxThickness} mm thick.`;

writeForm(PRESETS['iphone-15-pro']); $('#preset').value = 'iphone-15-pro';
generate();
window.__doorGen = { generate, readForm, writeForm, importJSON, get files() { return lastFiles; } };
