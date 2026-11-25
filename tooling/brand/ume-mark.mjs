// The mark of Atlas 5.2 „Unerring Ume“, the release of verified content: a plum drawn as an exact wireframe, built by points that
// hold their course. It is not a body of revolution drawn by wireframe-mark.mjs; the line style (Bloom's gradient, round ends), the
// `wm-*` classes, the namespacing by the mark's id and the handling of reduced motion are the same. Regenerate with
//   node tooling/brand/ume-mark.mjs [out]      (default: src/Atlas.Web/src/assets/brand/ume.svg)
//
// Geometry: the plum is a surface X(β, λ) in parametric latitude β and longitude λ: a spheroid (semi-axes A, C) whose radius sinks
// into the suture groove along one meridian, whose top sinks into the stem cavity and whose bottom draws out into a small tip. It is
// seen from a little above (TILT) in orthographic projection; its outline is the silhouette found by sampling the surface, smoothed
// by a Fourier series. Every line is a smooth chain of cubic segments laid on the surface (Hermite, from the curve's own derivative);
// its near side is where the depth is positive, and what passes behind the stone in the middle is masked by it.
// Motion (s): only transform, opacity and stroke-dashoffset animate (tooling/release-notes.test.mjs).
//   0      the outline draws itself from the top down both sides; the stone grows in the middle;
//   0.7    three points leave the bottom of the plum and climb it on loxodromes (a constant course: λ grows with the Mercator latitude),
//          120° apart, each dragging a hairline that fades out; they wind tight round the bottom, cross the middle fast and wind into
//          the stem cavity, where all three arrive at the same instant (4.1);
//          where the first one crosses each of nine equally spaced horizontal cuts, that cut unfolds from the crossing point both ways
//          round the plum, its two ends meeting on the far side; then its near side brightens;
//   4      the suture draws itself from the stem down, the stem grows from the cavity, the leaf opens;
//   then   now and then a point runs down the suture.
// `prefers-reduced-motion: reduce`, or a `[data-still]` ancestor, shows the final mark at once: every rule's resting state is the final one.
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";

const RAD = Math.PI / 180, TAU = 2 * Math.PI;
const f = (n) => Number(n.toFixed(2));
const f3 = (n) => Number(n.toFixed(3));
const pt = ([x, y]) => `${f(x)} ${f(y)}`;
const wrap = (a) => Math.atan2(Math.sin(a), Math.cos(a));

// The plum.
const A = 13.8, C = 18.6, TILT = 16 * RAD, LEAN = 7 * RAD, sinT = Math.sin(TILT), cosT = Math.cos(TILT);
const SUTURE = 58 * RAD, GROOVE = 0.06, GROOVE_W = 13 * RAD, FULL = 0.05; // FULL: the cheek opposite the suture is fuller // the groove: longitude (90° faces the viewer), depth, width
const CAVITY = 2.1, CAVITY_W = 24 * RAD, TIP = 0.9, TIP_W = 15 * RAD;
const STONE = [2.6, 3.9];
const CUTS = [-0.8, -0.6, -0.4, -0.2, 0, 0.2, 0.4, 0.6, 0.8];
const THREADS = [0, 120, 240].map((d) => d * RAD), TURNS = 2.2, POLE = 82 * RAD;
const RUN = 3.4, RUN_START = 0.7, UNFOLD = 0.9;
const DRIP = 1.6, DRIP_START = 7.5, DRIP_PERIOD = 12;

const surface = (b, l) => {
  const rho = Math.cos(b) * (1 - GROOVE * Math.exp(-((wrap(l - SUTURE) / GROOVE_W) ** 2))) * (1 + FULL * Math.cos(l - SUTURE - Math.PI));
  const y = C * Math.sin(b) - CAVITY * Math.exp(-(((Math.PI / 2 - b) / CAVITY_W) ** 2)) - TIP * Math.exp(-(((b + Math.PI / 2) / TIP_W) ** 2));
  return [A * rho * Math.cos(l), y, A * rho * Math.sin(l)];
};
// Seen from a little above, the plum leaning a little to the right.
const project = ([x, y, z]) => { const v = -(y * cosT - z * sinT); return [x * Math.cos(LEAN) - v * Math.sin(LEAN), x * Math.sin(LEAN) + v * Math.cos(LEAN), (y * sinT + z * cosT) / A]; };

