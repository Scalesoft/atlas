import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../api";
import PagePdfExportDialog from "./PagePdfExportDialog.vue";
import { fakeApi, problem } from "./settings/general/testing";
import { createFakeLiveFeed } from "../live/fakeLiveFeed";
import { liveFeedSourceKey } from "../live/liveFeed";

let wrapper: VueWrapper | undefined;
/** /hubs/live as the dialog hears it: PdfExportChanged on the page's channel. */
let feed = createFakeLiveFeed();
const global = () => ({ provide: { [liveFeedSourceKey as symbol]: feed.source } });
const settings = { enabled: false, storageProfileId: null, folder: "", fileName: "document.pdf", keepVersions: false, publishedOnly: false, version: 0, coverDetails: ["revision", "state", "revisionDate", "exportedAt", "pageId"] };
const state = (extra = {}) => ({ settings, profiles: [{ id: "profile-1", name: "Dokumentace", location: "\\\\files\\share" }], status: "paused", lastExportedAt: null, lastRevisionId: null, lastError: "", ...extra });
/** The folders of the fake profile: the root holds one handbook folder, everything below is empty. */
const folders = (url: URL) => {
  const folder = url.searchParams.get("folder") ?? "";
  return { folder, folders: folder ? [] : ["Modelsoft Identity - MPI"], truncated: false };
};
const destination = () => wrapper!.get("[data-pdf-destination]").text();
afterEach(() => { feed = createFakeLiveFeed(); vi.useRealTimers(); wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); vi.restoreAllMocks(); clearCsrf(); document.body.innerHTML = ""; });

it("browses folders from the root, creates a subfolder, shows the whole path and saves only on explicit confirmation", async () => {
  const calls = fakeApi((method, path, body, { url }) =>
    path.endsWith("/folders") ? method === "POST" ? { folder: "Modelsoft Identity - MPI/Archiv", folders: [], truncated: false } : folders(url)
      : method === "PUT" ? state({ settings: { ...body as object, version: 1 } }) : state());
  wrapper = mount(PagePdfExportDialog, { props: { pageId: "12345678-abcd", title: "Žlutá příručka" }, attachTo: document.body, global: global() });
  await flushPromises();

  expect(wrapper.text()).toContain("30 sekund");
  await wrapper.get("[data-pdf-enabled]").trigger("click");
  await wrapper.get("[data-pdf-profile] [data-value=\"profile-1\"]").trigger("click"); await flushPromises();
  expect(destination()).toContain("\\\\files\\share\\document RRRR-MM-DD HH-mm.pdf");
  await wrapper.get('[data-pdf-subfolder="Modelsoft Identity - MPI"]').trigger("click"); await flushPromises();
  expect(destination()).toContain("\\\\files\\share\\Modelsoft Identity - MPI\\document RRRR-MM-DD HH-mm.pdf");
  expect(wrapper.text()).toContain("Žádné podsložky.");
  await wrapper.get("[data-pdf-new-folder]").setValue("Archiv");
  await wrapper.get("[data-pdf-create-folder]").trigger("submit"); await flushPromises();
  expect(calls.find(call => call.method === "POST")).toEqual({ method: "POST", path: "/pages/12345678-abcd/pdf-export/folders",
    body: { storageProfileId: "profile-1", parent: "Modelsoft Identity - MPI", name: "Archiv" } });
  expect(destination()).toContain("\\\\files\\share\\Modelsoft Identity - MPI\\Archiv\\document RRRR-MM-DD HH-mm.pdf");
  // The root is a target too.
  await wrapper.get("[data-pdf-folder-root]").trigger("click"); await flushPromises();
  expect(destination()).toContain("\\\\files\\share\\document RRRR-MM-DD HH-mm.pdf");
  expect(calls.filter(call => call.method === "PUT")).toHaveLength(0);
  await wrapper.get("[data-save-bar-save]").trigger("click"); await flushPromises();
  expect(calls.find(call => call.method === "PUT")).toMatchObject({
    path: "/pages/12345678-abcd/pdf-export",
    body: { enabled: true, storageProfileId: "profile-1", folder: "", fileName: "document.pdf", keepVersions: false, publishedOnly: false, expectedVersion: 0, confirmTakeover: false },
  });
});

it("takes over a file the export did not write only after confirmation", async () => {
  const calls = fakeApi((method, path, body, { url }) => path.endsWith("/folders") ? folders(url)
    : method === "PUT" ? (body as { confirmTakeover: boolean }).confirmTakeover ? state({ settings: { ...body as object, version: 1 } })
      : problem(409, "Ve složce už je soubor document.pdf, který tento export nevytvořil.", "pdf_target_exists")
      : state());
  wrapper = mount(PagePdfExportDialog, { props: { pageId: "page", title: "Příručka" }, attachTo: document.body, global: global() }); await flushPromises();
  await wrapper.get("[data-pdf-enabled]").trigger("click");
  await wrapper.get("[data-pdf-profile] [data-value=\"profile-1\"]").trigger("click"); await flushPromises();
  await wrapper.get("[data-save-bar-save]").trigger("click"); await flushPromises();
  expect(document.body.textContent).toContain("Převzít existující PDF");
  expect(document.body.textContent).toContain("který tento export nevytvořil");
  expect(calls.filter(call => call.method === "PUT")).toHaveLength(1);
  (document.querySelector("[data-confirm-action]") as HTMLButtonElement).click(); await flushPromises();
  expect(calls.filter(call => call.method === "PUT").map(call => (call.body as { confirmTakeover: boolean }).confirmTakeover)).toEqual([false, true]);
});

