import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { Editor } from "@tiptap/core";
import type { CommentApi, CommentPage, ObjectComment } from "../../comments/commentApi";
import type { ObjectListener, ViewSessions } from "../../live/viewSession";
import { drafts, forgetUnsavedWork, hasUnsavedWork } from "../../lib/unsavedWork";
import { requestedQuote } from "../../comments/commentFocus";
import CommentComposer from "./CommentComposer.vue";
import CommentThread from "./CommentThread.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); vi.unstubAllGlobals(); drafts.clear(); });

const comment = (id: string, overrides: Partial<ObjectComment> = {}): ObjectComment => ({
  id, authorId: "00000000-0000-0000-0000-0000000000a1", authorName: "Jana Nováková", body: `Text **${id}**`, anchor: null,
  createdAt: "2026-10-26T09:00:00Z", editedAt: null, deleted: false, canEdit: false, canDelete: false, ...overrides,
});
const page = (items: ObjectComment[], hasMore = false, canPost = true): CommentPage => ({ items, hasMore, count: items.filter(x => !x.deleted).length, canPost });
function fakeApi(first: CommentPage): CommentApi {
  return {
    page: vi.fn(async () => first),
    post: vi.fn(async (_id: string, body: string, anchor?: string | null) => comment("mine", { body, anchor: anchor ?? null, createdAt: "2026-10-26T12:00:00Z", canEdit: true, canDelete: true })),
    edit: vi.fn(async (_id: string, id: string, body: string) => comment(id, { body, editedAt: "2026-10-26T12:30:00Z", canEdit: true, canDelete: true })),
    remove: vi.fn(async () => undefined),
    counts: vi.fn(async () => ({})),
  };
}
/** A view session that hands the test the thread's listener. */
function fakeViews() {
  const listeners: ObjectListener[] = [];
  const views: Pick<ViewSessions, "open"> = {
    open: vi.fn(() => ({ watch: (_id: string, listener: ObjectListener) => { listeners.push(listener); return () => {}; }, viewport() {}, presence() {}, close: vi.fn() })),
  };
  return { views, listeners };
}
async function mounted(api: CommentApi, props: Record<string, unknown> = {}) {
  wrapper = mount(CommentThread, { props: { objectId: "obj-1", api, lazy: false, ...props }, attachTo: document.body });
  await flushPromises();
  return wrapper;
}

