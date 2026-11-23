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
//   0      the whole tangerine: its outline and wireframe are drawn, then its overall length is dimensioned (20,9);
//   1.0    it cracks: a crack runs down the middle and the halves give a little;
//   1.4    the halves slowly part while their cut faces turn to the viewer in step, the segments appear one after another; the chain
//          beneath (depth of each half, gap) appears and every dimension stretches with them while its number counts up;
//   3.8    the numbers stop at 40,7 and 17,3 · 6 · 17,3, and the diameter of the cut, Ø 24, is drawn;
//   4.3    one segment of the right cut lights up and its angle is dimensioned on the tilted cut (an elliptic arc), counting up to 43°;
//   5.8    the right half slowly fades to a phantom and the segment comes out of it, growing into one segment seen from the side (the
//          „fish“), whose length dimension counts up to 19;
//   then   now and then a light runs round the left cut's peel (and, faintly, the phantom's).
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
  return { side, cx, dome, parallels, ellipse, segments, pith, bounds, face };
}

/** An open arrowhead at [x, y] pointing against the unit direction [ux, uy] (as the dimensions of strawberry-mark.mjs). */
const head = ([x, y], [ux, uy], a = 1.1, w = 0.5) => `M${pt([x + ux * a - uy * w, y + uy * a + ux * w])}L${pt([x, y])}L${pt([x + ux * a + uy * w, y + uy * a - ux * w])}`;

/** CSS cubic-bezier(x1, y1, x2, y2) as a function of time progress, to know where the halves are at any moment. */
function bezier(x1, y1, x2, y2) {
  const b = (s, p1, p2) => 3 * p1 * s * (1 - s) ** 2 + 3 * p2 * s * s * (1 - s) + s ** 3;
  return (p) => { let lo = 0, hi = 1; for (let i = 0; i < 40; i++) { const m = (lo + hi) / 2; if (b(m, x1, x2) < p) lo = m; else hi = m; } return b((lo + hi) / 2, y1, y2); };
}

// The parting: one animation (delay, duration, easing per keyframe interval) moves each half by X from the middle; every dimension that
// changes with it uses the same keyframes, so it stays exact, and the numbers are read from the same X at each moment.
const PART = { delay: 1, duration: 2.8, easing: [0.65, 0, 0.35, 1], stops: [0, 0.15, 0.4, 2.8], x: [D / 2, D / 2 - 0.6, D / 2 - 0.6, 0] };
const ease = bezier(...PART.easing);
// After the parting (s): one segment of the right cut lights up, its angle is dimensioned, then the half fades and the segment becomes the fish.
const FISH = { pick: 4.3, angle: 4.5, at: 5.8, duration: 2.6 };
function partX(t) {
  const s = Math.min(Math.max(t - PART.delay, 0), PART.duration), i = Math.min(PART.stops.findLastIndex((x) => x <= s), PART.stops.length - 2);
  return PART.x[i] + (PART.x[i + 1] - PART.x[i]) * ease((s - PART.stops[i]) / (PART.stops[i + 1] - PART.stops[i]));
}
const keyframes = (name, css) => `@keyframes ${name}{${PART.stops.map((s, i) => `${f(100 * s / PART.duration)}%{transform:${css(PART.x[i])}}`).join("")}}`;
/** The first moment the value reaches v (the values only grow). */
const when = (value, v) => { let t = PART.delay; while (t < PART.delay + PART.duration && value(t) < v) t += 0.005; return t; };
const numberOf = (v) => (Math.round(Math.max(v, 0) * 10) / 10).toFixed(1).replace(".", ",").replace(/,0$/, "");

