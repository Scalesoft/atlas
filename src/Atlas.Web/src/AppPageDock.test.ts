import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, onMounted } from "vue";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import type { Page } from "./api";
import { emptyDocument } from "./document";
import { useWorkspacePanel } from "./composables/useWorkspacePanel";
import { useAiCapabilities } from "./composables/useAiCapabilities";
import { useAssistant } from "./assistant/useAssistant";
import { jsonResponse as response, memoryStorage, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";

// The page dock of refresh 2.5 (board 03): Obsah · Hledat (3.5 J) · Historie · Přístup beside the article, remembered per account.
vi.mock("./components/DocumentEditor.vue", () => ({ __esModule: true, default: defineComponent({
  props: ["value", "readonly"], emits: ["ready", "collaborationStatus"],
  setup(_props, { emit, expose }) {
    expose({ focus: () => true, capture: () => ({ contentJson: "{}", markdown: "", collaborationGeneration: "g" }) });
    onMounted(() => { emit("ready"); emit("collaborationStatus", { enabled: true, canSave: true }); });
    return () => h("div", { class: "ProseMirror" }, [h("h2", { "data-heading-id": "a" }, "Úvod"), h("p", "Text"), h("h2", { "data-heading-id": "b" }, "Postup")]);
  },
}) }));
vi.mock("./components/assistant/AssistantPanel.vue", () => ({ __esModule: true, default: defineComponent({ setup: () => () => h("div", { class: "assistant-test" }, "Konverzace") }) }));
vi.mock("./components/assistant/AssistantPanelHeader.vue", () => ({ __esModule: true, default: defineComponent({ setup: () => () => h("span", { class: "assistant-header-test" }, "Asistent ⋯") }) }));
vi.mock("./components/PageHistoryPanel.vue", () => ({ __esModule: true, default: defineComponent({
  emits: ["close"], setup: () => () => h("div", { class: "history-test" }, "Historie stránky"),
}) }));
const page: Page = { id: "page-1", spaceId: "space-1", parentId: null, title: "Návod", version: 2, isPublished: true, publishedVersion: 2,
  contentJson: JSON.stringify(emptyDocument()), activeFrom: "2026-09-23T08:00:00Z", canEdit: true };
const { values: memory, storage } = memoryStorage();
let wrapper: VueWrapper | undefined;
let assistantCapable = false;
async function start(role = "Writer", width = 1440) {
  Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: width });
  const team: Route = path => {
    if (path === "/api/auth/me") return response({ id: "user-1", username: "u", displayName: "Uživatel", isWikiAdmin: false, appearance: { layout: "connected", accent: "ocean", mode: "system" } });
    if (path === "/api/spaces") return response([{ id: "space-1", name: "Tým", role, icon: "book", color: "blue" }]);
    if (path === "/api/pages/page-1") return response({ ...page, canEdit: role !== "Reader" });
    if (path === "/api/spaces/space-1/tree") return response({ spaceId: "space-1", version: 1, items: [{ ...page, sortOrder: 1024, canEdit: role !== "Reader" }] });
    if (path === "/api/spaces/space-1/pages") return response([page]);
    if (path === "/api/ai/capabilities") return response({ assistant: assistantCapable });
    if (path.startsWith("/api/me/ai") || path.startsWith("/api/assistant")) return response({});
    if (path.startsWith("/api/pages/page-1/connections")) return response({ linksHere: { items: [], total: 0 } });
    if (path.startsWith("/api/pages/page-1/history")) return response({ pageId: "page-1", currentVersion: 2, nextBeforeVersion: null,
      items: [{ id: "r2", version: 2, title: "Návod", authorId: "u", authorName: "Uživatel", activeFrom: "2026-09-23T08:00:00Z", activeTo: null, isCurrent: true, isPublished: true, lastPublishedAt: null }] });
    if (path.startsWith("/api/pages/page-1/access")) return response({ spaceId: "space-1", spaceName: "Tým", isPublished: true, canManage: false, items: [], hasMore: false,
      myAccess: { userId: "user-1", displayName: "Uživatel", username: "u", role, isWikiAdmin: false, canReadPublished: true, canReadDraft: true, canEdit: role !== "Reader", sources: [] } });
  };
  wrapper = mountApp({ url: "/#/pages/page-1", fetch: routedFetch(team, sessionRoutes, quietRoutes) });
  for (let i = 0; i < 5; i++) await flushPromises();
  return wrapper;
}
const dock = () => document.querySelector<HTMLElement>('[role="complementary"][aria-label="Boční panel stránky"]');
const tab = (name: string) => [...document.querySelectorAll<HTMLButtonElement>("[role=tab]")].find((button) => button.textContent?.trim() === name);
beforeEach(() => { memory.clear(); assistantCapable = false; vi.stubGlobal("localStorage", storage); });
afterEach(() => { wrapper?.unmount(); wrapper = undefined; useWorkspacePanel().reset(); useAiCapabilities().stop(); useAssistant().reset(); resetBrowser(); Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: 1024 }); });

