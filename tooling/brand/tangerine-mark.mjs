// The mark of Atlas 5.1 „Tandem Tangerine“, the release of live collaboration: a tangerine drawn as a technical section (řez). Its ten
// segments sit round the centre like people round one table, and the drawing keeps its dimensions. Like strawberry-mark.mjs it is not a
// body of revolution and has a generator of its own, with the same line style (Bloom's gradient, round ends), `wm-*` classes,
// namespacing by the mark's id and handling of reduced motion. Regenerate with
//   node tooling/brand/tangerine-mark.mjs [out]      (default: src/Atlas.Web/src/assets/brand/tangerine.svg)
//
// Motion (s):
//   0      the centre lines (dash-dot) extend from the centre;
//   0.35   the peel is drawn as if with a compass, the outer circle first, then the inner one;
//   1.1    the cut peel is hatched in one diagonal sweep;
//   1.5    the segments take their places round the centre one after another, clockwise from the top;
//   2.5    the dimensions are drawn: the diameter Ø 34 and the angle of one segment, 36°;
//   then   now and then two lights run round the peel in tandem, one right behind the other (two co-editors' pointers).
// `prefers-reduced-motion: reduce`, or a `[data-still]` ancestor, shows the final drawing at once: every rule's resting state is the final one.
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const RAD = Math.PI / 180, f = (n) => Number(n.toFixed(2)), pt = ([x, y]) => `${f(x)} ${f(y)}`;
// The section: centre, the peel (outer and inner radius), ten segments with a constant gap between them, the core.
const CX = 32, CY = 29, R = 17, RI = 13.8, N = 10, GAP = 2, CORE = 1.6;
/** The point at radius r and angle a (degrees, 0 at the top, clockwise). */
const at = (r, a) => [CX + r * Math.sin(a * RAD), CY - r * Math.cos(a * RAD)];

/** Segment i: a wedge between the core and the peel, its sides kept GAP / 2 off the dividing rays, its corners rounded. */
function segment(i) {
  const a0 = i * 360 / N, a1 = a0 + 360 / N, mid = (a0 + a1) / 2, r0 = CORE + 1.6, r1 = RI - 1.2;
  const side = (r, a, s) => at(r, a + s * (GAP / 2 / r) / RAD);
  return `M${pt(at(r0 - 0.6, mid))}Q${pt(side(r0, a0, 1))} ${pt(side(r0 + 1.4, a0, 1))}L${pt(side(r1 - 1.6, a0, 1))}Q${pt(side(r1, a0, 1))} ${pt(at(r1 + 0.1, a0 + 6))}`
    + `A${f(r1)} ${f(r1)} 0 0 1 ${pt(at(r1 + 0.1, a1 - 6))}Q${pt(side(r1, a1, -1))} ${pt(side(r1 - 1.6, a1, -1))}L${pt(side(r0 + 1.4, a1, -1))}Q${pt(side(r0, a1, -1))} ${pt(at(r0 - 0.6, mid))}Z`;
}

/** Hatching of the peel ring at 45°: each line clipped to the ring, one or two pieces. */
function hatching(spacing = 1.3) {
  const lines = [], n = [Math.SQRT1_2, Math.SQRT1_2], u = [Math.SQRT1_2, -Math.SQRT1_2];
  const p = (d, t) => [CX + d * n[0] + t * u[0], CY + d * n[1] + t * u[1]];
  for (let d = -R + spacing / 2; d < R; d += spacing) {
    const to = Math.sqrt(R * R - d * d);
    if (Math.abs(d) >= RI) lines.push({ d, path: `M${pt(p(d, -to))}L${pt(p(d, to))}` });
    else { const ti = Math.sqrt(RI * RI - d * d); lines.push({ d, path: `M${pt(p(d, -to))}L${pt(p(d, -ti))}M${pt(p(d, ti))}L${pt(p(d, to))}` }); }
  }
  return lines;
}

/** An open arrowhead at [x, y] pointing against the unit direction [ux, uy] (as the dimensions of strawberry-mark.mjs). */
const head = ([x, y], [ux, uy], a = 1.1, w = 0.5) => `M${pt([x + ux * a - uy * w, y + uy * a + ux * w])}L${pt([x, y])}L${pt([x + ux * a + uy * w, y + uy * a - ux * w])}`;

