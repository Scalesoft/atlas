// The wireframe mark of a minor or major release (AGENTS.md, Branches and releases): a body of revolution drawn in the line style of the Bloom
// logo (assets/brand/bloom.svg: the gradient #7058de → #4c78e5 → #159daa, round line ends), animated inside the SVG with transform,
// opacity and stroke-dashoffset only. A release supplies only the geometry (`profile`, `grow`, `open`); the style and the motion are
// the same for every release:
//   0 s        the outline draws itself;
//   ~0.9 s     the wireframe unfolds: the meridians open from the axis (the centre first), the latitudes run in from the bottom up;
//   ~1.8 s     what grows (a stem) grows from its base, what opens (a leaf) opens and rocks gently;
//   2.6–6 s    light runs along the outline and the meridians, faster and closer each time, and dies away;
//   then       the mark sways slowly and a soft light passes along the outline now and then (as Bloom's).
// `fade` (4.0 „Quiet Quince“) replaces the quickening light and the sway: three slow soft lights pass and each takes some of the mark
// with it, until only a trace stays, lit now and then by the passing light.
// `radiant` (4.1 „Radiant Raspberry“) with `drupelets`: the body is a cluster of small round drupelets; once they have popped in, two
// calm ripples of light run from the centre outwards (each drupelet lights up in turn, short rays flicker around the fruit) and the
// whole mark dissolves into the light and is gone (data-vanishes: where it must stay visible, ReleaseMark shows it still). Under reduced motion the drupelets only brighten once, by opacity.
// `prefers-reduced-motion: reduce`, or a `[data-still]` ancestor (Atlas's own „Omezit animace“), shows the final state at once.
// Atlas draws the mark inline (components/release/ReleaseMark.vue), so its motion starts when it appears: an <img> would run on the
// page's clock and show a finished mark. Its rules, keyframes and gradient are therefore namespaced by the mark's `id`.
// Write the asset with a script beside this one (pear-mark.mjs).

const f = (n) => Number(n.toFixed(2));
const path = (pts) => "M" + pts.map(([x, y]) => `${f(x)} ${f(y)}`).join("L");
const xml = (text) => text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/"/g, "&quot;");

/** Catmull-Rom through the profile points [[t from the top 0..1, radius]], never below 0. */
function radiusOf(profile) {
  return (t) => {
    const i = profile.findIndex(([x]) => x >= t);
    if (i <= 0) return profile[0][1];
    const p = (k) => profile[Math.max(0, Math.min(profile.length - 1, k))];
    const [x1, y1] = p(i - 1), [x2, y2] = p(i), y0 = p(i - 2)[1], y3 = p(i + 1)[1];
    const u = (t - x1) / (x2 - x1);
    return Math.max(0, 0.5 * (2 * y1 + (-y0 + y2) * u + (2 * y0 - 5 * y1 + 4 * y2 - y3) * u * u + (-y0 + 3 * y1 - 3 * y2 + y3) * u * u * u));
  };
}

/**
 * @param {object} mark
 * @param {string} mark.id          the mark's name (`mark:` in the release notes, assets/brand/<id>.svg)
 * @param {string} mark.label      accessible name of the standalone SVG ("Atlas 3.5 Performant Pear"); inside Atlas the mark is decoration
 * @param {string} [mark.desc]      what it shows, for the standalone SVG
 * @param {number[][]} mark.profile [t, radius] from the top (t 0) to the bottom (t 1) of the body, in viewBox units (64 × 64)
 * @param {number} [mark.top]       y of the top of the body; `bottom` likewise
 * @param {number[]} [mark.meridians] angles in degrees, symmetric around 0
 * @param {number[]} [mark.latitudes] t of each latitude ring
 * @param {{ d: string, origin: [number, number] }[]} [mark.grow] lines that grow from `origin` (a stem)
 * @param {{ d: string, rib?: string, origin: [number, number] }[]} [mark.open] closed shapes that open around `origin` (a leaf), with an optional rib
 * @param {boolean} [mark.fade]   the quiet finale: the mark fades away with slow lights instead of quickening light and sway
 * @param {boolean} [mark.dots]   with `fade`: a grid of dots on the body instead of the wires; the dots go out one by one as the lights pass
 * @param {boolean} [mark.drupelets] the body as a cluster of round drupelets (rows around the surface) instead of the wires
 * @param {boolean} [mark.radiant]   with `drupelets`: the finale of two ripples of light from the centre, short rays, no sway
 * @returns {string} the SVG
 */
