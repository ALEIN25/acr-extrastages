// Report saved preview ground-cover triangles by 250 m of route.
// Usage: node tools/acr-ref/ground-cover-bins.mjs preview.glb preview.json
import fs from 'node:fs';

const [glbPath, jsonPath] = process.argv.slice(2);
if (!glbPath || !jsonPath) throw new Error('usage: node ground-cover-bins.mjs preview.glb preview.json');
const bytes = fs.readFileSync(glbPath);
let p = 12, doc, bin;
while (p < bytes.length) {
  const n = bytes.readUInt32LE(p), type = bytes.readUInt32LE(p + 4); p += 8;
  if (type === 0x4e4f534a) doc = JSON.parse(bytes.toString('utf8', p, p + n));
  if (type === 0x004e4942) bin = bytes.subarray(p, p + n);
  p += n;
}
const preview = JSON.parse(fs.readFileSync(jsonPath, 'utf8'));
const route = preview.route;
if (!route?.length || !bin) throw new Error('preview route or GLB binary missing');
const length = route.slice(1).reduce((sum, q, i) => sum + Math.hypot(q[0]-route[i][0], q[2]-route[i][2]), 0);
const count = new Uint32Array(Math.ceil(length / 250));
function accessor(i) {
  const a = doc.accessors[i], v = doc.bufferViews[a.bufferView], start = (v.byteOffset ?? 0) + (a.byteOffset ?? 0);
  const component = a.componentType === 5125 ? 4 : 4;
  return { a, start, stride: v.byteStride ?? component * ({SCALAR:1,VEC2:2,VEC3:3,VEC4:4}[a.type]) };
}
function f3(x, y, z) { return [x, y, z]; }
function closest(x, z) {
  let best = [Infinity, 0], along = 0;
  for (let i = 1; i < route.length; i++) {
    const a = route[i-1], b = route[i], dx=b[0]-a[0], dz=b[2]-a[2], d=dx*dx+dz*dz;
    const t = d ? Math.max(0, Math.min(1, ((x-a[0])*dx+(z-a[2])*dz)/d)) : 0;
    const e=Math.hypot(x-a[0]-t*dx,z-a[2]-t*dz);
    if (e < best[0]) best=[e, along+t*Math.sqrt(d)];
    along += Math.sqrt(d);
  }
  return best[1];
}
const mats = doc.materials ?? [];
for (const mesh of doc.meshes ?? []) for (const prim of mesh.primitives ?? []) {
  const name = mats[prim.material]?.name ?? '';
  if (!/gc_billboard_d|ground_cover/i.test(name)) continue;
  const pos = accessor(prim.attributes.POSITION), idx = accessor(prim.indices);
  const indexBytes = idx.a.componentType === 5123 ? 2 : 4;
  for (let k=0; k<idx.a.count; k+=3) {
    const ix=[];
    for (let j=0;j<3;j++) ix.push(indexBytes===2 ? bin.readUInt16LE(idx.start+(k+j)*indexBytes) : bin.readUInt32LE(idx.start+(k+j)*indexBytes));
    const v=ix.map(i=>f3(bin.readFloatLE(pos.start+i*pos.stride),bin.readFloatLE(pos.start+i*pos.stride+4),bin.readFloatLE(pos.start+i*pos.stride+8)));
    const d=closest((v[0][0]+v[1][0]+v[2][0])/3,(v[0][2]+v[1][2]+v[2][2])/3);
    if (d < length) count[Math.floor(d/250)]++;
  }
}
console.log(`route ${length.toFixed(1)} m; ${count.reduce((a,b)=>a+b,0)} ground-cover triangles`);
for (let i=0;i<count.length;i++) console.log(`${String(i*250).padStart(4)}-${String(Math.min((i+1)*250,length).toFixed(0)).padStart(4)} m  ${count[i]} triangles`);