describe("CommentThread", () => {
  it("reads nothing before it comes near the viewport", async () => {
    // The page's one proximity observer (observeMarkdownProximity) watches the thread and hydrates it in an idle batch.
    let seen: ((entries: { target: Element; isIntersecting: boolean }[]) => void) | undefined, watched: Element | undefined;
    vi.stubGlobal("IntersectionObserver", class { constructor(callback: typeof seen) { seen = callback; } observe(element: Element) { watched = element; } unobserve() {} disconnect() {} });
    const api = fakeApi(page([comment("a")]));
    wrapper = mount(CommentThread, { props: { objectId: "obj-1", api }, attachTo: document.body });
    await flushPromises();
    expect(api.page).not.toHaveBeenCalled();
    seen!([{ target: watched!, isIntersecting: true }]);
    await new Promise(resolve => setTimeout(resolve, 5));
    await flushPromises();
    expect(api.page).toHaveBeenCalledWith("obj-1");
    expect(wrapper.findAll("[data-comment-id]")).toHaveLength(1);
  });

  it("shows the timeline: author, Markdown, „upraveno“, the placeholder of a deleted comment and only the allowed actions", async () => {
    const api = fakeApi(page([
      comment("mine", { canEdit: true, canDelete: true, editedAt: "2026-10-26T10:00:00Z" }),
      comment("gone", { deleted: true, body: "" }),
      comment("other", { anchor: "postup" }),
    ]));
    const view = await mounted(api, { title: "Komentáře" });
    const rows = view.findAll("[data-comment-id]");
    expect(rows.map(row => row.attributes("data-comment-id"))).toEqual(["mine", "gone", "other"]);
    expect(rows[0]!.text()).toContain("Jana Nováková");
    expect(rows[0]!.get("strong.comment-author").text()).toBe("Jana Nováková");
    expect(rows[0]!.find("[data-comment-edited]").exists()).toBe(true);
    expect(rows[0]!.get(".comment-body strong").text()).toBe("mine");
    expect(rows[0]!.findAll("[data-comment-edit], [data-comment-delete]")).toHaveLength(2);
    expect(rows[1]!.get("[data-comment-placeholder]").text()).toBe("Komentář byl odstraněn");
    expect(rows[1]!.find("[data-comment-edit]").exists()).toBe(false);
    expect(rows[2]!.find("[data-comment-edit], [data-comment-delete]").exists()).toBe(false);
    await rows[2]!.get("[data-comment-anchor]").trigger("click");
    expect(view.emitted("anchor")).toEqual([["postup"]]);
    expect(view.get("[data-comment-count]").text()).toBe("2");
  });

  it("posts at the top with the thread's anchor and empties the field", async () => {
    const api = fakeApi(page([comment("a")]));
    const view = await mounted(api, { anchor: "item-7" });
    view.findComponent(CommentComposer).vm.$emit("submit", "Nový **komentář**");
    await flushPromises();
    expect(api.post).toHaveBeenCalledWith("obj-1", "Nový **komentář**", "item-7");
    expect(view.findAll("[data-comment-id]").map(row => row.attributes("data-comment-id"))).toEqual(["mine", "a"]);
    expect(view.emitted("count")!.at(-1)).toEqual([2]);
  });

  it("edits in place and deletes after a confirmation, leaving the placeholder", async () => {
    const api = fakeApi(page([comment("mine", { canEdit: true, canDelete: true })]));
    const view = await mounted(api);
    await view.get("[data-comment-edit]").trigger("click");
    const editor = view.findAllComponents(CommentComposer).at(-1)!;
    editor.vm.$emit("submit", "Opraveno");
    await flushPromises();
    expect(api.edit).toHaveBeenCalledWith("obj-1", "mine", "Opraveno");
    expect(view.find("[data-comment-edited]").exists()).toBe(true);

    await view.get("[data-comment-delete]").trigger("click");
    await flushPromises();
    document.querySelector<HTMLButtonElement>("[data-confirm-action]")!.click();
    await flushPromises();
    expect(api.remove).toHaveBeenCalledWith("obj-1", "mine");
    expect(view.get("[data-comment-placeholder]").text()).toBe("Komentář byl odstraněn");
  });

  it("loads older comments and hears a colleague's new comment through the view session", async () => {
    const api = fakeApi(page([comment("b", { createdAt: "2026-10-26T10:00:00Z" })], true));
    const { views, listeners } = fakeViews();
    const view = await mounted(api, { viewKey: "board:board-1", views });
    expect(views.open).toHaveBeenCalledWith("board:board-1");

    api.page = vi.fn(async (_id: string, before?: string) => (before ? page([comment("a", { createdAt: "2026-10-26T08:00:00Z" })]) : page([])));
    await view.get("[data-comment-older]").trigger("click");
    await flushPromises();
    expect(api.page).toHaveBeenCalledWith("obj-1", "b");
    expect(view.find("[data-comment-older]").exists()).toBe(false);

    api.page = vi.fn(async () => page([comment("c", { createdAt: "2026-10-26T11:00:00Z", authorName: "Petr" }), comment("b", { createdAt: "2026-10-26T10:00:00Z" })]));
    listeners[0]!.changed!({ kind: "working" }, 3);
    await flushPromises();
    expect(api.page).not.toHaveBeenCalled();
    listeners[0]!.changed!({ kind: "comments", count: 3, latestId: "c" }, 3);
    await flushPromises();
    expect(view.findAll("[data-comment-id]").map(row => row.attributes("data-comment-id"))).toEqual(["c", "b", "a"]);
  });

  it("hides itself where comments are off and none were written, and says so where some were", async () => {
    let view = await mounted(fakeApi(page([], false, false)));
    expect(view.get("[data-comment-thread]").attributes("hidden")).toBeDefined();
    wrapper!.unmount();
    view = await mounted(fakeApi(page([comment("a")], false, false)));
    expect(view.get("[data-comment-thread]").attributes("hidden")).toBeUndefined();
    expect(view.find("[data-comments-off]").exists()).toBe(true);
    expect(view.find("[data-comment-composer]").exists()).toBe(false);
  });

  it("folds a page's empty discussion into one row that opens the field on demand; the dock shows the field at once", async () => {
    let view = await mounted(fakeApi(page([])), { title: "Komentáře" });
    expect(view.find("[data-comment-composer]").exists()).toBe(false);
    expect(view.find("[data-comments-empty]").exists()).toBe(false);
    await view.get("[data-comment-start]").trigger("click");
    expect(view.find("[data-comment-start]").exists()).toBe(false);
    view.findComponent(CommentComposer).vm.$emit("cancel");
    await flushPromises();
    expect(view.find("[data-comment-composer]").exists()).toBe(false);
    expect(view.find("[data-comment-start]").exists()).toBe(true);
    wrapper!.unmount();
    view = await mounted(fakeApi(page([])));
    expect(view.find("[data-comment-start]").exists()).toBe(false);
    expect(view.find("[data-comment-composer]").exists()).toBe(true);
    expect(view.find("[data-comments-empty]").exists()).toBe(true);
  });
});

