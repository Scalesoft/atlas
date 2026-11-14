import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  animateAtlas,
  collapseHeight,
  dismissOut,
  drawIcon,
  dropFromTrigger,
  extendReveal,
  flashRow,
  flyGhost,
  foldOut,
  glow,
  grow,
  literalMs,
  travelTiming,
  markerIn,
  meterFill,
  morphInto,
  motionMs,
  motionTokenFallbacks,
  popStatus,
  PROTOTYPE_MS,
  relocate,
  revealHeight,
  revealSentences,
  rippleOnce,
  RISE_IN_ROWS,
  revealWave,
  riseIn,
  scrollBehavior,
  solidify,
  unfoldedClip,
  unfoldFrom,
} from "./aiMotion";

interface Call {
  el: Element;
  frames: Keyframe[];
  options: KeyframeAnimationOptions;
  /** What the DOM looked like when the animation started. */
  snapshot: { connected: boolean; hidden: boolean; inert: boolean; dash: string };
}

let calls: Call[] = [];
let reduced = false;

beforeEach(() => {
  calls = [];
  reduced = false;
  vi.mocked(window.matchMedia).mockImplementation((query: string) => ({
    matches: query.includes("prefers-reduced-motion") ? reduced : false,
    media: query,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  }) as unknown as MediaQueryList);
  Element.prototype.animate = vi.fn(function (this: Element, frames: Keyframe[], options: KeyframeAnimationOptions) {
    calls.push({
      el: this,
      frames,
      options,
      snapshot: {
        connected: this.isConnected,
        hidden: (this as HTMLElement).hidden === true,
        inert: this.hasAttribute("inert"),
        dash: (this as SVGElement).style?.strokeDasharray ?? "",
      },
    });
    return { finished: Promise.resolve(), cancel: vi.fn() } as unknown as Animation;
  }) as unknown as Element["animate"];
});

afterEach(() => {
  document.body.replaceChildren();
  delete (Element.prototype as Partial<Element>).animate;
});

function element(html: string) {
  const host = document.createElement("div");
  host.className = "atlas";
  host.innerHTML = html.trim();
  document.body.append(host);
  return host.firstElementChild as HTMLElement;
}

const keys = (frames: Keyframe[]) => [...new Set(frames.flatMap((frame) => Object.keys(frame)))].sort();

