import { afterEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, onMounted, watch } from "vue";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { toast } from "vue-sonner";
import DocumentEditor from "./components/DocumentEditor.vue";
import type { Page } from "./api";
import { emptyDocument } from "./document";
import { jsonResponse as response, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";

const counters = vi.hoisted(() => ({ mounts: 0, hydratedMarkdown: "" }));
vi.mock("./components/DocumentEditor.vue", () => ({ __esModule: true, default: defineComponent({
  props: ["value", "readonly"], emits: ["ready", "change", "collaborationStatus", "editIntent"],
  setup(props, { emit, expose }) {
    counters.mounts++;
    watch(() => props.readonly, () => emit("collaborationStatus", { enabled: true, canSave: true }));
    expose({ focus: () => true, capture: () => ({ contentJson: JSON.stringify(props.value), markdown: counters.hydratedMarkdown, collaborationGeneration: "generation-1" }) });
    onMounted(() => { emit("ready"); emit("collaborationStatus", { enabled: true, canSave: true }); });
    return () => h("button", { class: "editor-test-change", onClick: () => emit("change") }, "Change editor");
  },
}) }));
vi.mock("./components/PageHistoryPanel.vue", () => ({ __esModule: true, default: defineComponent({
  props: ["page", "beforeRestore"], emits: ["close", "restored"],
  setup(props, { emit }) { return () => h("div", { class: "history-test" }, [
    h("button", { class: "history-test-close", onClick: () => emit("close") }, "Close"),
    h("button", { class: "history-test-restore", onClick: async () => {
      if (await props.beforeRestore()) emit("restored", { ...props.page, title: "Restored title", version: 3, publishedVersion: 1 });
    } }, "Restore"),
  ]); },
}) }));
vi.mock("./components/HistoryChanges.vue", () => ({ __esModule: true, default: defineComponent({
  props: ["revision", "previous", "label"],
  setup(props) { return () => h("div", { class: "changes-test" }, `${props.label}: ${props.previous.markdown} -> ${props.revision.markdown}`); },
}) }));
const page: Page = { id: "page-1", spaceId: "space-1", parentId: null, title: "Original", version: 2,
  isPublished: true, publishedVersion: 1, contentJson: JSON.stringify(emptyDocument()), activeFrom: "2026-01-01T08:00:00Z", canEdit: true };
let wrapper: VueWrapper | undefined;
/** The reader points into the text: the live editor attaches at once (3.6-F), no button to press. */
async function startEditing(app: VueWrapper) {
  app.getComponent(DocumentEditor).vm.$emit("editIntent", "point"); await flushPromises();
}
/** A writer's title is a field, not a heading (3.6-A). */
const titleOf = (app: VueWrapper) => (app.get(".document-title-text").element as HTMLTextAreaElement).value;
async function start(role = "Writer", custom?: Route) {
  counters.mounts = 0; counters.hydratedMarkdown = "";
  const team: Route = path => {
    if (path === "/api/auth/me") return response({ id: "user-1", username: "writer", displayName: "Writer", isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "system" } });
    if (path === "/api/spaces") return response([{ id: "space-1", name: "Team", role, icon: "book", color: "blue" }]);
    if (path === "/api/pages/page-1") return response(page);
    if (path === "/api/spaces/space-1/tree") return response({ spaceId: "space-1", version: 1, items: [{ ...page, sortOrder: 1024, canEdit: role !== "Reader" }] });
    if (path === "/api/spaces/space-1/pages") return response([page]);
  };
  wrapper = mountApp({ url: "/#/pages/page-1", fetch: routedFetch(custom, team, sessionRoutes, quietRoutes) });
  await flushPromises(); await flushPromises(); await flushPromises();
  return wrapper;
}
afterEach(() => { wrapper?.unmount(); wrapper = undefined; resetBrowser(); });
describe("history integration", () => {
  it("opens history from the timestamp without replacing the document", async () => {
    const app = await start("Writer", path => path === "/api/pages/page-1" ? Promise.resolve(response({ ...page, activeFrom: "2026-09-10T10:42:00Z", canEdit: true })) : undefined);
    await app.get('button.document-updated').trigger("click"); await flushPromises();
    expect(counters.mounts).toBe(1);
    await app.get(".history-test-close").trigger("click"); await flushPromises();
    expect(titleOf(app)).toBe("Original");
    expect(counters.mounts).toBe(1);
  });
  it("preserves the active editor while browsing and remounts only after confirmed restore", async () => {
    const success = vi.spyOn(toast, "success");
    const app = await start("Writer", (path, init) => path === "/api/pages/page-1" && init?.method === "PUT" ? Promise.resolve(response({ ...page, ...JSON.parse(init.body as string), version: 3 })) : undefined);
    const openHistory = async () => {
      await app.get('[aria-label="Další akce stránky"][aria-expanded]').trigger('click');
      // The action menu is portalled out of the wrapper.
      await new Promise(resolve => setTimeout(resolve, 40));
      const button = [...document.querySelectorAll('[role="menuitem"], button')]
        .find(item => item.textContent?.trim() === "Historie");
      expect(button, "the page action menu did not offer Historie").toBeDefined();
      (button as HTMLElement).click();
      await flushPromises();
    };
    expect(counters.mounts).toBe(1);
    await openHistory(); await flushPromises();
    expect(counters.mounts).toBe(1);
    await app.get(".history-test-close").trigger("click");
    await startEditing(app);
    await app.get(".editor-test-change").trigger("click");
    // The page's own changes ask nothing: opening the history checkpoints them, and restoring asks only for a component's input.
    const confirm = vi.spyOn(window, "confirm");
    await openHistory(); expect(app.find(".history-test").exists()).toBe(true);
    await app.get(".history-test-restore").trigger("click"); await flushPromises();
    expect(confirm).not.toHaveBeenCalled();
    expect(counters.mounts).toBe(2);
    expect(app.find(".history-test").exists()).toBe(false);
    expect(titleOf(app)).toBe("Restored title");
    expect(success).toHaveBeenCalledWith(expect.stringContaining("Historická verze byla obnovena"), expect.anything());
    expect(app.find("[data-page-save-state]").text()).not.toContain("Ukládám");
  });
  it("retains later local edits when an older publication completes", async () => {
    let resolvePublish!: (response: Response) => void;
    const app = await start("Writer", (path, init) => {
      if (path === "/api/pages/page-1" && init?.method === "PUT") return Promise.resolve(response({ ...page, ...JSON.parse(init.body as string), version: 3 }));
      if (path === "/api/pages/page-1/publish") return new Promise(resolve => { resolvePublish = resolve; });
    });
    await startEditing(app);
    await app.get(".document-title-text").setValue("Saved revision");
    await app.get(".document-title-text").trigger("blur");
    await vi.dynamicImportSettled(); await flushPromises();
    await app.get(".document-primary-actions .publish-button").trigger("click");
    await flushPromises();
    expect(resolvePublish).toBeTypeOf("function");
    // A change typed while the publication is on its way is a new change, waiting for the next checkpoint.
    await app.get(".editor-test-change").trigger("click");
    await app.get(".document-title-text").setValue("Later unsaved revision");
    resolvePublish(response({ ...page, title: "Saved revision", version: 3, publishedVersion: 3 }));
    await flushPromises();
    expect(titleOf(app)).toBe("Later unsaved revision");
    expect(app.get("[data-page-save-state]").text()).toContain("Ukládám");
    expect(counters.mounts).toBe(1);
  });
  it("checkpoints hydrated collaboration content before publishing even without a local change event", async () => {
    const writes: string[] = [];
    const app = await start("Writer", (path, init) => {
      if (path === "/api/pages/page-1" && init?.method === "PUT") {
        writes.push("save");
        const body = JSON.parse(init.body as string);
        expect(body.markdown).toBe("Hydrated shared content");
        expect(body.collaborationGeneration).toBe("generation-1");
        return Promise.resolve(response({ ...page, ...body, version: 3 }));
      }
      if (path === "/api/pages/page-1/publish") {
        writes.push("publish");
        // One click publishes exactly as before: no change summary in the body.
        expect(JSON.parse(init!.body as string)).toEqual({ expectedVersion: 3 });
        return Promise.resolve(response({ ...page, version: 3, publishedVersion: 3 }));
      }
    });
    await startEditing(app);
    counters.hydratedMarkdown = "Hydrated shared content";
    await vi.dynamicImportSettled(); await flushPromises();
    expect(app.find("[data-page-save-state]").exists()).toBe(false);
    await app.get(".document-primary-actions .publish-button").trigger("click"); await flushPromises();
    expect(writes).toEqual(["save", "publish"]);
  });
  it("publishes with the change summary typed in the panel of the publish arrow", async () => {
    let body: unknown;
    const app = await start("Writer", (path, init) => {
      if (path === "/api/pages/page-1" && init?.method === "PUT") return Promise.resolve(response({ ...page, ...JSON.parse(init.body as string), version: 3 }));
      if (path === "/api/pages/page-1/publish") { body = JSON.parse(init!.body as string); return Promise.resolve(response({ ...page, version: 3, publishedVersion: 3 })); }
    });
    await startEditing(app);
    await vi.dynamicImportSettled(); await flushPromises();
    await app.get('.document-actions button[aria-label="Publikovat s popisem změny…"]').trigger("click"); await flushPromises();
    const field = document.querySelector<HTMLInputElement>('input[name="changeSummary"]')!;
    field.value = "  Doplněn postup  "; field.dispatchEvent(new Event("input", { bubbles: true })); await flushPromises();
    [...document.querySelectorAll<HTMLButtonElement>('[role="dialog"] button')].find(button => button.textContent?.trim() === "Publikovat")!.click();
    await flushPromises(); await new Promise(resolve => setTimeout(resolve, 30)); await flushPromises();
    expect(body).toEqual({ expectedVersion: 3, changeSummary: "Doplněn postup" });
    expect(app.get(".document-status").text()).toContain("Stránka byla publikována");
  });
  it("discards the unpublished changes from the publish panel after showing them against the published version", async () => {
    let restore: unknown;
    const revision = (id: string, version: number, markdown: string) => ({ id, pageId: "page-1", version, title: "Original", markdown, contentJson: page.contentJson, isCurrent: version === 2 });
    const routes = (published: string | null): Route => (path, init) => {
      if (path === "/api/pages/page-1/publication") return Promise.resolve(response({ mode: null, spaceMode: "Manual", effective: "Manual", publishedVersion: 1, publishedAt: "2026-01-01T08:00:00Z", publishedBy: "Writer", publishedAutomatically: false, unpublishedChanges: true, canChange: true, publishedRevisionId: published }));
      if (path === "/api/pages/page-1/history?limit=1") return Promise.resolve(response({ pageId: "page-1", currentVersion: 2, items: [revision("rev-2", 2, "typo"), revision("rev-1", 1, "published")], nextBeforeVersion: null }));
      if (path.startsWith("/api/pages/page-1/history/")) return Promise.resolve(response(path.endsWith("rev-2") ? revision("rev-2", 2, "typo") : revision("rev-1", 1, "published")));
      if (path === "/api/pages/page-1/restore") { restore = JSON.parse(init!.body as string); return Promise.resolve(response({ ...page, version: 3, publishedVersion: 3 })); }
    };
    const openPanel = async (app: VueWrapper) => {
      await vi.dynamicImportSettled(); await flushPromises();
      await app.get('.document-actions button[aria-label="Publikovat s popisem změny…"]').trigger("click"); await flushPromises();
      return [...document.querySelectorAll<HTMLButtonElement>('[role="dialog"] button')].find(button => button.textContent?.trim() === "Zahodit změny…");
    };
    // No published revision to return to: nothing to discard to.
    let app = await start("Writer", routes(null));
    expect(await openPanel(app)).toBeUndefined();
    wrapper?.unmount(); resetBrowser();

    app = await start("Writer", routes("rev-1"));
    const success = vi.spyOn(toast, "success");
    (await openPanel(app))!.click();
    await vi.waitFor(() => expect(document.querySelector(".changes-test")?.textContent).toContain("published -> typo"));
    expect(restore).toBeUndefined();
    document.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-action]")!.click();
    await vi.waitFor(() => expect(restore).toEqual({ revisionId: "rev-1", expectedVersion: 2 }));
    await vi.waitFor(() => expect(success).toHaveBeenCalledWith(expect.stringContaining("Změny byly zahozeny"), expect.anything()));
    expect(counters.mounts).toBe(2);
    expect(app.find(".document-primary-actions .publish-button").exists()).toBe(false);
  });
  it("does not expose history to a Reader", async () => {
    const app = await start("Reader", path => path === "/api/pages/page-1" ? Promise.resolve(response({ ...page, canEdit: false })) : undefined);
    expect(app.findAll("button").some(button => button.text() === "Historie")).toBe(false);
    expect(app.find(".history-test").exists()).toBe(false);
  });
  it("lets the page's own right, not the space role, decide editing and every page action", async () => {
    const pageActions = async (app: VueWrapper) => {
      await app.get('[aria-label="Další akce stránky"][aria-expanded]').trigger("click");
      await vi.waitFor(() => expect(document.querySelector('[role="menuitem"]')).not.toBeNull());
      const labels = [...document.querySelectorAll('[role="menuitem"]')].map(item => item.textContent?.trim());
      document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true })); await flushPromises();
      return ["Historie", "Přesunout…", "Přesunout do koše…", "Automatický export PDF…"].filter(label => labels.includes(label));
    };
    // A Writer grant on this one page and no space role: the page is written and every page action is offered.
    let app = await start("Reader", path => path === "/api/pages/page-1" ? Promise.resolve(response({ ...page, canEdit: true })) : undefined);
    expect(app.getComponent(DocumentEditor).props("readonly")).toBe(false);
    expect(await pageActions(app)).toEqual(["Historie", "Přesunout…", "Přesunout do koše…", "Automatický export PDF…"]);
    wrapper?.unmount(); resetBrowser();
    // A read-only page in a space the account writes: none of them, and the page stays read-only.
    app = await start("Writer", path => path === "/api/pages/page-1" ? Promise.resolve(response({ ...page, canEdit: false })) : undefined);
    expect(app.getComponent(DocumentEditor).props("readonly")).toBe(true);
    expect(await pageActions(app)).toEqual([]);
    expect(app.find("button.document-updated").exists()).toBe(false);
  });
  it("says so when the right to write goes while typing, drops the stale save state and keeps the unsaved text to copy", async () => {
    let writable = true;
    const app = await start("Writer", path => path === "/api/pages/page-1" ? Promise.resolve(response({ ...page, canEdit: writable })) : undefined);
    await startEditing(app);
    await app.get(".editor-test-change").trigger("click");
    counters.hydratedMarkdown = "Typed after the downgrade";
    writable = false;
    const writeText = vi.fn(() => Promise.resolve());
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
    app.getComponent(DocumentEditor).vm.$emit("collaborationStatus", { enabled: true, canSave: false, pendingChanges: 1, error: { message: "[READ_ONLY] Stránku už nesmíte upravovat." } });
    await vi.waitFor(() => expect(app.find("[data-page-write-revoked]").exists()).toBe(true));
    expect(app.get("[data-page-write-revoked]").text()).toContain("Už nemáte oprávnění tuto stránku upravovat");
    expect(app.find("[data-page-save-state]").exists()).toBe(false);
    expect(app.getComponent(DocumentEditor).props("readonly")).toBe(true);
    const unload = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(unload);
    expect(unload.defaultPrevented).toBe(true);
    await app.get("[data-page-write-revoked] button").trigger("click"); await flushPromises();
    expect(writeText).toHaveBeenCalledWith("Typed after the downgrade");
  });
});

