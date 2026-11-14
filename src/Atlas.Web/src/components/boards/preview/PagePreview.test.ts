import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { ApiError } from "../../../api";
import type { ChannelListener } from "../../../live/liveConnection";
import type { ObjectListener } from "../../../live/viewSession";
import { h as vnode, ref } from "vue";
import { boardCanvasKey } from "../canvas/boardCanvasContext";
import PagePreview, { previewLingerMs, previewRefreshDelayMs } from "./PagePreview.vue";

// The page's live editor in place (BoardPageEditor `inline`): what it was given, and its `entered` and `close` driven by the test.
const inlineEditor = vi.hoisted(() => ({ props: [] as Record<string, unknown>[] }));
vi.mock("./BoardPageEditor.vue", async () => {
  const { defineComponent, h } = await import("vue");
  return { __esModule: true, default: defineComponent({
    props: { pageId: String, inline: Boolean, at: Object },
    emits: ["entered", "close"],
    setup(props, { emit }) {
      inlineEditor.props.push(props);
      return () => h("div", { class: "board-page-editor stub-inline", "data-board-page-editor": "" }, [
        h("button", { class: "stub-entered", onClick: () => emit("entered") }), h("button", { class: "stub-close", onClick: () => emit("close") })]);
    },
  }) };
});

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); vi.useRealTimers(); });

const pageId = "3F2B8C1E-5A4D-4E6F-9A1B-2C3D4E5F6A7B";
const h = (level: number, headingId: string, text: string) => ({ type: "heading", attrs: { level, headingId }, content: [{ type: "text", text }] });
const p = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const contentJson = (...content: object[]) => JSON.stringify({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }], document: { type: "doc", content } });
const runbook = contentJson(p("Úvod"), h(2, "postup", "Postup"), p("Krok jedna"), h(2, "kontakty", "Kontakty"), p("Jana"));

/** A live connection whose channels the test drives. */
function fakeLive() {
  const listeners = new Map<string, ChannelListener>();
  const routes = new Map<string, (payload: unknown) => string | undefined>();
  return {
    listeners, routes,
    route: vi.fn((event: string, channelOf: (payload: unknown) => string | undefined) => { routes.set(event, channelOf); }),
    subscribe: vi.fn((name: string, listener: ChannelListener) => {
      listeners.set(name, listener);
      return { presence: vi.fn(), close: vi.fn(() => { listeners.delete(name); }) };
    }),
  };
}
function preview(props: Record<string, unknown>) {
  const live = fakeLive();
  const load = vi.fn(async () => ({ contentJson: runbook }));
  wrapper = mount(PagePreview, { props: { pageId, live, load, ...props }, attachTo: document.body });
  return { live, load };
}