describe("CommentThread quote", () => {
  const editor = () => document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor;
  afterEach(() => { requestedQuote.value = undefined; });

  it("starts a new comment with the text the page had selected as a Markdown quote, in the field, once (the page's Diskuse, Alt C)", async () => {
    const api = fakeApi(page([]));
    requestedQuote.value = "> Záloha běží každou noc\n\n";
    await mounted(api, { quotes: true });
    await vi.waitFor(() => expect(editor()?.getText()).toContain("Záloha běží každou noc"));
    expect(requestedQuote.value).toBeUndefined();
    expect(editor()!.state.doc.firstChild!.type.name).toBe("blockquote");
    wrapper!.findComponent(CommentComposer).vm.submit();
    await flushPromises();
    expect(vi.mocked(api.post).mock.calls[0]![1]).toBe("> Záloha běží každou noc");
  });

  it("leaves the selection alone in a thread that does not quote (a board's, the page's section)", async () => {
    requestedQuote.value = "> Něco\n\n";
    await mounted(fakeApi(page([comment("a")])));
    await vi.waitFor(() => expect(editor()).toBeDefined());
    expect(editor()!.getText()).toBe("");
    expect(requestedQuote.value).toBe("> Něco\n\n");
  });
});

describe("CommentThread send", () => {
  const editor = () => document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor;
  const enter = (init: KeyboardEventInit = {}) => document.querySelector(".ProseMirror")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true, ...init }));

  it("has the paper plane (Odeslat (Enter)), dim while empty and accent with text, and Enter sends while Shift+Enter writes a line", async () => {
    const api = fakeApi(page([]));
    await mounted(api);
    await vi.waitFor(() => expect(editor()).toBeDefined());
    const send = wrapper!.get(".send-button");
    expect(send.attributes("aria-label")).toBe("Odeslat (Enter)");
    expect(send.attributes("aria-disabled")).toBe("true");
    expect(wrapper!.find(".comment-composer-actions [data-comment-submit]").exists()).toBe(false);
    editor()!.commands.insertContent("Hotovo");
    await flushPromises();
    expect(send.attributes("data-ready")).toBeDefined();
    enter({ shiftKey: true });
    await flushPromises();
    expect(api.post).not.toHaveBeenCalled();
    enter();
    await flushPromises();
    expect(api.post).toHaveBeenCalledWith("obj-1", "Hotovo", null);
  });

  it("behaves the same in a board's thread (an anchored one): Enter sends, Ctrl+Enter is no longer needed", async () => {
    const api = fakeApi(page([]));
    await mounted(api, { anchor: "item-1" });
    await vi.waitFor(() => expect(editor()).toBeDefined());
    expect(wrapper!.find(".send-button").exists()).toBe(true);
    expect(wrapper!.text()).toContain("Enter odešle, Shift+Enter nový řádek");
    editor()!.commands.insertContent("Na tabuli");
    await flushPromises();
    enter();
    await flushPromises();
    expect(api.post).toHaveBeenCalledWith("obj-1", "Na tabuli", "item-1");
  });
});

