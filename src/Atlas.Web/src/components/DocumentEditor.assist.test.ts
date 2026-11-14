import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { MarkdownPlugin, MarkdownRichValue } from "@atlas/markdown/core";
import { emptyDocument } from "../document";
import type { PageSourceDraftOptions } from "../pageSourceDraft";
import type { AtlasAssistContext, AtlasAssistFactory } from "../assist/types";
import DocumentEditor from "./DocumentEditor.vue";

// Plan §0 and §12 H.4/H.7: the assist plugin belongs to the live editor only, and with assistance off (no factory) the
// page editor is exactly what it is without the module. The source draft is replaced by a fake so Monaco never loads.
const fake = vi.hoisted(() => ({ options: undefined as unknown }));
vi.mock("../pageSource", async original => ({ ...await original<object>(), prefetchPageSource: vi.fn() }));
vi.mock("../pageSourceDraft", async original => {
  const actual = await original<typeof import("../pageSourceDraft")>();
  const { computed, ref } = await import("vue");
  return {
    ...actual,
    usePageSourceDraft(options: unknown) {
      fake.options = options;
      const text = ref(""), base = ref(""), busy = ref(false);
      return { text, base, busy, dirty: computed(() => false), apply: vi.fn(async () => true), discard: vi.fn(), close: vi.fn() };
    },
  };
});

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); vi.restoreAllMocks(); });

