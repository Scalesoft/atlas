// The mark of „Shared Strawberry“, the release that opens Atlas as open source: a strawberry that sends out a runner with smaller
// strawberries on it (the forks), drawn as a construction in the golden ratio. It is not a body of revolution, so it does not use
// wireframe-mark.mjs; the line style (Bloom's gradient, round ends), the `wm-*` classes, the namespacing by the mark's id and the
// handling of reduced motion are the same. Regenerate with
//   node tooling/brand/strawberry-mark.mjs [out]      (default: src/Atlas.Web/src/assets/brand/strawberry.svg)
//
// Geometry: a golden rectangle cut into squares, each 1/φ of the one before. The mother hangs in the first square, her child in the
// second, the grandchild and a bud in the next two, so each generation is 1/φ of its parent. The golden spiral through the squares is
// the runner that joins them. Every berry is the same unit shape (its widest point at the golden section of its height) and its seeds
// sit by Vogel's model of phyllotaxis: seed n at r = c·√n, turned n × 137.5° (the golden angle).
// Motion (s):
//   0      the squares and the start of the spiral draw themselves as thin construction lines, then the dimensions φ : 1 and 1 : 1/φ;
//   0.7    the mother grows from her crown, her seeds pop in one golden angle after another (the angle is dimensioned), the sepals open;
//   2.7    the runner shoots along the spiral; where it arrives the next berry grows (3.3 the child, 4.95 the grandchild, 6.05 the bud);
//   7.6    the construction lines and the dimensions fade away and the clean mark stays;
//   then   a soft light runs along the runner from the mother to the tip now and then.
// `prefers-reduced-motion: reduce`, or a `[data-still]` ancestor, shows the final mark at once: every rule's resting state is the final one.
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const PHI = (1 + Math.sqrt(5)) / 2, GOLDEN_ANGLE = 360 / PHI ** 2, RAD = Math.PI / 180;
const f = (n) => Number(n.toFixed(2));
const pt = ([x, y]) => `${f(x)} ${f(y)}`;

