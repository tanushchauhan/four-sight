// CardboardXR viewer — phone door generator.
// Rebuilds the 3D-printed viewer's phone door for any phone from a few dimensions.
// The outer perimeter, hinge knuckles and hair-tie latch are fixed (they mate with the viewer shell);
// the phone pad, TPU corner locators, camera window and vent holes are derived from the phone.
// Geometry follows the Fusion 360 model of the printed doors; units are mm.
//
// Door frame: x = across the phone (+x = latch side, -x = hinge side), y = along the phone
// (+y = camera end), z = up from the door's outer face. Seen from outside (the phone's back,
// camera end up), +x is on the LEFT.

export const FIXED = {
  HX: 38.6, HY: 79.59, CR: 13.5,          // door outline (same as the shell)
  z0: -2.5, t: 2.0,                       // outer face z, plate thickness
  hingeX: -41.4, hingeZ: 1.5, knuckleR: 2.5, knuckleLen: 20, knucklePitch: 48, pinR: 1.15,
  latchRise: 1.5, latchRound: 0.6,
  locatorTop: 5.75, pocketDepth: 1.4, dovetailDeg: 30,
  screenZ: 8.15,                          // phone screen plane: pad top = screenZ - phone thickness
  locOuterX: -37.15, locOuterY: -78.14, locOuterR: 12.05, locSideY: -63.89,
  legX: -36.2, legY: -57.5, legEnd: { bottom: -24.2, top: -22.9 },
  squeeze: 0.1, pocketInset: 1.0, padInset: 1.5, recessClear: 0.3,
  vent: { d: 2, px: 2.6, py: 1.495, x0: -33.8, y0: -74.75, xmax: 31.2, web: 1, windowWeb: 1.5, locatorWeb: 0.5,
          bay: [-36.1, 36.1, -77.09, 77.09, 11.0] },
};
const TAB = [[[38.0, -27.5], [42.2, -27.5], [43.0, -26.7], [43.0, 26.7], [42.2, 27.5], [38.0, 27.5]], [0, 0.3, 0.3, 0.3, 0.3, 0]];
const EAR = [[[38.0, 30.5], [41.399, 30.5], [41.801, 29.0], [42.80, 29.0], [45.6, 31.999], [45.6, 34.5], [38.0, 34.5]], [0, 0.8, 0.3, 0.3, 0.8, 0.8, 0]];

// Limits that keep the phone inside the shell and the locators printable.
export const LIMITS = {
  maxWidth: 72.2, maxLength: 154.0, minWidth: 60, minLength: 120,
  maxThickness: 9.15, minThickness: 6.5, minCornerR: 4, maxCornerR: 14,
  minLocatorWall: 0.5, thinLocatorWall: 1.0,   // TPU left round the phone's corners
};

/** The shortest and narrowest phone whose corners the fixed locators still reach round: each locator has
 *  to reach 1 mm past the 45-degree point of the phone's corner arc. */
export function minSize(cornerRadius) {
  const arc = cornerRadius * (1 - Math.SQRT1_2);
  return { length: 2 * (-FIXED.locSideY + 1 + arc), width: 2 * (-FIXED.legEnd.bottom + 1 + arc) };
}

/** TPU between the phone's corner arc and the locator's rounded outer edge (negative: the corner sticks out). */
export function locatorWall(d) {
  const F = FIXED, cx = -F.locOuterX - F.locOuterR, cy = -F.locOuterY - F.locOuterR;   // outer arc's centre
  return F.locOuterR - d.R - Math.hypot(Math.max(0, d.W - d.R - cx), Math.max(0, d.L - d.R - cy));
}

/** Phone spec (all mm):
 *  name, length, width, thickness, cornerRadius,
 *  camera: { top, left, width, height, radius }  — the raised camera plateau/bar seen on the BACK of the
 *          phone with the camera end at the top: distance from the top edge, distance from the left edge,
 *          its width and height, and its corner radius.
 *  cameraClearance (default 1.5) — gap added all round the camera opening. */
export function derive(spec) {
  const F = FIXED;
  const W = spec.width / 2, L = spec.length / 2, R = spec.cornerRadius;
  const cl = spec.cameraClearance ?? 1.5;
  const c = spec.camera;
  // camera plateau in door coords (back view: left edge = +x, top edge = +y)
  let x1 = W - c.left + cl, x0 = W - c.left - c.width - cl;
  let y1 = L - c.top + cl, y0 = L - c.top - c.height - cl;
  let r = Math.min((c.radius ?? 0) + cl, (x1 - x0) / 2 - 0.001, (y1 - y0) / 2 - 0.001);
  return { W, L, R, padTop: F.screenZ - spec.thickness, window: [x0, x1, y0, y1, r] };
}

