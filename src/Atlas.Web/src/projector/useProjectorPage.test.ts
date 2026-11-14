import { describe, expect, it, vi } from "vitest";
import { ref } from "vue";
import type { Page } from "../api";
import { holdsRequirements, useProjectorPage } from "./useProjectorPage";

const page = (extra: Partial<Page> = {}): Page => ({
  id: "p1", spaceId: "s1", parentId: "root-page", title: "KBN", version: 3, contentJson: "{}", publishedVersion: 1, activeFrom: "", canEdit: true, isCatalog: false, ...extra,
} as Page);

function harness(current: Page | undefined, enabled = true) {
  const api = {
    setCatalogPage: vi.fn(async (pageId: string, isCatalog: boolean) => ({ pageId, isCatalog })),
    createDeployment: vi.fn(async (): Promise<{ id: string; title: string; warnings?: string[] }> => ({ id: "copy", title: "KBN — kopie" })),
  };
  const deps = { api, openPage: vi.fn(async () => {}), refreshTree: vi.fn(async () => {}), toast: { success: vi.fn(), error: vi.fn() } };
  const shown = ref(current);
  return { shown, api, deps, projector: useProjectorPage(shown, ref(enabled), deps) };
}

describe("useProjectorPage", () => {
  // W3-6: every page open downloaded the space's whole catalog only to tell whether this page is one.
  it("tells a catalog page by the page's own flag, and nothing while the module is off", () => {
    const { shown, projector } = harness(page({ isCatalog: true }));
    expect(projector.catalogPage.value).toBe(true);
    shown.value = page({ id: "p2", isCatalog: false });
    expect(projector.catalogPage.value).toBe(false);
    expect(harness(page({ isCatalog: true }), false).projector.catalogPage.value).toBeUndefined();
  });

  it("marks and unmarks the page as the catalog on the page itself, and says so", async () => {
    const { shown, api, deps, projector } = harness(page());
    await projector.toggleCatalogPage();
    expect(api.setCatalogPage).toHaveBeenCalledWith("p1", true);
    expect(shown.value!.isCatalog).toBe(true);
    expect(projector.catalogPage.value).toBe(true);
    expect(deps.toast.success).toHaveBeenCalledWith("Stránka je katalog požadavků.");
    // The editor's summary menu changed it: the page follows.
    projector.setCatalog(false);
    expect(projector.catalogPage.value).toBe(false);
  });

  // U3-4: the copy was only announced; the person had to find it in the tree or catch the toast's action.
  it("founds a deployment beside the page under the name asked for, and opens it", async () => {
    const { api, deps, projector } = harness(page());
    projector.askDeployment();
    expect(projector.deployment.value?.title).toBe("KBN — kopie");
    projector.deployment.value!.title = "Kostelec";
    await projector.createDeployment();
    expect(api.createDeployment).toHaveBeenCalledWith("p1", { spaceId: "s1", parentId: "root-page", title: "Kostelec" });
    expect(projector.deployment.value).toBeUndefined();
    expect(deps.refreshTree).toHaveBeenCalledWith("s1");
    expect(deps.openPage).toHaveBeenCalledWith("copy");
    expect(deps.toast.success).toHaveBeenCalledWith("Nasazení „KBN — kopie“ je založené.", { action: expect.objectContaining({ label: "Otevřít" }) });
  });

  it("keeps the copy's warnings in the dialog until the person opens the copy", async () => {
    const { api, deps, projector } = harness(page());
    api.createDeployment.mockResolvedValueOnce({ id: "copy", title: "KBN — kopie", warnings: ["Grant správce pro Janu nastavte ručně."] });
    projector.askDeployment();
    await projector.createDeployment();
    expect(projector.deployment.value?.created).toEqual({ id: "copy", title: "KBN — kopie", warnings: ["Grant správce pro Janu nastavte ručně."] });
    expect(deps.openPage).not.toHaveBeenCalled();
    await projector.openCreated();
    expect(deps.openPage).toHaveBeenCalledWith("copy");
    expect(projector.deployment.value).toBeUndefined();
  });

  it("keeps the dialog with the server's reason when founding fails", async () => {
    const { api, projector } = harness(page());
    api.createDeployment.mockRejectedValueOnce(new Error("Název už existuje."));
    projector.askDeployment();
    await projector.createDeployment();
    expect(projector.deployment.value).toMatchObject({ busy: false, error: "Název už existuje." });
  });
});

describe("holdsRequirements", () => {
  it("finds a heading with a state and an old block, not a plain heading", () => {
    const doc = (heading: object) => JSON.stringify({ document: { type: "doc", content: [heading] } });
    expect(holdsRequirements(doc({ type: "heading", attrs: { level: 2, headingId: "a", state: "planned" }, content: [{ type: "text", text: "A" }] }))).toBe(true);
    expect(holdsRequirements(doc({ type: "requirement", attrs: { id: "a" } }))).toBe(true);
    expect(holdsRequirements(doc({ type: "heading", attrs: { level: 2, headingId: "a", state: null } }))).toBe(false);
    expect(holdsRequirements(undefined)).toBe(false);
  });
});
