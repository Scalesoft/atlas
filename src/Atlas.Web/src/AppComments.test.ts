// Comments in the shell (comments spec §4, §5): the section „Komentáře“ under a page being read, read after the page itself, and
// comment hits of the search in their own group leading to the page's heading.
import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { emptyDocument } from "./document";
import { pageLink } from "./pageLinks";
import { jsonResponse as response, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";

const space = { id: "space-1", name: "Provoz", icon: "book", color: "blue", role: "Writer", pinPolicy: "None", pinned: true, pageCount: 1, lastChangedAt: null };
const page = { id: "page-1", spaceId: space.id, parentId: null, title: "Runbook", version: 1, isPublished: true, contentJson: JSON.stringify(emptyDocument()), markdown: "", publishedVersion: 1 };
const comment = { id: "c1", authorId: "user-2", authorName: "Petr Svoboda", body: "Chybí **krok** s certifikátem", anchor: null, createdAt: "2026-10-26T09:00:00Z",
  editedAt: null, deleted: false, canEdit: false, canDelete: false };

let wrapper: VueWrapper | undefined;
let requested: string[] = [];
afterEach(() => { wrapper?.unmount(); wrapper = undefined; resetBrowser(); });
async function settle() { await vi.dynamicImportSettled(); await flushPromises(); await flushPromises(); await vi.dynamicImportSettled(); await flushPromises(); }

async function start(hash: string, extra?: Route) {
  const wiki: Route = path => {
    requested.push(path);
    if (path === "/api/auth/me") return response({ id: "user-1", username: "jana", displayName: "Jana", isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "light" } });
    if (path === "/api/spaces") return response([space]);
    if (path === "/api/ai/capabilities") return response({});
    if (path === `/api/spaces/${space.id}/tree`) return response({ spaceId: space.id, version: 1, items: [{ ...page, sortOrder: 1024, canEdit: true }] });
    if (path === `/api/spaces/${space.id}/pages`) return response([page]);
    if (path === `/api/pages/${page.id}`) return response(page);
    if (path === `/api/objects/${page.id}/comments`) return response({ items: [comment], hasMore: false, count: 1, canPost: true });
  };
  requested = [];
  wrapper = mountApp({ url: "/" + hash, fetch: routedFetch(extra, wiki, sessionRoutes, quietRoutes) });
  await settle();
  return wrapper;
}

describe("App: comments", () => {
  it("shows „Komentáře“ under the page being read, read only after the page", async () => {
    const app = await start(pageLink(page.id));
    await vi.waitFor(() => expect(app.find("[data-comment-thread] [data-comment-id]").exists()).toBe(true));
    const thread = app.get("[data-comment-thread]");
    expect(thread.get(".comment-thread-title").text()).toBe("Komentáře");
    expect(thread.text()).toContain("Petr Svoboda");
    expect(thread.get(".comment-body strong").text()).toBe("krok");
    expect(requested.indexOf(`/api/objects/${page.id}/comments`)).toBeGreaterThan(requested.indexOf(`/api/pages/${page.id}`));
    // The page's editor services (`[[` pages, people) come with the thread, not with the shell's first screen, yet reach the composer.
    expect(app.findComponent({ name: "CommentThread" }).props("host")).toMatchObject({ pages: expect.anything(), people: expect.anything() });
  });

  it("shows comment hits of the search as their own group leading to the page's heading", async () => {
    const search: Route = path => path.startsWith("/api/search") ? response({ items: [], hasMore: false, spaces: [], nextOffset: null,
      resources: [{ type: "comment", id: "c1", spaceId: space.id, spaceName: "Provoz", title: "Runbook", snippet: "Chybí krok s certifikátem",
        target: { objectId: page.id, typeId: "atlas.page", anchor: "instalace" } }] }) : undefined;
    const app = await start(`#/spaces/${space.id}`, search);
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "k", ctrlKey: true }));
    await settle();
    await app.get(".search-dialog input").setValue("certifikát");
    await new Promise(resolve => setTimeout(resolve, 260)); await settle();
    const group = document.querySelector<HTMLElement>('.search-group[data-kind="comments"]')!;
    expect(group.querySelector(".search-group-title")?.textContent).toContain("Komentáře");
    const hit = group.querySelector<HTMLAnchorElement>(".search-result")!;
    expect(hit.getAttribute("href")).toBe(pageLink(page.id, "instalace"));
    expect(hit.textContent).toContain("Komentář · Provoz");
    expect(hit.textContent).toContain("Chybí krok s certifikátem");
  });
});
