import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import EditorAssistExample from "./EditorAssistExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); vi.restoreAllMocks(); });

it("shows the page editor with assistance, without the AI glyph or a server", async () => {
  const fetch = vi.spyOn(globalThis, "fetch");
  wrapper = mount(EditorAssistExample, { attachTo: document.body });
  // Nothing heavy loads until the example is opened.
  expect(wrapper.findComponent({ name: "MarkdownEditor" }).exists()).toBe(false);
  await wrapper.get("[data-editor-assist-start]").trigger("click");
  // The pinned editor (0.9.0) and source mode both have the module.
  await vi.waitFor(() => expect(wrapper!.findComponent({ name: "MarkdownEditor" }).exists()).toBe(true), { timeout: 25_000 });
  const plugins = wrapper.getComponent({ name: "MarkdownEditor" }).props("plugins") as { id: string }[];
  expect(plugins.map(plugin => plugin.id)).toContain("assist");
  await flushPromises();
  expect(wrapper.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
  expect(fetch).not.toHaveBeenCalled();
}, 30_000); // The editor, the module and the adapter load cold.