// The silhouette: the farthest projected point in each direction from the centre, then a Fourier series through those radii.
const BINS = 720, far = new Array(BINS).fill(0);
for (let i = 0; i <= 360; i++) for (let j = 0; j < 360; j++) {
  const [x, y] = project(surface((i / 2 - 90) * RAD, j * RAD)), k = Math.round(((Math.atan2(y, x) + TAU) % TAU) / TAU * BINS) % BINS;
  far[k] = Math.max(far[k], Math.hypot(x, y));
}
const HARMONICS = 14, series = [];
for (let n = 0; n <= HARMONICS; n++) {
  let a = 0, b = 0;
  for (let k = 0; k < BINS; k++) { const t = TAU * k / BINS; a += far[k] * Math.cos(n * t); b += far[k] * Math.sin(n * t); }
  series.push([a / BINS * (n ? 2 : 1), b / BINS * 2]);
}
const radius = (t) => series.reduce((r, [a, b], n) => r + a * Math.cos(n * t) + b * Math.sin(n * t), 0);
for (let k = 0; k < BINS; k++) assert.ok(radius(TAU * k / BINS) > far[k] - 0.12, "the outline cuts into the plum");
// Centre the plum (with its stem) in the 64 × 64 square.
const top = Math.min(...Array.from({ length: 360 }, (_, i) => radius(i * RAD) * Math.sin(i * RAD)));
const bottom = Math.max(...Array.from({ length: 360 }, (_, i) => radius(i * RAD) * Math.sin(i * RAD)));
const CX = 32, CY = f(36.5 - (top + bottom) / 2);
const view = (p) => { const [x, y, z] = project(p); return [CX + x, CY + y, z]; };

/** A smooth chain of cubic segments along fn(t), t from t0 to t1 (either way), and its arc length by segment. */
function chain(fn, t0, t1, n) {
  const h = (t1 - t0) / n, e = Math.abs(h) * 1e-4, pts = [], lengths = [];
  const slope = (t) => { const a = fn(t - e), b = fn(t + e); return [(b[0] - a[0]) / (2 * e) * h / 3, (b[1] - a[1]) / (2 * e) * h / 3]; };
  let d = `M${pt(fn(t0))}`;
  for (let i = 0; i < n; i++) {
    const p0 = fn(t0 + h * i), p3 = fn(t0 + h * (i + 1)), s0 = slope(t0 + h * i), s3 = slope(t0 + h * (i + 1));
    const seg = [p0, [p0[0] + s0[0], p0[1] + s0[1]], [p3[0] - s3[0], p3[1] - s3[1]], p3].map(([x, y]) => [f(x), f(y)]);
    d += `C${pt(seg[1])} ${pt(seg[2])} ${pt(seg[3])}`;
    let len = 0;
    for (let k = 1, prev = seg[0]; k <= 48; k++) {
      const t = k / 48, u = 1 - t, q = [0, 1].map((j) => u ** 3 * seg[0][j] + 3 * u * u * t * seg[1][j] + 3 * u * t * t * seg[2][j] + t ** 3 * seg[3][j]);
      len += Math.hypot(q[0] - prev[0], q[1] - prev[1]); prev = q;
    }
    lengths.push(len);
  }
  return { d, lengths, length: lengths.reduce((a, b) => a + b, 0) };
}