export function check(spec) {
  const errors = [], warnings = [];
  const n = (v) => typeof v === 'number' && isFinite(v);
  for (const k of ['length', 'width', 'thickness', 'cornerRadius']) if (!n(spec[k])) errors.push(`Missing ${k}.`);
  const c = spec.camera || {};
  for (const k of ['top', 'left', 'width', 'height']) if (!n(c[k])) errors.push(`Missing camera ${k}.`);
  if (c.radius != null && !n(c.radius)) errors.push('Missing camera radius.');
  if (spec.cameraClearance != null && !n(spec.cameraClearance)) errors.push('Missing camera clearance.');
  if (errors.length) return { errors, warnings };
  if (spec.thickness > LIMITS.maxThickness) errors.push(`Too thick: ${spec.thickness} mm (max ${LIMITS.maxThickness} mm, measured without the camera bump).`);
  if (spec.thickness < LIMITS.minThickness) errors.push(`Too thin: ${spec.thickness} mm (min ${LIMITS.minThickness} mm).`);
  const before = errors.length;
  if (spec.width > LIMITS.maxWidth) errors.push(`Too wide: ${spec.width} mm. The viewer shell fits phones up to ${LIMITS.maxWidth} mm wide.`);
  if (spec.length > LIMITS.maxLength) errors.push(`Too long: ${spec.length} mm. The viewer shell fits phones up to ${LIMITS.maxLength} mm long.`);
  if (spec.width < LIMITS.minWidth || spec.length < LIMITS.minLength) errors.push('That looks too small for a phone. Check the units (mm).');
  if (spec.cornerRadius < LIMITS.minCornerR || spec.cornerRadius > LIMITS.maxCornerR) errors.push(`Corner radius ${spec.cornerRadius} mm is outside ${LIMITS.minCornerR}–${LIMITS.maxCornerR} mm.`);
  const d = derive(spec);
  if (errors.length === before) {   // the outline is in range, so check it against the fixed locators
    // A short phone's corners sit too far in for the locators to reach round them.
    const min = minSize(spec.cornerRadius), up = (v) => (Math.ceil(v * 10) / 10).toFixed(1);
    if (spec.length < min.length) errors.push(`Too short: ${spec.length} mm. The corner locators are in fixed places, so with ${spec.cornerRadius} mm corners they only reach round phones at least ${up(min.length)} mm long.`);
    if (spec.width < min.width) errors.push(`Too narrow: ${spec.width} mm. The corner locators are in fixed places, so with ${spec.cornerRadius} mm corners they only reach round phones at least ${up(min.width)} mm wide.`);
    // A big phone with square corners pushes its corners through the locators' rounded outer edges.
    const wall = locatorWall(d);
    if (wall < LIMITS.minLocatorWall) errors.push(wall < 0
      ? `The corners are too square for a phone this size: they'd stick out ${(-wall).toFixed(2)} mm past the TPU corner locators.`
      : `The corners are too square for a phone this size: the TPU corner locators would be only ${wall.toFixed(2)} mm thick round them (at least ${LIMITS.minLocatorWall} mm).`);
    else if (wall < LIMITS.thinLocatorWall) warnings.push(`The TPU corner locators will be only ${wall.toFixed(2)} mm thick round the corners, so they'll hold the phone a little less firmly.`);
  }
  if (c.width <= 0 || c.height <= 0) errors.push('The camera area needs a width and a height of more than 0 mm.');
  if ((c.radius ?? 0) < 0) errors.push("The camera area's corner radius can't be negative. Use 0 for square corners.");
  if ((spec.cameraClearance ?? 1.5) < 0) errors.push("The clearance around the camera can't be negative.");
  const [x0, x1, y0, y1] = d.window;
  if (c.left + c.width > spec.width + 0.01 || c.top + c.height > spec.length + 0.01 || c.left < 0 || c.top < 0)
    errors.push('The camera area extends past the phone outline. Check the camera numbers.');
  if (y0 < 0) warnings.push('The camera area reaches past the middle of the phone. Is "top" measured from the camera end?');
  if (d.padTop < FIXED.z0 + FIXED.t - 0.01) warnings.push(`This phone is thicker than ${(FIXED.screenZ - FIXED.z0 - FIXED.t).toFixed(2)} mm, so the door gets a ${(FIXED.z0 + FIXED.t - d.padTop).toFixed(2)} mm recess for it.`);
  return { errors, warnings };
}