export function wireframeMark({ id, label, desc = "", profile, top = 17, bottom = 56, cx = 32, tilt = 0.26, meridians = [-58, -28, 0, 28, 58], latitudes = [0.38, 0.56, 0.73, 0.87], grow = [], open = [], fade = false, dots = false, drupelets = false, radiant = false }) {
  const radius = radiusOf(profile), yAt = (t) => top + (bottom - top) * t, N = 60;

  // Silhouette: the sides down to the widest latitude, then the true lower contour of the body seen a little from above.
  let tMax = 0; for (let i = 0; i <= 400; i++) if (radius(i / 400) > radius(tMax)) tMax = i / 400;
  const rMax = radius(tMax), left = [], right = [], lower = [];
  for (let i = 0; i <= N; i++) { const t = tMax * i / N, r = radius(t); left.push([cx - r, yAt(t)]); right.unshift([cx + r, yAt(t)]); }
  for (let j = 1; j < N; j++) {
    const x = -rMax + 2 * rMax * j / N; let y = -Infinity;
    for (let i = 0; i <= 400; i++) { const t = i / 400, r = radius(t); if (r >= Math.abs(x)) y = Math.max(y, yAt(t) + tilt * Math.sqrt(r * r - x * x)); }
    lower.push([cx + x, y]);
  }
  const silhouette = path([...left, ...lower, ...right]) + "Z";

  // Meridians: the front half of the surface, seen a little from above.
  const meridianPaths = meridians.map((deg) => {
    const s = Math.sin(deg * Math.PI / 180), c = Math.cos(deg * Math.PI / 180), pts = [];
    for (let i = 2; i <= N - 1; i++) { const t = i / N, r = radius(t); pts.push([cx + r * s, yAt(t) + r * tilt * c]); }
    return path(pts);
  });
  // Latitudes: ellipses; the front arc drawn, the back arc faint.
  const arc = (t, from, to) => { const r = radius(t), pts = []; for (let i = 0; i <= 40; i++) { const a = from + (to - from) * i / 40; pts.push([cx + r * Math.sin(a), yAt(t) + r * tilt * Math.cos(a)]); } return path(pts); };
  const front = latitudes.map((t) => arc(t, -Math.PI / 2, Math.PI / 2));
  const back = latitudes.map((t) => arc(t, Math.PI / 2, 3 * Math.PI / 2));
  // Dots: the front of the body as a grid of dots, larger where they face the viewer.
  const grid = [];
  if (dots) for (let a = -78; a <= 78; a += 13) for (let j = 0; j < 12; j++) {
    const t = 0.15 + 0.072 * j, r = radius(t), c = Math.cos(a * Math.PI / 180);
    if (r >= 2) grid.push({ x: cx + r * Math.sin(a * Math.PI / 180), y: yAt(t) + r * tilt * c, size: 0.3 + 0.5 * c, t, a });
  }

  // Drupelets: rows around the body, the front half only, each row staggered half a step; `lit` is the twin that glows, `rays` flicker outside.
  const berries = [], rays = [], cy = yAt(0.45);
  if (drupelets) {
    for (let j = 0; j < 6; j++) {
      // Honeycomb rows; the outer drupelets sit on the outline itself, so the outline is the bumpy edge of the cluster.
      const t = 0.12 + 0.155 * j, size = 3.2, re = Math.max(0, radius(t) - size * 0.6), step = re < size ? Infinity : 1.94 * size / re;
      for (let k = -6; k <= 6; k++) {
        const a = (k + (j % 2) / 2) * (step === Infinity ? 0 : step);
        if (step === Infinity ? k === 0 : Math.abs(a) <= Math.PI / 2 + 0.05) berries.push({ x: cx + re * Math.sin(a), y: yAt(t) + re * tilt * Math.cos(a), size, t, a });
      }
    }
    for (const phi of [-150, -120, -90, -60, -30, 30, 60, 90, 120, 150]) {
      const s = Math.sin(phi * Math.PI / 180), c = Math.cos(phi * Math.PI / 180); let d = 0;
      for (; d < 40; d += 0.25) { const t = (cy + d * c - top) / (bottom - top); if (t < 0 || t > 1 || Math.abs(d * s) > radius(t)) break; }
      rays.push([[cx + (d + 3.4) * s, cy + (d + 3.4) * c], [cx + (d + 5.6) * s, cy + (d + 5.6) * c]]);
    }
  }

  // Timeline (s), approved on the pear (3.5). The meridians open from the centre outwards, the latitudes from the bottom up.
  const centre = (meridians.length - 1) / 2;
  const meridianDelay = meridians.map((_, i) => { const d = Math.round(Math.abs(i - centre)); return 0.9 + 0.025 * d * (d + 1); });
  const byDepth = latitudes.map((t, i) => [t, i]).sort((a, b) => b[0] - a[0]);
  const latitudeDelay = latitudes.map((_, i) => 1.25 + 0.1 * byDepth.findIndex(([, k]) => k === i));
  const growAt = 1.8, openAt = 2.05, step = 0.25, fast = 2.6, fastDuration = fade ? 10 : 3.4, sway = 2.6, glint = fast + fastDuration + 0.2;

  // The "performant" moment: light passes along the wires, closer and faster each time, then dies away.
  // The quiet one: three slow lights, after each the mark is fainter (`hush`), down to a trace.
  const beats = fade ? [0, 0.36, 0.72] : [0, 0.3, 0.52, 0.67, 0.78, 0.86, 0.92, 0.96, 0.985];
  const passLength = (b, i) => Math.min(0.22, ((i + 1 < beats.length ? beats[i + 1] : 1) - b) * 0.9);
  const accel = beats.flatMap((b, i) => {
    const len = passLength(b, i);
    return [`${f(b * 100)}%{stroke-dashoffset:0;opacity:0}`, `${f((b + len * 0.15) * 100)}%{opacity:${fade ? ".75" : ".95"}}`, `${f(Math.min(100, (b + len) * 100))}%{stroke-dashoffset:-100;opacity:0}`];
  }).join("");
  const levels = [1, 0.68, 0.42, 0.24];
  const hush = beats.map((b, i) => `${f(b * 100)}%{opacity:${levels[i]}}${f((b + passLength(b, i)) * 100)}%{opacity:${levels[i + 1]}}`).join("") + `100%{opacity:${levels[3]}}`;
  const ease = "cubic-bezier(.22,1,.36,1)", unfold = "cubic-bezier(.16,1,.3,1)";
  // Each dot pops in from the bottom up; three quarters go out during the three lights, a quarter dims and stays.
  const pts = grid.map(({ x, y, size, t, a }, k) => {
    const h = (Math.imul(k + 1, 2654435761) >>> 0) / 2 ** 32, wave = Math.floor(h * 4), pop = 0.9 + 0.7 * (1 - t) + 0.004 * Math.abs(a);
    const out = wave < 3 ? fast + (beats[wave] + passLength(beats[wave], wave) * (0.2 + (h * 4 - wave) * 0.8)) * fastDuration : fast + fastDuration * (0.94 + h * 0.05);
    return `<circle class="wm-pt${wave < 3 ? "" : " wm-stay"}" cx="${f(x)}" cy="${f(y)}" r="${f(size)}" style="animation-delay:${f(pop)}s,${f(out)}s"/>`;
  }).join("\n");
  const rippleAt = 3, reach = Math.max(...berries.map((b) => Math.hypot(b.x - cx, b.y - cy)), 1);
  const ripple = (x, y) => f(rippleAt + 1.1 * Math.hypot(x - cx, y - cy) / reach);
  const fruit = berries.map(({ x, y, size, t, a }) => `<circle class="wm-dr" cx="${f(x)}" cy="${f(y)}" r="${f(size)}" style="animation-delay:${f(0.9 + 0.9 * (1 - t) + 0.15 * Math.abs(a))}s"/>`).join("\n");
  const shine = berries.map(({ x, y, size, t, a }) => `<path class="wm-hl" d="M${f(x - size * 0.62)} ${f(y - size * 0.12)}A${f(size * 0.64)} ${f(size * 0.64)} 0 0 1 ${f(x - size * 0.12)} ${f(y - size * 0.62)}" style="animation-delay:${f(0.9 + 0.9 * (1 - t) + 0.15 * Math.abs(a))}s"/>`).join("\n");
  const lit = berries.map(({ x, y, size }) => `<circle class="wm-lit" cx="${f(x)}" cy="${f(y)}" r="${f(size * 0.72)}" style="animation-delay:${ripple(x, y)}s"/>`).join("\n");
  const sparks = rays.map(([[x1, y1], [x2, y2]]) => `<path class="wm-line wm-ray" d="M${f(x1)} ${f(y1)}L${f(x2)} ${f(y2)}" style="transform-origin:${f(x1)}px ${f(y1)}px;animation-delay:${ripple(x2, y2)}s"/>`).join("\n");
  const line = (cls, d) => `<path class="wm-line ${cls}" pathLength="100" d="${d}"/>`;
  const origin = ([x, y]) => `transform-origin:${f(x)}px ${f(y)}px`;

  const body = `${back.map((d, i) => line(`wm-mesh wm-back wm-base wm-l${i}`, d)).join("\n")}
${line("wm-shape wm-base", silhouette)}
${meridianPaths.map((d, i) => line(`wm-mesh wm-base wm-m${i}`, d)).join("\n")}
${front.map((d, i) => line(`wm-mesh wm-base wm-l${i}`, d)).join("\n")}`;
  const glints = `${line("wm-glint", silhouette)}
${line("wm-glint wm-fast", silhouette)}
${meridianPaths.map((d, i) => line(`wm-glint wm-fast wm-fm${i}`, d)).join("\n")}`;
  // The quiet finale: the body fades in a group of its own, the lights stay outside it, nothing sways or rocks.
  const quiet = `<g class="wm-hush">
${dots ? line("wm-shape wm-base wm-faint", silhouette) : body}
${grow.map((g, i) => `<g class="wm-grow${i}">${line(`wm-base wm-g${i}`, g.d)}</g>`).join("\n")}
${open.map((o, i) => `<g class="wm-open${i}">${line(`wm-shape wm-base wm-o${i}`, o.d)}${o.rib ? line(`wm-mesh wm-base wm-r${i}`, o.rib) : ""}</g>`).join("\n")}
</g>
${pts}
${glints}
${open.map((o, i) => line(`wm-glint wm-og${i}`, o.d)).join("\n")}`;

  // The radiant finale: the calyx is drawn over the fruit; nothing sways.
  const radiantBody = `<g class="wm-body">
${line("wm-shape wm-base wm-faint", silhouette)}
${fruit}
${shine}
${lit}
${grow.map((g, i) => `<g class="wm-grow${i}">${line(`wm-base wm-g${i}`, g.d)}</g>`).join("\n")}
${open.map((o, i) => `<g class="wm-open${i}">${line(`wm-shape wm-base wm-o${i} wm-calyx`, o.d)}</g>`).join("\n")}
</g>
${sparks}`;

  const radiantStill = ".wm .wm-lit{animation:wm-glow 1.6s ease-in-out .3s 1 both!important}";
  const still = ".wm *{animation:none!important;transform:none!important}.wm .wm-base{stroke-dasharray:none;stroke-dashoffset:0;opacity:1}.wm .wm-back{opacity:.3}.wm .wm-glint{display:none}";
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64" class="wm"${radiant ? ' data-vanishes' : ""} role="img" aria-label="${xml(label)}">
<title>${xml(label)}</title>${desc ? `<desc>${xml(desc)}</desc>` : ""}
<defs><linearGradient id="wm-gradient" x1="10" y1="6" x2="54" y2="60" gradientUnits="userSpaceOnUse"><stop stop-color="#7058de"/><stop offset=".52" stop-color="#4c78e5"/><stop offset="1" stop-color="#159daa"/></linearGradient></defs>
<style>
.wm .wm-line{fill:none;stroke:url(#wm-gradient);stroke-width:1.5;stroke-linecap:round;stroke-linejoin:round}
.wm .wm-shape{stroke-width:1.8;fill:url(#wm-gradient);fill-opacity:.07}
.wm .wm-mesh{stroke-width:.75;transform-origin:${cx}px ${f(yAt(0.59))}px}
.wm .wm-back{stroke-width:.55;opacity:.28}
.wm .wm-base{stroke-dasharray:100;animation:wm-ink 1.5s ${ease} both}
.wm .wm-shape.wm-base{animation-duration:1.7s}
.wm .wm-mesh.wm-base{animation:wm-ink 1.1s ${ease} both,wm-unfold 1.2s ${unfold} both}
.wm .wm-back.wm-base{animation-name:wm-ink-faint,wm-unfold}
${meridianDelay.map((d, i) => `.wm .wm-base.wm-m${i}{animation-delay:${f(d)}s,${f(d)}s}`).join("\n")}
${latitudeDelay.map((d, i) => `.wm .wm-base.wm-l${i}{animation-delay:${f(d)}s,${f(d)}s}`).join("\n")}
${grow.map((g, i) => `.wm .wm-grow${i}{${origin(g.origin)};animation:wm-grow .9s cubic-bezier(.34,1.56,.64,1) ${f(growAt + i * step)}s both}
.wm .wm-base.wm-g${i}{animation-delay:${f(growAt + 0.05 + i * step)}s}`).join("\n")}
${open.map((o, i) => `.wm .wm-open${i}{${origin(o.origin)};animation:wm-open 1.4s ${unfold} ${f(openAt + i * step)}s both}
${fade || radiant ? "" : `.wm .wm-rock${i}{${origin(o.origin)};animation:wm-rock 23s ease-in-out ${f(3 + i * step)}s infinite}
`}.wm .wm-base.wm-o${i}{animation-delay:${f(openAt + 0.1 + i * step)}s}
.wm .wm-base.wm-r${i}{animation-delay:${f(openAt + 0.15 + i * step)}s,${f(openAt + 0.15 + i * step)}s}
.wm .wm-glint.wm-og${i}{animation-delay:${f(glint + 11 + i * step)}s}`).join("\n")}
.wm .wm-glint{stroke:#fff;stroke-width:2.6;stroke-dasharray:5 95;opacity:0;fill:none;filter:drop-shadow(0 0 .65px #ffffffb3);animation:wm-pass 22s linear ${f(glint)}s infinite}
.wm .wm-glint.wm-fast{stroke-width:${fade ? "2.2;stroke-dasharray:16 84" : "1.6;stroke-dasharray:9 91"};animation:wm-accel ${fastDuration}s ${fade ? "ease-in-out" : "linear"} ${fast}s both}
${meridians.map((_, i) => `.wm .wm-fast.wm-fm${i}{animation-delay:${f(fast + Math.round(Math.abs(i - centre)) * 0.05)}s}`).join("\n")}
${radiant ? "" : fade ? `.wm .wm-hush{animation:wm-hush ${fastDuration}s cubic-bezier(.45,0,.55,1) ${fast}s both}` : `.wm .wm-sway{transform-origin:${cx}px ${f(bottom + 2)}px;animation:wm-sway 31s ease-in-out ${sway}s infinite}`}
${dots ? `.wm .wm-faint{stroke-width:1;animation-name:wm-ink-faint}
.wm .wm-pt{fill:url(#wm-gradient);transform-box:fill-box;transform-origin:center;animation:wm-dot .7s cubic-bezier(.34,1.56,.64,1) both,wm-vanish 1.6s ease-in-out forwards}
.wm .wm-pt.wm-stay{animation-name:wm-dot,wm-dim}
@keyframes wm-dot{from{transform:scale(0)}to{transform:scale(1)}}
@keyframes wm-vanish{to{opacity:0}}
@keyframes wm-dim{to{opacity:.4}}
` : ""}${drupelets ? `.wm .wm-faint{stroke-width:1;animation-name:wm-ink-faint}
.wm .wm-dr,.wm .wm-hl,.wm .wm-lit{transform-box:fill-box;transform-origin:center}
.wm .wm-dr{fill:url(#wm-gradient);fill-opacity:.3;stroke:url(#wm-gradient);stroke-width:.75}
.wm .wm-dr,.wm .wm-hl{animation:wm-dot .7s cubic-bezier(.34,1.56,.64,1) both}
.wm .wm-hl{fill:none;stroke:#fff;stroke-opacity:.65;stroke-width:.55}
.wm .wm-calyx{stroke-width:1.2}
.wm .wm-lit{fill:url(#wm-gradient);opacity:0;animation:wm-ripple 6s ease-in-out both}
.wm .wm-ray{stroke-width:.9;stroke-opacity:.6;opacity:0;animation:wm-ripple 6s ease-in-out both}
.wm .wm-body{transform-origin:${cx}px ${f(cy)}px;animation:wm-melt 2.6s cubic-bezier(.4,0,.6,1) ${f(rippleAt + 4.4)}s both}
@keyframes wm-dot{from{transform:scale(0)}to{transform:scale(1)}}
@keyframes wm-ripple{0%{opacity:0;transform:scale(.8)}8%{opacity:.5;transform:scale(1.1)}18%,28%{opacity:0;transform:scale(1.2)}36%{opacity:.8;transform:scale(1.15)}46%,54%{opacity:0;transform:scale(1.25)}62%,72%{opacity:1;transform:scale(1.2)}84%,100%{opacity:0;transform:scale(1.3)}}
@keyframes wm-melt{to{opacity:0;transform:scale(1.07)}}
@keyframes wm-glow{0%,100%{opacity:0}40%{opacity:.5}}
` : ""}@keyframes wm-ink{0%{stroke-dashoffset:100;opacity:0}12%{opacity:1}100%{stroke-dashoffset:0;opacity:1}}
@keyframes wm-ink-faint{0%{stroke-dashoffset:100;opacity:0}100%{stroke-dashoffset:0;opacity:.3}}
@keyframes wm-unfold{from{transform:scaleX(.04) translateY(3px)}to{transform:none}}
@keyframes wm-grow{from{transform:scaleY(0)}to{transform:scaleY(1)}}
@keyframes wm-open{from{transform:rotate(28deg) scale(.7);opacity:0}to{transform:rotate(0deg) scale(1);opacity:1}}
@keyframes wm-rock{0%,100%{transform:rotate(0deg)}45%{transform:rotate(-3deg)}}
@keyframes wm-accel{${accel}}
@keyframes wm-pass{0%{stroke-dashoffset:0;opacity:0}3%,17%{opacity:.9}20%,100%{stroke-dashoffset:-100;opacity:0}}
${fade ? `@keyframes wm-hush{${hush}}` : "@keyframes wm-sway{0%,100%{transform:rotate(0deg)}38%{transform:rotate(.6deg)}74%{transform:rotate(-.4deg)}}"}
@media(prefers-reduced-motion:reduce){${still}${radiant ? radiantStill : ""}}
${still.replace(/(^|\})\.wm/g, "$1[data-still] .wm")}
</style>
${radiant ? radiantBody : fade ? quiet : `<g class="wm-sway">
${body}
${glints}
${grow.map((g, i) => `<g class="wm-grow${i}">${line(`wm-base wm-g${i}`, g.d)}</g>`).join("\n")}
${open.map((o, i) => `<g class="wm-open${i}"><g class="wm-rock${i}">${line(`wm-shape wm-base wm-o${i}`, o.d)}${o.rib ? line(`wm-mesh wm-base wm-r${i}`, o.rib) : ""}${line(`wm-glint wm-og${i}`, o.d)}</g></g>`).join("\n")}
</g>`}
</svg>
`;
  // One namespace per mark: two marks inline on one page keep their own timing, keyframes and gradient.
  return svg.replace('class="wm"', `class="wm-${id}"`).replaceAll(".wm ", `.wm-${id} `).replaceAll("wm-gradient", `${id}-gradient`)
    .replace(/\bwm-(ink-faint|ink|unfold|grow|open|rock|accel|pass|sway|hush|dot|vanish|dim|ripple|melt|glow)(?![\w-])/g, `${id}-$1`);
}
