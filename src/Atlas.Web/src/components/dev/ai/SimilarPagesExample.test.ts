import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import SimilarPagesExample from "./SimilarPagesExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.useRealTimers(); });

it("types the title, shows the panel 400 ms later, and keeps the AI glyph out of it", async () => {
  vi.useFakeTimers();
  wrapper = mount(SimilarPagesExample, { attachTo: document.body });
  const panel = () => wrapper!.find(".similar-stage [data-similar-pages]");
  await vi.advanceTimersByTimeAsync(1700);
  expect(panel().exists()).toBe(false);
  await vi.advanceTimersByTimeAsync(1000);
  await flushPromises();
  expect((wrapper.get("input[name='similar-title']").element as HTMLInputElement).value).toBe("Připojení na VPN z mobilu");
  expect(panel().exists()).toBe(true);
  expect(panel().findAll("[data-similar-page]")).toHaveLength(3);
  await panel().get("[data-suggestion-dismiss]").trigger("click");
  await flushPromises();
  expect(panel().exists()).toBe(false);
  // The static cases: a section match opens the heading, a missing score draws no meter.
  expect(wrapper.find('a[href*="heading=z-mobilu"]').exists()).toBe(true);
  expect(wrapper.text()).toContain("sekce „Z mobilu“");
  expect(wrapper.findAll(".similar-grid .similar-meter").length).toBeGreaterThan(0);
  expect(wrapper.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
});