describe("aiMotion", () => {
  it("keeps the final icon state without resolving motion styles when Web Animations is unavailable", () => {
    delete (Element.prototype as Partial<Element>).animate;
    const svg = element('<svg><path d="M2 5L4 7L8 2"/></svg>') as unknown as SVGElement;
    const shape = svg.querySelector("path")!;
    shape.style.strokeDashoffset = "40";
    const computedStyle = vi.spyOn(window, "getComputedStyle");
    try {
      expect(animateAtlas(svg, [{ opacity: 0 }, { opacity: 1 }])).toBeNull();
      expect(drawIcon(svg)).toEqual([]);
      expect(shape.style.strokeDasharray).toBe("40");
      expect(shape.style.strokeDashoffset).toBe("0");
      expect(computedStyle).not.toHaveBeenCalled();
    } finally {
      computedStyle.mockRestore();
    }
  });

  it("applies the final state before animating, so content is ready at once", () => {
    const panel = element("<div hidden>Odpověď</div>");
    revealHeight(panel);
    expect(calls.at(-1)!.snapshot.hidden).toBe(false);

    const list = element("<ul></ul>");
    grow(list, () => list.append(document.createElement("li")));
    // grow measures a height change; jsdom has no layout, so it is not animated, but the mutation ran.
    expect(list.children).toHaveLength(1);

    const rect = element('<svg><rect width="10" height="10"/></svg>').querySelector("rect")!;
    solidify(rect);
    expect(calls.at(-1)!.snapshot.dash).toBe("11 0");
    expect(rect.style.strokeDasharray).toBe("11 0");

    const span = element("<p>Kontaktujte <span>Jana Nováka</span>.</p>").querySelector("span")!;
    const chip = document.createElement("span");
    chip.textContent = "Jan Novák";
    morphInto(span, chip);
    expect(span.isConnected).toBe(false);
    expect(calls.find((call) => call.el === chip)!.snapshot.connected).toBe(true);

    // Entrances never leave a fill behind: once they end, the element shows its own final styles.
    for (const call of calls) expect(call.options.fill === "both" || call.options.fill === "forwards").toBe(false);

    // Without the Web Animations API nothing animates, and the state is still final.
    delete (Element.prototype as Partial<Element>).animate;
    const later = element("<div hidden></div>");
    expect(revealHeight(later)).toBeNull();
    expect(later.hidden).toBe(false);
  });

  it("unfolds on to content that grew while it unfolded, from where it is, in the time it has left", () => {
    const tree = element("<div hidden></div>");
    let height = 30;
    Object.defineProperty(tree, "offsetHeight", { configurable: true, get: () => height });
    revealHeight(tree);
    // clip, not hidden: a hidden element is a scroll container, and a row scrolled into view inside it jumped back.
    expect(calls.at(-1)!.frames.map((frame) => frame.overflow)).toEqual(["clip", "clip"]);

    height = 12; // the animated height now; the pages that loaded meanwhile are three hundred pixels tall
    const cancel = vi.fn(() => { height = 300; });
    const unfolding = { playState: "running", currentTime: 100, effect: { getComputedTiming: () => ({ endTime: 280 }) }, cancel } as unknown as Animation;
    expect(extendReveal(tree, unfolding)).not.toBeNull();
    expect(cancel).toHaveBeenCalledTimes(1);
    expect(calls.at(-1)!.frames).toMatchObject([{ height: "12px", overflow: "clip" }, { height: "300px", opacity: 1, overflow: "clip" }]);
    expect(calls.at(-1)!.options).toMatchObject({ duration: 180, fill: "none" });

    // A finished unfolding has nothing left to extend; reduced motion never clipped the content.
    expect(extendReveal(tree, { ...unfolding, playState: "finished" } as unknown as Animation)).toBeNull();
    reduced = true;
    expect(extendReveal(tree, unfolding)).toBeNull();
    expect(cancel).toHaveBeenCalledTimes(1);
  });

  it("takes a leaving element out of use at once and keeps it gone until the caller removes it", () => {
    const chip = element("<button>Návrh</button>");
    dismissOut(chip);
    const call = calls.at(-1)!;
    expect(call.snapshot.inert).toBe(true);
    expect(chip.getAttribute("aria-hidden")).toBe("true");
    expect(call.options.fill).toBe("forwards");
    expect(call.frames.at(-1)).toMatchObject({ opacity: 0, transform: "scale(.6)" });
    expect(call.options.duration).toBe(160);

    const row = element("<div>Řádek</div>");
    collapseHeight(row);
    expect(calls.at(-1)!.snapshot.inert).toBe(true);
    expect(calls.at(-1)!.options).toMatchObject({ duration: 240, fill: "forwards" });
  });

  it("folds and unfolds a content-box element from its height without padding and border, so it never first grows (N9)", () => {
    const row = element("<div>Řádek</div>");
    row.style.cssText = "box-sizing: content-box; padding: 6px 0 4px; border-top: 1px solid";
    Object.defineProperty(row, "offsetHeight", { configurable: true, get: () => 45 });
    collapseHeight(row);
    expect(calls.at(-1)!.frames[0]).toMatchObject({ height: "34px" });
    revealHeight(row);
    expect(calls.at(-1)!.frames.at(-1)).toMatchObject({ height: "34px" });
    row.style.boxSizing = "border-box";
    collapseHeight(row);
    expect(calls.at(-1)!.frames[0]).toMatchObject({ height: "45px" });
  });

  it("reduces keyframes to opacity, background and colour for reduced motion and caps them at 160 ms", () => {
    const el = element("<div></div>");
    reduced = true;
    animateAtlas(el, [
      { opacity: 0, transform: "translateY(6px)", filter: "blur(4px)", color: "red" },
      { opacity: 1, transform: "none", filter: "blur(0)", offset: 0.7, backgroundColor: "blue" },
    ], { duration: 420, delay: 130 });
    expect(calls[0]!.frames).toEqual([{ opacity: 0, color: "red" }, { opacity: 1, offset: 0.7, backgroundColor: "blue" }]);
    expect(calls[0]!.options).toMatchObject({ duration: 160, delay: 130, easing: "linear" });

    // Pure movement keeps a 160 ms no-op, so callers that await it still resolve on time.
    animateAtlas(el, [{ transform: "scale(.9)" }, { transform: "none" }], { duration: 100 });
    expect(calls[1]!.frames).toEqual([{ opacity: 1 }, { opacity: 1 }]);
    expect(calls[1]!.options.duration).toBe(100);

    // The preference is read on every call, so a change applies at once.
    reduced = false;
    animateAtlas(el, [{ transform: "scale(.9)" }, { transform: "none" }], { duration: 420 });
    expect(calls[2]!.frames).toEqual([{ transform: "scale(.9)" }, { transform: "none" }]);
    expect(calls[2]!.options).toMatchObject({ duration: 420, easing: "cubic-bezier(.22, 1, .36, 1)" });
  });

  it("turns the glow into a background-only glowfade of the same length for reduced motion", () => {
    const el = element("<span></span>");
    glow(el);
    expect(keys(calls[0]!.frames)).toEqual(["backgroundColor", "boxShadow", "offset"]);
    reduced = true;
    glow(el);
    expect(keys(calls[1]!.frames)).toEqual(["backgroundColor", "offset"]);
    expect(calls[1]!.frames[0]).toMatchObject({ backgroundColor: "var(--atlas-ai-glow-color)" });
    expect(calls[1]!.options.duration).toBe(calls[0]!.options.duration);
    expect(calls[1]!.options.duration).toBe(1600);
  });

  it("uses the Atlas tokens for durations and easing", () => {
    const host = element("<div></div>");
    for (const [name, value] of Object.entries({
      "--atlas-ai-reveal": "421ms", "--atlas-ai-stagger": "131ms", "--atlas-ai-gather": "521ms",
      "--atlas-ai-morph": "381ms", "--atlas-ai-glow": "1.7s", "--atlas-ai-solidify": "361ms",
      "--atlas-motion-slow": "281ms", "--atlas-motion-base": "201ms", "--atlas-motion-surface": "161ms",
      "--atlas-ease-out": "cubic-bezier(.1, .2, .3, .4)", "--atlas-ease-inout": "cubic-bezier(.5, .6, .7, .8)",
    })) host.style.setProperty(name, value);
    // Tokens are inherited from the surrounding .atlas, as in the app.
    const child = (html: string) => {
      const template = document.createElement("template");
      template.innerHTML = html;
      const node = template.content.firstElementChild as HTMLElement;
      host.append(node);
      return node;
    };

    revealSentences(child("<p><span>Jedna.</span> <span>Dvě.</span></p>"));
    expect(calls.map((call) => [call.options.duration, call.options.delay, call.options.easing])).toEqual([
      [421, 0, "cubic-bezier(.1, .2, .3, .4)"], [421, 131, "cubic-bezier(.1, .2, .3, .4)"],
    ]);
    expect(calls[0]!.frames).toEqual([
      { opacity: 0, filter: "blur(4px)", transform: "translateY(6px)" }, { opacity: 1, filter: "blur(0)", transform: "none" },
    ]);

    calls = [];
    solidify(child('<svg><rect/></svg>').querySelector("rect")!);
    glow(child("<span></span>"));
    revealHeight(child("<div></div>"));
    dropFromTrigger(child("<div></div>"), "in");
    dismissOut(child("<div></div>"));
    expect(calls.map((call) => [call.options.duration, call.options.easing])).toEqual([
      [361, "cubic-bezier(.5, .6, .7, .8)"],
      [1700, "cubic-bezier(.1, .2, .3, .4)"],
      [281, "cubic-bezier(.1, .2, .3, .4)"],
      [201, "cubic-bezier(.1, .2, .3, .4)"],
      [161, "cubic-bezier(.1, .2, .3, .4)"],
    ]);

    calls = [];
    const from = child("<span>VPN</span>");
    const to = child("<span>1</span>");
    flyGhost(from, to, '<span class="chip">1</span>');
    const ghost = calls.find((call) => call.el !== to)!;
    expect(ghost.options).toMatchObject({ duration: 521, easing: "cubic-bezier(.5, .6, .7, .8)" });
    expect(ghost.el.getAttribute("aria-hidden")).toBe("true");

    calls = [];
    const span = child("<span>Jana Nováka</span>");
    const chip = document.createElement("span");
    morphInto(span, chip);
    expect(calls[0]!.options.duration).toBe(381);
  });

  it("falls back to the values motion.css defines when no token is in scope", () => {
    const css = readFileSync(resolve(process.cwd(), "src/motion.css"), "utf8").replace(/\r\n/g, "\n").replace(/\/\*[\s\S]*?\*\//g, "");
    const start = css.indexOf(":root,\n.atlas,");
    const tokens = css.slice(start, css.indexOf("}", start) + 1);
    const declaredValue = (name: string): string | undefined => {
      const value = tokens.match(new RegExp(`${name}:\\s*([^;]+);`))?.[1]?.trim();
      const alias = /^var\((--[a-z-]+)\)$/.exec(value ?? "");
      return alias ? declaredValue(alias[1]!) : value;
    };
    for (const [name, fallback] of Object.entries(motionTokenFallbacks)) {
      const declared = declaredValue(name);
      expect(declared, name).toBeDefined();
      if (typeof fallback === "number") expect(declared, name).toBe(`calc(${fallback}ms * var(--atlas-motion-scale, 1))`);
      else expect(declared!.replace(/\s+/g, ""), name).toBe(fallback.replace(/\s+/g, ""));
    }

    const loose = document.createElement("span");
    document.body.append(loose);
    glow(loose);
    expect(calls[0]!.options.duration).toBe(1600);
  });

  it("drawIcon animates every path from its own length", () => {
    const svg = element('<svg><circle cx="12" cy="12" r="10"/><path d="m9 12 2 2 4-4"/></svg>') as unknown as SVGSVGElement;
    const [circle, path] = [...svg.children] as SVGGeometryElement[];
    Object.assign(circle!, { getTotalLength: () => 62.8 });
    Object.assign(path!, { getTotalLength: () => 8.5 });
    const animations = drawIcon(svg);
    expect(animations).toHaveLength(2);
    expect(calls.map((call) => [call.el, call.frames, call.options.duration])).toEqual([
      [circle, [{ strokeDashoffset: 62.8 }, { strokeDashoffset: 0 }], 240],
      [path, [{ strokeDashoffset: 8.5 }, { strokeDashoffset: 0 }], 240],
    ]);
    // The final stroke is set on the element itself: the icon is complete even if the animation never runs.
    expect(circle!.style.strokeDasharray).toBe("62.8");
    expect(circle!.style.strokeDashoffset).toBe("0");
  });

  // A style read after an animation has started recalculates the page: on a page of 130 blocks, that was a layout per row (perf 3.5).
  it("reads the motion styles once for a whole wave, not once per row", () => {
    const page = element(`<div>${"<p>řádek</p>".repeat(30)}</div>`);
    const computedStyle = vi.spyOn(window, "getComputedStyle");
    // 3.6 perf: measuring which rows are in view laid the page out before its first paint.
    const measured = vi.spyOn(Element.prototype, "getBoundingClientRect");
    try {
      revealWave(page);
      expect(calls).toHaveLength(8);
      expect(computedStyle.mock.calls.length).toBeLessThanOrEqual(2);
      expect(measured).not.toHaveBeenCalled();
    } finally {
      computedStyle.mockRestore();
      measured.mockRestore();
    }
  });

  it("staggers rows 40 ms apart for the first eight, the rest with the eighth; under reduced motion all at once (F14, F17)", () => {
    const list = Array.from({ length: 10 }, (_, index) => element(`<li>${index}</li>`));
    riseIn(list, RISE_IN_ROWS);
    expect(calls.map((call) => call.options.delay)).toEqual([0, 40, 80, 120, 160, 200, 240, 280, 280, 280]);
    calls = [];
    reduced = true;
    riseIn(list, RISE_IN_ROWS);
    expect(calls.map((call) => call.options.delay)).toEqual(Array(10).fill(0));
    expect(calls.every((call) => Number(call.options.duration) <= 160)).toBe(true);
  });

  it("staggers cards, meters and the publish status like the prototype", () => {
    const cards = [element("<a>1</a>"), element("<a>2</a>"), element("<a>3</a>")];
    riseIn(cards, { delay: 90 });
    expect(calls.map((call) => [call.options.duration, call.options.delay])).toEqual([[220, 90], [220, 150], [220, 210]]);
    expect(calls[0]!.frames[0]).toEqual({ opacity: 0, transform: "translateY(8px)" });

    calls = [];
    meterFill([element("<i></i>"), element("<i></i>")]);
    expect(calls.map((call) => [call.frames, call.options.duration, call.options.delay])).toEqual([
      [[{ transform: "scaleX(0)" }, { transform: "scaleX(1)" }], 400, 0],
      [[{ transform: "scaleX(0)" }, { transform: "scaleX(1)" }], 400, 80],
    ]);

    calls = [];
    riseIn([element("<li>1</li>"), element("<li>2</li>")], RISE_IN_ROWS);
    expect(calls.map((call) => [call.frames[0], call.options.duration, call.options.delay])).toEqual([
      [{ opacity: 0, transform: "translateY(8px)" }, 200, 0], [{ opacity: 0, transform: "translateY(8px)" }, 200, 40],
    ]);

    calls = [];
    const page = element(`<div>${"<p>řádek</p>".repeat(30)}</div>`);
    revealWave(page);
    expect(calls).toHaveLength(8);
    expect(calls.slice(0, 2).map((call) => [call.frames[0], call.options.delay])).toEqual([[{ opacity: 0.6, transform: "translateY(8px)" }, 0], [{ opacity: 0.6, transform: "translateY(8px)" }, 40]]);

    calls = [];
    popStatus(element("<span>Publikováno</span>"));
    expect(calls[0]!.frames).toEqual([
      { transform: "scale(.8)", opacity: 0 }, { transform: "scale(1.06)", opacity: 1, offset: 0.6 }, { transform: "none" },
    ]);
    expect(calls[0]!.options.duration).toBe(320);
  });
  it("flashes a finished check row from the hover colour to transparent in 500 ms", () => {
    // Prototype: Založení a publikace · moment 6, a row of the pre-publish check gets its result.
    const row = element("<div>Možné heslo v textu</div>");
    flashRow(row);
    expect(calls[0]!.frames).toEqual([{ backgroundColor: "var(--hover)" }, { backgroundColor: "transparent" }]);
    expect(calls[0]!.options).toMatchObject({ duration: 500, easing: "cubic-bezier(.22, 1, .36, 1)", fill: "backwards" });
    reduced = true;
    flashRow(row);
    expect(calls[1]!.frames).toEqual(calls[0]!.frames);
    expect(calls[1]!.options).toMatchObject({ duration: 160, easing: "linear" });
  });

  it("follows a preview that forces reduced motion or slows motion down, as the prototype's switches", () => {
    // The gallery compares the primitives with the prototype: "Omezený pohyb" and "Zpomalit 3×". In the
    // browser motion.css resolves the tokens already slowed down (registered <time>); jsdom has no
    // stylesheet, so the preview sets the values the browser would compute.
    const preview = element("<section data-atlas-reduced-motion><span></span><p></p></section>");
    preview.style.setProperty("--atlas-motion-scale", "3");
    preview.style.setProperty("--atlas-motion-surface", "0.48s");
    preview.style.setProperty("--atlas-ai-glow", "4.8s");
    const [span, paragraph] = [...preview.children] as HTMLElement[];
    dismissOut(span!);
    expect(calls[0]!.frames).toEqual([{ opacity: 1 }, { opacity: 0 }]);
    // min(480, 160 × 3): the reduced cap slows down with the preview, as the prototype's anim().
    expect(calls[0]!.options).toMatchObject({ duration: 480, easing: "linear" });

    preview.removeAttribute("data-atlas-reduced-motion");
    riseIn([paragraph!], { delay: 90 });
    // riseIn's numbers are prototype literals: they are scaled here.
    expect(calls[1]!.options).toMatchObject({ duration: 660, delay: 270 });
    glow(paragraph!);
    // The glow is a token: already 4.8 s, never 14.4 s.
    expect(calls[2]!.options.duration).toBe(4800);
  });

  it("slows tokens down once (in CSS) and the prototype's literals here, so nothing runs 9× slower", () => {
    const preview = element("<section><div></div><div></div><div></div><div></div><span></span></section>");
    preview.style.setProperty("--atlas-motion-scale", "3");
    preview.style.setProperty("--atlas-motion-slow", "0.84s");
    preview.style.setProperty("--atlas-motion-pop", "960ms");
    const [revealed, collapsed, popped, dropped, rippled] = [...preview.children] as HTMLElement[];
    revealHeight(revealed!);
    collapseHeight(collapsed!);
    popStatus(popped!);
    dropFromTrigger(dropped!, "out");
    rippleOnce(rippled!);
    expect(calls.map((call) => call.options.duration)).toEqual([840, 720, 960, 540, 1800]);
    expect(literalMs(rippled!, PROTOTYPE_MS.ripple)).toBe(1800);
    expect(literalMs(element("<i></i>"), 240)).toBe(240);
  });

  it("reads a token resolved as seconds, as milliseconds or as an unregistered calc()", () => {
    const host = element("<div></div>");
    host.style.setProperty("--atlas-motion-base", "0.6s");
    expect(motionMs(host, "--atlas-motion-base")).toBe(600);
    host.style.setProperty("--atlas-motion-base", "600ms");
    expect(motionMs(host, "--atlas-motion-base")).toBe(600);
    // An engine without @property computes the substituted calc() as written.
    host.style.setProperty("--atlas-motion-base", "calc(200ms * 3)");
    expect(motionMs(host, "--atlas-motion-base")).toBe(600);
    host.style.setProperty("--atlas-motion-base", "nonsense");
    expect(motionMs(host, "--atlas-motion-base")).toBe(200);
    for (const [name, value] of [["--atlas-motion-pop", 320], ["--atlas-motion-loop", 1600], ["--atlas-motion-spin", 800], ["--atlas-motion-pulse", 1000], ["--atlas-motion-defer", 150], ["--atlas-motion-glow", 1600], ["--atlas-motion-menu", 140]] as const) {
      expect(motionMs(host, name), name).toBe(value);
    }
  });

  it("times what travels far by its distance, from rest, and a little shorter on the way back", () => {
    const host = element("<div></div>");
    // A short branch, a tree of 400 px, most of a screen: 1.1× the base token up to 1.25×, never more.
    expect([0, 90, 150, 400, 600, 5000].map((px) => Math.round(travelTiming(host, px).duration))).toEqual([220, 220, 220, 250, 250, 250]);
    expect(travelTiming(host, -400).duration).toBe(travelTiming(host, 400).duration);
    expect(travelTiming(host, 400, true).duration).toBeCloseTo(212.5);
    expect(travelTiming(host, 400).easing).toBe("cubic-bezier(.3, .2, .1, 1)");
    // The token carries a preview's slow-down, and its curve is whatever is in scope (linear under reduced motion).
    host.style.setProperty("--atlas-motion-base", "0.6s");
    host.style.setProperty("--atlas-ease-travel", "linear");
    expect(travelTiming(host, 400)).toEqual({ duration: 750, easing: "linear" });
  });

  it("unfolds a panel out of its trigger and folds it away in place (prototype: opening and closing the search)", () => {
    const panel = element('<div class="search" hidden>Hledání</div>');
    const trigger = element("<button>Hledat</button>");
    const rects = new Map<Element, Partial<DOMRect>>([
      [panel, { left: 100, top: 50, right: 700, bottom: 450, width: 600, height: 400 }],
      [trigger, { left: 300, top: 60, right: 460, bottom: 92, width: 160, height: 32 }],
    ]);
    const original = Element.prototype.getBoundingClientRect;
    Element.prototype.getBoundingClientRect = function (this: Element) {
      return { left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0, ...rects.get(this) } as DOMRect;
    };
    try {
      unfoldFrom(panel, trigger);
    } finally {
      Element.prototype.getBoundingClientRect = original;
    }
    expect(calls[0]!.snapshot.hidden).toBe(false);
    expect(calls[0]!.frames).toEqual([
      { clipPath: "inset(10px 240px 358px 200px round 8px)", opacity: 0.7 },
      { clipPath: "inset(0px 0px 0px 0px round 14px)", opacity: 1 },
    ]);
    // The search opens and closes in the time of a dialog: base to open, the shorter surface to close.
    expect(calls[0]!.options).toMatchObject({ duration: 200, easing: "cubic-bezier(.22, 1, .36, 1)", fill: "backwards" });

    // Without a visible trigger only the opacity changes.
    unfoldFrom(panel, null);
    expect(calls[1]!.frames).toEqual([{ opacity: 0.7 }, { opacity: 1 }]);

    foldOut(panel);
    expect(calls[2]!.snapshot.inert).toBe(true);
    expect(panel.getAttribute("aria-hidden")).toBe("true");
    expect(calls[2]!.frames).toEqual([{ opacity: 1, transform: "none" }, { opacity: 0, transform: "translateY(8px) scale(.98)" }]);
    expect(calls[2]!.options).toMatchObject({ duration: 160, fill: "forwards" });

    // Reduced motion: the clip and the scale go, the fade stays, at most 160 ms, linear.
    reduced = true;
    unfoldFrom(element("<div></div>"), trigger);
    foldOut(element("<div></div>"));
    expect(calls[3]!.frames).toEqual([{ opacity: 0.7 }, { opacity: 1 }]);
    expect(calls[4]!.frames).toEqual([{ opacity: 1 }, { opacity: 0 }]);
    expect(calls[4]!.options).toMatchObject({ duration: 160, easing: "linear" });
  });

  describe("unfoldFrom on a real palette (the search opening from the sidebar's field)", () => {
    const panelBox = { left: 80, top: 64, right: 1200, bottom: 688, width: 1120, height: 624 };
    function unfold(panelHtml: string, triggerHtml: string, triggerBox: Partial<DOMRect>, box: Partial<DOMRect> = panelBox) {
      const panel = element(panelHtml);
      const trigger = element(triggerHtml);
      const rects = new Map<Element, Partial<DOMRect>>([[panel, box], [trigger, triggerBox]]);
      const original = Element.prototype.getBoundingClientRect;
      Element.prototype.getBoundingClientRect = function (this: Element) {
        return { left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0, ...rects.get(this) } as DOMRect;
      };
      try { unfoldFrom(panel, trigger); } finally { Element.prototype.getBoundingClientRect = original; }
      return { panel, frames: calls.at(-1)!.frames };
    }

    it("ends on a clip that already holds the panel's shadow, so removing the clip changes nothing", () => {
      const { panel, frames } = unfold('<div style="box-shadow: rgba(20, 47, 59, 0.28) 0px 24px 64px 0px">Hledání</div>', "<button>Hledat</button>", { left: 300, top: 70, right: 460, bottom: 102, width: 160, height: 32 });
      // 64 px of blur is spent at one and a half times its radius (3 σ): 96 px, shifted 24 px down.
      expect(frames.at(-1)).toEqual({ clipPath: "inset(-72px -96px -120px -96px round 14px)", opacity: 1 });
      expect(unfoldedClip(panel)).toBe("inset(-72px -96px -120px -96px round 14px)");
      // Inner shadows stay inside the box; several shadows take the widest reach on each side.
      expect(unfoldedClip(element('<div style="box-shadow: inset 0 0 0 1px red, rgb(0 0 0 / 0.5) -4px 0px 8px 2px, 0px 2px 4px 0px black"></div>'))).toBe("inset(-14px -10px -14px -18px round 14px)");
      expect(unfoldedClip(element("<div></div>"))).toBe("inset(0px 0px 0px 0px round 14px)");
    });

    it("starts on the trigger's own box when the trigger lies beside the panel and near it, and travels to its place", () => {
      const { frames } = unfold("<div>Hledání</div>", "<button>Hledat</button>", { left: 10, top: 58, right: 219, bottom: 94, width: 209, height: 36 });
      // A window of the trigger's size in the panel's nearest corner, carried onto the trigger: 10 − 80 and 58 − 64.
      expect(frames).toEqual([
        { clipPath: "inset(0px 911px 588px 0px round 8px)", opacity: 0.7, transform: "translate(-70px, -6px)" },
        { clipPath: "inset(0px 0px 0px 0px round 14px)", opacity: 1, transform: "none" },
      ]);
    });

    it("opens in place when the trigger is further from the panel than its own larger side (a wide window): no third look", () => {
      const field = { left: 10, top: 64, right: 219, bottom: 100, width: 209, height: 36 };
      const inPlace = [
        { clipPath: "inset(0px 1120px 588px 0px round 8px)", opacity: 0.7 },
        { clipPath: "inset(0px 0px 0px 0px round 14px)", opacity: 1 },
      ];
      // 1920 px: the palette starts at 400, 390 px from the field that is 209 px wide.
      expect(unfold("<div>Hledání</div>", "<button>Hledat</button>", field, { left: 400, top: 64, right: 1520, bottom: 688, width: 1120, height: 624 }).frames).toEqual(inPlace);
      // The rule is the trigger's own size: exactly as far as it is wide still travels, one pixel further does not.
      expect(unfold("<div>Hledání</div>", "<button>Hledat</button>", field, { left: 219, top: 64, right: 1339, bottom: 688, width: 1120, height: 624 }).frames[0])
        .toEqual({ clipPath: "inset(0px 911px 588px 0px round 8px)", opacity: 0.7, transform: "translate(-209px, 0px)" });
      expect(unfold("<div>Hledání</div>", "<button>Hledat</button>", field, { left: 220, top: 64, right: 1340, bottom: 688, width: 1120, height: 624 }).frames).toEqual(inPlace);
    });

    it("does not travel from a trigger nobody sees (a collapsed sidebar, a closed drawer): it opens from the nearest edge", () => {
      const hidden = unfold("<div>Hledání</div>", '<button style="visibility: hidden">Hledat</button>', { left: -262, top: 58, right: -53, bottom: 94, width: 209, height: 36 });
      expect(hidden.frames).toEqual([
        { clipPath: "inset(0px 1120px 594px 0px round 8px)", opacity: 0.7 },
        { clipPath: "inset(0px 0px 0px 0px round 14px)", opacity: 1 },
      ]);
      const offScreen = unfold("<div>Hledání</div>", "<button>Hledat</button>", { left: -262, top: 58, right: -53, bottom: 94, width: 209, height: 36 });
      expect(offScreen.frames).toEqual(hidden.frames);
    });
  });

  it("scrolls smoothly only without reduced motion", () => {
    const el = element("<div></div>");
    expect(scrollBehavior(el)).toBe("smooth");
    reduced = true;
    expect(scrollBehavior(el)).toBe("auto");
    reduced = false;
    const forced = element("<section data-atlas-reduced-motion><p></p></section>");
    expect(scrollBehavior(forced.firstElementChild)).toBe("auto");
  });

  it("spins a marker in, ripples a warning once and moves a card to its new place", () => {
    const ring = element("<svg><circle r=\"12\"/></svg>");
    const icon = element("<span>!</span>");
    markerIn(ring, icon);
    expect(calls.map((call) => [call.el, call.frames[0], call.options.duration, call.options.delay])).toEqual([
      [ring, { transform: "rotate(-140deg) scale(.5)", opacity: 0 }, 280, 0],
      [icon, { opacity: 0, transform: "scale(.4)" }, 240, 60],
    ]);

    calls = [];
    rippleOnce(element("<span></span>"));
    expect(calls[0]!.frames).toEqual([{ opacity: 0.7, transform: "scale(1)" }, { opacity: 0, transform: "scale(2.4)" }]);
    expect(calls[0]!.options).toMatchObject({ duration: 600, easing: "cubic-bezier(.2, .7, .3, 1)" });

    calls = [];
    const card = element("<div></div>");
    let moved = false;
    relocate(card, () => { moved = true; });
    // The new place is applied first; jsdom has no layout, so the card has nowhere to travel.
    expect(moved).toBe(true);
    expect(calls).toEqual([]);
  });
  it("flies the copy in viewport coordinates even inside a transformed ancestor", () => {
    // A page that rose in keeps a transform, which makes it the containing block of a fixed element.
    const from = element("<span>VPN</span>");
    const to = element("<span>1</span>");
    const rects = new Map<Element, Partial<DOMRect>>([[from, { left: 300, top: 200 }], [to, { left: 500, top: 400 }]]);
    const original = Element.prototype.getBoundingClientRect;
    Element.prototype.getBoundingClientRect = function (this: Element) {
      const rect = rects.get(this) ?? (this.hasAttribute("data-atlas-motion") ? { left: 100, top: 50 } : {});
      return { left: 0, top: 0, width: 0, height: 0, ...rect } as DOMRect;
    };
    try {
      flyGhost(from, to, "<span>1</span>");
    } finally {
      Element.prototype.getBoundingClientRect = original;
    }
    const flight = calls.find((call) => call.el.hasAttribute("data-atlas-motion"))!;
    expect(flight.frames[0]!.transform).toBe("translate(200px,150px) scale(.92)");
    expect(flight.frames.at(-1)!.transform).toBe("translate(400px,350px) scale(1)");
  });
});
