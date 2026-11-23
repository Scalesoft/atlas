// The mark of Atlas 5.1 „Tandem Tangerine“, the release of live collaboration: a tangerine cut crosswise whose two halves part and turn
// their cut faces to the viewer, drawn as a dimensioned technical drawing. Like strawberry-mark.mjs it has a generator of its own, with
// the same line style (Bloom's gradient, round ends), `wm-*` classes, namespacing by the mark's id and handling of reduced motion. Regenerate with
//   node tooling/brand/tangerine-mark.mjs [out]      (default: src/Atlas.Web/src/assets/brand/tangerine.svg)
//
// Geometry: each half is half of an oblate spheroid (equatorial radius R, polar semi-axis C) turned by PHI about the vertical axis, in
// orthographic projection. Its cut face is then an ellipse (R·sin PHI wide, R high, so its vertical diameter is true length), the dome's
// outline is half of the spheroid's silhouette ellipse and its parallels are the visible arcs of the circles parallel to the cut.
//
// Motion (s):
//   0      the whole tangerine: its outline and wireframe are drawn;
//   1.0    it cracks: a crack runs down the middle and the halves give a little;
//   1.35   the halves part and their cut faces open towards the viewer, the segments appear one after another;
//   2.6    the dimensions are drawn: the diameter of the cut, Ø 24, then the gap between the halves, 6;
//   then   now and then a light runs round both cut peels at once, in tandem.
// `prefers-reduced-motion: reduce`, or a `[data-still]` ancestor, shows the final drawing at once: every rule's resting state is the final one.
// The 20 px icon of ReleaseMark leaves the dimensions out; they would only be noise there.
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const RAD = Math.PI / 180, f = (n) => Number(n.toFixed(2)), pt = ([x, y]) => `${f(x)} ${f(y)}`;
const R = 12, C = 0.8 * R, RI = R - 1.5, PHI = 35, S = Math.sin(PHI * RAD), CO = Math.cos(PHI * RAD);
const A = R * S, RX = Math.sqrt(C * C * CO * CO + R * R * S * S), GAP = 6, CY = 29, MID = 31;
const D = 2 * A + GAP, HALVES = [{ side: -1, cx: MID - D / 2 }, { side: 1, cx: MID + D / 2 }];
// The segments of each half: their relative widths (irregular, as in a real fruit) and the angle of the first membrane.
const SEGMENTS = { [-1]: { start: 8, w: [1.08, .92, 1.12, .86, 1.04, .98, 1.15, .88, .97] }, [1]: { start: -14, w: [.95, 1.1, .9, 1.06, 1.12, .87, 1, .93, 1.07] } };

/** Catmull-Rom through closed points as cubic Béziers: a smooth irregular outline (affine maps keep it). */
function smooth(points) {
  const n = points.length, p = (i) => points[(i + n) % n];
  let d = `M${pt(p(0))}`;
  for (let i = 0; i < n; i++) {
    const [a, b, c, e] = [p(i - 1), p(i), p(i + 1), p(i + 2)];
    d += `C${pt([b[0] + (c[0] - a[0]) / 6, b[1] + (c[1] - a[1]) / 6])} ${pt([c[0] - (e[0] - b[0]) / 6, c[1] - (e[1] - b[1]) / 6])} ${pt(c)}`;
  }
  return `${d}Z`;
}

/** One half: its dome, wireframe, cut face with peel, segments and pith, in final position (the motion only moves and opens it). */
function half({ side, cx }) {
  // A point of the cut face at radius r and angle a (degrees, 0 at the top, clockwise as the viewer sees the right half).
  const face = (r, a) => [cx + side * S * r * Math.sin(a * RAD), CY - r * Math.cos(a * RAD)];
  const ellipse = (r) => `M${pt([cx, CY - r])}A${f(r * S)} ${f(r)} 0 1 1 ${pt([cx, CY + r])}A${f(r * S)} ${f(r)} 0 1 1 ${pt([cx, CY - r])}`;
  const sweep = side < 0 ? 0 : 1;
  const dome = `M${pt([cx, CY - R])}A${f(RX)} ${R} 0 0 ${sweep} ${pt([cx, CY + R])}`;
  // Parallels behind the cut at w (along the axis, away from the face): the arc whose surface faces the viewer.
  const parallels = [0.4, 0.75].map((t) => {
    const w = t * C, r = R * Math.sqrt(1 - t * t), c = cx + side * w * CO, k = (w / (C * C)) * S * R * R / (r * CO);
    const y = r * Math.sqrt(1 - k * k), x = c + side * r * S * k;
    return `M${pt([x, CY - y])}A${f(r * S)} ${f(r)} 0 0 ${sweep} ${pt([x, CY + y])}`;
  }).join("");
  const { start, w } = SEGMENTS[side], total = w.reduce((s, x) => s + x), bounds = [start];
  for (const x of w) bounds.push(bounds.at(-1) + 360 * x / total);
  const segments = w.map((_, i) => {
    const a0 = bounds[i], a1 = bounds[i + 1], mid = (a0 + a1) / 2, r0 = 1.9, r1 = (RI - 0.55) * (1 + 0.035 * Math.sin(i * 2.3 + side)), g = (r) => 0.55 / r / RAD;
    return smooth([[r0, mid], [r0 + 1.3, a0 + g(r0 + 1.3)], [r1 - 1.3, a0 + g(r1)], [r1 - 0.1, a0 + g(r1) + (a1 - a0) * 0.2], [r1, mid],
      [r1 - 0.1, a1 - g(r1) - (a1 - a0) * 0.2], [r1 - 1.3, a1 - g(r1)], [r0 + 1.3, a1 - g(r0 + 1.3)]].map(([r, a]) => face(r, a)));
  });
  // The pith in the middle: a little core and the fibres where the membranes meet.
  const pith = bounds.slice(0, -1).map((a) => `M${pt(face(0.45, a + 25))}Q${pt(face(1.1, a - 12))} ${pt(face(1.75, a))}`).join("");
  return { side, cx, dome, parallels, ellipse, segments, pith };
}

