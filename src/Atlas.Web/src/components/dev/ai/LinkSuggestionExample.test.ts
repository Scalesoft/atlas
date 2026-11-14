import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import LinkSuggestionExample from "./LinkSuggestionExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); vi.restoreAllMocks(); });

it("shows the page editor with link mentions, without the AI glyph or a server", async () => {
  const fetch = vi.spyOn(globalThis, "fetch");
  wrapper = mount(LinkSuggestionExample, { attachTo: document.body });
  expect(wrapper.text()).toContain("Ponechat text jako odkaz");
  expect(wrapper.text()).toContain("Nahradit názvem stránky");
  expect(wrapper.findComponent({ name: "MarkdownEditor" }).exists()).toBe(false);
  await wrapper.get("[data-link-suggestion-start]").trigger("click");
  await vi.waitFor(() => expect(wrapper!.findComponent({ name: "MarkdownEditor" }).exists()).toBe(true), { timeout: 25_000 });
  const plugins = wrapper.getComponent({ name: "MarkdownEditor" }).props("plugins") as { id: string }[];
  expect(plugins.map(plugin => plugin.id)).toContain("assist");
  await flushPromises();
  expect(wrapper.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
  expect(fetch).not.toHaveBeenCalled();
}, 30_000); // The editor, the module and the adapter load cold.
