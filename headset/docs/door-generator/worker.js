// Builds the door off the main thread and returns preview meshes + download files.
import Module from './vendor/manifold.js';
import { zipSync, strToU8 } from './vendor/fflate.js';
import { buildDoor, check } from './door.js';
import { toSTL } from './stl.js';

// Manifold's objects live in WebAssembly memory that JavaScript's garbage collector never frees, and a
// door makes hundreds of them. One shared instance ran out of memory after 50 to 150 doors, so each door
// gets a fresh instance. The compiled code is shared, and the old instance's memory is freed with it.
const wasm = fetch(new URL('./vendor/manifold.wasm', import.meta.url))
  .then((r) => { if (!r.ok) throw new Error(`Could not load manifold.wasm (HTTP ${r.status}).`); return r.arrayBuffer(); })
  .then((bytes) => WebAssembly.compile(bytes));

function freshManifold() {
  return wasm.then((compiled) => new Promise((resolve, reject) => {
    Module({ instantiateWasm: (imports, done) => { WebAssembly.instantiate(compiled, imports).then((inst) => done(inst, compiled), reject); return {}; } })
      .then((M) => { M.setup(); resolve(M); }, reject);
  }));
}

function meshArrays(manifold) {
  const mesh = manifold.getMesh();
  const np = mesh.numProp, P = mesh.vertProperties;
  const pos = new Float32Array(mesh.numVert * 3);
  for (let i = 0; i < mesh.numVert; i++) { pos[3 * i] = P[i * np]; pos[3 * i + 1] = P[i * np + 1]; pos[3 * i + 2] = P[i * np + 2]; }
  return { pos, idx: new Uint32Array(mesh.triVerts) };
}

function xmlEsc(s) { return String(s).replace(/[<>&"]/g, (c) => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', '"': '&quot;' }[c])); }

/** One 3MF object made of components (door + 4 locators) so slicers load it as a single multi-part object. */
function to3MF(title, parts) {
  const objs = []; const comps = [];
  parts.forEach(({ name, arrays }, i) => {
    const id = i + 1; const { pos, idx } = arrays;
    const v = []; for (let k = 0; k < pos.length; k += 3) v.push(`<vertex x="${pos[k].toFixed(4)}" y="${pos[k + 1].toFixed(4)}" z="${pos[k + 2].toFixed(4)}"/>`);
    const t = []; for (let k = 0; k < idx.length; k += 3) t.push(`<triangle v1="${idx[k]}" v2="${idx[k + 1]}" v3="${idx[k + 2]}"/>`);
    objs.push(`<object id="${id}" name="${xmlEsc(name)}" type="model"><mesh><vertices>${v.join('')}</vertices><triangles>${t.join('')}</triangles></mesh></object>`);
    comps.push(`<component objectid="${id}"/>`);
  });
  const oid = parts.length + 1;
  const model = `<?xml version="1.0" encoding="UTF-8"?>\n<model unit="millimeter" xml:lang="en-US" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">` +
    `<metadata name="Title">${xmlEsc(title)}</metadata><metadata name="Application">CardboardXR door generator</metadata>` +
    `<resources>${objs.join('')}<object id="${oid}" name="${xmlEsc(title)}" type="model"><components>${comps.join('')}</components></object></resources>` +
    `<build><item objectid="${oid}" transform="1 0 0 0 1 0 0 0 1 128 128 0"/></build></model>`;
  const ct = '<?xml version="1.0" encoding="UTF-8"?>\n<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/></Types>';
  const rels = '<?xml version="1.0" encoding="UTF-8"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/></Relationships>';
  return zipSync({ '[Content_Types].xml': strToU8(ct), '_rels/.rels': strToU8(rels), '3D/3dmodel.model': strToU8(model) });
}

self.onmessage = async (e) => {
  const { id, spec } = e.data;
  try {
    const chk = check(spec);
    if (chk.errors.length) { self.postMessage({ id, check: chk }); return; }
    const M = await freshManifold();
    const t0 = performance.now();
    const r = buildDoor(M, spec);
    const label = (spec.name || 'Phone').trim() || 'Phone';
    const door = { name: `Door (PETG) - ${label}`, arrays: meshArrays(r.door) };
    const locs = r.locators.map((l) => ({ name: l.name, arrays: meshArrays(l.mesh), side: l.side, end: l.end }));
    const parts = [door, ...locs];
    const base = `Door - ${label}`.replace(/[\\/:*?"<>|]+/g, '-');
    const stls = { [`${base} (PETG, outer face down).stl`]: toSTL(r.door) };
    r.locators.forEach((l) => { stls[`${base} - ${l.name}.stl`] = toSTL(l.mesh); });
    const files = {
      threeMF: { name: `${base} (PETG + TPU locators).3mf`, data: to3MF(`Door - ${label}`, parts) },
      stlZip: { name: `${base} (STL files).zip`, data: zipSync(Object.fromEntries(Object.entries(stls).map(([k, v]) => [k, [v, { level: 6 }]]))) },
    };
    const stats = {
      ms: Math.round(performance.now() - t0), holes: r.holes, derived: r.derived,
      doorVolume: r.door.volume(), locatorVolumes: r.locators.map((l) => l.mesh.volume()),
      pieces: r.door.decompose().length, locatorPieces: r.locators.map((l) => l.mesh.decompose().length),
    };
    const transfer = [door.arrays.pos.buffer, door.arrays.idx.buffer, ...locs.flatMap((l) => [l.arrays.pos.buffer, l.arrays.idx.buffer]), files.threeMF.data.buffer, files.stlZip.data.buffer];
    self.postMessage({ id, check: chk, door, locs, files, stats }, transfer);
  } catch (err) {
    self.postMessage({ id, error: String(err && err.stack || err) });
  }
};