export function mark() {
  const circle = (r) => `M${pt([CX, CY - r])}A${r} ${r} 0 1 1 ${pt([CX, CY + r])}A${r} ${r} 0 1 1 ${pt([CX, CY - r])}`;
  const line = (cls, d, delay) => `<path class="wm-line ${cls}" pathLength="100" d="${d}" style="animation-delay:${f(delay)}s"/>`;
  const hatch = hatching();

  // The diameter under the section: extension lines from the axis, the dimension line, Ø 34 beneath it.
  const dy = CY + R + 4.2, dimAt = 2.5;
  const diameter = [CX - R, CX + R].map((x) => line("wm-dl wm-ink", `M${pt([x, CY + 1.2])}L${pt([x, dy + 1])}`, dimAt)).join("\n")
    + "\n" + line("wm-dl wm-ink", `M${pt([CX - R, dy])}L${pt([CX + R, dy])}`, dimAt + 0.25)
    + `\n<path class="wm-line wm-dl wm-show" d="${head([CX - R, dy], [1, 0])}${head([CX + R, dy], [-1, 0])}" style="animation-delay:${f(dimAt + 0.5)}s"/>`
    + `\n<text class="wm-label wm-show" x="${CX}" y="${f(dy + 3.6)}" text-anchor="middle" style="animation-delay:${f(dimAt + 0.6)}s">Ø ${2 * R}</text>`;
  // The angle of one segment, upper right: its two dividing rays extended past the peel, the arc between them, 36° outside.
  const a0 = 36, a1 = 72, ra = R + 3.2, angleAt = dimAt + 0.4;
  const angle = [a0, a1].map((a) => line("wm-dl wm-ink", `M${pt(at(R + 0.9, a))}L${pt(at(ra + 1.2, a))}`, angleAt)).join("\n")
    + "\n" + line("wm-dl wm-ink", `M${pt(at(ra, a0))}A${ra} ${ra} 0 0 1 ${pt(at(ra, a1))}`, angleAt + 0.25)
    + `\n<path class="wm-line wm-dl wm-show" d="${head(at(ra, a0), [Math.cos(a0 * RAD), Math.sin(a0 * RAD)])}${head(at(ra, a1), [-Math.cos(a1 * RAD), -Math.sin(a1 * RAD)])}" style="animation-delay:${f(angleAt + 0.5)}s"/>`
    + `\n<text class="wm-label wm-show" x="${f(at(ra + 2.4, 54)[0])}" y="${f(at(ra + 2.4, 54)[1] + 1)}" style="animation-delay:${f(angleAt + 0.6)}s">36°</text>`;

  const label = "Atlas 5.1 Tandem Tangerine";
  const desc = "Mandarinka narýsovaná jako technický řez ve stylu loga Atlasu: z kříže os se kružítkem vykreslí slupka, ta se vyšrafuje, kolem středu si jeden po druhém sednou dílky jako lidé u jednoho stolu a výkres dostane kóty Ø 34 a 36°. Pak po slupce občas proběhnou dvě světla v tandemu, jedno těsně za druhým.";
  return `<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64" class="wm-tangerine" role="img" aria-label="${label}">
<title>${label}</title><desc>${desc}</desc>
<defs><linearGradient id="tangerine-gradient" x1="10" y1="6" x2="54" y2="60" gradientUnits="userSpaceOnUse"><stop stop-color="#7058de"/><stop offset=".52" stop-color="#4c78e5"/><stop offset="1" stop-color="#159daa"/></linearGradient></defs>
<style>
.wm-tangerine .wm-line{fill:none;stroke:url(#tangerine-gradient);stroke-width:1.5;stroke-linecap:round;stroke-linejoin:round}
.wm-tangerine .wm-axis{stroke-width:.4;stroke-dasharray:5 1.2 .4 1.2;animation:tangerine-extend .9s cubic-bezier(.22,1,.36,1) both}
.wm-tangerine .wm-axis.wm-h{transform-origin:${CX}px ${CY}px}
.wm-tangerine .wm-axis.wm-v{transform-origin:${CX}px ${CY}px;animation-name:tangerine-extend-v}
.wm-tangerine .wm-peel{stroke-width:1.8;fill:url(#tangerine-gradient);fill-opacity:.07}
.wm-tangerine .wm-inner{stroke-width:.9}
.wm-tangerine .wm-ink{stroke-dasharray:100;animation:tangerine-ink 1.1s cubic-bezier(.22,1,.36,1) both}
.wm-tangerine .wm-peel.wm-ink{animation-duration:1.3s;animation-timing-function:cubic-bezier(.45,0,.3,1)}
.wm-tangerine .wm-hatch{stroke-width:.35;animation:tangerine-show .4s ease both}
.wm-tangerine .wm-seg{stroke-width:.9;fill:url(#tangerine-gradient);fill-opacity:.1;transform-origin:${CX}px ${CY}px;animation:tangerine-sit .8s cubic-bezier(.34,1.4,.64,1) both}
.wm-tangerine .wm-core{stroke-width:.9;transform-origin:${CX}px ${CY}px;animation:tangerine-sit .6s cubic-bezier(.34,1.56,.64,1) 1.45s both}
.wm-tangerine .wm-dl{stroke-width:.35}
.wm-tangerine .wm-show{animation:tangerine-show .5s ease both}
.wm-tangerine .wm-label{font:500 2.6px ui-sans-serif,system-ui,sans-serif;fill:url(#tangerine-gradient)}
.wm-tangerine .wm-glint{stroke:#fff;stroke-width:2.2;stroke-dasharray:4 96;opacity:0;fill:none;filter:drop-shadow(0 0 .65px #ffffffb3);animation:tangerine-pass 14s linear 4.2s infinite}
.wm-tangerine .wm-glint+.wm-glint{animation-delay:4.55s}
@keyframes tangerine-extend{from{transform:scaleX(0)}}
@keyframes tangerine-extend-v{from{transform:scaleY(0)}}
@keyframes tangerine-ink{0%{stroke-dashoffset:100;opacity:0}12%{opacity:1}100%{stroke-dashoffset:0;opacity:1}}
@keyframes tangerine-show{from{opacity:0}}
@keyframes tangerine-sit{from{transform:scale(.3);opacity:0}}
@keyframes tangerine-pass{0%{stroke-dashoffset:0;opacity:0}2%,18%{opacity:.9}21%,100%{stroke-dashoffset:-100;opacity:0}}
@media(prefers-reduced-motion:reduce){.wm-tangerine *{animation:none!important}.wm-tangerine .wm-glint{display:none}}
[data-still] .wm-tangerine *{animation:none!important}[data-still] .wm-tangerine .wm-glint{display:none}
</style>
<path class="wm-line wm-axis wm-h" d="M${pt([CX - R - 4, CY])}L${pt([CX + R + 4, CY])}"/>
<path class="wm-line wm-axis wm-v" d="M${pt([CX, CY - R - 4])}L${pt([CX, CY + R + 4])}"/>
${line("wm-peel wm-ink", circle(R), 0.35)}
${line("wm-inner wm-ink", circle(RI), 0.75)}
${hatch.map(({ d, path }) => `<path class="wm-line wm-hatch" d="${path}" style="animation-delay:${f(1.1 + 0.25 * (d + R) / (2 * R))}s"/>`).join("\n")}
${Array.from({ length: N }, (_, i) => `<path class="wm-line wm-seg" d="${segment(i)}" style="animation-delay:${f(1.5 + 0.09 * i)}s"/>`).join("\n")}
<circle class="wm-line wm-core" cx="${CX}" cy="${CY}" r="${CORE}"/>
${diameter}
${angle}
<path class="wm-glint" pathLength="100" d="${circle(R)}"/>
<path class="wm-glint" pathLength="100" d="${circle(R)}"/>
</svg>
`;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const out = process.argv[2] ?? fileURLToPath(new URL("../../src/Atlas.Web/src/assets/brand/tangerine.svg", import.meta.url));
  const svg = mark();
  writeFileSync(out, svg);
  console.log(`${out}: ${svg.length} B`);
}
