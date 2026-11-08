// Binary STL writer for a manifold-3d Mesh (or Manifold)
export function toSTL(manifold) {
  const mesh = manifold.getMesh();
  const np = mesh.numProp, P = mesh.vertProperties, T = mesh.triVerts;
  const nt = T.length / 3;
  const buf = new ArrayBuffer(84 + nt * 50); const dv = new DataView(buf);
  const head = 'CardboardXR door generator';
  for (let i = 0; i < head.length; i++) dv.setUint8(i, head.charCodeAt(i));
  dv.setUint32(80, nt, true);
  let o = 84;
  for (let t = 0; t < nt; t++) {
    const v = [0, 1, 2].map((k) => { const i = T[3 * t + k] * np; return [P[i], P[i + 1], P[i + 2]]; });
    const ux = v[1][0] - v[0][0], uy = v[1][1] - v[0][1], uz = v[1][2] - v[0][2];
    const wx = v[2][0] - v[0][0], wy = v[2][1] - v[0][1], wz = v[2][2] - v[0][2];
    let nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx; const l = Math.hypot(nx, ny, nz) || 1;
    nx /= l; ny /= l; nz /= l;
    for (const c of [nx, ny, nz]) { dv.setFloat32(o, c, true); o += 4; }
    for (const p of v) for (const c of p) { dv.setFloat32(o, c, true); o += 4; }
    dv.setUint16(o, 0, true); o += 2;
  }
  return new Uint8Array(buf);
}
