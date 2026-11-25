import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, onMounted, watch } from "vue";
import { toast } from "vue-sonner";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import DocumentEditor from "./components/DocumentEditor.vue";
import type { Page } from "./api";
import { emptyDocument } from "./document";
import { jsonResponse as response, memoryStorage, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";

vi.mock("./components/DocumentEditor.vue", () => ({ __esModule: true, default: defineComponent({
  props: ["value", "readonly"], emits: ["ready", "change", "collaborationStatus", "editIntent"],
  setup(props, { emit, expose }) {
    expose({ focus: () => true, capture: () => ({ contentJson: JSON.stringify(props.value), markdown: "", collaborationGeneration: "generation-1" }), navigateToHeading: vi.fn(), selectionActions: () => [] });
    watch(() => props.readonly, () => emit("collaborationStatus", { enabled: true, canSave: true }));
    onMounted(() => { emit("ready"); emit("collaborationStatus", { enabled: true, canSave: true }); });
    return () => h("button", { class: "edit-content", onClick: () => emit("change") }, "Edit content");
  },
}) }));
const page: Page = { id: "page-1", spaceId: "space-1", parentId: null, title: "První článek", version: 1, isPublished: true, publishedVersion: 1, contentJson: JSON.stringify(emptyDocument()), activeFrom: "2026-01-01T08:00:00Z", canEdit: true };
let wrapper: VueWrapper | undefined;
beforeEach(() => { vi.stubGlobal("localStorage", memoryStorage().storage); });
async function settle() { await flushPromises(); await vi.dynamicImportSettled(); await flushPromises(); }
// Its library lists a favourite, so this route comes before the empty library of quietRoutes.
const engineering: Route = (path, init) => {
  if (path === "/api/auth/me") return response({ id: "user-1", username: "writer", displayName: "Writer", isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "system" } });
  if (path === "/api/spaces") return response([{ id: "space-1", name: "Engineering", role: "Writer", icon: "book", color: "blue" }]);
  if (path === "/api/spaces/space-1/tree") return response({ spaceId: "space-1", version: 1, items: [{ ...page, canEdit: true }, { ...page, id: "page-2", title: "Druhý článek", canEdit: true }] });
  if (path === "/api/pages/page-1") return response(page);
  if (path === "/api/pages/page-2") return response({ ...page, id: "page-2", title: "Druhý článek" });
  if (path.endsWith("/favorite")) return response({ isFavorite: JSON.parse(init!.body as string).isFavorite, visitedAt: null });
  if (path.startsWith("/api/me/library/")) return response({ items: [{ ...page, spaceName: "Engineering", isFavorite: true, visitedAt: null, publishedAt: null }], hasMore: false, nextOffset: null });
  if (path.startsWith("/api/search?")) return response({ items: [], spaces: [], hasMore: false });
};
async function start(route: string, custom?: Route) {
  const fetchMock = routedFetch(custom, engineering, sessionRoutes, quietRoutes);
  wrapper = mountApp({ url: route, fetch: fetchMock }); await settle();
  return { app: wrapper, fetchMock };
}
// Menu actions are portalled out of the wrapper, so this looks in the document
// and falls back to the wrapper for buttons that are still inline.
function button(app: VueWrapper, text: string) {
  const inline = app.findAll("button").find(item => item.text() === text);
  if (inline) return inline;
  const el = [...document.querySelectorAll("button")].find(item => item.textContent?.trim() === text);
  return { element: el, isVisible: () => Boolean(el), trigger: async (event: string) => { el?.dispatchEvent(new Event(event, { bubbles: true })); }, text: () => el?.textContent ?? "" } as unknown as ReturnType<VueWrapper["findAll"]>[number];
}
afterEach(() => { wrapper?.unmount(); wrapper = undefined; resetBrowser(); });
/** The title field's text (a writer's title is a textarea, not a heading; 3.6-A). */
const titleOf = (app: VueWrapper) => (app.get(".document-title-text").element as HTMLTextAreaElement).value;
/** The reader points into the text: the live editor attaches at once (3.6-F), no button to press. */
async function startEditing(app: VueWrapper) {
  app.getComponent(DocumentEditor).vm.$emit("editIntent", "point"); await settle();
}
/** A button of the open UnsavedChangesDialog (Zůstat, Zahodit, Uložit). */
const leaveButton = (label: string) => [...document.querySelectorAll<HTMLButtonElement>('dialog[open] button')].find(item => item.textContent?.trim() === label);


