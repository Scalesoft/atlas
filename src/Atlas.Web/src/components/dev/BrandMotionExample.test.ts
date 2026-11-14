import { readFileSync } from "node:fs";
import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { KeepAlive, defineComponent, h, nextTick, ref } from "vue";
import BrandMotionExample from "./BrandMotionExample.vue";
import AtlasBrandLogo from "../AtlasBrandLogo.vue";
import AtlasMotionMark from "../brand/AtlasMotionMark.vue";

let wrapper: ReturnType<typeof mount>;
afterEach(() => {
  wrapper?.unmount();
  document.body.innerHTML = "";
});

const source = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");

it("renders the brand examples with local data", async () => {
  wrapper = mount(BrandMotionExample, { attachTo: document.body });
  await flushPromises();
  expect(wrapper.findAll(".atlas-brand-logo").length).toBeGreaterThan(3);
  expect(wrapper.findAll(".atlas-motion-mark")).toHaveLength(14);
  expect(wrapper.find(".login-layout").exists()).toBe(true);
});

it("moves the logo and the mark by the data attributes the styles hang on", () => {
  const logo = mount(AtlasBrandLogo, { props: { motion: "hero" } });
  expect(logo.get(".atlas-brand-logo").attributes("data-motion")).toBe("hero");
  expect(mount(AtlasBrandLogo).get(".atlas-brand-logo").attributes("data-motion")).toBe("ambient");
  expect(mount(AtlasMotionMark, { props: { variant: "scan" } }).get("svg").attributes("data-active")).toBe("false");
  expect(mount(AtlasMotionMark, { props: { variant: "scan", active: true } }).get("svg").attributes("data-active")).toBe("true");
});

it("keeps the brand loops to the entry screens and the active mark (plan O1)", () => {
  const logo = source("../AtlasBrandLogo.vue");
  // The shine keeps passing in every logo (owner, 2026-10-05); the drift loops only in the hero, the sway plays once.
  expect(logo).toMatch(/^\.bloom-light \{[^}]*animation: bloom-wave [^;]* infinite;/m);
  expect(logo).toMatch(/^\[data-motion="hero"\] \.bloom-drift \{[^}]*infinite/m);
  expect(logo).not.toMatch(/^\.bloom-(page|drift) \{[^}]*infinite/m);
  const mark = source("../brand/AtlasMotionMark.vue");
  for (const line of mark.split("\n").filter((row) => /animation: [\w-]+ var\(.*infinite/.test(row))) expect(line, line).toMatch(/^\[data-active="true"\]/);
});

it("passes the ambient shine once per wave on its own layers and lets it rest in between", async () => {
  // The hero's light travels inside the SVG mask; the ambient logo keeps the same still picture with that light switched off.
  const style = source("../AtlasBrandLogo.vue").split("<style")[1]!;
  expect(style).toMatch(/^\[data-motion="static"\] \.bloom-light, \[data-motion="ambient"\] \.bloom-light \{ display: none; \}/m);
  expect(style).toMatch(/^\.bloom-shine-band \{ animation: bloom-pass calc\(var\(--atlas-brand-wave\) \* \.46\) linear both; \}/m);
  expect(style).not.toMatch(/^\.bloom-shine[\w-]* \{[^}]*infinite/m);

  vi.useFakeTimers();
  const tokens: Record<string, string> = { "--atlas-brand-wave": "9s", "--atlas-brand-wave-delay": "3.6s" };
  vi.spyOn(window, "getComputedStyle").mockImplementation(() => ({ getPropertyValue: (name: string) => tokens[name] ?? "" }) as CSSStyleDeclaration);
  // A pass waits for an idle moment (it reads styles); here one comes at once.
  vi.stubGlobal("requestIdleCallback", (callback: IdleRequestCallback) => { callback({ didTimeout: false, timeRemaining: () => 50 }); return 0; });
  try {
    const host = document.createElement("div");
    document.body.append(host);
    const ambient = mount(AtlasBrandLogo, { attachTo: host });
    expect(ambient.find(".bloom-shine").exists()).toBe(false);
    await vi.advanceTimersByTimeAsync(3600);
    const first = ambient.get(".bloom-shine").element;
    expect(ambient.findAll(".bloom-shine-strokes path")).toHaveLength(4);
    await vi.advanceTimersByTimeAsync(9000);
    // A new element per pass restarts the finite animation; the one before it is gone.
    expect(ambient.get(".bloom-shine").element).not.toBe(first);
    expect(first.isConnected).toBe(false);
    // Reduced motion: the pass is skipped (and the styles hide the light).
    host.setAttribute("data-atlas-reduced-motion", "");
    const held = ambient.get(".bloom-shine").element;
    await vi.advanceTimersByTimeAsync(9000);
    expect(ambient.get(".bloom-shine").element).toBe(held);
    ambient.unmount();
    expect(vi.getTimerCount()).toBe(0);
    for (const motion of ["hero", "static"] as const) {
      const still = mount(AtlasBrandLogo, { props: { motion }, attachTo: host });
      await vi.advanceTimersByTimeAsync(20000);
      expect(still.find(".bloom-shine").exists()).toBe(false);
      expect(vi.getTimerCount()).toBe(0);
      still.unmount();
    }
  } finally {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  }
});

it("rests while the logo is kept alive off the document and starts over when it returns", async () => {
  vi.useFakeTimers();
  const tokens: Record<string, string> = { "--atlas-brand-wave": "9s", "--atlas-brand-wave-delay": "3.6s" };
  vi.spyOn(window, "getComputedStyle").mockImplementation(() => ({ getPropertyValue: (name: string) => tokens[name] ?? "" }) as CSSStyleDeclaration);
  // A pass waits for an idle moment (it reads styles); here one comes at once.
  vi.stubGlobal("requestIdleCallback", (callback: IdleRequestCallback) => { callback({ didTimeout: false, timeRemaining: () => 50 }); return 0; });
  try {
    // The sidebar's navigation is set aside like this while settings hold the rail (App.vue).
    const shown = ref(true);
    const kept = mount(defineComponent({ setup: () => () => h(KeepAlive, null, { default: () => (shown.value ? h(AtlasBrandLogo) : null) }) }), { attachTo: document.body });
    await vi.advanceTimersByTimeAsync(3600);
    expect(kept.find(".bloom-shine").exists()).toBe(true);
    shown.value = false;
    await nextTick();
    expect(vi.getTimerCount()).toBe(0);
    shown.value = true;
    await nextTick();
    // No light left over from before (back in the document its animation would run again at once); the usual delay first.
    expect(kept.find(".bloom-shine").exists()).toBe(false);
    await vi.advanceTimersByTimeAsync(3600);
    expect(kept.find(".bloom-shine").exists()).toBe(true);
    kept.unmount();
    expect(vi.getTimerCount()).toBe(0);
  } finally {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  }
});

it("takes the brand timing from tokens, not literals", () => {
  for (const path of ["../AtlasBrandLogo.vue", "../auth/LoginLayout.vue", "../brand/AtlasMotionMark.vue"]) {
    const style = source(path).split("<style")[1]!;
    const declarations = style.split("\n").filter((row) => /(animation|transition)[\w-]*:/.test(row));
    for (const row of declarations) expect(row, `${path}: ${row}`).not.toMatch(/\d\.?\d*m?s\b|cubic-bezier|ease-in-out/);
  }
});