// ------------------------------------------------------------------ 2D helpers
function rrectPts(x0, x1, y0, y1, r, seg = 12) {
  const pts = [];
  const cs = [[x1 - r, y0 + r, -90], [x1 - r, y1 - r, 0], [x0 + r, y1 - r, 90], [x0 + r, y0 + r, 180]];
  for (const [cx, cy, a0] of cs) {
    if (r <= 1e-9) { pts.push([cx, cy]); continue; }
    for (let k = 0; k <= seg; k++) { const a = (a0 + 90 * k / seg) * Math.PI / 180; pts.push([cx + r * Math.cos(a), cy + r * Math.sin(a)]); }
  }
  return pts;
}
function arcSeg(r) { return Math.max(4, Math.ceil(r * 2.2)); }  // ~0.6 mm chords
function rrect(M, x0, x1, y0, y1, r) { return new M.CrossSection([rrectPts(x0, x1, y0, y1, r, arcSeg(r))]); }

/** polygon with per-vertex fillet radii */
function roundedPoly(pts, radii) {
  const out = []; const n = pts.length;
  for (let i = 0; i < n; i++) {
    const r = radii[i]; const p = pts[i];
    if (!r) { out.push(p); continue; }
    const a = pts[(i - 1 + n) % n], b = pts[(i + 1) % n];
    const u = norm([a[0] - p[0], a[1] - p[1]]), v = norm([b[0] - p[0], b[1] - p[1]]);
    const ang = Math.acos(Math.max(-1, Math.min(1, u[0] * v[0] + u[1] * v[1])));
    const tlen = r / Math.tan(ang / 2);
    const t1 = [p[0] + u[0] * tlen, p[1] + u[1] * tlen], t2 = [p[0] + v[0] * tlen, p[1] + v[1] * tlen];
    const bis = norm([u[0] + v[0], u[1] + v[1]]); const dc = r / Math.sin(ang / 2);
    const c = [p[0] + bis[0] * dc, p[1] + bis[1] * dc];
    let a1 = Math.atan2(t1[1] - c[1], t1[0] - c[0]), a2 = Math.atan2(t2[1] - c[1], t2[0] - c[0]);
    let da = a2 - a1; while (da > Math.PI) da -= 2 * Math.PI; while (da < -Math.PI) da += 2 * Math.PI;
    const seg = 6;
    for (let k = 0; k <= seg; k++) { const t = a1 + da * k / seg; out.push([c[0] + r * Math.cos(t), c[1] + r * Math.sin(t)]); }
  }
  return out;
}
function norm(v) { const l = Math.hypot(v[0], v[1]); return [v[0] / l, v[1] / l]; }

function prism(M, cs, za, zb) { return M.Manifold.extrude(cs, zb - za).translate([0, 0, za]); }

// ------------------------------------------------------------------ locators (2D, booleans)
function locatorRegions(M, d, sx, sy, inset) {
  // envelope for the (-x,-y) corner, mirrored by sx (+1 top / -1 bottom) and sy (+1 camera / -1 USB end)
  const F = FIXED;
  const xEnd = sx < 0 ? F.legEnd.bottom : F.legEnd.top;     // short-leg end (negative, base frame)
  const env = M.CrossSection.intersection(
    new M.CrossSection([[[F.locOuterX, F.locOuterY], [xEnd, F.locOuterY], [xEnd, F.locSideY], [F.locOuterX, F.locSideY]]]),
    rrect(M, F.locOuterX, F.locOuterX + 60, F.locOuterY, F.locOuterY + 60, F.locOuterR));
  let shape = env;
  const xi = -d.W + F.squeeze;
  if (xi - F.legX >= 0.6) shape = M.CrossSection.union(shape, new M.CrossSection([[[F.legX, F.locSideY], [0, F.locSideY], [0, F.legY], [F.legX, F.legY]]]));
  const e = F.squeeze + inset;
  const phone = rrect(M, -d.W + e, d.W - e, -d.L + e, d.L - e, d.R - e);
  shape = M.CrossSection.difference(shape, phone);
  if (sx > 0) shape = shape.mirror([1, 0]);
  if (sy > 0) shape = shape.mirror([0, 1]);
  return shape;
}

