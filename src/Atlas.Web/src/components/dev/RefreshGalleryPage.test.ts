import { readdirSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import RefreshGalleryPage from "./RefreshGalleryPage.vue";
import { resetGalleryMotion } from "./galleryMotion";

// Every refresh example renders on its own, from local data only. Sibling examples are stubbed, as in AiGalleryPage.test.
const names = readdirSync(resolve(import.meta.dirname, "refresh"))
  .filter((file) => file.endsWith("Example.vue")).sort().map((file) => file.replace(/\.vue$/, ""));
let page: ReturnType<typeof mount>;
let fetch: ReturnType<typeof vi.spyOn>;
beforeEach(() => {
  resetGalleryMotion();
  Element.prototype.animate = vi.fn(() => ({ finished: Promise.resolve(), cancel: vi.fn() }) as unknown as Animation) as unknown as Element["animate"];
  fetch = vi.spyOn(globalThis, "fetch");
});
afterEach(() => {
  page?.unmount();
  resetGalleryMotion();
  delete (Element.prototype as Partial<Element>).animate;
  vi.restoreAllMocks();
  document.body.replaceChildren();
});

it("lists the examples in file-name order", async () => {
  page = mount(RefreshGalleryPage, { attachTo: document.body, global: { stubs: Object.fromEntries(names.map((name) => [name, true])) } });
  await flushPromises();
  expect(page.findAll("[data-refresh-example]").map((example) => example.attributes("data-refresh-example"))).toEqual(names);
});

it("has examples to render", () => {
  expect(names.length).toBeGreaterThan(0);
});

it.each(names)("renders %s without backend calls", async (name) => {
  const stubs = Object.fromEntries(names.filter((sibling) => sibling !== name).map((sibling) => [sibling, true]));
  page = mount(RefreshGalleryPage, { attachTo: document.body, global: { stubs } });
  await flushPromises();
  expect(page.get(`[data-refresh-example="${name}"]`).text().length).toBeGreaterThan(0);
  expect(fetch).not.toHaveBeenCalled();
});