/** Where the depth of fn changes sign between t0 and t1: the near and far pieces, in order. */
function pieces(fn, t0, t1) {
  const out = [], near = (t) => fn(t)[2] > 0, N = 2000;
  let from = t0;
  for (let i = 1; i <= N; i++) {
    const a = t0 + (t1 - t0) * (i - 1) / N, b = t0 + (t1 - t0) * i / N;
    if (near(a) === near(b)) continue;
    let lo = a, hi = b;
    for (let k = 0; k < 40; k++) { const m = (lo + hi) / 2; if (near(m) === near(lo)) lo = m; else hi = m; }
    out.push({ t0: from, t1: hi, near: near(a) }); from = hi;
  }
  out.push({ t0: from, t1, near: near(t1) });
  return out;
}

const [SX, SY] = STONE;
const hidden = (p) => { const x = p[0] - CX, y = p[1] - CY, u = x * Math.cos(LEAN) + y * Math.sin(LEAN), v = -x * Math.sin(LEAN) + y * Math.cos(LEAN); return (u / (SX + 0.3)) ** 2 + (v / (SY + 0.3)) ** 2 < 1; };
/**
 * Masked by the stone where it passes behind it. A line that also passes in front of the stone (`nearD`, its near side) gets a mask
 * of its own that leaves that near side uncovered.
 */
const masks = [];
const stoneHole = () => `<rect width="64" height="64" fill="#fff"/><ellipse cx="${CX}" cy="${CY}" rx="${f(SX + 0.3)}" ry="${f(SY + 0.3)}" transform="rotate(${f(LEAN / RAD)} ${CX} ${CY})"/>`;
function behind(fn, t0, t1, nearD) {
  let back = false, front = false;
  for (let i = 0; i <= 800; i++) { const p = fn(t0 + (t1 - t0) * i / 800); if (hidden(p)) p[2] > 0 ? (front = true) : (back = true); }
  if (!back) return "";
  if (!front) return ' mask="url(#ume-behind)"';
  assert.ok(nearD, "a line piece passes both behind and in front of the stone");
  const id = `ume-behind${masks.length}`;
  masks.push(`<mask id="${id}" maskUnits="userSpaceOnUse" x="0" y="0" width="64" height="64">${stoneHole()}<path d="${nearD}" fill="none" stroke="#fff" stroke-width="1.4"/></mask>`);
  return ` mask="url(#${id})"`;
}

// The loxodromes: Mercator latitude m runs evenly in time, λ = λ0 + K m, β = 2 atan(e^m) − 90°.
const M = Math.log(Math.tan(Math.PI / 4 + POLE / 2)), K = TURNS * TAU / (2 * M);
const lat = (m) => 2 * Math.atan(Math.exp(m)) - Math.PI / 2;
const threads = THREADS.map((l0, i) => {
  const fn = (m) => view(surface(lat(m), l0 + K * m)), parts = pieces(fn, -M, M);
  let s = 0;
  const table = []; // [m, arc length] along the whole thread
  const groups = parts.map((p) => {
    const n = Math.max(2, Math.ceil((p.t1 - p.t0) / 0.2)), c = chain(fn, p.t0, p.t1, n), start = s;
    c.lengths.forEach((len, k) => { table.push([p.t0 + (p.t1 - p.t0) * k / n, s]); s += len; });
    return `<g style="--us0:${f3(start)}"${p.near ? "" : ' class="wm-far"'}${behind(fn, p.t0, p.t1)}><path class="wm-o wm-tl" d="${c.d}"/>${["wm-t3", "wm-t2", "wm-t1"].map((t) => `<path class="wm-o wm-tr ${t}" d="${c.d}"/>`).join("")}<path class="wm-o wm-hd" d="${c.d}"/></g>`;
  });
  table.push([M, s]);
  const at = (m) => { const k = table.findIndex(([x], j) => j === table.length - 1 || table[j + 1][0] >= m); const [m0, s0] = table[k], [m1, s1] = table[Math.min(k + 1, table.length - 1)]; return m1 > m0 ? s0 + (s1 - s0) * (m - m0) / (m1 - m0) : s0; };
  const frames = Array.from({ length: 61 }, (_, k) => `${f(100 * k / 60)}%{stroke-dashoffset:calc((var(--ul) + var(--us0)) * 1px - ${f3(at(-M + 2 * M * k / 60))}px)}`).join("");
  return { l0, fn, groups, frames, i };
});
for (const t of threads) { const [x, y] = t.fn(M), [px, py] = view(surface(Math.PI / 2, 0)); assert.ok(Math.hypot(x - px, y - py) < 2.6, "a thread ends round the stem"); }

