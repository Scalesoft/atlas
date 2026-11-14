import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import AiCapabilitiesExample from "./AiCapabilitiesExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

it("makes the surfaces vanish silently and replaces a started action with the neutral sentence", async () => {
  wrapper = mount(AiCapabilitiesExample, { attachTo: document.body });
  const stage = () => wrapper!.get("[data-caps-stage]");
  expect(stage().find("[data-caps-semantic]").exists()).toBe(true);
  expect(stage().find("[data-caps-related]").exists()).toBe(true);
  expect(stage().find("[data-caps-answer]").exists()).toBe(true);
  expect(stage().find("[data-caps-assistant]").exists()).toBe(true);
  const run = wrapper.findAll("button").find(b => b.text().startsWith("Spustit akci"))!;
  await run.trigger("click");
  await flushPromises();
  expect(wrapper.get("[data-caps-outcome]").text()).toBe("Tahle funkce teď není k dispozici.");
  expect(stage().find("[data-caps-semantic], [data-caps-related], [data-caps-placement], [data-caps-answer], [data-caps-assistant]").exists()).toBe(false);
  expect(wrapper.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
});