it("opens PDF settings beside the page and guards their unsaved changes on refresh", async () => {
  const app = await start("Writer", path => path === "/api/pages/page-1/pdf-export" ? Promise.resolve(response({
    settings: { enabled: false, storageProfileId: null, folder: "page-page1", fileName: "document.pdf", keepVersions: false, publishedOnly: false, version: 0, coverDetails: ["revision", "state", "revisionDate", "exportedAt", "pageId"] },
    profiles: [{ id: "store", name: "Dokumentace" }], status: "paused", lastExportedAt: null, lastRevisionId: null, lastError: "",
  })) : undefined);
  await app.get('[aria-label="Další akce stránky"][aria-expanded]').trigger("click");
  await new Promise(resolve => setTimeout(resolve, 40));
  ([...document.querySelectorAll('[role="menuitem"]')].find(item => item.textContent?.trim() === "Automatický export PDF…") as HTMLElement).click();
  await flushPromises(); await vi.waitFor(() => expect(app.find("[data-pdf-enabled]").exists()).toBe(true));
  expect(counters.mounts).toBe(1);
  expect(titleOf(app)).toBe("Original");
  await app.get("[data-pdf-enabled]").trigger("click");
  const enabled = app.get("[data-pdf-enabled]").attributes("aria-checked");
  expect(enabled).toBe("true");
  const unload = new Event("beforeunload", { cancelable: true });
  window.dispatchEvent(unload);
  expect(unload.defaultPrevented).toBe(true);
});