/** An open arrowhead at [x, y] pointing against the unit direction [ux, uy] (as the dimensions of strawberry-mark.mjs). */
const head = ([x, y], [ux, uy], a = 1.1, w = 0.5) => `M${pt([x + ux * a - uy * w, y + uy * a + ux * w])}L${pt([x, y])}L${pt([x + ux * a + uy * w, y + uy * a - ux * w])}`;

export function mark() {
  const line = (cls, d, delay) => `<path class="wm-line ${cls}" pathLength="100" d="${d}" style="animation-delay:${f(delay)}s"/>`;
  const show = (cls, d, delay) => `<path class="wm-line ${cls} wm-show" d="${d}" style="animation-delay:${f(delay)}s"/>`;
  const halves = HALVES.map(half);
  const drawHalf = (h) => `<g class="wm-half wm-${h.side < 0 ? "l" : "r"}">
${line("wm-dome wm-ink", h.dome, 0)}
${line("wm-wire wm-ink", h.parallels, 0.3)}
<g class="wm-face" style="transform-origin:${f(h.cx)}px ${CY}px">
<path class="wm-line wm-peel" d="${h.ellipse(R)}"/>
<path class="wm-line wm-inner" d="${h.ellipse(RI)}"/>
${h.segments.map((d, i) => `<path class="wm-line wm-seg" d="${d}" style="animation-delay:${f(1.75 + 0.06 * i)}s"/>`).join("\n")}
<path class="wm-line wm-pith" d="${h.pith}"/>
<path class="wm-glint" pathLength="100" d="${h.ellipse(R)}"/>
</g>
</g>`;
  const [l, r] = halves;

  // The crack down the middle of the whole fruit, seen only while it breaks.
  const crack = `M${pt([MID, CY - R])}` + [-0.6, 0.5, -0.4, 0.7, -0.5, 0.3, 0].map((dx, i) => `L${pt([MID + dx, CY - R + (i + 1) * 2 * R / 7])}`).join("");

  // The diameter of the right cut, to its right: extension lines from the top and bottom of the face, Ø along the dimension line.
  const dx = r.cx + RX + 4.4, dimAt = 2.6;
  const diameter = [CY - R, CY + R].map((y) => line("wm-dl wm-ink", `M${pt([r.cx + 1.4, y])}L${pt([dx + 1, y])}`, dimAt)).join("\n")
    + "\n" + line("wm-dl wm-ink", `M${pt([dx, CY - R])}L${pt([dx, CY + R])}`, dimAt + 0.25)
    + "\n" + show("wm-dl", head([dx, CY - R], [0, 1]) + head([dx, CY + R], [0, -1]), dimAt + 0.5)
    + `\n<text class="wm-label wm-show" x="${f(dx - 1)}" y="${CY}" transform="rotate(-90 ${f(dx - 1)} ${CY})" text-anchor="middle" style="animation-delay:${f(dimAt + 0.6)}s">Ø ${2 * R}</text>`;
  // The gap between the halves, beneath them: extension lines down from the nearest points of the two cuts.
  const gy = CY + R + 3.2, gx = [l.cx + A, r.cx - A], gapAt = dimAt + 0.45;
  const gap = gx.map((x) => line("wm-dl wm-ink", `M${pt([x, CY + 1.4])}L${pt([x, gy + 1])}`, gapAt)).join("\n")
    + "\n" + line("wm-dl wm-ink", `M${pt([gx[0], gy])}L${pt([gx[1], gy])}`, gapAt + 0.25)
    + "\n" + show("wm-dl", head([gx[0], gy], [1, 0]) + head([gx[1], gy], [-1, 0]), gapAt + 0.5)
    + `\n<text class="wm-label wm-show" x="${MID}" y="${f(gy + 3.4)}" text-anchor="middle" style="animation-delay:${f(gapAt + 0.6)}s">${GAP}</text>`;

  const label = "Atlas 5.1 Tandem Tangerine";
  const desc = `Mandarinka ve stylu loga Atlasu: celá praskne, napříč rozkrojené půlky se od sebe oddálí a natočí řezem k divákovi, dílky na řezu se ukážou jeden po druhém a výkres dostane kóty, průměr řezu Ø ${2 * R} a mezeru mezi půlkami ${GAP}. Pak po slupkách obou řezů občas proběhne světlo, v obou zároveň.`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64" class="wm-tangerine" role="img" aria-label="${label}">
<title>${label}</title><desc>${desc}</desc>
<defs><linearGradient id="tangerine-gradient" x1="10" y1="6" x2="54" y2="60" gradientUnits="userSpaceOnUse"><stop stop-color="#7058de"/><stop offset=".52" stop-color="#4c78e5"/><stop offset="1" stop-color="#159daa"/></linearGradient></defs>
<style>
.wm-tangerine .wm-line{fill:none;stroke:url(#tangerine-gradient);stroke-width:1.5;stroke-linecap:round;stroke-linejoin:round}
.wm-tangerine .wm-dome{fill:url(#tangerine-gradient);fill-opacity:.07}
.wm-tangerine .wm-wire{stroke-width:.5}
.wm-tangerine .wm-ink{stroke-dasharray:100;animation:tangerine-ink 1s cubic-bezier(.45,0,.3,1) both}
.wm-tangerine .wm-l{animation:tangerine-part-l 1.25s cubic-bezier(.22,1,.36,1) 1s both}
.wm-tangerine .wm-r{animation:tangerine-part-r 1.25s cubic-bezier(.22,1,.36,1) 1s both}
.wm-tangerine .wm-face{animation:tangerine-open .9s cubic-bezier(.22,1,.36,1) 1.35s both}
.wm-tangerine .wm-peel{stroke-width:1.2;fill:url(#tangerine-gradient);fill-opacity:.08}
.wm-tangerine .wm-inner{stroke-width:.6}
.wm-tangerine .wm-seg{stroke-width:.5;fill:url(#tangerine-gradient);fill-opacity:.16;animation:tangerine-show .5s ease both}
.wm-tangerine .wm-pith{stroke-width:.35}
.wm-tangerine .wm-crack{stroke-width:.7;opacity:0;animation:tangerine-crack 1s ease-out 1s}
.wm-tangerine .wm-dl{stroke-width:.35}
.wm-tangerine .wm-show{animation:tangerine-show .5s ease both}
.wm-tangerine .wm-label{font:500 2.6px ui-sans-serif,system-ui,sans-serif;fill:url(#tangerine-gradient)}
.wm-tangerine .wm-glint{stroke:#fff;stroke-width:1.8;stroke-dasharray:5 95;opacity:0;fill:none;filter:drop-shadow(0 0 .6px #ffffffb3);animation:tangerine-pass 14s linear 4.2s infinite}
@keyframes tangerine-ink{0%{stroke-dashoffset:100;opacity:0}12%{opacity:1}100%{stroke-dashoffset:0;opacity:1}}
@keyframes tangerine-part-l{0%{transform:translateX(${f(D / 2)}px)}20%,30%{transform:translateX(${f(D / 2 - 0.6)}px)}100%{transform:translateX(0)}}
@keyframes tangerine-part-r{0%{transform:translateX(${f(-D / 2)}px)}20%,30%{transform:translateX(${f(-D / 2 + 0.6)}px)}100%{transform:translateX(0)}}
@keyframes tangerine-open{from{transform:scaleX(0);opacity:0}}
@keyframes tangerine-crack{0%{stroke-dashoffset:100;opacity:1}30%{stroke-dashoffset:0;opacity:1}60%,100%{stroke-dashoffset:0;opacity:0}}
@keyframes tangerine-show{from{opacity:0}}
@keyframes tangerine-pass{0%{stroke-dashoffset:0;opacity:0}2%,18%{opacity:.9}21%,100%{stroke-dashoffset:-100;opacity:0}}
@media(prefers-reduced-motion:reduce){.wm-tangerine *{animation:none!important}.wm-tangerine .wm-glint{display:none}}
[data-still] .wm-tangerine *{animation:none!important}[data-still] .wm-tangerine .wm-glint{display:none}
.release-mark[data-variant="icon"] .wm-tangerine .wm-dim{display:none}
</style>
${drawHalf(l)}
${drawHalf(r)}
<path class="wm-line wm-crack" pathLength="100" stroke-dasharray="100" d="${crack}"/>
<g class="wm-dim">
${diameter}
${gap}
</g>
</svg>
`;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const out = process.argv[2] ?? fileURLToPath(new URL("../../src/Atlas.Web/src/assets/brand/tangerine.svg", import.meta.url));
  const svg = mark();
  writeFileSync(out, svg);
  console.log(`${out}: ${svg.length} B`);
}
