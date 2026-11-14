import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// Read from the project root: under jsdom `import.meta.url` is not a file: URL.
const motionCss = readFileSync(resolve(process.cwd(), "src/motion.css"), "utf8").replace(/\r\n/g, "\n").replace(/\/\*[\s\S]*?\*\//g, "");

function declarations(block: string) {
  return new Map([...block.matchAll(/(--(?:atlas|st)-[a-z-]+):\s*([^;]+);/g)].map(([, name, value]) => [name!, value!.trim()]));
}

/** The rule whose selector list starts at `selector` (the first match). */
function rule(css: string, selector: string) {
  const start = css.indexOf(selector);
  expect(start, selector).toBeGreaterThanOrEqual(0);
  const open = css.indexOf("{", start);
  return { selector: css.slice(start, open).trim(), body: css.slice(open + 1, css.indexOf("}", open)) };
}

const tokenRule = rule(motionCss, ":root,\n.atlas,");
const tokens = declarations(tokenRule.body);
const reducedStart = motionCss.indexOf("@media (prefers-reduced-motion: reduce)");
const reducedMedia = rule(motionCss.slice(reducedStart), ":root,");
const reducedTokens = declarations(reducedMedia.body);
const forced = rule(motionCss, "[data-atlas-reduced-motion],\n");
const forcedTokens = declarations(forced.body);

/** Every duration token is `calc(<prototype ms> * var(--atlas-motion-scale, 1))`: the slow-down is in the token. */
function scaledMs(value: string | undefined) {
  const match = /^calc\((\d+)ms \* var\(--atlas-motion-scale, 1\)\)$/.exec(value ?? "");
  expect(match, value).not.toBeNull();
  return Number(match![1]);
}

const DURATIONS = {
  "--atlas-motion-fast": 120, "--atlas-motion-base": 200, "--atlas-motion-slow": 280,
  "--atlas-motion-menu": 140, "--atlas-motion-surface": 160, "--atlas-motion-pop": 320,
  "--atlas-motion-loop": 1600, "--atlas-motion-spin": 800, "--atlas-motion-pulse": 1000, "--atlas-motion-defer": 150,
  "--atlas-ai-reveal": 420, "--atlas-ai-stagger": 130, "--atlas-ai-gather": 520, "--atlas-ai-morph": 380,
  "--atlas-ai-glow": 1600, "--atlas-ai-solidify": 360, "--atlas-ai-flight": 1800,
} as const;

describe("motion tokens", () => {
  it("are declared on :root as well as on .atlas, the Reka portals and previews", () => {
    // The full-screen editor and the editor's overlays live in <body>, outside .atlas (plan §2.8 item 3).
    expect(tokenRule.selector.split(",").map((part) => part.trim())).toEqual([
      ":root",
      ".atlas",
      ':where([data-slot="dropdown-menu-content"]',
      '[data-slot="popover-content"]',
      '[data-slot="tooltip-content"]',
      "[data-atlas-motion])",
    ]);
  });

  it("define the prototype's durations, easings and distances", () => {
    for (const [name, value] of Object.entries(DURATIONS)) expect(scaledMs(tokens.get(name)), name).toBe(value);
    expect(tokens.get("--atlas-motion-glow")).toBe("var(--atlas-ai-glow)");
    expect(tokens.get("--atlas-ease-out")).toBe("cubic-bezier(.22, 1, .36, 1)");
    expect(tokens.get("--atlas-ease-inout")).toBe("cubic-bezier(.65, 0, .35, 1)");
    // The prototype has no ease-in; exits take the same curve (plan O4).
    expect(tokens.get("--atlas-ease-in")).toBe("var(--atlas-ease-out)");
    expect(tokens.get("--atlas-motion-ease")).toBe("var(--atlas-ease-out)");
    // What travels far leaves from rest instead of covering a quarter of its way in the first frame (2.6.2).
    expect(tokens.get("--atlas-ease-travel")).toBe("cubic-bezier(.3, .2, .1, 1)");
    // The arrow of a tree turns with the unfolding it starts and ends with it (travelTiming's 1.25× cap).
    expect(motionCss).toContain(".atlas .tree-toggle > svg { transition: rotate calc(var(--atlas-motion-base) * 1.25) var(--atlas-ease-travel); }");
    expect(tokens.get("--atlas-rise")).toBe("8px");
    expect(tokens.get("--atlas-lift")).toBe("2px");
    expect(tokens.get("--atlas-press")).toBe(".98");
    expect(tokens.get("--st-pop-from")).toBe(".8");
    expect(tokens.get("--atlas-glow-spread")).toBe("3px");
  });

  it("register every duration as a <time>, so the slowed value resolves and scripts read a time", () => {
    for (const name of [...Object.keys(DURATIONS), "--atlas-motion-glow"]) {
      const registered = new RegExp(`@property ${name} \\{ syntax: "<time>"; inherits: true; initial-value: (\\d+)ms; \\}`).exec(motionCss);
      expect(registered, name).not.toBeNull();
      expect(Number(registered![1]), name).toBe(name === "--atlas-motion-glow" ? 1600 : DURATIONS[name as keyof typeof DURATIONS]);
    }
    // The application never sets the scale; only a preview does.
    expect(motionCss).not.toMatch(/--atlas-motion-scale:/);
  });

  it("turn reduced motion into fades of at most 160 ms, linear, without distance (the prototype's anim())", () => {
    expect(reducedStart).toBeGreaterThan(motionCss.indexOf(tokenRule.body));
    for (const name of Object.keys(DURATIONS)) {
      if (!reducedTokens.has(name)) continue;
      expect(scaledMs(reducedTokens.get(name)), name).toBeLessThanOrEqual(160);
      expect(scaledMs(reducedTokens.get(name)), name).toBe(Math.min(DURATIONS[name as keyof typeof DURATIONS], 160));
    }
    for (const name of ["--atlas-motion-base", "--atlas-motion-slow", "--atlas-motion-surface", "--atlas-motion-pop", "--atlas-ai-reveal", "--atlas-ai-gather", "--atlas-ai-morph", "--atlas-ai-solidify", "--atlas-ai-flight"]) {
      expect(scaledMs(reducedTokens.get(name)), name).toBe(160);
    }
    for (const name of ["--atlas-ease-out", "--atlas-ease-inout", "--atlas-ease-in", "--atlas-ease-travel"]) expect(reducedTokens.get(name), name).toBe("linear");
    expect(reducedTokens.get("--atlas-rise")).toBe("0px");
    expect(reducedTokens.get("--atlas-lift")).toBe("0px");
    expect(reducedTokens.get("--atlas-press")).toBe("1");
    expect(reducedTokens.get("--st-pop-from")).toBe("1");
    expect(reducedTokens.get("--atlas-glow-spread")).toBe("0px");
    // The glow keeps its length (a background-only fade is not movement), the stagger keeps the order and
    // loops keep their tokens: they stand still instead.
    for (const name of ["--atlas-ai-glow", "--atlas-motion-glow", "--atlas-ai-stagger", "--atlas-motion-loop", "--atlas-motion-spin", "--atlas-motion-pulse", "--atlas-motion-defer"]) {
      expect(reducedTokens.has(name), name).toBe(false);
    }
  });

  it("apply the same reduced tokens under a preview's [data-atlas-reduced-motion], also to nested .atlas", () => {
    expect(forced.selector).toBe('[data-atlas-reduced-motion],\n[data-atlas-reduced-motion] :where(.atlas, [data-slot="dropdown-menu-content"], [data-slot="popover-content"], [data-slot="tooltip-content"], [data-atlas-motion])');
    expect([...forcedTokens]).toEqual([...reducedTokens]);
    // Later than the token rule with the same specificity, so it wins on a nested .atlas.
    expect(motionCss.indexOf(forced.body)).toBeGreaterThan(motionCss.indexOf(tokenRule.body));
  });

  it("never switch animation off for reduced motion", () => {
    // The old global kill-switch made every surface snap; it must not come back.
    expect(motionCss).not.toMatch(/(animation|transition):\s*none\s*!important/);
    expect(motionCss).not.toMatch(/0\.01ms/);
  });

  it("stop loops under reduced motion and under the preview switch", () => {
    const stopMedia = motionCss.slice(reducedStart).slice(motionCss.slice(reducedStart).indexOf("@media", 10));
    const stopForced = motionCss.slice(motionCss.indexOf("[data-atlas-reduced-motion] :is("));
    for (const block of [stopMedia, stopForced]) {
      for (const loop of [".loading-skeleton-rows", 'button[aria-busy="true"]', ".atlas-spinner", ".atlas-pulse", "[aria-busy=\"true\"]::after"]) expect(block, loop).toContain(loop);
      // A busy indicator still fades in after the defer; the sweep stands still.
      expect(block).toMatch(/\{\s*animation:\s*atlas-backdrop-in var\(--atlas-motion-base\) var\(--atlas-ease-out\) var\(--atlas-motion-defer\) both;/);
      expect(block.slice(0, block.indexOf("@keyframes"))).not.toContain("atlas-progress");
    }
    expect(stopForced).toContain("[data-atlas-reduced-motion] .tree-toggle > svg { transition-property: opacity; }");
  });

  it("run the loops of work on the loop, spin, pulse and defer tokens", () => {
    expect(motionCss).toMatch(/\.loading-skeleton-rows \{\s*animation: atlas-loading-pulse var\(--atlas-motion-loop\)/);
    expect(motionCss).toMatch(/\.atlas-spinner\) \{\s*animation: atlas-spin var\(--atlas-motion-spin\) linear infinite;/);
    expect(motionCss).toMatch(/\.atlas \.atlas-pulse \{ animation: atlas-pulse var\(--atlas-motion-pulse\) ease-in-out infinite; \}/);
    expect(motionCss).toContain("atlas-backdrop-in var(--atlas-motion-base) var(--atlas-ease-out) var(--atlas-motion-defer) both, atlas-progress var(--atlas-motion-loop) ease-in-out infinite");
  });
});

describe("menus, toasts, pops and highlights", () => {
  const keyframes = (name: string) => {
    const start = motionCss.indexOf(`@keyframes ${name} `);
    expect(start, name).toBeGreaterThanOrEqual(0);
    return motionCss.slice(start, motionCss.indexOf("\n}", start) + 2);
  };

  it("grow menus and popovers from their trigger and fold them away like the prototype's menuIn/menuOut", () => {
    expect(keyframes("atlas-menu-in")).toMatch(/from \{ opacity: 0; transform: scale\(\.94\) translateY\(calc\(var\(--atlas-rise\) \* -\.5\)\); \}/);
    expect(keyframes("atlas-menu-out")).toMatch(/to \{ opacity: 0; transform: scale\(\.97\); \}/);
    expect(motionCss).toMatch(/\[data-slot="popover-content"\]\) \{\n {2}animation: atlas-menu-in var\(--atlas-motion-menu\) var\(--atlas-ease-out\);/);
    expect(motionCss).toMatch(/\[data-slot="popover-content"\]\)\[data-state="closed"\] \{\n {2}animation: atlas-menu-out var\(--atlas-motion-fast\) var\(--atlas-ease-out\) forwards;/);
    // The 4 px start at the trigger on every side; the side rules have no specificity, so closing and reduced motion win.
    expect(keyframes("atlas-menu-in-top")).toMatch(/from \{ opacity: 0; transform: scale\(\.94\) translateY\(calc\(var\(--atlas-rise\) \* \.5\)\); \}/);
    expect(keyframes("atlas-menu-in-left")).toMatch(/from \{ opacity: 0; transform: scale\(\.94\) translateX\(calc\(var\(--atlas-rise\) \* \.5\)\); \}/);
    expect(keyframes("atlas-menu-in-right")).toMatch(/from \{ opacity: 0; transform: scale\(\.94\) translateX\(calc\(var\(--atlas-rise\) \* -\.5\)\); \}/);
    for (const side of ["top", "left", "right"]) expect(motionCss).toContain(`:where([data-slot="dropdown-menu-content"], [data-slot="popover-content"]):where([data-side="${side}"]) { animation-name: atlas-menu-in-${side}; }`);
    // A tooltip only fades.
    expect(motionCss).toMatch(/:where\(\[data-slot="tooltip-content"\]\) \{\n {2}animation: atlas-backdrop-in var\(--atlas-motion-menu\)/);
    // Under reduced motion the scale goes too: menus only fade.
    expect(motionCss).toContain('[data-atlas-reduced-motion] :where([data-slot="dropdown-menu-content"], [data-slot="popover-content"]) { animation-name: atlas-backdrop-in; }');
    expect(motionCss).toContain('[data-atlas-reduced-motion] :where([data-slot="dropdown-menu-content"], [data-slot="popover-content"])[data-state="closed"] { animation-name: atlas-dismiss; }');
    const media = motionCss.slice(motionCss.indexOf("@media", reducedStart + 10));
    expect(media).toContain('{ animation-name: atlas-backdrop-in; }');
  });

  it("move toasts 10 px in and 6 px out on the rise token", () => {
    expect(keyframes("atlas-toast-in")).toContain("from { opacity: 0; transform: translateY(calc(var(--atlas-rise) * 1.25)); }");
    expect(keyframes("atlas-toast-out")).toContain("to { opacity: 0; transform: translateY(calc(var(--atlas-rise) * .75)); }");
  });

  it("pop like popStatus and highlight like the AI glow", () => {
    // Plan O14: .8 → 1.06 → 1 at 60 %.
    expect(keyframes("st-pop")).toContain("0% { opacity: 0; transform: scale(var(--st-pop-from, .8)); }");
    expect(keyframes("st-pop")).toContain("60% { opacity: 1; transform: scale(calc(1 + (1 - var(--st-pop-from, .8)) * .3)); }");
    // Plan O13: the glow colour and a ring that is 0 px under reduced motion.
    expect(keyframes("st-highlight")).toContain("0% { background-color: var(--atlas-ai-glow-color); box-shadow: 0 0 0 var(--atlas-glow-spread) var(--atlas-ai-glow-color); }");
    expect(keyframes("st-highlight")).not.toContain("background-color: transparent");
  });
});

describe("AI motion tokens", () => {
  it("defines the glow colour and the shine for light and dark", () => {
    expect(tokens.get("--atlas-ai-glow-color")).toBe("color-mix(in srgb, var(--accent) 18%, transparent)");
    expect(tokens.get("--atlas-ai-shine")).toBe("rgb(255 255 255 / .9)");
    const dark = rule(motionCss, ':root[data-mode="dark"],');
    expect(dark.selector).toContain('.atlas[data-mode="dark"]');
    expect(declarations(dark.body).get("--atlas-ai-glow-color")).toBe("color-mix(in srgb, var(--accent) 20%, transparent)");
    expect(declarations(dark.body).get("--atlas-ai-shine")).toBe("rgb(255 255 255 / .55)");
  });
});

describe("AI motion classes", () => {
  const aiCss = readFileSync(resolve(process.cwd(), "src/styles/ai.css"), "utf8").replace(/\r\n/g, "\n");
  const aiReduced = aiCss.slice(aiCss.indexOf("@media (prefers-reduced-motion: reduce)"));

  it("take the prototype's loops and glow on the Atlas tokens, which carry the slow-down", () => {
    expect(aiCss).toMatch(/\.atlas-ai-twinkle[^{]*\{[^}]*animation:\s*atlas-ai-twinkle var\(--atlas-motion-loop\) var\(--atlas-ease-inout\) infinite/);
    expect(aiCss).toMatch(/\.atlas-ai-shimmer\s*\{[^}]*animation:\s*atlas-ai-shimmer var\(--atlas-motion-loop\) linear infinite/);
    expect(aiCss).toMatch(/\.atlas-glow\s*\{[^}]*animation:\s*atlas-ai-glow var\(--atlas-ai-glow\) var\(--atlas-ease-out\)/);
    expect(aiCss).toMatch(/\.atlas-suggestion-outline[^{]*\{[^}]*stroke-dasharray:\s*6 5/);
    // Multiplying here again would slow a scaled token down twice.
    expect(aiCss.replace(/\/\*[\s\S]*?\*\//g, "")).not.toContain("--atlas-motion-scale");
  });

  it("stop the loops and fade the glow's background only under reduced motion", () => {
    expect(aiReduced).toMatch(/\.atlas-ai-twinkle[^{]*,\s*\.atlas-ai-shimmer\s*\{\s*animation:\s*none/);
    expect(aiReduced).toMatch(/\.atlas-ai-shimmer\s*\{[^}]*color:\s*var\(--muted\)/);
    expect(aiReduced).toMatch(/\.atlas-glow\s*\{\s*animation-name:\s*atlas-ai-glowfade/);
    // A preview (the gallery) forces the same path with an attribute, like the prototype's switch.
    const forcedAi = aiCss.slice(aiCss.lastIndexOf("}", aiCss.indexOf(":where([data-atlas-reduced-motion])")));
    expect(forcedAi).toMatch(/\[data-atlas-reduced-motion\]\) \.atlas-ai-shimmer\s*\{\s*animation:\s*none/);
    expect(forcedAi).toMatch(/\[data-atlas-reduced-motion\]\) \.atlas-glow\s*\{\s*animation-name:\s*atlas-ai-glowfade/);
    const glowfade = aiCss.slice(aiCss.indexOf("@keyframes atlas-ai-glowfade"));
    expect(glowfade.slice(0, glowfade.indexOf("}\n}") + 3)).not.toContain("box-shadow");
  });
});

describe("Tailwind defaults", () => {
  it("run the generated components' transitions and spinner on the motion tokens", () => {
    const theme = readFileSync(resolve(process.cwd(), "src/styles/theme.css"), "utf8").replace(/\r\n/g, "\n");
    expect(theme).toContain("--default-transition-duration: var(--atlas-motion-fast);");
    expect(theme).toContain("--default-transition-timing-function: var(--atlas-ease-out);");
    expect(theme).toContain("--animate-spin: atlas-spin var(--atlas-motion-spin) linear infinite;");
  });
});