export function mark() {
  const line = (cls, d, delay) => `<path class="wm-line ${cls}" pathLength="100" d="${d}" style="animation-delay:${f(delay)}s"/>`;
  const show = (cls, d, delay) => `<path class="wm-line ${cls} wm-show" d="${d}" style="animation-delay:${f(delay)}s"/>`;
  const label = (x, y, text, delay, cls = "wm-show") => `<text class="wm-label ${cls}" x="${f(x)}" y="${f(y)}" text-anchor="middle" style="animation-delay:${f(delay)}s">${text}</text>`;
  /** A number that counts while something moves: one text per reading, each shown only for its moment, the last one stays. */
  function counter(value, from, x, y, end = PART.delay + PART.duration, number = numberOf, rotate = 0) {
    const readings = [], at = rotate ? ` transform="rotate(${rotate} ${f(x)} ${f(y)})"` : "";
    for (let t = from; t < end - 1e-9; t += 0.06) {
      const text = number(value(t));
      if (readings.at(-1)?.text === text) readings.at(-1).until = t + 0.06; else readings.push({ text, t, until: t + 0.06 });
    }
    const final = number(value(end)), last = readings.at(-1)?.text === final ? readings.pop().t : end;
    const text = (content, cls, style) => `<text class="wm-label ${cls}" x="${f(x)}" y="${f(y)}"${at} text-anchor="middle" style="${style}">${content}</text>`;
    return readings.map(({ text: v, t, until }) => text(v, "wm-tick", `animation-duration:${f(until - t)}s;animation-delay:${f(t)}s`)).join("\n")
      + "\n" + text(final, "wm-hold", `animation-delay:${f(last)}s`);
  }

  const [l, r] = HALVES.map(half);
  const TOT = 2 * RX + D, oy = CY - R - 3.4, gy = CY + R + 3.2;
  // How far the cut faces have turned to the viewer: in step with the parting, so the faces never overlap and the gap only grows.
  const open = (x) => 1 - 2 * x / D, depth = (x) => RX + A * open(x);
  const gap = (t) => GAP * open(partX(t)), total = (t) => TOT - 2 * partX(t), halfDepth = (t) => depth(partX(t));
  const chainFrom = when(gap, 0.3), arrowsFrom = when(gap, 2.4);
  // The segment of the right cut that becomes the fish: the one pointing up towards the gap, where its angle has room.
  const mids = r.bounds.slice(0, -1).map((a, i) => (a + r.bounds[i + 1]) / 2);
  const pick = mids.reduce((best, m, i) => Math.abs(((m - 315 + 540) % 360) - 180) < Math.abs(((mids[best] - 315 + 540) % 360) - 180) ? i : best, 0);

  // The dimensions that travel with a half: the overall length's extension line and arrow above, and below, the chain: the half's depth
  // (from the dome to the edge of the cut, growing as the face turns) and the gap, whose extension line follows the edge of the cut.
  const halfDims = (h) => {
    const s = h.side, outer = h.cx + s * RX, inner = h.cx - s * A, name = s < 0 ? "l" : "r";
    return `<g class="wm-dim">
${line("wm-dl wm-ink", `M${pt([outer, CY - 1.4])}L${pt([outer, oy - 1])}`, 0.5)}
${show("wm-dl", head([outer, oy], [-s, 0]), 0.9)}
<g class="wm-show" style="animation-delay:${f(chainFrom)}s">
<path class="wm-line wm-dl" d="M${pt([outer, CY + 1.4])}L${pt([outer, gy + 1])}${head([outer, gy], [-s, 0])}"/>
<g class="wm-depth" style="transform-origin:${f(outer)}px 0"><path class="wm-line wm-dl" d="M${pt([outer, gy])}L${pt([inner, gy])}"/></g>
<g class="wm-edge-${name}"><path class="wm-line wm-dl" d="M${pt([inner, CY + 1.4])}L${pt([inner, gy + 1])}${head([inner, gy], [s, 0])}"/>
${show("wm-dl", head([inner, gy], [-s, 0]), arrowsFrom)}</g>
<g class="wm-mid-${name}">
${counter(halfDepth, chainFrom, (outer + inner) / 2, gy + 3.4)}
</g>
</g>
</g>`;
  };
  const drawHalf = (h) => `<g class="wm-half wm-${h.side < 0 ? "l" : "r"}">
<g class="${h.side > 0 ? "wm-ghost" : ""}">
${line("wm-dome wm-ink", h.dome, 0)}
${line("wm-wire wm-ink", h.parallels, 0.3)}
<g class="wm-show" style="animation-delay:1s"><g class="wm-face" style="transform-origin:${f(h.cx)}px ${CY}px">
<path class="wm-line wm-peel" d="${h.ellipse(R)}"/>
<path class="wm-line wm-inner" d="${h.ellipse(RI)}"/>
${h.segments.map((d, i) => `<path class="wm-line wm-seg" d="${d}" style="animation-delay:${f(1.6 + 0.07 * i)}s"/>`).join("\n")}
<path class="wm-line wm-pith" d="${h.pith}"/>
${h.side > 0 ? `<path class="wm-line wm-seg wm-pick" d="${h.segments[pick]}" style="animation-delay:${f(FISH.pick)}s"/>` : ""}
<path class="wm-glint" pathLength="100" d="${h.ellipse(R)}"/>
</g></g>
</g>
${halfDims(h)}
</g>`;

  // The crack down the middle of the whole fruit, seen only while it breaks.
  const crack = `M${pt([MID, CY - R])}` + [-0.6, 0.5, -0.4, 0.7, -0.5, 0.3, 0].map((dx, i) => `L${pt([MID + dx, CY - R + (i + 1) * 2 * R / 7])}`).join("");

  // The overall length above: measured on the whole fruit first, then it grows as the halves part.
  const overall = `<g class="wm-stretch wm-total"><path class="wm-line wm-dl wm-ink" pathLength="100" d="M${pt([l.cx - RX, oy])}L${pt([r.cx + RX, oy])}" style="animation-delay:.6s"/></g>
${counter(total, 0.9, MID, oy - 1)}`;
  // The gap beneath, between the two cuts: its line stretches with the halves and its number counts up.
  const between = `<g class="wm-show" style="animation-delay:${f(chainFrom)}s"><g class="wm-stretch wm-gap"><path class="wm-line wm-dl" d="M${pt([l.cx + A, gy])}L${pt([r.cx - A, gy])}"/></g></g>
${counter(gap, chainFrom, MID, gy + 3.4)}`;
  // The diameter of the right cut, to its right, once the halves are apart: extension lines from the top and bottom of the face.
  const dx = r.cx + RX + 4.4, dimAt = PART.delay + PART.duration + 0.1;
  const diameter = [CY - R, CY + R].map((y) => line("wm-dl wm-ink", `M${pt([r.cx + 1.4, y])}L${pt([dx + 1, y])}`, dimAt)).join("\n")
    + "\n" + line("wm-dl wm-ink", `M${pt([dx, CY - R])}L${pt([dx, CY + R])}`, dimAt + 0.25)
    + "\n" + show("wm-dl", head([dx, CY - R], [0, 1]) + head([dx, CY + R], [0, -1]), dimAt + 0.5)
    + `\n<text class="wm-label wm-show" x="${f(dx - 1)}" y="${CY}" transform="rotate(-90 ${f(dx - 1)} ${CY})" text-anchor="middle" style="animation-delay:${f(dimAt + 0.6)}s">Ø ${2 * R}</text>`;

  // The angle of the picked segment, on the tilted cut: its rays and an arc that the tilt turns into a piece of an ellipse; the number
  // counts up as the arc is drawn.
  const a0 = r.bounds[pick], a1 = r.bounds[pick + 1], ra = R + 2.4, deg = a1 - a0, arcAt = FISH.angle + 0.2;
  const tangent = (a) => { const v = [S * Math.cos(a * RAD), Math.sin(a * RAD)], n = Math.hypot(...v); return [v[0] / n, v[1] / n]; };
  const t0 = tangent(a0), t1 = tangent(a1), inkEase = bezier(0.45, 0, 0.3, 1);
  const angle = [a0, a1].map((a) => line("wm-dl wm-ink", `M${pt(r.face(R + 0.6, a))}L${pt(r.face(ra + 1.2, a))}`, FISH.angle)).join("\n")
    + "\n" + line("wm-dl wm-ink", `M${pt(r.face(ra, a0))}A${f(ra * S)} ${f(ra)} 0 0 1 ${pt(r.face(ra, a1))}`, arcAt)
    + "\n" + show("wm-dl", head(r.face(ra, a0), t0) + head(r.face(ra, a1), [-t1[0], -t1[1]]), arcAt + 0.9)
    + "\n" + counter((t) => deg * inkEase(Math.min(Math.max(t - arcAt, 0), 1)), arcAt, r.face(ra + 2.6, (a0 + a1) / 2)[0], r.face(ra + 2.6, (a0 + a1) / 2)[1] + 0.9, arcAt + 1, (v) => `${Math.round(v)}°`);

  // The fish: the picked segment comes out of the half, which fades to a phantom, and grows into one segment seen from the side,
  // a half-moon with its edges running tip to tip; its length is dimensioned and counts up as it grows.
  const FX = r.cx - 1, FY = CY, FL = 19, T = [FX - 1, FY - FL / 2], B = [FX - 1, FY + FL / 2], fishEase = bezier(0.65, 0, 0.35, 1);
  const edge = (x) => `M${pt(T)}C${pt([x, FY - 8])} ${pt([x, FY + 8])} ${pt(B)}`;
  const from = r.face(RI * 0.55, (a0 + a1) / 2), fx = FX + 10.2, fishEnd = FISH.at + FISH.duration;
  const fish = `<g class="wm-fish" style="transform-origin:${f(FX)}px ${FY}px">
<path class="wm-line wm-fishbody" d="${edge(FX + 9)}C${pt([FX + 1.6, FY + 7])} ${pt([FX + 1.6, FY - 7])} ${pt(T)}Z"/>
<path class="wm-line wm-ridge" d="${edge(FX + 4.6)}"/>
<path class="wm-line wm-vein" d="${[0.45, 0.75].map((k) => edge(FX + 4.6 + 4.4 * k)).join("")}"/>
<g class="wm-dim">
<path class="wm-line wm-dl" d="${[T, B].map(([, y]) => `M${pt([FX + 1.2, y])}L${pt([fx + 0.9, y])}`).join("")}M${pt([fx, T[1]])}L${pt([fx, B[1]])}${head([fx, T[1]], [0, 1])}${head([fx, B[1]], [0, -1])}"/>
${counter((t) => FL * (0.3 + 0.7 * fishEase(Math.min(Math.max((t - FISH.at) / FISH.duration, 0), 1))), FISH.at, fx - 1, FY, fishEnd, numberOf, -90)}
</g>
</g>`;
  const fishMotion = `@keyframes tangerine-fish{from{transform:translate(${f(from[0] - FX)}px,${f(from[1] - FY)}px) scale(.3);opacity:0}}`;

  const name = "Atlas 5.1 Tandem Tangerine", part = `${PART.duration}s cubic-bezier(${PART.easing.join(",")}) ${PART.delay}s both`;
  const desc = `Mandarinka ve stylu loga Atlasu jako technický výkres: celá se okótuje, praskne a napříč rozkrojené půlky se pomalu oddálí a natočí řezem k divákovi; kóty celkové délky a mezery mezi půlkami přitom napočítávají, dokud se nezastaví na ${numberOf(TOT)} a ${numberOf(GAP)}. Okótuje se i hloubka obou půlek a průměr řezu Ø ${2 * R}. Pak se na pravém řezu rozsvítí jeden dílek a okótuje se jeho úhel ${Math.round(deg)}°, pravá půlka pomalu vybledne do pomocných čar a dílek z ní vyroste jako samostatná rybička s kótou délky ${numberOf(FL)}.`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64" class="wm-tangerine" role="img" aria-label="${name}">
<title>${name}</title><desc>${desc}</desc>
<defs><linearGradient id="tangerine-gradient" x1="10" y1="6" x2="54" y2="60" gradientUnits="userSpaceOnUse"><stop stop-color="#7058de"/><stop offset=".52" stop-color="#4c78e5"/><stop offset="1" stop-color="#159daa"/></linearGradient></defs>
<style>
.wm-tangerine .wm-line{fill:none;stroke:url(#tangerine-gradient);stroke-width:1.5;stroke-linecap:round;stroke-linejoin:round}
.wm-tangerine .wm-dome{fill:url(#tangerine-gradient);fill-opacity:.07}
.wm-tangerine .wm-wire{stroke-width:.5}
.wm-tangerine .wm-ink{stroke-dasharray:100;animation:tangerine-ink 1s cubic-bezier(.45,0,.3,1) both}
.wm-tangerine .wm-l{animation:tangerine-part-l ${part}}
.wm-tangerine .wm-r{animation:tangerine-part-r ${part}}
.wm-tangerine .wm-stretch{transform-origin:${MID}px 0}
.wm-tangerine .wm-total{animation:tangerine-total ${part}}
.wm-tangerine .wm-face,.wm-tangerine .wm-gap{animation:tangerine-open ${part}}
.wm-tangerine .wm-depth{animation:tangerine-depth ${part}}
.wm-tangerine .wm-edge-l{animation:tangerine-edge-l ${part}}
.wm-tangerine .wm-edge-r{animation:tangerine-edge-r ${part}}
.wm-tangerine .wm-mid-l{animation:tangerine-mid-l ${part}}
.wm-tangerine .wm-mid-r{animation:tangerine-mid-r ${part}}
.wm-tangerine .wm-peel{stroke-width:1.2;fill:url(#tangerine-gradient);fill-opacity:.08}
.wm-tangerine .wm-inner{stroke-width:.6}
.wm-tangerine .wm-seg{stroke-width:.5;fill:url(#tangerine-gradient);fill-opacity:.16;animation:tangerine-show .5s ease both}
.wm-tangerine .wm-pith{stroke-width:.35}
.wm-tangerine .wm-pick{fill-opacity:.45}
.wm-tangerine .wm-ghost{opacity:.16;animation:tangerine-ghost 2s ease ${FISH.at}s both}
.wm-tangerine .wm-fish{animation:tangerine-fish ${FISH.duration}s cubic-bezier(.65,0,.35,1) ${FISH.at}s both}
.wm-tangerine .wm-fishbody{stroke-width:1.2;fill:url(#tangerine-gradient);fill-opacity:.16}
.wm-tangerine .wm-ridge{stroke-width:.6}
.wm-tangerine .wm-vein{stroke-width:.35}
.wm-tangerine .wm-crack{stroke-width:.7;opacity:0;animation:tangerine-crack 1s ease-out 1s}
.wm-tangerine .wm-dl{stroke-width:.35}
.wm-tangerine .wm-show{animation:tangerine-show .5s ease both}
.wm-tangerine .wm-label{font:500 2.6px ui-sans-serif,system-ui,sans-serif;fill:url(#tangerine-gradient)}
.wm-tangerine .wm-tick{opacity:0;animation-name:tangerine-tick;animation-timing-function:linear}
.wm-tangerine .wm-hold{animation:tangerine-show 1ms both}
.wm-tangerine .wm-glint{stroke:#fff;stroke-width:1.8;stroke-dasharray:5 95;opacity:0;fill:none;filter:drop-shadow(0 0 .6px #ffffffb3);animation:tangerine-pass 14s linear 9s infinite}
@keyframes tangerine-ink{0%{stroke-dashoffset:100;opacity:0}12%{opacity:1}100%{stroke-dashoffset:0;opacity:1}}
${keyframes("tangerine-part-l", (x) => `translateX(${f(x)}px)`)}
${keyframes("tangerine-part-r", (x) => `translateX(${f(-x)}px)`)}
${keyframes("tangerine-total", (x) => `scaleX(${f((TOT - 2 * x) / TOT)})`)}
${keyframes("tangerine-open", (x) => `scaleX(${f(open(x))})`)}
${keyframes("tangerine-depth", (x) => `scaleX(${f(depth(x) / depth(0))})`)}
${[-1, 1].map((s) => `${keyframes(`tangerine-edge-${s < 0 ? "l" : "r"}`, (x) => `translateX(${f(s * (depth(0) - depth(x)))}px)`)}
${keyframes(`tangerine-mid-${s < 0 ? "l" : "r"}`, (x) => `translateX(${f(s * (depth(0) - depth(x)) / 2)}px)`)}`).join("\n")}
@keyframes tangerine-crack{0%{stroke-dashoffset:100;opacity:1}30%{stroke-dashoffset:0;opacity:1}60%,100%{stroke-dashoffset:0;opacity:0}}
@keyframes tangerine-show{from{opacity:0}}
@keyframes tangerine-ghost{from{opacity:1}}
${fishMotion}
@keyframes tangerine-tick{from,to{opacity:1}}
@keyframes tangerine-pass{0%{stroke-dashoffset:0;opacity:0}2%,18%{opacity:.9}21%,100%{stroke-dashoffset:-100;opacity:0}}
@media(prefers-reduced-motion:reduce){.wm-tangerine *{animation:none!important}.wm-tangerine .wm-glint{display:none}}
[data-still] .wm-tangerine *{animation:none!important}[data-still] .wm-tangerine .wm-glint{display:none}
.release-mark[data-variant="icon"] .wm-tangerine .wm-dim{display:none}
</style>
${drawHalf(l)}
${drawHalf(r)}
<path class="wm-line wm-crack" pathLength="100" stroke-dasharray="100" d="${crack}"/>
<g class="wm-dim">
${overall}
${between}
${diameter}
${angle}
</g>
${fish}
</svg>
`;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  const out = process.argv[2] ?? fileURLToPath(new URL("../../src/Atlas.Web/src/assets/brand/tangerine.svg", import.meta.url));
  const svg = mark();
  writeFileSync(out, svg);
  console.log(`${out}: ${svg.length} B`);
}
