import { readdirSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { motionTokenFallbacks, MOTION_SCALE_PROPERTY } from "../../motion/aiMotion";
import AiGalleryPage from "./AiGalleryPage.vue";
import { useGalleryMotion, resetGalleryMotion } from "./galleryMotion";

// Exercise every real example with the actual gallery provider and replay wiring,
// one at a time. Mounting all examples together exceeded CI's setup deadline.
// Only sibling example roots are substituted; selected descendants stay real.
const names = readdirSync(resolve(import.meta.dirname, "ai"))
  .filter((file) => file.endsWith("Example.vue")).sort().map((file) => file.replace(/\.vue$/, ""));
let motionStyles: HTMLStyleElement;
let page: ReturnType<typeof mount>;
let fetch: ReturnType<typeof vi.spyOn>;
let open: ReturnType<typeof vi.spyOn>;
beforeEach(() => {
  resetGalleryMotion();
  // jsdom recursively resolves missing inherited custom properties; provide the real token defaults.
  motionStyles = document.createElement("style");
  motionStyles.textContent = `:root { ${MOTION_SCALE_PROPERTY}: 1; ${Object.entries(motionTokenFallbacks).map(([key, value]) => `${key}: ${typeof value === "number" ? `${value}ms` : value};`).join(" ")} }`;
  document.head.append(motionStyles);
  Element.prototype.animate = vi.fn(() => ({ finished: Promise.resolve(), cancel: vi.fn() }) as unknown as Animation) as unknown as Element["animate"];
  fetch = vi.spyOn(globalThis, "fetch");
  open = vi.spyOn(XMLHttpRequest.prototype, "open");
});

afterEach(() => {
  page?.unmount();
  motionStyles.remove();
  resetGalleryMotion();
  delete (Element.prototype as Partial<Element>).animate;
  vi.restoreAllMocks();
  document.body.replaceChildren();
});

it.each(names)("renders and replays real %s without backend calls", async (name) => {
  const stubs = Object.fromEntries(names.filter((sibling) => sibling !== name).map((sibling) => [sibling, true]));
  page = mount(AiGalleryPage, { attachTo: document.body, global: { stubs } });
  await flushPromises();
  expect(names.length).toBeGreaterThanOrEqual(3);
  expect(page.findAll("[data-ai-example]").map((example) => example.attributes("data-ai-example"))).toEqual(names);
  const selected = () => page.get(`[data-ai-example="${name}"]`);
  expect(selected().text().length).toBeGreaterThan(0);
  expect(fetch).not.toHaveBeenCalled();
  expect(open).not.toHaveBeenCalled();
  // The switches are the gallery's (ComponentGallery header), for every page.
  expect(page.find('.ai-gallery-controls [role="switch"]').exists()).toBe(false);
  const first = selected().element;
  useGalleryMotion().slow.value = true;
  await flushPromises();
  expect(selected().element).not.toBe(first);
  expect(selected().text().length).toBeGreaterThan(0);
  expect(fetch).not.toHaveBeenCalled();
  expect(open).not.toHaveBeenCalled();
});
