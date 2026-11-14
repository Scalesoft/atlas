// Module Projector in the shell (K29): a space gets the tabs Přehled nasazení and Na co čekáme with addresses of their own, only
// while GET /api/modules names the module; without it neither tab renders and neither view's chunk is imported.
import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { useModules } from "./composables/useModules";
import { emptyDocument } from "./document";
import { jsonResponse as response, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";

const imported = vi.hoisted(() => ({ views: 0 }));
const stub = vi.hoisted(() => async (name: string) => {
  imported.views++;
  const { defineComponent, h } = await import("vue");
  return { __esModule: true, default: defineComponent({ props: ["spaceId"], setup: props => () => h("div", { [`data-${name}-stub`]: props.spaceId }) }) };
});
vi.mock("./projector/overview/DeploymentOverview.vue", () => stub("deployments"));
vi.mock("./projector/overview/WaitingList.vue", () => stub("waiting"));

const appearance = { layout: "connected", accent: "blue", mode: "light" };
const member = { id: "user-2", username: "member", displayName: "Test Member", isWikiAdmin: false, appearance };
const space = { id: "space-1", name: "Tým", icon: "book", color: "blue", role: "Writer", pinPolicy: "None", pinned: true, pageCount: 1, lastChangedAt: null };
const tree = { spaceId: "space-1", version: 4, items: [{ id: "p1", parentId: null, title: "KBN", version: 2, sortOrder: 1024, isPublished: true, canEdit: true, type: "page" }] };

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; useModules().stop(); resetBrowser(); imported.views = 0; });
async function settle() { await vi.dynamicImportSettled(); await flushPromises(); await flushPromises(); await vi.dynamicImportSettled(); await flushPromises(); }
async function start(hash: string, modules: string[], extra?: Route) {
  const wiki: Route = path => {
    const custom = extra?.(path);
    if (custom) return custom;
    if (path === "/api/auth/me") return response(member);
    if (path === "/api/spaces") return response([space]);
    if (path === "/api/modules") return response({ modules: modules.map(id => ({ id, version: 1 })), contentTypes: [] });
    if (path === "/api/ai/capabilities") return response({});
    if (path === "/api/spaces/space-1/tree") return response(tree);
    if (path.startsWith("/api/integrations/youtrack/")) return response([]);
  };
  wrapper = mountApp({ url: "/" + hash, fetch: routedFetch(wiki, sessionRoutes, quietRoutes) });
  await settle();
  return wrapper;
}
const tabs = (app: VueWrapper) => app.findAll(".space-tabs [role=tab]").map(tab => tab.attributes("data-space-tab"));

describe("App: module Projector", () => {
  it("shows neither tab and imports neither view while the module is off; the address falls back to Stránky", async () => {
    const app = await start("#/spaces/space-1/deployments", ["pages"]);
    expect(tabs(app)).toEqual(["pages", "contacts", "about"]);
    expect(app.find("[data-deployments-stub]").exists()).toBe(false);
    expect(imported.views).toBe(0);
  });

  it("opens Přehled nasazení and Na co čekáme by their tabs and addresses", async () => {
    const app = await start("#/spaces/space-1/deployments", ["pages", "projector"]);
    expect(tabs(app)).toEqual(["pages", "contacts", "about", "deployments", "waiting"]);
    expect(app.get("[data-deployments-stub]").attributes("data-deployments-stub")).toBe("space-1");
    await app.get('[data-space-tab="waiting"]').trigger("click");
    await settle();
    expect(location.hash).toBe("#/spaces/space-1/waiting");
    expect(app.get("[data-waiting-stub]").attributes("data-waiting-stub")).toBe("space-1");
  });

  // U3-12: „Nový“ on a catalog page put the new page into the catalog; W3-6: every page open downloaded the whole catalog.
  it("starts a new page beside a catalog page, not under it, and tells a catalog by the page itself", async () => {
    const catalogTree = { ...tree, items: [{ ...tree.items[0], id: "folder" }, { ...tree.items[0], id: "catalog", parentId: "folder", title: "Katalog" }] };
    const catalogPage = { id: "catalog", spaceId: "space-1", parentId: "folder", title: "Katalog", version: 2, isPublished: true, publishedVersion: 2, canEdit: true,
      contentJson: JSON.stringify(emptyDocument()), markdown: "", activeFrom: "", isCatalog: true };
    const catalogDownloads = vi.fn();
    const app = await start("#/pages/catalog", ["pages", "projector"], path => {
      if (path === "/api/spaces/space-1/tree") return response(catalogTree);
      if (path === "/api/pages/catalog") return response(catalogPage);
      if (path.startsWith("/api/projector/spaces/space-1/catalog")) { catalogDownloads(); return response({ definitions: [] }); }
    });
    await settle();
    expect(app.find("[data-page-catalog]").exists()).toBe(true);
    expect(catalogDownloads).not.toHaveBeenCalled();
    await app.get(".sidebar-new-page").trigger("click");
    await settle();
    expect(app.getComponent({ name: "PageCreationFlow" }).props("initialParentId")).toBe("folder");
  });
});
