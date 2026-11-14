import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { ApiError } from "../../../api";
import { contentImportApi, type WikiImportReport } from "../../../contentImport";
import WikiJsImportModule from "./WikiJsImportModule.vue";
import { mountPage } from "./testing";

vi.mock("../../../contentImport", () => ({ contentImportApi: { preview: vi.fn(), commit: vi.fn(), get: vi.fn(), sourceUrl: (id: string) => `/source/${id}`, reportUrl: (id: string) => `/report/${id}` } }));

const report = (): WikiImportReport => ({
  id: "batch",
  reviewToken: "review",
  expiresAt: "2099-01-01T00:00:00Z",
  spaceId: null,
  spaceName: "Import Wiki.js",
  counts: { create: 1, update: 0, skip: 0, conflict: 0, assets: 0, unsupported: 1 },
  items: [{ path: "page.md", kind: "page", action: "create", title: "Imported page", pageId: "page", expectedVersion: null, sha256: "hash", warnings: ["Check content"] }],
  warnings: ["Original ZIP is retained"],
  canCommit: true,
  completedAt: null,
});
const done = (): WikiImportReport => ({ ...report(), spaceId: "space", completedAt: "2026-09-07T12:00:00Z", canCommit: false });

let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.resetAllMocks();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});
function open() {
  const mounted = mountPage(WikiJsImportModule);
  wrapper = mounted.wrapper;
  return mounted;
}
async function choose(view: VueWrapper, name = "wiki.zip") {
  const input = view.get('input[type="file"]');
  Object.defineProperty(input.element, "files", { value: [new File(["zip"], name, { type: "application/zip" })], configurable: true });
  await input.trigger("change");
}
async function preview(view: VueWrapper) {
  await choose(view);
  await view.get("[data-import-source]").setValue("wiki");
  await view.get("[data-flow-next]").trigger("click");
  await flushPromises();
}
async function acknowledge(view: VueWrapper) {
  await view.get("[data-import-ack-warnings]").trigger("click");
  await view.get("[data-import-ack-quarantine]").trigger("click");
}