describe("CommentThread draft", () => {
  it("keeps an unsent comment by its object and anchor when the thread goes, and opens the page's section with it (UX-P-6)", async () => {
    const editor = () => document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor;
    const api = fakeApi(page([]));
    await mounted(api, { anchor: "item-1" });
    await vi.waitFor(() => expect(editor()).toBeDefined());
    editor()!.commands.insertContent("Rozepsaný komentář");
    await flushPromises();
    expect(hasUnsavedWork()).toBe(true);
    wrapper!.unmount();
    expect(hasUnsavedWork()).toBe(true);

    // Another anchor of the same object starts empty.
    await mounted(api, { anchor: "item-2" });
    await vi.waitFor(() => expect(editor()).toBeDefined());
    expect(editor()!.getText()).toBe("");
    wrapper!.unmount();

    // The page's section without comments is folded, unless a draft waits in it.
    await mounted(api, { anchor: "item-1", title: "Komentáře" });
    await vi.waitFor(() => expect(editor()).toBeDefined());
    expect(editor()!.getText()).toBe("Rozepsaný komentář");
    wrapper!.findComponent(CommentComposer).vm.submit();
    await flushPromises();
    expect(api.post).toHaveBeenCalledWith("obj-1", "Rozepsaný komentář", "item-1");
    expect(hasUnsavedWork()).toBe(false);
  });

  it("keeps nothing of a comment thrown away with Zrušit, or of one sent while the thread went", async () => {
    const editor = () => document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor;
    const api = fakeApi(page([]));
    await mounted(api, { title: "Komentáře" });
    await wrapper!.get("[data-comment-start]").trigger("click");
    await vi.waitFor(() => expect(editor()).toBeDefined());
    editor()!.commands.insertContent("Nakonec ne");
    await flushPromises();
    await wrapper!.get("[data-comment-cancel]").trigger("click");
    await flushPromises();
    expect(hasUnsavedWork()).toBe(false);
    wrapper!.unmount();
    expect(drafts.size).toBe(0);

    let sent!: () => void;
    vi.mocked(api.post).mockImplementationOnce(async (_id, body) => { await new Promise<void>(resolve => { sent = resolve; }); return comment("mine", { body }); });
    await mounted(api);
    await vi.waitFor(() => expect(editor()).toBeDefined());
    editor()!.commands.insertContent("Odchází");
    await flushPromises();
    wrapper!.findComponent(CommentComposer).vm.submit();
    wrapper!.unmount();
    sent();
    await flushPromises();
    expect(drafts.size).toBe(0);
  });

  it("keeps nothing for the next account once the session ended (forgetUnsavedWork)", async () => {
    const editor = () => document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor;
    await mounted(fakeApi(page([])));
    await vi.waitFor(() => expect(editor()).toBeDefined());
    editor()!.commands.insertContent("Cizí koncept");
    await flushPromises();
    forgetUnsavedWork();
    wrapper!.unmount();
    expect(hasUnsavedWork()).toBe(false);
    await mounted(fakeApi(page([])));
    await vi.waitFor(() => expect(editor()).toBeDefined());
    expect(editor()!.getText()).toBe("");
  });
});