describe("page dock", () => {
  it("opens with Obsah beside the article on a wide screen, without taking the focus, and lists the page headings", async () => {
    await start();
    expect(dock()).not.toBeNull();
    expect([...document.querySelectorAll("[role=tab]")].map((button) => button.textContent?.trim())).toEqual(["Obsah", "Hledat", "Historie", "Přístup"]);
    expect(tab("Obsah")!.getAttribute("aria-selected")).toBe("true");
    expect(dock()!.contains(document.activeElement)).toBe(false);
    await vi.waitFor(() => expect([...dock()!.querySelectorAll(".page-outline-entry")].map((entry) => entry.textContent)).toEqual(["Úvod", "Postup"]), { timeout: 4000 });
    // The outline row above the article is for pages without the dock.
    expect(document.querySelector(".document-header .page-outline-disclosure")).toBeNull();
    expect(document.querySelector(".page-dock-toggle")!.getAttribute("aria-pressed")).toBe("true");
  });

  it("steps aside when the window narrows below a wide screen and comes back when it widens, keeping the reader's choice", async () => {
    // The dock covered the whole page, top bar included, after a desktop window was narrowed to a phone width.
    // The room is the window's width (2.6: App follows `resize`, no longer a media query listener).
    const resize = (width: number) => { Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: width }); window.dispatchEvent(new Event("resize")); };
    await start();
    expect(dock()).not.toBeNull();
    resize(1100);
    for (let i = 0; i < 3; i++) await flushPromises();
    expect(dock()).toBeNull();
    expect(memory.get("atlas:reading:user-1:dock")).toBeUndefined();
    resize(1440);
    for (let i = 0; i < 3; i++) await flushPromises();
    expect(dock()).not.toBeNull();
  });
  it("remembers that the reader closed it and stays closed on the next visit", async () => {
    const app = await start();
    await app.get(".page-dock-toggle").trigger("click"); await flushPromises();
    expect(memory.get("atlas:reading:user-1:dock")).toBe("closed");
    expect(app.get(".page-dock-toggle").attributes("aria-pressed")).toBe("false");
    app.unmount(); wrapper = undefined; useWorkspacePanel().reset(); document.body.innerHTML = "";
    await start();
    expect(dock()).toBeNull();
    await wrapper!.get(".page-dock-toggle").trigger("click"); await flushPromises();
    expect(dock()).not.toBeNull();
    expect(memory.get("atlas:reading:user-1:dock")).toBe("open");
  });

  it("summarises the history and the access in their tabs and opens the full surfaces from there", async () => {
    await start();
    tab("Historie")!.click();
    await vi.waitFor(() => expect(dock()!.textContent).toContain("Verze 2"), { timeout: 4000 });
    (dock()!.querySelector<HTMLButtonElement>("[data-open-history]"))!.click();
    await vi.waitFor(() => expect(document.querySelector(".history-test")).not.toBeNull(), { timeout: 4000 });
    tab("Přístup")!.click();
    await vi.waitFor(() => expect(dock()!.textContent).toContain("Můžete stránku číst i upravovat."), { timeout: 4000 });
    (dock()!.querySelector<HTMLButtonElement>("[data-open-access]"))!.click();
    await vi.waitFor(() => expect(location.hash).toBe("#/pages/page-1/access"), { timeout: 4000 });
    // The access view replaces the article; the dock of the page goes with it.
    expect(dock()).toBeNull();
  });

  it("switches to the assistant as one of its tabs, with the assistant's own header under the tabs", async () => {
    assistantCapable = true;
    await start();
    await vi.waitFor(() => expect(tab("Asistent")).toBeDefined(), { timeout: 4000 });
    tab("Asistent")!.click();
    await vi.waitFor(() => expect(document.querySelector(".assistant-test")).not.toBeNull(), { timeout: 4000 });
    expect(tab("Asistent")!.getAttribute("aria-selected")).toBe("true");
    expect(document.querySelector(".workspace-panel-subheader .assistant-header-test")).not.toBeNull();
    tab("Obsah")!.click();
    await vi.waitFor(() => expect(document.querySelector(".assistant-test")).toBeNull(), { timeout: 4000 });
    expect(tab("Obsah")!.getAttribute("aria-selected")).toBe("true");
  });

  it("opens Hledat with the focus in its field on Ctrl+F and lists the page's matches with their section", async () => {
    await start();
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "f", ctrlKey: true, bubbles: true, cancelable: true }));
    await vi.waitFor(() => expect(document.activeElement?.matches("[data-page-search-input]")).toBe(true), { timeout: 4000 });
    expect(tab("Hledat")!.getAttribute("aria-selected")).toBe("true");
    const input = document.activeElement as HTMLInputElement;
    input.value = "postup"; input.dispatchEvent(new Event("input"));
    await vi.waitFor(() => expect(dock()!.querySelector(".page-search-count")?.textContent).toBe("1 z 1"), { timeout: 4000 });
    expect(dock()!.querySelector(".page-search-place")?.textContent).toBe("Postup");
  });

  it("offers no history tab to a reader and does not open by itself on a narrow screen", async () => {
    await start("Reader");
    expect([...document.querySelectorAll("[role=tab]")].map((button) => button.textContent?.trim())).toEqual(["Obsah", "Hledat", "Přístup"]);
    wrapper!.unmount(); wrapper = undefined; useWorkspacePanel().reset(); document.body.innerHTML = "";
    await start("Writer", 1100);
    expect(dock()).toBeNull();
  });
});