// The unit berry in a unit square: stem top (.5, .134), crown (.5, .215), tip (.5, .96); four cubic curves from the crown and back.
const BODY = [[.5, .215], [.38, .16], [.2, .27], [.2, .5], [.2, .7], [.4, .9], [.5, .96], [.6, .9], [.8, .7], [.8, .5], [.8, .27], [.62, .16], [.5, .215]];
const SEPALS = { 5: [[.24, .28], [.37, .37], [.5, .41], [.63, .37], [.76, .28]], 3: [[.3, .31], [.5, .4], [.7, .31]] };
const OUTLINE = [0, 1, 2, 3].flatMap((i) => {
  const [a, b, c, d] = BODY.slice(i * 3, i * 3 + 4);
  return Array.from({ length: 16 }, (_, k) => { const t = k / 16, u = 1 - t; return [0, 1].map((j) => u ** 3 * a[j] + 3 * u * u * t * b[j] + 3 * u * t * t * c[j] + t ** 3 * d[j]); });
});
/** Distance of a point inside the unit body from its outline; negative outside. */
function inside([x, y]) {
  let odd = false, near = Infinity;
  OUTLINE.forEach(([x1, y1], i) => {
    const [x2, y2] = OUTLINE[(i + 1) % OUTLINE.length], dx = x2 - x1, dy = y2 - y1;
    if ((y1 > y) !== (y2 > y) && x < x1 + dx * (y - y1) / dy) odd = !odd;
    const t = Math.max(0, Math.min(1, ((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy)));
    near = Math.min(near, Math.hypot(x - x1 - t * dx, y - y1 - t * dy));
  });
  return odd ? near : -near;
}

/** A berry in the square [x, y, s]; `c` is the seed spacing of Vogel's model (0: no seeds), `sepals` 5, 3 or 0. */
function berry(x, y, s, { c = 0.062, sepals = 5 } = {}) {
  const P = ([u, v]) => [x + u * s, y + v * s], crown = P([.5, .215]), seeds = [];
  const body = `M${pt(P(BODY[0]))}` + [0, 1, 2, 3].map((i) => "C" + BODY.slice(i * 3 + 1, i * 3 + 4).map((p) => pt(P(p))).join(" ")).join("") + "Z";
  if (c) for (let n = 1; c * Math.sqrt(n) < 0.5; n++) {
    const r = c * Math.sqrt(n), a = n * GOLDEN_ANGLE * RAD, q = [0.5 + r * Math.sin(a), 0.53 - r * Math.cos(a)];
    if (inside(q) > 0.055) seeds.push(P(q));
  }
  const leaves = (SEPALS[sepals] ?? []).map((tip) => {
    const [tx, ty] = P(tip), mx = (crown[0] + tx) / 2, my = (crown[1] + ty) / 2, len = Math.hypot(tx - crown[0], ty - crown[1]);
    const nx = -(ty - crown[1]) / len * 0.05 * s, ny = (tx - crown[0]) / len * 0.05 * s;
    return `M${pt(crown)}Q${pt([mx + nx, my + ny])} ${pt([tx, ty])}Q${pt([mx - nx, my - ny])} ${pt(crown)}Z`;
  });
  return { body, seeds, leaves, crown, top: P([.5, .134]), centre: P([.5, .53]) };
}

/** One berry of generation `g`: from `at` it grows from its crown, its seeds pop in in the order of the golden angle, its sepals open. */
function drawBerry(b, g, at, { stem = `M${pt(b.top)}L${pt(b.crown)}`, pace = 1 } = {}) {
  const origin = `transform-origin:${f(b.crown[0])}px ${f(b.crown[1])}px`, step = Math.min(0.05, 0.9 / (b.seeds.length || 1)) * pace;
  return `<path class="wm-line wm-base wm-g${g}" pathLength="100" d="${stem}" style="animation-delay:${f(at - 0.15)}s"/>
<g class="wm-berry" style="${origin};animation-delay:${f(at)}s">
<path class="wm-line wm-shape wm-base wm-g${g}" pathLength="100" d="${b.body}" style="animation-delay:${f(at)}s"/>
${b.seeds.map(([x, y], i) => `<circle class="wm-seed" cx="${f(x)}" cy="${f(y)}" r="${[0.42, 0.36, 0.3][g]}" style="animation-delay:${f(at + 0.55 * pace + step * i)}s"/>`).join("\n")}
${b.leaves.map((d, i) => `<g class="wm-sepal" style="${origin};animation-delay:${f(at + 0.7 * pace + 0.1 * i)}s"><path class="wm-line wm-shape wm-calyx wm-g${g}" d="${d}"/></g>`).join("\n")}
</g>`;
}

/** A dimension line with open arrowheads at both ends and a label beside its middle. */
function dimension([x1, y1], [x2, y2], label, [ox, oy], anchor = "middle") {
  const len = Math.hypot(x2 - x1, y2 - y1), ux = (x2 - x1) / len, uy = (y2 - y1) / len, a = 1.1, w = 0.5;
  const head = (x, y, d) => `M${pt([x + d * (ux * a - uy * w), y + d * (uy * a + ux * w)])}L${pt([x, y])}L${pt([x + d * (ux * a + uy * w), y + d * (uy * a - ux * w)])}`;
  return `${thin(`M${pt([x1, y1])}L${pt([x2, y2])}`)}<path class="wm-line wm-dl" d="${head(x1, y1, 1)}${head(x2, y2, -1)}"/>
<text class="wm-label" x="${f((x1 + x2) / 2 + ox)}" y="${f((y1 + y2) / 2 + oy)}" text-anchor="${anchor}">${label}</text>`;
}
const thin = (d) => `<path class="wm-line wm-dl" pathLength="100" d="${d}"/>`;
/** A group of dimensions that appears at `at` and is gone when the drawing recedes. */
const note = (at, inner) => `<g class="wm-dim" style="animation-delay:${f(at)}s,var(--recede)">${inner.replaceAll('pathLength="100"', `pathLength="100" style="animation-delay:${f(at)}s"`)}</g>`;
const aid = (d, at) => `<path class="wm-line wm-aid" pathLength="100" d="${d}" style="animation-delay:${f(at)}s,var(--recede)"/>`;

export function mark() {
  // The golden rectangle s0·φ × s0, a little left of the centre: what stays of it (from the mother's left side) is centred.
  const s0 = 28, X0 = (64 - s0 * PHI) / 2 - 1.8, Y0 = 18, W = s0 * PHI, squares = [], arcs = [];
  for (let k = 0, x = X0, y = Y0, w = W, h = s0; k < 6; k++) {
    // Each square is cut from the left, the top, the right and the bottom of what remains; its quarter circle turns clockwise.
    squares.push([...[[x, y], [x, y], [x + w - h, y], [x, y + h - w]][k % 4], k % 2 ? w : h]);
    arcs.push({ c: [[x + h, y + h], [x, y + w], [x + w - h, y], [x + w, y + h - w]][k % 4], r: k % 2 ? w : h, from: 180 + 90 * k });
    if (k % 4 === 0) { x += h; w -= h; } else if (k % 4 === 1) { y += w; h -= w; } else if (k % 4 === 2) w -= h; else h -= w;
  }
  const on = (k, a) => [arcs[k].c[0] + arcs[k].r * Math.cos((arcs[k].from + a) * RAD), arcs[k].c[1] + arcs[k].r * Math.sin((arcs[k].from + a) * RAD)];
  /** The spiral from angle `a` on arc `k` to angle `b` on arc `l` (angles from the arc's start, 0–90). */
  const along = (k, a, l, b) => `M${pt(on(k, a))}` + arcs.slice(k, l + 1).map(({ r }, i) => `A${f(r)} ${f(r)} 0 0 1 ${pt(on(k + i, k + i === l ? b : 90))}`).join("");

  const [q0, q1, q2, q3] = squares, s1 = q1[2];
  // The mother and the child hang under the arch of the spiral, their stems meet it; the two smallest sit inside its curl on side stems.
  const mother = berry(q0[0], q0[1], q0[2]);
  const child = berry(q1[0], q1[1], q1[2], { c: 0.08 });
  const grand = berry(q2[0] + 0.08 * q2[2], q2[1] + 0.1 * q2[2], 0.72 * q2[2], { c: 0.115, sepals: 3 });
  const bud = berry(q3[0] + 0.28 * q3[2], q3[1] + 0.1 * q3[2], 0.7 * q3[2], { c: 0, sepals: 0 });
  const grandStem = `M${pt(on(2, 22))}Q${pt([q2[0] + 0.62 * q2[2], q2[1] + 0.08 * q2[2]])} ${pt(grand.crown)}`;
  const budStem = `M${pt(on(3, 67.5))}Q${pt([q3[0] + 0.3 * q3[2], q3[1] + 0.05 * q3[2]])} ${pt(bud.crown)}`;
  const runner = [[along(0, 60, 1, 30), 2.7, 0.8], [along(1, 30, 2, 22), 4.4, 0.6], [along(2, 22, 3, 67.5), 5.6, 0.5], [along(3, 67.5, 5, 90), 6.4, 0.8]];
  const recede = 7.6;

  // Dimensions: the sides of the squares under and beside the rectangle, and the angle between two successive seeds.
  const by = Y0 + s0 + 4, rx = X0 + W + 3.6;
  const sizes = [X0, X0 + s0, X0 + W].map((x) => thin(`M${pt([x, Y0 + s0 + 0.9])}L${pt([x, by + 1])}`)).join("") + dimension([X0, by], [X0 + s0, by], "φ", [0, 3.3]) + dimension([X0 + s0, by], [X0 + W, by], "1", [0, 3.3])
    + [Y0, Y0 + s1, Y0 + s0].map((y) => thin(`M${pt([X0 + W + 0.9, y])}L${pt([rx + 1, y])}`)).join("") + dimension([rx, Y0], [rx, Y0 + s1], "1", [1.5, 0.9], "start") + dimension([rx, Y0 + s1], [rx, Y0 + s0], "1/φ", [1.3, 0.9], "start");
  const [ox, oy] = mother.centre, ray = 0.25 * s0, ar = 0.16 * s0, dir = (n, r) => [ox + r * Math.sin(n * GOLDEN_ANGLE * RAD), oy - r * Math.cos(n * GOLDEN_ANGLE * RAD)];
  const angle = thin(`M${pt(dir(1, ray))}L${pt([ox, oy])}L${pt(dir(2, ray))}`) + thin(`M${pt(dir(1, ar))}A${f(ar)} ${f(ar)} 0 0 1 ${pt(dir(2, ar))}`)
    + thin(`M${pt(dir(1.92, ar + 0.6))}L${pt([X0 + 8.6, Y0 + 3.9])}`) + `<text class="wm-label" x="${f(X0 + 0.9)}" y="${f(Y0 + 3.4)}">137.5°</text>`;

  // The version is not part of the mark: its number is decided with the release and stands in the release notes only.
  const label = "Atlas Shared Strawberry";
  const desc = "Jahoda ve stylu loga Atlasu, narýsovaná podle zlatého řezu: rozkreslí se zlatý obdélník se čtverci a kóty φ : 1, v prvním čtverci vyroste jahoda a její nažky naskakují jedna po druhé po zlatém úhlu 137,5°; pak z ní po zlaté spirále vyrazí šlahoun a v dalších čtvercích na něm vyrostou menší jahůdky, každá 1/φ té předchozí. Kóty a pomocné čáry nakonec zmizí a zůstane čistá značka.";
  return `<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64" class="wm-strawberry" role="img" aria-label="${label}">
<title>${label}</title><desc>${desc}</desc>
<defs><linearGradient id="strawberry-gradient" x1="10" y1="6" x2="54" y2="60" gradientUnits="userSpaceOnUse"><stop stop-color="#7058de"/><stop offset=".52" stop-color="#4c78e5"/><stop offset="1" stop-color="#159daa"/></linearGradient></defs>
<style>
.wm-strawberry{--recede:${recede}s}
.wm-strawberry .wm-line{fill:none;stroke:url(#strawberry-gradient);stroke-width:1.5;stroke-linecap:round;stroke-linejoin:round}
.wm-strawberry .wm-shape{fill:url(#strawberry-gradient);fill-opacity:.07}
.wm-strawberry .wm-g1{stroke-width:1.25}
.wm-strawberry .wm-g2{stroke-width:1}
.wm-strawberry .wm-g3{stroke-width:.8}
.wm-strawberry .wm-calyx{stroke-width:.9}
.wm-strawberry .wm-calyx.wm-g2{stroke-width:.6}
.wm-strawberry .wm-runner{stroke-width:1.15}
.wm-strawberry .wm-base{stroke-dasharray:100;animation:strawberry-ink 1.3s cubic-bezier(.22,1,.36,1) both}
.wm-strawberry .wm-runner.wm-base{animation-timing-function:cubic-bezier(.45,0,.3,1)}
.wm-strawberry .wm-berry{animation:strawberry-grow 1.3s cubic-bezier(.3,1.35,.6,1) both}
.wm-strawberry .wm-seed{fill:url(#strawberry-gradient);transform-box:fill-box;transform-origin:center;animation:strawberry-dot .5s cubic-bezier(.34,1.56,.64,1) both}
.wm-strawberry .wm-sepal{animation:strawberry-open 1s cubic-bezier(.16,1,.3,1) both}
.wm-strawberry .wm-aid{stroke-width:.35;stroke-dasharray:100;opacity:0;animation:strawberry-aid 1.1s ease both,strawberry-out 1.4s ease forwards}
.wm-strawberry .wm-dim{opacity:0;animation:strawberry-show .5s ease both,strawberry-out 1.4s ease forwards}
.wm-strawberry .wm-dl{stroke-width:.35}
.wm-strawberry .wm-dl[pathLength]{stroke-dasharray:100;animation:strawberry-ink .9s cubic-bezier(.22,1,.36,1) both}
.wm-strawberry .wm-label{font:500 2.6px ui-sans-serif,system-ui,sans-serif;fill:url(#strawberry-gradient)}
.wm-strawberry .wm-glint{stroke:#fff;stroke-width:1.9;stroke-dasharray:6 94;opacity:0;fill:none;filter:drop-shadow(0 0 .65px #ffffffb3);animation:strawberry-pass 16s linear ${f(recede + 2)}s infinite}
@keyframes strawberry-ink{0%{stroke-dashoffset:100;opacity:0}12%{opacity:1}100%{stroke-dashoffset:0;opacity:1}}
@keyframes strawberry-grow{from{transform:scale(.12)}}
@keyframes strawberry-dot{from{transform:scale(0)}}
@keyframes strawberry-open{from{transform:rotate(24deg) scale(.4);opacity:0}}
@keyframes strawberry-aid{from{stroke-dashoffset:100;opacity:0}to{stroke-dashoffset:0;opacity:.55}}
@keyframes strawberry-show{from{opacity:0}to{opacity:1}}
@keyframes strawberry-out{to{opacity:0}}
@keyframes strawberry-pass{0%{stroke-dashoffset:0;opacity:0}2%,22%{opacity:.9}25%,100%{stroke-dashoffset:-100;opacity:0}}
@media(prefers-reduced-motion:reduce){.wm-strawberry *{animation:none!important}.wm-strawberry .wm-glint{display:none}}
[data-still] .wm-strawberry *{animation:none!important}[data-still] .wm-strawberry .wm-glint{display:none}
</style>
${squares.map(([x, y, s], k) => aid(`M${pt([x, y])}h${f(s)}v${f(s)}h${f(-s)}Z`, 0.12 * k)).join("\n")}
${aid(along(0, 0, 0, 60), 0.3)}
${note(0.9, sizes)}
${runner.map(([d, at, duration]) => `<path class="wm-line wm-runner wm-base" pathLength="100" d="${d}" style="animation-delay:${at}s;animation-duration:${duration}s"/>`).join("\n")}
${drawBerry(mother, 0, 0.7)}
${note(1.5, angle)}
${drawBerry(child, 1, 3.3, { pace: 0.85 })}
${drawBerry(grand, 2, 4.95, { stem: grandStem, pace: 0.7 })}
${drawBerry(bud, 3, 6.05, { stem: budStem, pace: 0.6 })}
<path class="wm-glint" pathLength="100" d="${along(0, 60, 5, 90)}"/>
</svg>
`;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const out = process.argv[2] ?? fileURLToPath(new URL("../../src/Atlas.Web/src/assets/brand/strawberry.svg", import.meta.url));
  const svg = mark();
  writeFileSync(out, svg);
  console.log(`${out}: ${svg.length} B`);
}