describe("Wiki.js import", () => {
  it("labels the dropzone, shows the chosen file and accepts a dropped one", async () => {
    const { wrapper: view } = open();
    expect(view.get('input[type="file"]').attributes("aria-label")).toBe("Přetáhněte ZIP sem nebo vyberte soubor");
    await choose(view, "wiki-export.zip");
    expect(view.get("[data-zip-dropzone]").text()).toContain("wiki-export.zip");
    await view.get("[data-zip-dropzone]").trigger("drop", { dataTransfer: { files: [new File(["zip"], "dropped.zip")] } });
    expect(view.get("[data-zip-dropzone]").text()).toContain("dropped.zip");
    vi.mocked(contentImportApi.preview).mockResolvedValue(report());
    await view.get("[data-import-source]").setValue("wiki");
    expect(view.get("[data-flow-next]").attributes("disabled")).toBeUndefined();
    await view.get("[data-flow-next]").trigger("click");
    await flushPromises();
    expect(vi.mocked(contentImportApi.preview).mock.calls[0]![0].name).toBe("dropped.zip");
  });

  it("requires both acknowledgements, then imports, reports the space and clears the draft", async () => {
    vi.mocked(contentImportApi.preview).mockResolvedValue(report());
    vi.mocked(contentImportApi.commit).mockResolvedValue(done());
    const { wrapper: view, guard } = open();
    await preview(view);
    expect(guard.dirty.value).toBe(true);
    expect(view.get('[data-count="create"]').text()).toContain("1");
    expect(view.get("[data-flow-next]").text()).toContain("Potvrdit import konceptů");
    expect(view.get("[data-flow-next]").attributes("aria-disabled")).toBe("true");
    await view.get("[data-import-ack-warnings]").trigger("click");
    expect(view.get("[data-flow-next]").attributes("aria-disabled")).toBe("true");
    await view.get("[data-import-ack-quarantine]").trigger("click");
    await view.get("[data-flow-next]").trigger("click");
    await flushPromises();
    expect(contentImportApi.commit).toHaveBeenCalledTimes(1);
    expect(view.findComponent(WikiJsImportModule).emitted("imported")).toEqual([["space"]]);
    expect(view.get("[data-import-done]").text()).toContain("Import byl dokončen");
    expect(view.get("[data-import-open-space]").attributes("href")).toContain("space");
    expect(guard.dirty.value).toBe(false);
    await view.get("[data-import-again]").trigger("click");
    expect(view.find("[data-import-source]").exists()).toBe(true);
    expect(view.find('[data-step="review"]').exists()).toBe(false);
  });

  it("cancels a preview without importing and releases the draft so onboarding can continue", async () => {
    vi.mocked(contentImportApi.preview).mockResolvedValue(report());
    const { wrapper: view, guard } = open();
    await preview(view);
    await view.get("[data-flow-cancel]").trigger("click");
    await flushPromises();
    expect(contentImportApi.commit).not.toHaveBeenCalled();
    expect(guard.dirty.value).toBe(false);
    expect(view.find("[data-import-source]").exists()).toBe(true);
  });

  it("keeps an interrupted confirmation reviewable and checks the same batch instead of importing again", async () => {
    vi.mocked(contentImportApi.preview).mockResolvedValue(report());
    vi.mocked(contentImportApi.commit).mockRejectedValue(new TypeError("Network unavailable"));
    vi.mocked(contentImportApi.get).mockResolvedValue(done());
    const { wrapper: view } = open();
    await preview(view);
    await acknowledge(view);
    await view.get("[data-flow-next]").trigger("click");
    await flushPromises();
    expect(view.text()).toContain("výsledek zatím není ověřen");
    expect(view.get("[data-flow-next]").attributes("aria-disabled")).toBe("true");
    expect(view.get("[data-import-source]").element.closest("fieldset")?.disabled).toBe(true);
    expect(view.get("[data-flow-cancel]").attributes("disabled")).toBeDefined();
    expect(view.get("[data-import-check]").element.closest("fieldset")?.disabled).toBe(false);
    await view.get("[data-import-check]").trigger("click");
    await flushPromises();
    expect(contentImportApi.get).toHaveBeenCalledWith("batch");
    expect(contentImportApi.commit).toHaveBeenCalledTimes(1);
    expect(view.findComponent(WikiJsImportModule).emitted("imported")).toEqual([["space"]]);
  });

  it("lists each page's conversion findings and narrows the list to them", async () => {
    vi.mocked(contentImportApi.preview).mockResolvedValue({
      ...report(),
      items: [
        {
          ...report().items[0]!,
          tags: ["ops"],
          findings: [
            { construct: "wiki-link", impact: "unresolved", count: 1, line: 12, excerpt: "[[Missing]]", message: "Wiki odkaz neodpovídá žádné stránce." },
            { construct: "kbd", impact: "degraded", count: 3, line: 28, excerpt: "<kbd>", message: "Klávesová zkratka je kód." },
            { construct: "comment", impact: "lost", count: 1, line: null, excerpt: null, message: "Komentář byl vypuštěn." },
          ],
        },
        { path: "clean.md", kind: "page", action: "create", title: "Clean page", pageId: "clean", expectedVersion: null, sha256: "h", warnings: [], findings: [] },
        // A report stored before findings existed.
        { path: "old.md", kind: "page", action: "skip", title: "Old page", pageId: "old", expectedVersion: 1, sha256: "o", warnings: [], findings: null, tags: null },
      ],
    });
    const { wrapper: view } = open();
    await preview(view);
    const entries = view.get('ul[aria-label="Převod stránky Imported page"]').findAll("li");
    expect(entries.map((entry) => entry.attributes("data-impact"))).toEqual(["unresolved", "degraded", "lost"]);
    expect(entries[0]!.text()).toContain("Nepřiřazený odkaz");
    expect(entries[0]!.text()).toContain("řádek 12");
    expect(entries[0]!.get("code").text()).toBe("[[Missing]]");
    expect(entries[1]!.text()).toContain("3×, poprvé na řádku 28");
    expect(entries[2]!.text()).toContain("Jen v ZIP");
    expect(entries[2]!.find("code").exists()).toBe(false);
    expect(view.get('ul[aria-label="Štítky stránky"]').text()).toBe("ops");
    expect(view.text()).toContain("1 stránka má části, které se nepřenesly beze změny");
    expect(view.text()).toContain("Old page");
    await view.get('[data-import-show] [data-value="findings"]').trigger("click");
    await flushPromises();
    expect(view.text()).not.toContain("Clean page");
    expect(view.text()).not.toContain("Old page");
    expect(view.text()).toContain("Imported page");
  });

  it("invalidates the preview and acknowledgements when source options change in place", async () => {
    vi.mocked(contentImportApi.preview).mockResolvedValue(report());
    const { wrapper: view } = open();
    await preview(view);
    await acknowledge(view);
    await view.get("[data-import-space]").setValue("Upravený prostor");
    expect(view.text()).not.toContain("Imported page");
    expect(view.get('[data-step="review"] fieldset').attributes("disabled")).toBeDefined();
    expect(view.get('[data-step="source"] [data-flow-next]').text()).toBe("Připravit náhled");
    expect(contentImportApi.commit).not.toHaveBeenCalled();
    await view.get('[data-step="source"] [data-flow-next]').trigger("click");
    await flushPromises();
    expect(contentImportApi.preview).toHaveBeenLastCalledWith(expect.any(File), expect.objectContaining({ spaceName: "Upravený prostor" }), expect.any(AbortSignal));
    expect(view.get("[data-import-ack-warnings]").attributes("aria-checked")).toBe("false");
    expect(view.get("[data-import-ack-quarantine]").attributes("aria-checked")).toBe("false");
    expect(view.get('[data-step="review"] [data-flow-next]').attributes("aria-disabled")).toBe("true");
  });

  it("hides the private report when access was revoked during the confirmation", async () => {
    vi.mocked(contentImportApi.preview).mockResolvedValue(report());
    vi.mocked(contentImportApi.commit).mockRejectedValue(new ApiError(403, "Access revoked"));
    const { wrapper: view } = open();
    await preview(view);
    await acknowledge(view);
    await view.get("[data-flow-next]").trigger("click");
    await flushPromises();
    expect(view.text()).not.toContain("Imported page");
    expect(view.find("[data-import-source]").exists()).toBe(true);
    expect(view.get("[data-import-error]").text()).toContain("Access revoked");
  });

  it("blocks the confirmation after a conflict and says to prepare a new preview", async () => {
    vi.mocked(contentImportApi.preview).mockResolvedValue(report());
    vi.mocked(contentImportApi.commit).mockRejectedValue(new ApiError(409, "Obsah se mezitím změnil."));
    const { wrapper: view } = open();
    await preview(view);
    await acknowledge(view);
    await view.get("[data-flow-next]").trigger("click");
    await flushPromises();
    expect(view.text()).toContain("Import nelze potvrdit");
    expect(view.get("[data-flow-next]").attributes("aria-disabled")).toBe("true");
    await view.get("[data-import-new-preview]").trigger("click");
    expect(view.text()).not.toContain("Imported page");
    expect(view.get('[data-step="source"] [data-flow-next]').attributes("disabled")).toBeUndefined();
  });
});
