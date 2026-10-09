// Generates the mod's icons into ../Images: white shapes with a dark outline, so the game can tint them.
// Usage: node tools/make-icons.js
const fs = require('fs'), path = require('path'), zlib = require('zlib');

function crc32(buf) {
  let c, crc = 0xffffffff;
  for (let n = 0; n < buf.length; n++) {
    c = (crc ^ buf[n]) & 0xff;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crc = (crc >>> 8) ^ c;
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function png(size, shade) {
  const raw = Buffer.alloc(size * (size * 4 + 1));
  const S = 4; // supersampling per axis
  for (let y = 0; y < size; y++) {
    raw[y * (size * 4 + 1)] = 0;
    for (let x = 0; x < size; x++) {
      let r = 0, g = 0, b = 0, a = 0;
      for (let sy = 0; sy < S; sy++) for (let sx = 0; sx < S; sx++) {
        // Coordinates in -1..1, y up.
        const u = ((x + (sx + 0.5) / S) / size) * 2 - 1, v = 1 - ((y + (sy + 0.5) / S) / size) * 2;
        const [cr, cg, cb, ca] = shade(u, v);
        r += cr * ca; g += cg * ca; b += cb * ca; a += ca;
      }
      const o = y * (size * 4 + 1) + 1 + x * 4;
      const n = S * S;
      raw[o] = a ? Math.round(r / a * 255) : 0;
      raw[o + 1] = a ? Math.round(g / a * 255) : 0;
      raw[o + 2] = a ? Math.round(b / a * 255) : 0;
      raw[o + 3] = Math.round(a / n * 255);
    }
  }
  const chunk = (type, data) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const td = Buffer.concat([Buffer.from(type), data]);
    const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td));
    return Buffer.concat([len, td, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}

// Fill white inside (d < 0), dark outline of width w around it, transparent outside.
const layered = (d, w) => d < 0 ? [1, 1, 1, 1] : d < w ? [0.08, 0.08, 0.1, 1] : [0, 0, 0, 0];
const circle = (u, v, r) => Math.hypot(u, v) - r;
const diamond = (u, v, r) => (Math.abs(u) + Math.abs(v)) / Math.SQRT2 - r / Math.SQRT2;
// Chevron arrow pointing up: a triangle with a notch cut from the bottom.
function chevron(u, v) {
  const tri = Math.max(-v - 0.55, (Math.abs(u) * 0.866 + v * 0.5) - 0.42);
  const notch = (Math.abs(u) * 0.866 + (v + 0.55) * 0.5) - 0.16;
  return Math.max(tri, -notch);
}

const out = path.join(__dirname, '..', 'Images');
fs.mkdirSync(out, { recursive: true });
// Trail dot: soft-edged disc.
fs.writeFileSync(path.join(out, 'dot.png'), png(32, (u, v) => {
  const d = circle(u, v, 0.62);
  return d < 0 ? [1, 1, 1, 1] : d < 0.14 ? [0.08, 0.08, 0.1, 1] : [0, 0, 0, Math.max(0, 0.35 - (d - 0.14) * 1.6)];
}));
// Waypoint: outlined diamond with a hollow centre.
fs.writeFileSync(path.join(out, 'waypoint.png'), png(64, (u, v) => {
  const outer = diamond(u, v, 0.86), inner = -diamond(u, v, 0.34);
  const ring = Math.max(outer, inner);
  return layered(ring, 0.1);
}));
// Edge arrow, pointing up (rotated in game).
fs.writeFileSync(path.join(out, 'arrow.png'), png(48, (u, v) => layered(chevron(u, v), 0.12)));
// Soulstorm generator: a lightning bolt in a ring.
function inPolygon(u, v, points) {
  let inside = false;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const [xi, yi] = points[i], [xj, yj] = points[j];
    if ((yi > v) !== (yj > v) && u < ((xj - xi) * (v - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}
function edgeDistance(u, v, points) {
  let best = Infinity;
  for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
    const [ax, ay] = points[j], [bx, by] = points[i];
    const dx = bx - ax, dy = by - ay;
    const t = Math.max(0, Math.min(1, ((u - ax) * dx + (v - ay) * dy) / (dx * dx + dy * dy)));
    best = Math.min(best, Math.hypot(u - (ax + t * dx), v - (ay + t * dy)));
  }
  return best;
}
const bolt = [[0.12, 0.62], [-0.3, -0.02], [-0.02, -0.02], [-0.14, -0.62], [0.3, 0.06], [0.02, 0.06]];
fs.writeFileSync(path.join(out, 'generator.png'), png(64, (u, v) => {
  const inBolt = inPolygon(u, v, bolt);
  const boltEdge = edgeDistance(u, v, bolt);
  if (inBolt) return [1, 1, 1, 1];
  if (boltEdge < 0.09) return [0.08, 0.08, 0.1, 1];
  const ring = Math.abs(Math.hypot(u, v) - 0.82);
  if (ring < 0.07) return [1, 1, 1, 1];
  if (ring < 0.14) return [0.08, 0.08, 0.1, 1];
  return [0, 0, 0, 0];
}));
// Banner backdrop: a soft white band (tinted dark in game), fading out at its ends and edges. Square here; stretched to
// the banner in game, where only the fades' proportions matter.
const smooth = (e0, e1, x) => { const t = Math.min(1, Math.max(0, (x - e0) / (e1 - e0))); return t * t * (3 - 2 * t); };
fs.writeFileSync(path.join(out, 'band.png'), png(128, (u, v) => {
  const across = smooth(1, 0.45, Math.abs(u));
  const down = smooth(1, 0.55, Math.abs(v));
  return [1, 1, 1, across * down];
}));
console.log('icons written to', out);