// The cuts: a cut unfolds from where the first thread crosses it, both ways round, the ends meeting on the far side.
const cuts = CUTS.map((h, j) => {
  const b = Math.asin(h), m = Math.log(Math.tan(Math.PI / 4 + b / 2)), from = THREADS[0] + K * m;
  const fn = (l) => view(surface(b, l)), at = f(RUN_START + RUN * (m + M) / (2 * M));
  const near = pieces(fn, from, from + TAU).filter((p) => p.near).map((p) => chain(fn, p.t0, p.t1, Math.ceil((p.t1 - p.t0) / (20 * RAD))).d).join("");
  const halves = [from + Math.PI, from - Math.PI].map((to) => chain(fn, from, to, 9).d);
  return `<g style="--ud:${at}s;--un:${f(at + UNFOLD)}s"${behind(fn, from, from + TAU, near)}>${halves.map((d) => `<path class="wm-o wm-unf" pathLength="100" d="${d}"/><path class="wm-o wm-uh" pathLength="100" d="${d}"/>`).join("")}<path class="wm-o wm-near" d="${near}"/></g>`;
});

// The outline, from the top down both sides, and the body under it.
const outline = (t) => [CX + radius(t) * Math.cos(t), CY + radius(t) * Math.sin(t)];
const body = chain(outline, -Math.PI / 2, 1.5 * Math.PI, 24).d + "Z";
const sides = [chain(outline, -Math.PI / 2, Math.PI / 2, 12).d, chain(outline, -Math.PI / 2, -1.5 * Math.PI, 12).d];
// The suture: its near side, from the stem cavity down.
const sutureFn = (b) => view(surface(b, SUTURE));
const suturePiece = pieces(sutureFn, POLE, -Math.PI / 2).find((p) => p.near);
const suture = chain(sutureFn, suturePiece.t0, suturePiece.t1, 10);
const NORTH = view(surface(Math.PI / 2, 0));
const stem = `M${pt(NORTH)}C${pt([NORTH[0] - 0.15, NORTH[1] - 2.6])} ${pt([NORTH[0] + 0.4, NORTH[1] - 4.6])} ${pt([NORTH[0] + 1.7, NORTH[1] - 6.2])}`;
const leafAt = [f(NORTH[0] + 0.55), f(NORTH[1] - 3.6)];
const leaf = `M${pt(leafAt)}C${pt([leafAt[0] + 2.4, leafAt[1] - 4])} ${pt([leafAt[0] + 6.3, leafAt[1] - 5.4])} ${pt([leafAt[0] + 10, leafAt[1] - 4.6])}C${pt([leafAt[0] + 7.8, leafAt[1] - 1.5])} ${pt([leafAt[0] + 4.1, leafAt[1] + 0.2])} ${pt(leafAt)}Z`;
const rib = `M${pt([leafAt[0] + 0.6, leafAt[1] - 0.3])}C${pt([leafAt[0] + 3.6, leafAt[1] - 1.8])} ${pt([leafAt[0] + 6.7, leafAt[1] - 3.3])} ${pt([leafAt[0] + 9, leafAt[1] - 4])}`;
const dripLen = (units) => f3(units / suture.length * 100);
/** The drip runs down the suture in the first DRIP seconds of each period, fading in and out at its ends. */
const dripFrames = () => {
  const end = 100 * DRIP / DRIP_PERIOD, at = (k) => f(end * k / 10), off = (k) => `stroke-dashoffset:calc(var(--ul) * 1px - ${f(10 * k)}px)`;
  return Array.from({ length: 11 }, (_, k) => `${at(k)}%{${off(k)};opacity:${k === 0 || k === 10 ? 0 : 1}}`).join("") + `100%{${off(10)};opacity:0}`;
};