describe("CommentComposer", () => {
  it("sends the Markdown a page saves with Ctrl+Enter, nothing when empty", async () => {
    wrapper = mount(CommentComposer, { attachTo: document.body });
    await vi.waitFor(() => expect(document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor).toBeDefined());
    const press = () => document.querySelector(".ProseMirror")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", keyCode: 13, ctrlKey: true, bubbles: true, cancelable: true }));
    press();
    expect(wrapper.emitted("submit")).toBeUndefined();
    const editor = document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")!.editor!;
    editor.commands.insertContent("Díky ");
    editor.chain().toggleBold().insertContent("všem").run();
    await flushPromises();
    press();
    expect(wrapper.emitted("submit")).toEqual([["Díky **všem**"]]);
  });

  it("sends with a plain Enter only with enterSubmits; Shift+Enter is a new line, and the day chip resets to today after a send", async () => {
    wrapper = mount(CommentComposer, { props: { enterSubmits: true, dayChip: true }, attachTo: document.body });
    await vi.waitFor(() => expect(document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor).toBeDefined());
    const press = (shiftKey = false) => document.querySelector(".ProseMirror")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", keyCode: 13, shiftKey, bubbles: true, cancelable: true }));
    document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")!.editor!.commands.insertContent("Hotovo");
    await flushPromises();
    press(true);
    expect(wrapper.emitted("submit")).toBeUndefined();
    expect(wrapper.get("[data-comment-day]").text()).toBe("dnes ▾");
    await wrapper.get("input[type=date]").setValue("2026-10-01");
    expect(wrapper.get("[data-comment-day]").text()).toBe("1. 10. 2026 ▾");
    press();
    expect(wrapper.emitted("submit")).toEqual([["Hotovo", "2026-10-01"]]);
    (wrapper.vm as unknown as { reset(): void }).reset();
    await flushPromises();
    expect(wrapper.get("[data-comment-day]").text()).toBe("dnes ▾");
  });

  it("sends with Enter while a suggestion list is only hidden elsewhere on the page (the detail's dependency search), not while one is shown", async () => {
    wrapper = mount(CommentComposer, { props: { enterSubmits: true }, attachTo: document.body });
    await vi.waitFor(() => expect(document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor).toBeDefined());
    const list = document.body.appendChild(document.createElement("div"));
    list.className = "md-suggestion-list";
    document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")!.editor!.commands.insertContent("Hotovo");
    await flushPromises();
    const press = () => document.querySelector(".ProseMirror")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", keyCode: 13, bubbles: true, cancelable: true }));
    // jsdom lays nothing out: a shown list has client rects.
    const rects = vi.spyOn(Element.prototype, "getClientRects").mockReturnValue([{}] as unknown as DOMRectList);
    press();
    expect(wrapper.emitted("submit")).toBeUndefined();
    rects.mockRestore();
    press();
    expect(wrapper.emitted("submit")).toEqual([["Hotovo"]]);
    list.remove();
  });

  it("keeps Ctrl+Enter alone for the other composers: a plain Enter sends nothing", async () => {
    wrapper = mount(CommentComposer, { attachTo: document.body });
    await vi.waitFor(() => expect(document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor).toBeDefined());
    document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")!.editor!.commands.insertContent("Text");
    await flushPromises();
    document.querySelector(".ProseMirror")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", keyCode: 13, bubbles: true, cancelable: true }));
    expect(wrapper.emitted("submit")).toBeUndefined();
  });

  it("cancels with Escape and discards the draft (P3)", async () => {
    wrapper = mount(CommentComposer, { props: { cancelable: true }, attachTo: document.body });
    await vi.waitFor(() => expect(document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor).toBeDefined());
    const escape = () => document.querySelector(".ProseMirror")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")!.editor!.commands.insertContent("Rozepsáno");
    await flushPromises();
    escape();
    expect(wrapper.emitted("cancel")).toHaveLength(1);
    document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")!.editor!.commands.clearContent(true);
    await flushPromises();
    escape();
    expect(wrapper.emitted("cancel")).toHaveLength(2);
  });

  it("is a compact composer: no block handle, so nothing of the editor's gutter overlaps the dock or the page section", async () => {
    wrapper = mount(CommentComposer, { attachTo: document.body });
    await vi.waitFor(() => expect(document.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror")?.editor).toBeDefined());
    await flushPromises();
    expect(document.querySelector("[data-testid=block-handle]")).toBeNull();
  });
});
