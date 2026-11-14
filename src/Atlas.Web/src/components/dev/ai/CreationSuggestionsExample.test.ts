import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import CreationSuggestionsExample from "./CreationSuggestionsExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

it("shows meaning matches, applies the placement into the field, and keeps the AI glyph out", async () => {
  wrapper = mount(CreationSuggestionsExample, { attachTo: document.body });
  expect(wrapper.text()).toContain("podobné tématem");
  const field = () => wrapper!.get("[data-location-value]").text();
  expect(field()).toBe("IT a infrastruktura");
  await wrapper.get("[data-location-value]").element.closest(".creation-stage")!.querySelector<HTMLElement>("[data-suggestion-apply]")!.click();
  await flushPromises();
  expect(field()).toBe("IT a infrastruktura / Síť / VPN");
  await wrapper.get("[data-template-suggestion] [data-suggestion-dismiss]").trigger("click");
  await flushPromises();
  expect(wrapper.findAll("[data-template-suggestion]").length).toBe(0);
  expect(wrapper.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
});