describe("PagePreview", () => {
  it("renders the page read through the page API, only once the card is visible", async () => {
    const { load, live } = preview({ visible: false });
    await flushPromises();
    expect(load).not.toHaveBeenCalled();
    expect(live.subscribe).not.toHaveBeenCalled();
    await wrapper!.setProps({ visible: true });
    await vi.waitFor(() => expect(wrapper!.text()).toContain("Krok jedna"));
    expect(load).toHaveBeenCalledWith(pageId, expect.any(AbortSignal));
    expect(wrapper!.text()).toContain("Úvod");
    expect(wrapper!.find(".page-preview-scroll.nodrag.nowheel").exists()).toBe(true);
  });

  // Owner, 4.0: nothing of a composed deployment board could be moved. Vue Flow starts no drag inside `.nodrag`, and the whole card was one.
  it("leaves a card of its own (select) free to be dragged: none of it is a no-drag area, as a preview's text is", async () => {
    wrapper = mount(PagePreview, { props: { pageId, live: fakeLive(), load: async () => ({ contentJson: runbook }), select: () => ({ own: true }) },
      slots: { default: () => vnode("div", { "data-own-card": "" }) }, attachTo: document.body });
    await vi.waitFor(() => expect(wrapper!.find("[data-own-card]").exists()).toBe(true));
    expect(wrapper!.get("[data-own-card]").element.closest(".nodrag")).toBeNull();
    expect(wrapper!.find(".page-preview-scroll.nowheel").exists()).toBe(true);
  });

  it("renders one section: its heading up to the next heading of the same level", async () => {
    preview({ anchor: "postup" });
    await vi.waitFor(() => expect(wrapper!.text()).toContain("Krok jedna"));
    expect(wrapper!.text()).not.toContain("Úvod");
    expect(wrapper!.text()).not.toContain("Jana");
  });

  it("says when the section is gone and offers the whole page", async () => {
    preview({ anchor: "smazana" });
    await vi.waitFor(() => expect(wrapper!.find("[data-page-preview-missing]").exists()).toBe(true));
    expect(wrapper!.text()).toContain("Sekce už ve stránce není");
    await wrapper!.get("[data-page-preview-missing] button").trigger("click");
    expect(wrapper!.emitted("wholePage")).toHaveLength(1);
    await wrapper!.setProps({ anchor: null });
    await vi.waitFor(() => expect(wrapper!.text()).toContain("Jana"));
  });

  // D10, K13: on a board read only (a reader of the board, its lock) the page is not edited from its card either.
  it("is never entered on a board that is read only, nor by who may only read the page", async () => {
    const load = vi.fn(async () => ({ contentJson: runbook, canEdit: true, title: "Příručka" }));
    wrapper = mount(PagePreview, { props: { pageId, live: fakeLive(), load, editing: true }, attachTo: document.body,
      global: { provide: { [boardCanvasKey as symbol]: { canEdit: ref(false) } } } });
    await vi.waitFor(() => expect(wrapper!.emitted("done")).toHaveLength(1));
    expect(wrapper!.text()).toContain("Krok jedna");
    expect(wrapper!.find("[data-board-page-editor]").exists()).toBe(false);
    wrapper!.unmount();
    preview({ editing: true });
    await vi.waitFor(() => expect(wrapper!.emitted("done")).toHaveLength(1));
    expect(wrapper!.find("[data-board-page-editor]").exists()).toBe(false);
  });

  // 3.6 (owner, 2026-11-06): no „Upravit“; the writer clicks into the text and the page's editor takes the preview's place there.
  it("has no „Upravit“ and is edited in place: the editor builds over the preview and takes its place once entered (COL2-9)", async () => {
    inlineEditor.props.length = 0;
    preview({ load: vi.fn(async () => ({ contentJson: runbook, canEdit: true, title: "Příručka" })) });
    await vi.waitFor(() => expect(wrapper!.text()).toContain("Krok jedna"));
    expect(wrapper!.find("[data-board-edit-page]").exists()).toBe(false);
    expect(wrapper!.text()).not.toContain("Upravit");
    expect(wrapper!.get(".page-preview-scroll").classes()).not.toContain("has-edit");
    expect(wrapper!.find("[data-board-page-editor]").exists()).toBe(false);

    await wrapper!.setProps({ editing: true, at: { x: 40, y: 50 } });
    await vi.waitFor(() => expect(wrapper!.find(".stub-inline").exists()).toBe(true));
    expect(inlineEditor.props.at(-1)).toMatchObject({ pageId, inline: true, at: { x: 40, y: 50 } });
    // Unseen over the preview until it can take the caret; then the preview gives way.
    expect(wrapper!.get(".stub-inline").classes()).toContain("is-entering");
    expect(wrapper!.find(".page-preview-content").exists()).toBe(true);
    await wrapper!.get(".stub-entered").trigger("click");
    expect(wrapper!.get(".stub-inline").classes()).not.toContain("is-entering");
    await vi.waitFor(() => expect(wrapper!.find(".page-preview-content").exists()).toBe(false));
    expect(wrapper!.emitted("done")).toBeUndefined();

    // Esc or a press outside (the editor's `close`): the board ends the editing and the preview comes back.
    await wrapper!.get(".stub-close").trigger("click");
    expect(wrapper!.emitted("done")).toHaveLength(1);
    await wrapper!.setProps({ editing: false, at: undefined });
    expect(wrapper!.find(".stub-inline").exists()).toBe(false);
    await vi.waitFor(() => expect(wrapper!.find(".page-preview-content").exists()).toBe(true));
  });

  it("aims Enter and F2 at the start of what the preview shows", async () => {
    inlineEditor.props.length = 0;
    preview({ load: vi.fn(async () => ({ contentJson: runbook, canEdit: true })), editing: true });
    await vi.waitFor(() => expect(wrapper!.find(".stub-inline").exists()).toBe(true));
    expect(inlineEditor.props.at(-1)!.at).toBeUndefined();
    await wrapper!.get(".stub-entered").trigger("click");
    expect(inlineEditor.props.at(-1)!.at).toEqual({ x: expect.any(Number), y: expect.any(Number) });
  });

  it("shows a page the reader may not see as locked, without any of its content", async () => {
    const live = fakeLive();
    wrapper = mount(PagePreview, { props: { pageId, live, load: async () => { throw new ApiError(404, "Nenalezeno"); } }, attachTo: document.body });
    await vi.waitFor(() => expect(wrapper!.find("[data-page-preview-locked]").exists()).toBe(true));
    expect(wrapper!.text()).toBe("Stránka není dostupná");
  });

  it("listens to the page's channel while visible and reloads after a change settles", async () => {
    const { live, load } = preview({});
    await vi.waitFor(() => expect(wrapper!.text()).toContain("Krok jedna"));
    expect(live.routes.get("PageChanged")?.({ pageId, kind: "working" })).toBe(`page:${pageId.toLowerCase()}`);
    const listener = live.listeners.get(`page:${pageId.toLowerCase()}`)!;
    expect(listener).toBeDefined();

    vi.useFakeTimers();
    load.mockResolvedValue({ contentJson: contentJson(p("Nový obsah")) });
    listener.message!("PageChanged", { pageId, kind: "working" });
    listener.message!("PageChanged", { pageId, kind: "published" });
    await vi.advanceTimersByTimeAsync(previewRefreshDelayMs - 1);
    expect(load).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(load).toHaveBeenCalledTimes(2);
    vi.useRealTimers();
    await vi.waitFor(() => expect(wrapper!.text()).toContain("Nový obsah"));
  });

  it("reloads after a reconnect, and on its return when a change came while it was off screen or it stopped listening", async () => {
    const { live, load } = preview({});
    await vi.waitFor(() => expect(load).toHaveBeenCalledTimes(1));
    const name = `page:${pageId.toLowerCase()}`;
    vi.useFakeTimers();
    live.listeners.get(name)!.subscribed!({ sequence: 1 });
    await vi.advanceTimersByTimeAsync(previewRefreshDelayMs);
    expect(load).toHaveBeenCalledTimes(1);
    live.listeners.get(name)!.subscribed!({ sequence: 2 });
    await vi.advanceTimersByTimeAsync(previewRefreshDelayMs);
    expect(load).toHaveBeenCalledTimes(2);

    await wrapper!.setProps({ visible: false });
    await wrapper!.setProps({ visible: true });
    expect(load).toHaveBeenCalledTimes(2);
    await wrapper!.setProps({ visible: false });
    live.listeners.get(name)!.message!("PageChanged", { pageId, kind: "published" });
    await vi.advanceTimersByTimeAsync(previewRefreshDelayMs);
    expect(load).toHaveBeenCalledTimes(2);
    await wrapper!.setProps({ visible: true });
    expect(load).toHaveBeenCalledTimes(3);

    await wrapper!.setProps({ visible: false });
    await vi.advanceTimersByTimeAsync(previewLingerMs);
    expect(live.listeners.size).toBe(0);
    await wrapper!.setProps({ visible: true });
    expect(load).toHaveBeenCalledTimes(4);
    expect(live.listeners.has(name)).toBe(true);
  });

  it("on a board listens to its page in the board's view session, not on a channel of its own", async () => {
    const watched = new Map<string, ObjectListener>();
    const view = { watch: vi.fn((id: string, listener: ObjectListener) => { watched.set(id, listener); return () => watched.delete(id); }), viewport: vi.fn(), presence: vi.fn(), close: vi.fn() };
    const { live, load } = preview({ view: () => view });
    await vi.waitFor(() => expect(load).toHaveBeenCalledTimes(1));
    expect(live.subscribe).not.toHaveBeenCalled();
    expect(view.watch).toHaveBeenCalledWith(pageId, expect.anything());
    vi.useFakeTimers();
    watched.get(pageId)!.changed!(null, 4);
    await vi.advanceTimersByTimeAsync(previewRefreshDelayMs);
    expect(load).toHaveBeenCalledTimes(2);
    watched.get(pageId)!.missed!(6);
    await vi.advanceTimersByTimeAsync(previewRefreshDelayMs);
    expect(load).toHaveBeenCalledTimes(3);
    wrapper!.unmount();
    wrapper = undefined;
    expect(watched.size).toBe(0);
  });
});
