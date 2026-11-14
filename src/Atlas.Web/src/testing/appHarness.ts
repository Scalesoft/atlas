// Scaffolding shared by the tests that mount the real App.vue and drive it through the DOM, fetch and the URL: a JSON
// response, a fetch that answers by route, the mount and the cleanup. A test file states only what is its own: its
// payloads, its extra or overriding routes and the DocumentEditor stand-in its assertions look at. The stand-ins differ
// in what they render, expose and emit, and vi.mock is hoisted, so they stay in the test file.
//
// Plain functions with explicit arguments. Nothing here registers hooks or remembers state between calls: the test file
// keeps its wrapper, unmounts it, settles as many times as it needs and calls resetBrowser() where its afterEach says.
import { vi } from "vitest";
import { mount } from "@vue/test-utils";
import type { Component } from "vue";
import App from "../App.vue";
import { clearCsrf } from "../api";
import { forgetPageReferences } from "../editorDirectories";
import { contactApi } from "../contacts/contactApi";
import { forgetPageConnections } from "../pageConnections";

export function jsonResponse(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });
}

/** Answers one request, or returns undefined to leave it to the next route. */
export type Route = (path: string, init?: RequestInit) => Response | Promise<Response> | undefined;

/**
 * A fetch that asks the routes in order and returns the first answer. A request nobody answers rejects, so a new
 * request the application makes shows up as a failing test instead of passing silently. Put a route that answers
 * with something the test asserts on before the shared ones: the first answer wins. Undefined entries are skipped, so
 * an optional override can be passed straight in.
 */
export function routedFetch(...routes: (Route | undefined)[]) {
  return vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
    const path = String(input);
    for (const route of routes) {
      const answer = route?.(path, init);
      if (answer) return answer;
    }
    // The app reads a page with its bundle (`?bundle=true`), opening it with `&visit=true`; a route written for the page answers both.
    const plain = path.replace(/\?(bundle=true(&visit=true)?|visit=true)$/, "");
    if (plain !== path) for (const route of routes) {
      const answer = route?.(plain, init);
      if (answer) return answer;
    }
    throw new Error(`Unexpected request: ${path}`);
  });
}

/** What the application asks for whatever the test is about: whether the database is ready at start, and a CSRF token before its first write. */
export const sessionRoutes: Route = (path) => {
  if (path === "/api/auth/csrf") return jsonResponse({ token: "test-csrf" });
  if (path === "/api/setup/status") return jsonResponse({ databaseReady: true, requiresSetup: false });
};

/** Defaults for opening the shell and a page: an empty personal library, nothing favourite or visited. A test that needs other answers puts its own route first. */
export const quietRoutes: Route = (path) => {
  if (path.startsWith("/api/me/library/")) return jsonResponse({ items: [], hasMore: false, nextOffset: null });
  // Domů at `#/` (the home dashboard): nothing changed.
  if (path.startsWith("/api/me/changes")) return jsonResponse({ items: [], next: null });
  if (path === "/api/notifications/unread") return jsonResponse({ count: 0 });
  if (/\/personal-state$|\/visit$/.test(path)) return jsonResponse({ isFavorite: false, visitedAt: null });
  // The live state read beside a page's bundle (readPageBundle): none, so an editor joins through the server.
  if (/^\/api\/pages\/[^/]+\/collaboration\/state$/.test(path)) return new Response(null, { status: 204 });
  // An open page reads its discussion (comments spec); none by default.
  if (/^\/api\/objects\/[^/]+\/comments(\?|$)/.test(path)) return jsonResponse({ items: [], hasMore: false, count: 0, canPost: true });
};

/** Clears the CSRF token, puts the address in place, installs `fetch` and mounts App.vue into the document. */
export function mountApp(options: { url: string; fetch: (input: string, init?: RequestInit) => Promise<Response>; stubs?: Record<string, Component | boolean> }) {
  clearCsrf();
  history.replaceState(null, "", options.url);
  vi.stubGlobal("fetch", options.fetch);
  return mount(App, { attachTo: document.body, ...(options.stubs && { global: { stubs: options.stubs } }) });
}

/** What must not outlive a test; call it after unmounting the wrapper and after any reset of the test's own. */
export function resetBrowser() {
  // Spies on shared objects (history, window) never outlive their test, also when it fails before its own mockRestore:
  // a leftover history.pushState spy made the next test that spies on it wrap itself and recurse.
  vi.restoreAllMocks();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  clearCsrf();
  // The answers the app keeps for a while across pages (links, contacts) must not reach the next test.
  forgetPageReferences(); forgetPageConnections(); contactApi.forget();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
}

/** A localStorage in memory: the stub for `vi.stubGlobal("localStorage", storage)`, and the values a test reads or presets. */
export function memoryStorage() {
  const values = new Map<string, string>();
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); },
    clear: () => { values.clear(); },
    key: (index: number) => [...values.keys()][index] ?? null,
    get length() { return values.size; },
  };
  return { values, storage };
}