describe("personal navigation and exports", () => {
  // The page's own changes ask nothing (3.6-B): Hotovo, Zahodit změny and the live-draft question are gone, a checkpoint goes instead.
  it('publishes a checkpointed change with Publikovat and stays in the editor', async () => {
    const toasted = vi.spyOn(toast, 'success').mockClear();
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const { app, fetchMock } = await start('/#/pages/page-1', (path, init) => {
      if (path === '/api/pages/page-1/publish') return response({ ...page, version: 2, publishedVersion: 2 });
      if (path === '/api/pages/page-1' && init?.method === 'PUT') return response({ ...page, version: 2 });
    });
    const confirm = vi.spyOn(window, 'confirm');
    const calls = (method: string, suffix = '') => fetchMock.mock.calls.filter(([path, init]) => init?.method === method && String(path).endsWith(`/api/pages/page-1${suffix}`)).length;
    await startEditing(app);
    expect(app.find(".publish-button").exists()).toBe(false);
    await app.get('.edit-content').trigger('click');
    // The change is checkpointed 1.5 s after the last one; only then the revision is waiting to be published.
    await vi.advanceTimersByTimeAsync(1500); await settle();
    expect([calls('PUT'), calls('POST', '/publish')]).toEqual([1, 0]);
    await app.get('.document-primary-actions .publish-button').trigger('click'); await settle();
    expect(calls('POST', '/publish')).toBe(1);
    // Publishing no longer ends editing.
    expect(app.getComponent(DocumentEditor).props('readonly')).toBe(false);
    expect(toasted).toHaveBeenCalledWith('Stránka byla publikována.', expect.anything());
    expect(confirm).not.toHaveBeenCalled();
  });
  it('offers Publikovat while reading a page whose saved changes are not published', async () => {
    const toasted = vi.spyOn(toast, 'success').mockClear();
    const { app, fetchMock } = await start('/#/pages/page-1', (path, init) => {
      if (path === '/api/pages/page-1' && !init?.method) return response({ ...page, version: 2 });
      // Publishing checkpoints the live session first (it may hold what no change event reported), so a PUT is expected here.
      if (path === '/api/pages/page-1' && init?.method === 'PUT') return response({ ...page, version: 2 });
      if (path === '/api/pages/page-1/publish') return response({ ...page, version: 2, publishedVersion: 2 });
    });
    // The draft is said once, in the top bar; the state by the title steps aside.
    expect(app.get('.document-status').text()).toBe('');
    expect(app.get('.workspace-bar').attributes('data-publication')).toBe('draft');
    expect(app.get('[data-publication-state="draft"]').text()).toContain('Koncept – čtenáři vidí');
    await button(app, 'Zveřejnit změny').trigger('click'); await settle();
    const publish = fetchMock.mock.calls.find(([path]) => String(path).endsWith('/publish'))!;
    expect(JSON.parse(publish[1]!.body as string)).toMatchObject({ expectedVersion: 2 });
    expect(toasted).toHaveBeenCalledWith('Stránka byla publikována.', expect.anything());
    expect(app.find('.publish-button').exists()).toBe(false);
  });
  it('does not carry a publication success to a different draft', async () => {
    const toasted = vi.spyOn(toast, 'success').mockClear();
    const { app } = await start('/#/pages/page-1', (path, init) => {
      if (path === '/api/pages/page-1' && !init?.method) return Promise.resolve(response({ ...page, version: 2 }));
      if (path === '/api/pages/page-1/publish') return Promise.resolve(response({ ...page, version: 2, publishedVersion: 2 }));
      if (path === '/api/pages/page-1' && init?.method === 'PUT') return Promise.resolve(response({ ...page, version: 2 }));
      if (path === '/api/pages/page-2') return Promise.resolve(response({ ...page, id: 'page-2', title: 'Druhý článek', isPublished: false, publishedVersion: null }));
    });
    await button(app, 'Zveřejnit změny').trigger('click'); await settle();
    expect(toasted).toHaveBeenCalledWith('Stránka byla publikována.', expect.anything());
    const target = app.findAll('.tree-page').find(link => link.text().includes('Druhý článek'))!;
    await target.trigger('click'); await settle();
    expect(app.get('.document-status').text()).toBe('');
    expect(app.get('[data-publication-state="draft"]').text()).toContain('Koncept – zatím nepublikováno, čtenáři ji nevidí');
  });
  it("moves through the server CAS without optimistic changes or remounting the document", async () => {
    let finish!: (value: Response) => void, committed = false;
    const { app, fetchMock } = await start("/#/pages/page-1", path => {
      if (path === "/api/pages/page-2/move") return new Promise(resolve => { finish = resolve; });
      if (path === "/api/spaces/space-1/tree" && committed) return Promise.resolve(response({ spaceId:"space-1", version:2, items:[{...page,canEdit:true},{...page,id:"page-2",title:"Druhý článek",parentId:"page-1",canEdit:true}] }));
    });
    const original = app.get(".edit-content").element;
    const target = app.findAll(".tree-row").find(row=>row.text().includes("První článek"))!;
    const source = app.findAll(".tree-row").find(row=>row.text().includes("Druhý článek"))!;
    vi.spyOn(target.element,"getBoundingClientRect").mockReturnValue({top:0,height:40} as DOMRect);
    await source.trigger("dragstart",{dataTransfer:{setData:vi.fn(),effectAllowed:""}});
    await target.trigger("drop",{clientY:20}); await settle();
    const call = fetchMock.mock.calls.find(([path])=>path==="/api/pages/page-2/move")!;
    expect(JSON.parse(call[1]!.body as string)).toEqual({parentId:"page-1",beforePageId:null,expectedTreeVersion:1});
    expect(app.get(".edit-content").element).toBe(original);
    expect(app.findAll('.tree-row[draggable="true"]')).toHaveLength(0);
    committed = true; finish(response({version:2,affectedIds:["page-2"]})); await settle();
    expect(app.text()).toContain("Stránka byla přesunuta.");
    expect(app.get(".edit-content").element).toBe(original);
    const child = app.findAll(".tree-row").find(row=>row.text().includes("Druhý článek"))!;
    expect(child.element.parentElement?.parentElement?.parentElement?.tagName).toBe("LI");
  });
  it("keeps dirty content and lets a tree drag start: the page's changes save by themselves (3.6)", async () => {
    const { app, fetchMock } = await start("/#/pages/page-1");
    await app.get(".edit-content").trigger("click");
    const original = app.get(".edit-content").element;
    await app.findAll(".tree-row")[1]!.trigger("dragstart",{dataTransfer:{setData:vi.fn()}});
    expect(app.text()).not.toContain("Před přesunem nejprve uložte rozepsané změny.");
    expect(app.get(".edit-content").element).toBe(original);
    expect(fetchMock.mock.calls.some(([path])=>String(path).endsWith("/move"))).toBe(false);
  });
  it("refreshes a conflicting tree once and requires a new user move", async () => {
    const { app, fetchMock } = await start("/#/pages/page-1", path => path.endsWith("/move") ? Promise.resolve(response({detail:"Tree changed"},409)) : undefined);
    const rows = app.findAll(".tree-row");
    vi.spyOn(rows[0]!.element,"getBoundingClientRect").mockReturnValue({top:0,height:40} as DOMRect);
    await rows[1]!.trigger("dragstart",{dataTransfer:{setData:vi.fn()}});
    await rows[0]!.trigger("drop",{clientY:20}); await settle();
    expect(app.text()).toContain("Struktura prostoru se mezitím změnila.");
    expect(fetchMock.mock.calls.filter(([path])=>String(path).endsWith("/move"))).toHaveLength(1);
    expect(fetchMock.mock.calls.filter(([path])=>String(path).endsWith("/tree"))).toHaveLength(2);
  });
  it("moves a page to the space root when it is dropped on the space row", async () => {
    const child = { ...page, id: "page-2", title: "Druhý článek", parentId: "page-1", canEdit: true };
    const { app, fetchMock } = await start("/#/pages/page-1", path => {
      if (path === "/api/spaces/space-1/tree") return Promise.resolve(response({ spaceId: "space-1", version: 1, items: [{ ...page, canEdit: true }, child] }));
      if (path === "/api/pages/page-2/move") return Promise.resolve(response({ version: 2, affectedIds: ["page-2"] }));
    });
    const source = app.findAll(".tree-row").find(row => row.text().includes("Druhý článek"))!;
    await source.trigger("dragstart", { dataTransfer: { setData: vi.fn(), effectAllowed: "" } });
    const spaceRow = app.get(".sidebar-space-row");
    await spaceRow.trigger("dragover", { dataTransfer: { dropEffect: "" } });
    expect(spaceRow.classes()).toContain("drop-inside");
    await spaceRow.trigger("drop"); await settle();
    const call = fetchMock.mock.calls.find(([path]) => path === "/api/pages/page-2/move")!;
    expect(JSON.parse(call[1]!.body as string)).toEqual({ parentId: null, beforePageId: null, expectedTreeVersion: 1 });
  });
  it("shows ancestor navigation and leaves a changed document without asking, a checkpoint goes on the way", async () => {
    const child = {...page,id:"page-2",title:"Druhý článek",parentId:"page-1"};
    const { app, fetchMock } = await start("/#/pages/page-2", (path, init) => path === "/api/pages/page-2" ? Promise.resolve(response(init?.method === "PUT" ? { ...child, version: 2 } : child)) : path.endsWith("/tree") ? Promise.resolve(response({spaceId:"space-1",version:1,items:[page,child]})) : undefined);
    expect(app.get("a.breadcrumb-ancestor").text()).toBe("První článek");
    await app.get(".edit-content").trigger("click");
    const confirm = vi.spyOn(window,"confirm");
    await app.get("a.breadcrumb-ancestor").trigger("click"); await settle();
    expect(leaveButton("Zůstat")).toBeUndefined();
    expect(confirm).not.toHaveBeenCalled();
    expect(fetchMock.mock.calls.some(([path, init]) => path === "/api/pages/page-2" && init?.method === "PUT")).toBe(true);
    expect(location.hash).toBe("#/pages/page-1");
    expect(titleOf(app)).toBe("První článek");
  });
  it("clears the search route on native Escape cancellation without losing the article", async () => {
    const { app } = await start("/#/pages/page-1");
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("interní dotaz");
    expect(location.search).toContain("search=1");
    const dialog = app.get(".search-dialog").element as HTMLDialogElement;
    const cancel = new Event("cancel", { cancelable: true });
    dialog.dispatchEvent(cancel);
    // Model the native browser fallback, which is intentionally absent in jsdom.
    if (!cancel.defaultPrevented) dialog.close();
    await settle();
    expect(dialog.open).toBe(false);
    expect(location.search).toBe("");
    expect(location.hash).toBe("#/pages/page-1");
    expect(titleOf(app)).toBe("První článek");
  });
  it("clears search state and dialog visibility at the account boundary", async () => {
    const { app } = await start("/#/pages/page-1", path => path === "/api/auth/logout" ? Promise.resolve(new Response(null, { status: 204 })) : undefined);
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("předchozí uživatel");
    await app.get(".profile-launch").trigger("click");
    // Wait for the lazy profile chunk itself, then its mocked requests and Vue render.
    await vi.dynamicImportSettled(); await settle();
    expect(app.find("[data-profile-logout]").exists()).toBe(true);
    await app.get("[data-profile-logout]").trigger("click"); await settle();
    expect(location.search).toBe("");
    expect((app.get(".search-dialog").element as HTMLDialogElement).open).toBe(false);
    expect((app.get(".omnisearch-input input").element as HTMLInputElement).value).toBe("");
    expect(app.get("#search-results").text()).toBe("");
  });
  it("resolves direct space and library links, visits an opened article and uses explicit favorite state", async () => {
    const { app, fetchMock } = await start("/#/spaces/space-1");
    expect(app.get(".overview h1").text()).toBe("Engineering");
    await button(app, "Knihovna").trigger("click"); await settle();
    expect(location.hash).toBe("#/library/spaces");
    await button(app, "Oblíbené").trigger("click"); await settle();
    expect(location.hash).toBe("#/library/favorites");
    await vi.waitFor(() => expect(app.get(".personal-library h2").text()).toBe("Oblíbené"));
    await app.get('[aria-label="Otevřít První článek"]').trigger("click"); await settle();
    // The opening records the visit in the page's own read (3.6-G N7), not with a request of its own.
    expect(fetchMock.mock.calls.some(([path]) => path === "/api/pages/page-1?bundle=true&visit=true")).toBe(true);
    expect(fetchMock.mock.calls.some(([path]) => path === "/api/pages/page-1/visit")).toBe(false);
    await app.get('[aria-label="Přidat do oblíbených"]').trigger("click"); await settle();
    expect(app.get(".favorite-button").attributes("aria-pressed")).toBe("true");
    await app.get('[aria-label="Odebrat z oblíbených"]').trigger("click"); await settle();
    expect(fetchMock.mock.calls.filter(([path]) => String(path).endsWith("/favorite")).map(([, init]) => JSON.parse(init!.body as string))).toEqual([{ isFavorite: true }, { isFavorite: false }]);
  });
  it("opens a direct drafts route and leaves a changed editor for personal lists with a checkpoint, without asking", async () => {
    const { app, fetchMock } = await start("/#/library/drafts");
    expect(app.get(".personal-library h2").text()).toBe("Moje koncepty");
    await app.get('[aria-label="Otevřít První článek"]').trigger("click"); await settle();
    await app.get(".edit-content").trigger("click");
    await button(app, "Knihovna").trigger("click"); await settle();
    expect(leaveButton("Zůstat")).toBeUndefined();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === "PUT")).toBe(true);
    expect(location.hash).toBe("#/library/spaces"); expect(app.find(".edit-content").exists()).toBe(false);
  });
  it("restores the underlying article before reopening search from browser history", async () => {
    const { app } = await start("/#/pages/page-1");
    await app.get(".search-launch").trigger("click");
    history.replaceState(null, "", "/?search=1&q=manual#/pages/page-2");
    window.dispatchEvent(new PopStateEvent("popstate")); await settle();
    expect(titleOf(app)).toBe("Druhý článek");
    expect((app.get(".search-dialog").element as HTMLDialogElement).open).toBe(true);
    await app.get('.search-dialog .app-dialog-header [aria-label="Zavřít"]').trigger("click");
    expect(titleOf(app)).toBe("Druhý článek");
    expect(location.hash).toBe("#/pages/page-2");
  });
  it("does not let a late personal response from a previous article change the current star", async () => {
    let oldState!: (value: Response) => void;
    const { app } = await start("/#/pages/page-1", path => path === "/api/pages/page-1/personal-state" ? new Promise(resolve => { oldState = resolve; }) : undefined);
    history.replaceState(null, "", "/#/pages/page-2"); window.dispatchEvent(new PopStateEvent("popstate")); await settle();
    oldState(response({ isFavorite: true, visitedAt: null })); await settle();
    expect(app.get(".favorite-button").attributes("aria-pressed")).toBe("false");
    expect(titleOf(app)).toBe("Druhý článek");
  });
  it("saves an edited article before fetching its ZIP and reports export failures", async () => {
    const order: string[] = [];
    const { app } = await start("/#/pages/page-1", (path, init) => {
      if (path === "/api/pages/page-1" && init?.method === "PUT") { order.push("save"); return Promise.resolve(response({ ...page, ...JSON.parse(init.body as string), version: 2 })); }
      if (path === "/api/pages/page-1/export") { order.push("export"); return Promise.resolve(response({ detail: "Export právě připravuje jiný požadavek." }, 409)); }
    });
    await app.get(".document-title-text").setValue("Uložený název"); await app.get(".document-title-text").trigger("blur"); await settle();
    // The title is checkpointed on blur; exporting flushes whatever is still waiting, so make sure something is.
    await app.get(".edit-content").trigger("click");
    await app.get('[aria-label="Další akce stránky"][aria-expanded]').trigger('click');
    expect(button(app, "Exportovat stránku").isVisible()).toBe(true);
    await button(app, "Exportovat stránku").trigger("click"); await settle();
    expect(order.at(-1)).toBe("export");
    expect(order).toContain("save");
    expect(app.text()).toContain("Export právě připravuje jiný požadavek.");
  });
  it("does not export a snapshot when newer edits arrive during its save", async () => {
    let finish!: (value: Response) => void;
    const { app, fetchMock } = await start("/#/pages/page-1", (path, init) => path === "/api/pages/page-1" && init?.method === "PUT" ? new Promise(resolve => { finish = resolve; }) : undefined);
    await app.get(".edit-content").trigger("click");
    await app.get('[aria-label="Další akce stránky"][aria-expanded]').trigger('click');
    expect(button(app, "Exportovat stránku").isVisible()).toBe(true);
    await button(app, "Exportovat stránku").trigger("click"); await settle();
    expect(finish).toBeTypeOf("function");
    await app.get(".edit-content").trigger("click");
    finish(response({ ...page, version: 2 })); await settle();
    expect(fetchMock.mock.calls.some(([path]) => String(path).endsWith("/export"))).toBe(false);
    expect(app.text()).toContain("Během ukládání přibyly další změny. Spusťte export znovu.");
    expect(app.get(".document-status").text()).toContain("Během ukládání přibyly další změny");
    expect(button(app, 'Stáhnout neuložené změny').exists()).toBe(true);
  });
});