// ------------------------------------------------------------------ vents
function segDist(p, a, b) {
  const ax = b[0] - a[0], ay = b[1] - a[1]; const l2 = ax * ax + ay * ay;
  const t = l2 < 1e-18 ? 0 : Math.max(0, Math.min(1, ((p[0] - a[0]) * ax + (p[1] - a[1]) * ay) / l2));
  return Math.hypot(p[0] - a[0] - t * ax, p[1] - a[1] - t * ay);
}
function inside(poly, p) {
  let ins = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const [x1, y1] = poly[j], [x2, y2] = poly[i];
    if ((y1 > p[1]) !== (y2 > p[1]) && p[0] < x1 + (p[1] - y1) * (x2 - x1) / (y2 - y1)) ins = !ins;
  }
  return ins;
}
function polysDist(polys, p) {
  let best = Infinity;
  for (const poly of polys) {
    if (inside(poly, p)) return -1;
    for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) best = Math.min(best, segDist(p, poly[j], poly[i]));
  }
  return best;
}
function rrectSD(p, x0, x1, y0, y1, r) {
  const cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, hx = (x1 - x0) / 2 - r, hy = (y1 - y0) / 2 - r;
  const qx = Math.abs(p[0] - cx) - hx, qy = Math.abs(p[1] - cy) - hy;
  return Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0) - r;
}
export function ventLayout(d, pocketPolys) {
  const V = FIXED.vent, hr = V.d / 2;
  const tilt = FIXED.pocketDepth * Math.tan(FIXED.dovetailDeg * Math.PI / 180);
  const out = [];
  const rows = Math.round(2 * Math.abs(V.y0) / V.py);
  for (let j = 0; j <= rows; j++) {
    const y = V.y0 + V.py * j;
    for (let i = 0; i < 40; i++) {
      if ((i + j) % 2) continue;
      const x = V.x0 + V.px * i;
      if (x > V.xmax + 1e-6) break;
      const p = [x, y];
      if (rrectSD(p, ...V.bay) > -(hr + V.web) + 1e-6) continue;
      if (rrectSD(p, ...d.window) < hr + V.windowWeb - 1e-6) continue;
      if (polysDist(pocketPolys, p) < tilt + hr + V.locatorWeb - 1e-6) continue;
      out.push(p);
    }
  }
  return out;
}