export function mark() {
  const label = "Atlas 5.2 Unerring Ume";
  const desc = "Švestka ume ve stylu loga Atlasu jako přesný drátěný model s rýhou, jamkou u stopky a peckou uvnitř: vykreslí se obrys a uprostřed vyroste pecka. Pak ze spodku švestky vyrazí tři body a stoupají po loxodromách, kurzem, který se nemění; kde první z nich protne vodorovný řez, řez se z toho bodu rozvine oběma směry kolem plodu. Všechny tři body ve stejném okamžiku vplují do jamky u stopky, odkud vyroste stopka s listem a dolů se vykreslí rýha. Občas pak po rýze sklouzne bod.";
  const run = `${RUN}s linear ${RUN_START}s both`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64" class="wm-ume" role="img" aria-label="${label}">
<title>${label}</title><desc>${desc}</desc>
<defs><linearGradient id="ume-gradient" x1="10" y1="6" x2="54" y2="60" gradientUnits="userSpaceOnUse"><stop stop-color="#7058de"/><stop offset=".52" stop-color="#4c78e5"/><stop offset="1" stop-color="#159daa"/></linearGradient>
<radialGradient id="ume-stone" cx=".36" cy=".3" r=".8"><stop stop-color="#ece6ff"/><stop offset=".42" stop-color="#7058de"/><stop offset=".8" stop-color="#4c78e5"/><stop offset="1" stop-color="#159daa"/></radialGradient>
<mask id="ume-behind" maskUnits="userSpaceOnUse" x="0" y="0" width="64" height="64">${stoneHole()}</mask>${masks.join("")}
<radialGradient id="ume-skin" cx=".38" cy=".3" r=".78"><stop stop-color="#7058de" stop-opacity=".02"/><stop offset=".62" stop-color="#4c78e5" stop-opacity=".08"/><stop offset="1" stop-color="#159daa" stop-opacity=".18"/></radialGradient></defs>
<style>
.wm-ume .wm-line{fill:none;stroke:url(#ume-gradient);stroke-width:1.3;stroke-linecap:round;stroke-linejoin:round}
.wm-ume .wm-ink{stroke-dasharray:100;animation:ume-ink 1.2s cubic-bezier(.22,1,.36,1) both}
.wm-ume .wm-body{fill:url(#ume-skin);animation:ume-show .8s ease .2s both}
.wm-ume .wm-stone{fill:url(#ume-stone);transform-origin:${CX}px ${CY}px;animation:ume-grow 1s cubic-bezier(.3,1.35,.6,1) .35s both}
.wm-ume .wm-o{fill:none;stroke:url(#ume-gradient);stroke-width:.25}
.wm-ume .wm-unf{--ul:100;opacity:.14;stroke-dasharray:100 200;animation:ume-unfold ${UNFOLD}s cubic-bezier(.33,0,.2,1) var(--ud) both}
.wm-ume .wm-uh{--ul:.5;stroke-width:.62;stroke-linecap:round;stroke-dasharray:.5 300;opacity:0;animation:ume-unfold ${UNFOLD}s cubic-bezier(.33,0,.2,1) var(--ud) both,ume-flash ${UNFOLD}s linear var(--ud) both}
.wm-ume .wm-near{opacity:.26;animation:ume-near .7s ease var(--un) both}
.wm-ume .wm-tl{--ul:400;stroke-dasharray:400 400;opacity:.13}
.wm-ume .wm-far{opacity:.5}
.wm-ume .wm-tr{stroke-dasharray:var(--ul) 400}
.wm-ume .wm-t1{--ul:1.6;stroke-opacity:.8}
.wm-ume .wm-t2{--ul:3.8;stroke-opacity:.34}
.wm-ume .wm-t3{--ul:7.5;stroke-opacity:.14}
.wm-ume .wm-hd{--ul:.01;stroke-width:.7;stroke-linecap:round;stroke-dasharray:.01 400}
.wm-ume .wm-tr,.wm-ume .wm-hd{opacity:0}
${threads.map((t) => `.wm-ume .wm-th${t.i} .wm-tr,.wm-ume .wm-th${t.i} .wm-hd{animation:ume-th${t.i} ${run},ume-life ${run}}\n.wm-ume .wm-th${t.i} .wm-tl{animation:ume-th${t.i} ${run},ume-settle 1.4s ease ${f(RUN_START + RUN)}s both}`).join("\n")}
.wm-ume .wm-suture{stroke-width:.6;stroke-dasharray:100;opacity:.6;animation:ume-ink 1.1s cubic-bezier(.22,1,.36,1) 4.05s both}
.wm-ume .wm-drip{opacity:0;stroke-dasharray:var(--ul) 300;animation:ume-drip ${DRIP_PERIOD}s linear ${DRIP_START}s infinite both}
.wm-ume .wm-leaf{fill:url(#ume-gradient);fill-opacity:.07;stroke-width:1}
.wm-ume .wm-rib{stroke-width:.5}
.wm-ume .wm-sprout{transform-origin:${leafAt[0]}px ${leafAt[1]}px;animation:ume-open 1s cubic-bezier(.16,1,.3,1) 4.35s both}
@keyframes ume-ink{0%{stroke-dashoffset:100;opacity:0}12%{opacity:1}100%{stroke-dashoffset:0;opacity:1}}
@keyframes ume-show{from{opacity:0}}
@keyframes ume-grow{from{transform:scale(.1)}}
@keyframes ume-near{from{opacity:0}}
@keyframes ume-settle{from{opacity:.3}}
@keyframes ume-open{from{transform:rotate(24deg) scale(.4);opacity:0}}
@keyframes ume-unfold{from{stroke-dashoffset:calc(var(--ul) * 1px)}to{stroke-dashoffset:calc(var(--ul) * 1px - 100px)}}
@keyframes ume-flash{0%{opacity:0}8%{opacity:1}85%{opacity:1}100%{opacity:0}}
@keyframes ume-life{0%{opacity:0}3%{opacity:1}96%{opacity:1}100%{opacity:0}}
@keyframes ume-drip{${dripFrames()}}
${threads.map((t) => `@keyframes ume-th${t.i}{${t.frames}}`).join("\n")}
@media(prefers-reduced-motion:reduce){.wm-ume *{animation:none!important}}
[data-still] .wm-ume *{animation:none!important}
</style>
<path class="wm-body" d="${body}"/>
<g transform="rotate(${f(LEAN / RAD)} ${CX} ${CY})"><ellipse class="wm-stone" cx="${CX}" cy="${CY}" rx="${SX}" ry="${SY}"/></g>
${cuts.join("\n")}
${threads.map((t) => `<g class="wm-th wm-th${t.i}">${t.groups.join("")}</g>`).join("\n")}
<path class="wm-o wm-suture" pathLength="100" d="${suture.d}"/>
<path class="wm-o wm-drip" pathLength="100" d="${suture.d}" style="--ul:${dripLen(3.5)};stroke-opacity:.35"/><path class="wm-o wm-drip" pathLength="100" d="${suture.d}" style="--ul:${dripLen(1.4)};stroke-opacity:.8"/><path class="wm-o wm-drip" pathLength="100" d="${suture.d}" style="--ul:.03;stroke-width:.7;stroke-linecap:round"/>
${sides.map((d) => `<path class="wm-line wm-ink" pathLength="100" d="${d}"/>`).join("\n")}
<path class="wm-line wm-ink" pathLength="100" d="${stem}" style="animation-delay:4s;animation-duration:.7s"/>
<g class="wm-sprout"><path class="wm-line wm-leaf" d="${leaf}"/><path class="wm-line wm-rib" d="${rib}"/></g>
</svg>
`;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const out = process.argv[2] ?? fileURLToPath(new URL("../../src/Atlas.Web/src/assets/brand/ume.svg", import.meta.url));
  const svg = mark();
  writeFileSync(out, svg);
  console.log(`${out}: ${svg.length} B`);
}
