import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import AiPreferencesExample from "./AiPreferencesExample.vue";
import SpaceAiExample from "./SpaceAiExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

it("shows the preferences form with local data, saves a change and carries no AI glyph", async () => {
  wrapper = mount(AiPreferencesExample, { attachTo: document.body });
  await flushPromises();
  const first = wrapper.findAll(".ai-preferences")[0]!;
  await first.findAll('[data-ai-suggestions-mode] [role="radio"]').find(item => item.text().includes("Vypnuto"))!.trigger("click");
  await first.get("[data-save-bar-save]").trigger("click");
  await flushPromises();
  expect(first.get('[data-summary="mode"]').text()).toContain("Vypnuto");
  expect(wrapper.text()).toContain("Proč ne nová komponenta");
  expect(wrapper.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
});

it("shows the space switch with its impact preview, a conflict and the hidden section", async () => {
  wrapper = mount(SpaceAiExample, { attachTo: document.body });
  await flushPromises();
  const switches = wrapper.findAll("[data-space-ai-included]");
  expect(switches).toHaveLength(2);
  await switches[0]!.trigger("click");
  expect(wrapper.find("[data-space-ai-impact]").exists()).toBe(true);
  await switches[1]!.trigger("click");
  await wrapper.findAll("[data-save-bar-save]")[1]!.trigger("click");
  await flushPromises();
  expect(wrapper.find("[data-space-ai-conflict]").exists()).toBe(true);
  expect(wrapper.text()).toContain("Tato sekce neexistuje");
});