it("preserves a dirty draft on status refresh and failed saves, and retries only saved settings", async () => {
  let current = state({ settings: { ...settings, enabled: true, storageProfileId: "profile-1", folder: "Modelsoft Identity - MPI", version: 4 }, status: "error", lastError: "Síť není dostupná." });
  const calls = fakeApi((method, path, _body, { url }) => path.endsWith("/folders") ? folders(url)
    : method === "PUT" ? problem(409, "Nastavení mezitím někdo změnil.") : method === "POST" ? undefined : current);
  wrapper = mount(PagePdfExportDialog, { props: { pageId: "page", title: "Příručka" }, attachTo: document.body, global: global() }); await flushPromises();
  await wrapper.get("[data-pdf-filename]").setValue("my-draft.pdf");
  current = { ...current, settings: { ...current.settings, fileName: "server.pdf", version: 5 } };
  window.dispatchEvent(new Event("focus")); await flushPromises();
  expect(wrapper.get("[data-pdf-filename]").element).toHaveProperty("value", "my-draft.pdf");
  expect(wrapper.get("[data-pdf-run]").attributes("disabled")).toBeDefined();
  await wrapper.get("[data-save-bar-save]").trigger("click"); await flushPromises();
  expect(wrapper.text()).toContain("Nastavení mezitím někdo změnil.");
  expect(wrapper.get("[data-pdf-filename]").element).toHaveProperty("value", "my-draft.pdf");
  expect(calls.find(call => call.method === "PUT")!.body).toMatchObject({ expectedVersion: 4 });
  await wrapper.get("[data-save-bar-cancel]").trigger("click");
  await wrapper.get("[data-pdf-run]").trigger("click"); await flushPromises();
  expect(calls.filter(call => call.method === "POST")).toEqual([{ method: "POST", path: "/pages/page/pdf-export/run", body: undefined }]);
});

it("lets the status badge settle when an export in progress completes, and keeps a state that was already current quiet", async () => {
  const enabled = { ...settings, enabled: true, storageProfileId: "profile-1", version: 2 };
  let current = state({ status: "pending", settings: enabled });
  fakeApi((_method, path, _body, { url }) => path.endsWith("/folders") ? folders(url) : current);
  wrapper = mount(PagePdfExportDialog, { props: { pageId: "page", title: "Příručka" }, attachTo: document.body, global: global() }); await flushPromises();
  expect(wrapper.get(".status-badge").text()).toContain("Čeká na export");
  expect(wrapper.get(".status-badge").attributes("data-confirmed")).toBeUndefined();
  current = state({ status: "current", settings: enabled, lastExportedAt: "2026-10-11T10:00:00Z" });
  // The server tells the page's editors that the export stored a file.
  feed.emit("page:page", { pageId: "page" }); await flushPromises();
  expect(wrapper.get(".status-badge").text()).toContain("Aktuální PDF");
  expect(wrapper.get(".status-badge").attributes("data-confirmed")).toBeDefined();
  wrapper.unmount();
  fakeApi((_method, path, _body, { url }) => path.endsWith("/folders") ? folders(url) : current);
  wrapper = mount(PagePdfExportDialog, { props: { pageId: "page", title: "Příručka" }, attachTo: document.body, global: global() }); await flushPromises();
  expect(wrapper.get(".status-badge").text()).toContain("Aktuální PDF");
  expect(wrapper.get(".status-badge").attributes("data-confirmed")).toBeUndefined();
});

it("asks before closing a dirty draft and leaves the form available after staying", async () => {
  fakeApi(() => state());
  wrapper = mount(PagePdfExportDialog, { props: { pageId: "page", title: "Příručka" }, attachTo: document.body, global: global() }); await flushPromises();
  await wrapper.get("[data-pdf-enabled]").trigger("click");
  await wrapper.get('button[aria-label="Zavřít"]').trigger("click");
  expect(wrapper.emitted("close")).toBeUndefined();
  expect(wrapper.text()).toContain("Neuložené změny");
  await wrapper.findAll("button").find(button => button.text() === "Zůstat")!.trigger("click");
  expect(wrapper.emitted("close")).toBeUndefined();
  expect(wrapper.get("[data-pdf-enabled]").attributes("aria-checked")).toBe("true");
});

it("reads the export's state only when the server tells it changed or the window comes back, never on a timer", async () => {
  vi.useFakeTimers();
  const calls = fakeApi((_method, path, _body, { url }) => path.endsWith("/folders") ? folders(url) : state());
  wrapper = mount(PagePdfExportDialog, { props: { pageId: "page", title: "Příručka" }, attachTo: document.body, global: global() }); await flushPromises();
  const reads = () => calls.filter(call => call.method === "GET" && call.path === "/pages/page/pdf-export").length;
  const settled = reads();
  await vi.advanceTimersByTimeAsync(10 * 60_000);
  expect(reads()).toBe(settled);
  feed.emit("page:other", { pageId: "other" }); await flushPromises();
  expect(reads()).toBe(settled);
  feed.emit("page:page", { pageId: "page" }); await flushPromises();
  expect(reads()).toBe(settled + 1);
  feed.fail("page:page"); await flushPromises();
  expect(wrapper.get("[data-live-update-note]").text()).toContain("Živá aktualizace nedostupná");
});