function probe() {
  const created = vi.fn(() => ({}));
  const plugin: MarkdownPlugin = { id: "atlas-assist-probe", apiVersion: 1, create: created };
  const contexts: AtlasAssistContext[] = [];
  const factory = vi.fn<AtlasAssistFactory>(context => { contexts.push(context); return plugin; });
  return { plugin, created, factory, contexts };
}
const value = (): MarkdownRichValue => ({ ...emptyDocument(), document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Chyba je v ATL-17." }] }] } });
const props = (extra: Record<string, unknown> = {}) => ({
  value: value(), readonly: false, dark: false, accent: "blue" as const, spaceId: "space", pageId: "page", documentTitle: "Runbook",
  canEdit: true, canEditSource: true, saveSource: vi.fn(async () => ({ ok: true as const })), ...extra,
});
const pluginIds = (view: VueWrapper, name: "MarkdownEditor") =>
  (view.getComponent({ name }).props("plugins") as MarkdownPlugin[]).map(plugin => plugin.id);
type Exposed = { capture(): { contentJson: string; markdown: string }; openSource(): boolean };

describe("the assist plugin in the page editor", () => {
  it("adds the assist plugin only to the page's editor, never to source or save projections", async () => {
    const { plugin, created, factory } = probe();
    wrapper = mount(DocumentEditor, { props: props({ assist: factory }), attachTo: document.body });
    await vi.waitFor(() => expect(wrapper!.emitted("ready")).toBeTruthy());
    await vi.waitFor(() => expect(wrapper!.findComponent({ name: "MarkdownEditor" }).exists()).toBe(true));
    const live = pluginIds(wrapper, "MarkdownEditor");
    expect(live.filter(id => id === plugin.id)).toHaveLength(1);
    expect(live.at(-1)).toBe(plugin.id);
    expect(factory).toHaveBeenCalledTimes(1);
    expect(created).toHaveBeenCalledTimes(1);

    // Re-renders keep the very same list (the editor refuses a changed one) and never ask again.
    const list = wrapper.getComponent({ name: "MarkdownEditor" }).props("plugins");
    await wrapper.setProps({ documentTitle: "Runbook 2", assist: probe().factory });
    await flushPromises();
    expect(wrapper.getComponent({ name: "MarkdownEditor" }).props("plugins")).toBe(list);

    // Save projection and the page source build their own content plugins: no assist among them.
    const captured = (wrapper.vm as unknown as Exposed).capture();
    expect(captured.markdown).toContain("ATL-17");
    expect((wrapper.vm as unknown as Exposed).openSource()).toBe(true);
    await flushPromises();
    const sourcePlugins = (fake.options as PageSourceDraftOptions).plugins!();
    expect(sourcePlugins.map(item => item.id)).not.toContain(plugin.id);
    expect(factory).toHaveBeenCalledTimes(1);
    expect(created).toHaveBeenCalledTimes(1);
  });

  it("produces the same document JSON, Markdown and slash items with assist off as without the plugin", async () => {
    const without = mount(DocumentEditor, { props: props(), attachTo: document.body });
    const off = mount(DocumentEditor, { props: props({ assist: null }), attachTo: document.body });
    try {
      for (const view of [without, off]) await vi.waitFor(() => expect(view.findComponent({ name: "MarkdownEditor" }).exists()).toBe(true));
      await flushPromises();
      const editors = [without, off].map(view => view.getComponent({ name: "MarkdownEditor" }));
      const ids = editors.map(editor => (editor.props("plugins") as MarkdownPlugin[]).map(plugin => plugin.id));
      expect(ids[1]).toEqual(ids[0]);
      expect(ids[0]).not.toContain("assist");
      const captured = [without, off].map(view => (view.vm as unknown as Exposed).capture());
      expect(captured[1]).toEqual(captured[0]);
      // The slash menu: the same actions in the same order.
      const slash = editors.map(editor => {
        const controller = (editor.vm as unknown as { controller?: { actions?: { slash?: { id: string }[] } } }).controller;
        return controller?.actions?.slash?.map(action => action.id) ?? null;
      });
      expect(slash[0]?.length).toBeGreaterThan(5);
      expect(slash[1]).toEqual(slash[0]);
      // No assist DOM, no assist stylesheet.
      expect(document.querySelector('[class*="md-assist"]')).toBeNull();
      expect([...document.querySelectorAll("style")].some(style => style.textContent?.includes(".md-assist"))).toBe(false);
    } finally { without.unmount(); off.unmount(); }
  });

  // 3.6 opens the live editor before the chunk arrives (fe69fde01): the running editor attaches it in place, never re-keyed.
  it("attaches a chunk that arrives late to the running editor, with the same plugin list", async () => {
    const { plugin, created, factory } = probe();
    let assist: AtlasAssistFactory | null = null;
    wrapper = mount(DocumentEditor, { props: props({ assist, assistExpected: true }), attachTo: document.body });
    await vi.waitFor(() => expect(wrapper!.findComponent({ name: "MarkdownEditor" }).exists()).toBe(true));
    const list = wrapper.getComponent({ name: "MarkdownEditor" }).props("plugins");
    assist = factory;
    await wrapper.setProps({ assist });
    await flushPromises();
    expect(wrapper.getComponent({ name: "MarkdownEditor" }).props("plugins")).toBe(list);
    expect(pluginIds(wrapper, "MarkdownEditor")).not.toContain(plugin.id);
    expect(factory).toHaveBeenCalledTimes(1);
    expect(created).toHaveBeenCalledTimes(1);
  });

  it("gives the adapter the page's own directories and opens the picker in replace mode", async () => {
    const { factory, contexts } = probe();
    wrapper = mount(DocumentEditor, { props: props({ assist: factory }), attachTo: document.body });
    await vi.waitFor(() => expect(contexts).toHaveLength(1));
    const context = contexts[0]!;
    expect(context.pageId()).toBe("page");
    const pending = context.pickSecret({ mode: "replace", initialQuery: "Nouzový účet", signal: new AbortController().signal });
    await vi.waitFor(() => expect(document.body.querySelector("[data-secret-picker-mode=replace]")).not.toBeNull());
    expect(document.body.textContent).toContain("Uložte heslo do trezoru (KeePass) a vyberte ho.");
    expect((document.body.querySelector(".secret-picker input") as HTMLInputElement).value).toBe("Nouzový účet");
    // An editor popover now: Esc closes it and the request is answered null.
    document.body.querySelector(".secret-picker")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    expect(await pending).toBeNull();
  });
});