// ------------------------------------------------------------------ build
export function buildDoor(M, spec) {
  const F = FIXED; const d = derive(spec);
  const z0 = F.z0, zt = z0 + F.t, padTop = d.padTop;
  const parts = [];
  // 1 plate
  let door = prism(M, rrect(M, -F.HX, F.HX, -F.HY, F.HY, F.CR), z0, zt);
  // 2 phone pad (or a recess for thick phones)
  if (padTop > zt + 1e-6) door = door.add(prism(M, rrect(M, -d.W + F.padInset, d.W - F.padInset, -d.L + F.padInset, d.L - F.padInset, d.R - F.padInset), zt - 0.01, padTop));
  // 3 hinge knuckles (profile in x-z, extruded along y)
  {
    const hx = F.hingeX, hz = F.hingeZ, kr = F.knuckleR, s45 = Math.SQRT1_2;
    const tpx = hx - kr * s45, tpz = hz - kr * s45, x45 = tpx + (tpz - z0);
    const prof = [[x45, z0], [-38.0, z0], [-38.0, zt], [hx + kr, zt]];
    const n = 30;                               // 7.5 deg steps (hits the 90 deg top exactly)
    for (let k = 0; k <= n; k++) { const a = (225 * k / n) * Math.PI / 180; prof.push([hx + kr * Math.cos(a), hz + kr * Math.sin(a)]); }
    const sec = new M.CrossSection([prof]);
    for (let k = 0; k < 3; k++) {
      const yc = (k - 1) * F.knucklePitch;
      // extrude along +Z, then rotate so section y -> world z and extrusion -> world y
      const kn = M.Manifold.extrude(sec, F.knuckleLen).rotate([90, 0, 0]).translate([0, yc + F.knuckleLen / 2, 0]);
      door = door.add(kn);
    }
    // teardrop pin hole along y, point up
    const pr = F.pinR, s2 = Math.SQRT1_2, td = [];
    for (let k = 0; k <= 24; k++) { const a = (135 + 270 * k / 24) * Math.PI / 180; td.push([hx + pr * Math.cos(a), hz + pr * Math.sin(a)]); }
    td.push([hx, hz + pr / s2]);
    const hole = M.Manifold.extrude(new M.CrossSection([td]), 2 * F.HY + 10).rotate([90, 0, 0]).translate([0, F.HY + 5, 0]);
    door = door.subtract(hole);
  }
  // 4 hair-tie latch: base at plate level + raised rim with rounded top edges
  {
    const polys = [roundedPoly(TAB[0], TAB[1])];
    for (const sy of [1, -1]) { const p = roundedPoly(EAR[0].map(([x, y]) => [x, sy * y]), EAR[1]); polys.push(sy < 0 ? p.reverse() : p); }
    door = door.add(prism(M, M.CrossSection.union(polys.map((p) => new M.CrossSection([p]))), z0, zt));
    const rimPolys = [roundedPoly(TAB[0].map(([x, y]) => [Math.max(x, 38.75), y]), TAB[1])];
    for (const sy of [1, -1]) { const p = roundedPoly(EAR[0].map(([x, y]) => [Math.max(x, 38.75), sy * y]), EAR[1]); rimPolys.push(sy < 0 ? p.reverse() : p); }
    const rim = M.CrossSection.union(rimPolys.map((p) => new M.CrossSection([p])));
    const ztl = zt + F.latchRise, rr = F.latchRound;
    door = door.add(prism(M, rim, zt - 0.01, ztl - rr));
    const ns = 4;                                   // rounded top edge as 4 stepped insets (sub-layer)
    for (let k = 0; k < ns; k++) {
      const za = ztl - rr + rr * k / ns, zb = ztl - rr + rr * (k + 1) / ns, zm = (za + zb) / 2 - (ztl - rr);
      const inset = rr - Math.sqrt(Math.max(0, rr * rr - zm * zm));
      door = door.add(prism(M, rim.offset(-inset, 'Round'), za - 0.001, zb));
    }
  }
  // 5 TPU locators: dovetail pocket fill + pad-height fill + phone-corner band
  const tan = Math.tan(F.dovetailDeg * Math.PI / 180);
  const corners = [[-1, -1, 'bottom', 'USB-end'], [1, -1, 'top', 'USB-end'], [-1, 1, 'bottom', 'camera-end'], [1, 1, 'top', 'camera-end']];
  const locs = []; const pocketPolys = [];
  for (const [sx, sy, side, end] of corners) {
    const pocket = locatorRegions(M, d, sx, sy, F.pocketInset);
    const upper = locatorRegions(M, d, sx, sy, 0);
    pocketPolys.push(...pocket.toPolygons());
    const ns = 7, dz = F.pocketDepth / ns;       // dovetail as 0.2 mm steps (one per print layer)
    let loc = null;
    for (let k = 0; k < ns; k++) {
      const za = zt - dz * (k + 1), zb = zt - dz * k;
      const sl = prism(M, pocket.offset(tan * dz * (k + 0.5), 'Round'), za, zb + 0.001);
      loc = loc ? loc.add(sl) : sl;
    }
    if (padTop > zt) loc = loc.add(prism(M, pocket, zt - 0.001, padTop + 0.001));
    loc = loc.add(prism(M, upper, Math.min(padTop, zt), F.locatorTop));
    locs.push({ name: `TPU locator - ${side} - ${end} corner`, side, end, mesh: loc });
  }
  door = door.subtract(M.Manifold.union(locs.map((l) => l.mesh)));
  // recess for phones thicker than the standard pad allows
  if (padTop < zt - 1e-6) {
    const cl = F.recessClear;
    door = door.subtract(prism(M, rrect(M, -d.W - cl, d.W + cl, -d.L - cl, d.L + cl, d.R + cl), padTop, zt + 0.01));
  }
  // 6 camera window through the door and locators (up to the pad top)
  const [wx0, wx1, wy0, wy1, wr] = d.window;
  const win = prism(M, rrect(M, wx0, wx1, wy0, wy1, wr), z0 - 1, padTop);
  door = door.subtract(win);
  for (const l of locs) l.mesh = l.mesh.subtract(win);
  // 7 vent holes
  const holes = ventLayout(d, pocketPolys);
  if (holes.length) {
    const circ = M.CrossSection.circle(FIXED.vent.d / 2, 20);
    const cs = M.CrossSection.union(holes.map(([x, y]) => circ.translate([x, y])));
    door = door.subtract(prism(M, cs, z0 - 1, Math.max(padTop, zt) + 0.5));
  }
  // to print orientation: outer face on the bed (z = 0)
  door = door.translate([0, 0, -z0]);
  for (const l of locs) l.mesh = l.mesh.translate([0, 0, -z0]);
  return { door, locators: locs, holes: holes.length, derived: d };
}
