import { afterEach, describe, expect, it, vi } from "vitest";
import source from "../public/early-reads.js?raw";
import { parseShellHash } from "./shellRoute";

const page = "6227b75c-0db2-4c79-a47d-b3f1d8bc7f0e";
const space = "93cc07fb-5e15-57d8-93b1-c392fb6d9b11";
type Reads = Map<string, Promise<Response>>;

/** Runs public/early-reads.js against a fake window and fetch; returns the paths it read once its chain settled. */
async function run(hash: string, me: { status: number; isWikiAdmin?: boolean } = { status: 200 }, running?: Reads) {
  const window: { atlasEarlyReads?: Reads } = { atlasEarlyReads: running };
  const fetch = vi.fn(async (url: string) => url === "/api/auth/me"
    ? new Response(JSON.stringify({ id: "u", isWikiAdmin: me.isWikiAdmin ?? false }), { status: me.status })
    : new Response("{}", { status: 200 }));
  new Function("window", "location", "fetch", source)(window, { pathname: "/", hash }, fetch);
  await new Promise((resolve) => setTimeout(resolve, 0));
  return { paths: fetch.mock.calls.map(([url]) => url.slice("/api".length)), reads: window.atlasEarlyReads };
}

afterEach(() => {
  delete (globalThis as { atlasEarlyReads?: Reads }).atlasEarlyReads;
  vi.restoreAllMocks();
  vi.resetModules();
});

describe("early reads", () => {
  it("reads what the first view reads: the session, then the spaces and modules, and the page of the address", async () => {
    expect((await run(`#/pages/${page}?heading=a`, { status: 200, isWikiAdmin: true })).paths)
      .toEqual(["/setup/status", "/auth/me", `/pages/${page}/collaboration/state`, `/pages/${page}?bundle=true`, "/spaces", "/modules"]);
    expect((await run(`#/spaces/${space}`)).paths).toEqual(["/setup/status", "/auth/me", `/spaces/${space}/tree`, "/spaces", "/modules"]);
  });

  it("reads nothing beyond the session for a visitor who is signed out, and nothing at all once the app runs", async () => {
    expect((await run("#/", { status: 401 })).paths).toEqual(["/setup/status", "/auth/me"]);
    expect((await run(`#/pages/${page}`, { status: 200 }, new Map())).paths).toEqual([]);
  });

  // App.vue prefetches the page for the page and page-access views and the tree for the space view; early-reads.js must
  // never read what the app would not ask for (it would be a second request), so its address rules are a subset.
  it.each([
    `#/pages/${page}`, `#/pages/${page}?block=b`, `#/pages/${page}/access`, `#/pages/${page}/access/links`, `#/pages/${page}/history`,
    `#/spaces/${space}`, `#/spaces/${space}/about`, `#/spaces/${space}/new-page`, "#/pages/page-1", "#/library",
  ])("reads for %s only what App.vue prefetches", async (hash) => {
    const route = parseShellHash(hash);
    for (const path of (await run(hash, { status: 401 })).paths.slice(2)) {
      if (path.startsWith("/pages/")) expect(route).toMatchObject({ pageId: path.slice("/pages/".length).replace(/\?bundle=true$|\/collaboration\/state$/, "") });
      if (path.startsWith("/pages/")) expect(["page", "pageAccess"]).toContain(route.view);
      else expect(route).toEqual({ view: "space", spaceId: space });
    }
  });

  it("is adopted once by the app's plain GET, then the app reads from the server again", async () => {
    const { reads } = await run(`#/pages/${page}`, { status: 200 });
    (globalThis as { atlasEarlyReads?: Reads }).atlasEarlyReads = reads;
    const fetch = vi.spyOn(globalThis, "fetch").mockImplementation(async () => new Response(JSON.stringify({ id: "fresh" })));
    const { api, dropEarlyReads } = await import("./api");
    // The very GET App.vue's prefetchPage makes.
    expect(await api(`/pages/${page}?bundle=true`)).toEqual({});
    expect(fetch).not.toHaveBeenCalled();
    expect(await api(`/pages/${page}?bundle=true`)).toEqual({ id: "fresh" });
    // A request with a signal is the app's own; an unused early read is dropped when the first view has loaded.
    await api("/spaces", { signal: new AbortController().signal });
    dropEarlyReads();
    expect(await api("/modules")).toEqual({ id: "fresh" });
    expect(fetch).toHaveBeenCalledTimes(3);
  });
});
