import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, onMounted } from "vue";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { toast } from "vue-sonner";
import { emptyDocument } from "./document";
import { jsonResponse as response, memoryStorage, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";

/*
 * P6 (jeden systém oznámení, Zpět): what the tree does to a page (rename, move, remove a reference) says so with a toast whose „Zpět“
 * undoes it, through the same requests as the first time. The page header's own status ("Stránka byla publikována.") is not a toast and is
 * covered by the App tests that read `.document-status`.
 */
vi.mock("./components/DocumentEditor.vue", () => ({ __esModule: true, default: defineComponent({
  props: ["value", "readonly"], emits: ["ready", "change", "collaborationStatus"],
  setup(_props, { emit, expose }) {
    expose({ focus: () => true, capture: () => ({ contentJson: "{}", markdown: "", collaborationGeneration: "generation-1" }), navigateToHeading: vi.fn(), selectionActions: () => [] });
    onMounted(() => { emit("ready"); emit("collaborationStatus", { enabled: true, canSave: true }); });
    return () => h("div", { class: "edit-content" });
  },
}) }));
const page = (id: string, title: string) => ({ id, spaceId: "space-1", parentId: null, title, version: 1, isPublished: true, publishedVersion: 1,
  contentJson: JSON.stringify(emptyDocument()), activeFrom: "2026-01-01T08:00:00Z", canEdit: true });
const tree = [page("page-1", "První článek"), page("page-2", "Druhý článek")];
const requests: { method: string; path: string; body?: Record<string, unknown> }[] = [];
let wikiAdmin = false, refuseRename = false, holdFirstRename: Promise<void> | undefined;
const site: Route = (path, init) => {
  if (init?.method && init.method !== "GET") requests.push({ method: init.method, path, body: init.body ? JSON.parse(String(init.body)) : undefined });
  if (path === "/api/auth/me") return response({ id: "user-1", username: "writer", displayName: "Writer", isWikiAdmin: wikiAdmin, appearance: { layout: "connected", accent: "blue", mode: "system" } });
  if (path === "/api/spaces") return response([{ id: "space-1", name: "Engineering", role: wikiAdmin ? "Admin" : "Writer", icon: "book", color: "blue" }]);
  if (path === "/api/spaces/space-1/tree") return response({ spaceId: "space-1", version: 1, items: tree });
  if (path === "/api/spaces/space-1/settings") return response({ id: "space-1", name: "Engineering", icon: "book", color: "blue", description: "", pinPolicy: "None", version: "v1", cover: null });
  // What a wiki admin's shell and the space's Obecné ask besides.
  if (path === "/api/ai/capabilities") return response({ semanticSearch: false, related: false, editorLinks: false, createSuggestions: false });
  if (path === "/api/admin/setup") return response({ pending: false });
  if (path === "/api/admin/backups") return response({ warning: null });
  if (path.endsWith("/admin/trash-cleanup")) return response({ scope: "space", automatic: false, days: 60, version: null });
  if (init?.method === "POST" && (path === "/api/spaces/space-1/trash" || path === "/api/spaces/space-1/restore")) return new Response(null, { status: 204 });
  const title = path.match(/^\/api\/pages\/([\w-]+)\/title$/), move = path.match(/^\/api\/pages\/([\w-]+)\/move$/), one = path.match(/^\/api\/pages\/([\w-]+)$/);
  if (title && init?.method === "PUT" && refuseRename) return response({ detail: "Název nelze změnit." }, 409);
  if (title && init?.method === "PUT") {
    const answer = response({ ...tree.find(item => item.id === title[1]), title: JSON.parse(String(init.body)).title, version: 2 });
    const held = holdFirstRename;
    holdFirstRename = undefined;
    return held ? held.then(() => answer) : answer;
  }
  if (move && init?.method === "POST") return response({ version: 2, affectedIds: [move[1]] });
  if (one && !init?.method) return response(tree.find(item => item.id === one[1]));
  if (path.startsWith("/api/search?")) return response({ items: [], spaces: [], hasMore: false });
};

let wrapper: VueWrapper | undefined;
const settle = async () => { await flushPromises(); await vi.dynamicImportSettled(); await flushPromises(); };
beforeAll(async () => { await import("./App.vue"); });
beforeEach(() => { requests.length = 0; wikiAdmin = false; refuseRename = false; holdFirstRename = undefined; vi.stubGlobal("localStorage", memoryStorage().storage); });
afterEach(() => { wrapper?.unmount(); wrapper = undefined; resetBrowser(); });
async function start() {
  wrapper = mountApp({ url: "/#/pages/page-1", fetch: routedFetch(site, sessionRoutes, quietRoutes) });
  await settle();
}
const row = (id: string) => document.querySelector<HTMLElement>(`.tree-page[data-tree-item="${id}"]`)!;
const key = (target: Element, init: KeyboardEventInit) => target.dispatchEvent(new KeyboardEvent("keydown", { bubbles: true, cancelable: true, ...init }));
/** Zpět of the last toast with that action. */
const undo = (spy: { mock: { calls: unknown[][] } }) => {
  const options = spy.mock.calls.at(-1)![1] as { action?: { label: string; onClick: () => void } };
  expect(options.action?.label).toBe("Zpět");
  options.action!.onClick();
};

describe("the tree says what it did, with Zpět", () => {
  it("renames a page in place and Zpět names the old title back", async () => {
    const success = vi.spyOn(toast, "success");
    await start();
    row("page-2").focus();
    key(row("page-2"), { key: "F2" });
    await settle();
    const field = document.querySelector<HTMLInputElement>(".tree-rename")!;
    field.value = "Nový název";
    field.dispatchEvent(new Event("input", { bubbles: true }));
    key(field, { key: "Enter" });
    await settle();
    expect(requests.filter(request => request.path.endsWith("/title")).map(request => request.body)).toEqual([{ title: "Nový název", expectedVersion: 1 }]);
    expect(success).toHaveBeenCalledWith(expect.stringContaining("Název byl změněn"), expect.anything());
    undo(success);
    await settle();
    expect(requests.filter(request => request.path.endsWith("/title")).map(request => request.body?.title)).toEqual(["Nový název", "Druhý článek"]);
    expect(success).toHaveBeenLastCalledWith("Původní název je zpět v rozpracované verzi.", expect.anything());
  });

  it("moves a page under its neighbour with Alt+→ and Zpět puts it back where it stood", async () => {
    const success = vi.spyOn(toast, "success");
    await start();
    row("page-2").focus();
    key(row("page-2"), { key: "ArrowRight", altKey: true });
    // The keyboard stays on the moved row without waiting for a frame: it lands in the next render after the tree has reloaded.
    row("page-2").blur();
    await settle();
    expect(document.activeElement).toBe(row("page-2"));
    expect(requests.filter(request => request.path.endsWith("/move")).map(request => request.body)).toEqual([{ parentId: "page-1", beforePageId: null, expectedTreeVersion: 1 }]);
    // One toast for a run of steps: each replaces the last.
    expect(success).toHaveBeenCalledWith("Stránka byla přesunuta.", expect.objectContaining({ id: "tree-move" }));
    undo(success);
    await settle();
    expect(requests.filter(request => request.path.endsWith("/move")).map(request => request.body).at(-1)).toEqual({ parentId: null, beforePageId: null, expectedTreeVersion: 1 });
    expect(success).toHaveBeenLastCalledWith("Přesun je vrácen.", expect.anything());
  });

  it("keeps a second rename typed while the first is still saving (P17): both are sent", async () => {
    let release!: () => void;
    holdFirstRename = new Promise<void>(resolve => { release = resolve; });
    await start();
    const rename = async (id: string, title: string) => {
      row(id).focus();
      key(row(id), { key: "F2" });
      await settle();
      const field = document.querySelector<HTMLInputElement>(".tree-rename")!;
      field.value = title;
      field.dispatchEvent(new Event("input", { bubbles: true }));
      key(field, { key: "Enter" });
      await settle();
    };
    await rename("page-2", "Nový název");
    await rename("page-1", "Jiný název");
    release();
    await settle();
    expect(requests.filter(request => request.path.endsWith("/title")).map(request => request.body?.title)).toEqual(["Nový název", "Jiný název"]);
  });

  it("shows no banner for a confirmation any more: a link copied is a toast", async () => {
    const success = vi.spyOn(toast, "success");
    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: vi.fn(async () => {}) } });
    await start();
    row("page-2").focus();
    key(row("page-2"), { key: "ContextMenu" });
    await vi.waitFor(() => expect(document.querySelector('[data-tree-action="copyLink"]')).not.toBeNull());
    document.querySelector<HTMLElement>('[data-tree-action="copyLink"]')!.click();
    await vi.waitFor(() => expect(success).toHaveBeenCalledWith("Odkaz je zkopírovaný.", expect.anything()));
    expect(document.querySelector(".global-notice")).toBeNull();
  });

  it("says a refused rename in a toast and puts the old title back, not in the open page's header", async () => {
    const error = vi.spyOn(toast, "error");
    refuseRename = true;
    await start();
    row("page-2").focus();
    key(row("page-2"), { key: "F2" });
    await settle();
    const field = document.querySelector<HTMLInputElement>(".tree-rename")!;
    field.value = "Nový název";
    field.dispatchEvent(new Event("input", { bubbles: true }));
    key(field, { key: "Enter" });
    await settle();
    expect(error).toHaveBeenCalledWith("Název nelze změnit.", expect.anything());
    expect(row("page-2").textContent).toContain("Druhý článek");
    expect(document.querySelector(".document-status")?.textContent ?? "").not.toContain("Název nelze změnit.");
  });

  it("moves a space to the trash and Zpět restores it: the wiki admin who trashed it may always take it back", async () => {
    const success = vi.spyOn(toast, "success");
    wikiAdmin = true;
    wrapper = mountApp({ url: "/#/spaces/space-1/settings/general", fetch: routedFetch(site, sessionRoutes, quietRoutes) });
    await settle();
    await vi.waitFor(() => expect(document.querySelector("[data-space-trash-open]")).not.toBeNull(), { timeout: 20000, interval: 100 });
    document.querySelector<HTMLButtonElement>("[data-space-trash-open]")!.click();
    await settle();
    const name = document.querySelector<HTMLInputElement>("[data-space-trash-name]")!;
    name.value = "Engineering";
    name.dispatchEvent(new Event("input"));
    await settle();
    document.querySelector<HTMLButtonElement>("[data-confirm-action]")!.click();
    await settle();
    expect(requests.map(request => `${request.method} ${request.path}`)).toContain("POST /api/spaces/space-1/trash");
    expect(success).toHaveBeenCalledWith("Prostor je v koši.", expect.anything());
    undo(success);
    await settle();
    expect(requests.map(request => `${request.method} ${request.path}`)).toContain("POST /api/spaces/space-1/restore");
    expect(success).toHaveBeenLastCalledWith("Prostor je zpět.", expect.anything());
  }, 40000); // the settings chunks of the wiki admin load cold here
});
