import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { emptyDocument } from "../../../document";
import { jsonResponse, routedFetch } from "../../../testing/appHarness";
import DocumentEditor from "../../DocumentEditor.vue";
import BoardPageEditor from "./BoardPageEditor.vue";

// The page's editor as the panel uses it: a change, the session's status, and the snapshot a checkpoint sends.
vi.mock("../../DocumentEditor.vue", async () => {
  const { defineComponent, h } = await import("vue");
  return { default: defineComponent({
    emits: ["change", "collaborationStatus", "collaborationPeers"],
    setup(_, { emit, expose }) {
      expose({ capture: () => ({ contentJson: "{}", markdown: "", collaborationGeneration: "session" }), focus: () => true });
      return () => h("button", { class: "stub-change", onClick: () => emit("change") }, "change");
    },
  }) };
});

const page = { id: "page-1", spaceId: "space-1", title: "Stránka", version: 1, contentJson: JSON.stringify(emptyDocument()), canEdit: true };
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.unstubAllGlobals(); });

async function open() {
  const fetchMock = routedFetch(path => {
    if (path.endsWith("/auth/me")) return jsonResponse({ id: "user-1", displayName: "Jana" });
    if (path.endsWith("/auth/csrf")) return jsonResponse({ token: "csrf" });
    if (path.endsWith(`/pages/${page.id}`)) return jsonResponse(page);
    return undefined;
  });
  vi.stubGlobal("fetch", fetchMock);
  wrapper = mount(BoardPageEditor, { props: { pageId: page.id }, attachTo: document.body });
  await flushPromises();
  const editor = wrapper.findComponent(DocumentEditor);
  editor.vm.$emit("collaborationStatus", { enabled: true, canSave: true, connection: "online", pendingChanges: 0, projectionSave: "current", error: null, generation: "session" });
  await flushPromises();
  const puts = () => fetchMock.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === "PUT");
  return { puts };
}

describe("BoardPageEditor", () => {
  it("saves the page by itself, as the wiki's page does (3.6), and has no Uložit koncept", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const { puts } = await open();
    expect(document.body.textContent).not.toContain("Uložit koncept");
    document.querySelector<HTMLButtonElement>(".stub-change")!.click();
    await flushPromises();
    expect(puts()).toHaveLength(0);
    vi.advanceTimersByTime(1500);
    await vi.waitFor(() => expect(puts()).toHaveLength(1));
    expect(document.querySelector("[data-board-page-editor-save]")?.getAttribute("data-save-state")).toBe("saved");
  });

  // 3.6: a page preview on a board is edited in place (PagePreview): only the editor, no panel; Esc and a press outside end it.
  it("inline: only the editor in place of the preview, which says when it entered; Esc and a press outside end it", async () => {
    vi.stubGlobal("fetch", routedFetch(path => {
      if (path.endsWith("/auth/me")) return jsonResponse({ id: "user-1", displayName: "Jana" });
      if (path.endsWith(`/pages/${page.id}`)) return jsonResponse(page);
      return undefined;
    }));
    wrapper = mount(BoardPageEditor, { props: { pageId: page.id, inline: true }, attachTo: document.body });
    await flushPromises();
    expect(document.querySelector("[role=dialog]")).toBeNull();
    expect(document.querySelector("[data-board-page-editor-state]")).toBeNull();
    expect(wrapper.find("[data-board-page-editor]").exists()).toBe(true);
    wrapper.findComponent(DocumentEditor).vm.$emit("ready");
    await vi.waitFor(() => expect(wrapper!.emitted("entered")).toHaveLength(1));
    wrapper.get("[data-board-page-editor]").element.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    expect(wrapper.emitted("close")).toHaveLength(1);
    wrapper.unmount();

    wrapper = mount(BoardPageEditor, { props: { pageId: page.id, inline: true }, attachTo: document.body });
    await flushPromises();
    wrapper.get(".stub-change").element.dispatchEvent(new MouseEvent("pointerdown", { bubbles: true }));
    expect(wrapper.emitted("close")).toBeUndefined();
    document.body.dispatchEvent(new MouseEvent("pointerdown", { bubbles: true }));
    expect(wrapper.emitted("close")).toHaveLength(1);
  });

  it("closing takes the last changes at once", async () => {
    const { puts } = await open();
    document.querySelector<HTMLButtonElement>(".stub-change")!.click();
    await flushPromises();
    document.querySelector<HTMLButtonElement>('[aria-label="Zavřít úpravu stránky"]')!.click();
    await vi.waitFor(() => expect(puts()).toHaveLength(1));
  });
});
