// Test helpers for the general settings pages: a fake API on `fetch` and a mount inside the
// providers a settings surface gives its pages (route, context, dirty guard, flash).
import { vi } from "vitest";
import { mount } from "@vue/test-utils";
import { defineComponent, h, provide, type Component } from "vue";
import { createFakeLiveFeed } from "../../../live/fakeLiveFeed";
import { liveFeedSourceKey, type LiveFeedSource } from "../../../live/liveFeed";
import type { SettingsContext } from "../context";
import { provideSettingsContext } from "../context";
import { createDirtyGuard, provideDirtyGuard, type DirtyGuard } from "../dirtyGuard";
import { createSettingsFlash, provideSettingsFlash, type SettingsFlash } from "../flash";
import { provideSettingsRoute } from "../useSettingsRoute";

export type FakeHandler = (method: string, path: string, body: unknown) => unknown;
/** What a handler can read beyond the method, path and body: the whole URL (the query string) and the headers. */
export interface FakeRequest {
  url: URL;
  headers: Headers;
}
export type FakeRequestHandler = (method: string, path: string, body: unknown, request: FakeRequest) => unknown;
export interface FakeCall {
  method: string;
  path: string;
  body: unknown;
}

/** Answers `/api/...` requests with `handler`; `undefined` is 204, a Response is passed through. */
export function fakeApi(handler: FakeRequestHandler) {
  const calls: FakeCall[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: string, init?: RequestInit) => {
      const url = new URL(String(input), "http://atlas.test");
      const method = init?.method ?? "GET";
      const raw = init?.body;
      const body = raw instanceof FormData ? raw : raw ? JSON.parse(String(raw)) : undefined;
      const path = url.pathname.replace(/^\/api/, "");
      if (path === "/auth/csrf") return Response.json({ token: "csrf" });
      calls.push({ method, path, body });
      const result = handler(method, path, body, { url, headers: new Headers(init?.headers) });
      if (result instanceof Response) return result;
      if (result === undefined) return new Response(null, { status: 204 });
      return Response.json(structuredClone(result));
    }),
  );
  return calls;
}

export type FakeUrlHandler = (method: string, url: URL, body: unknown) => unknown;
/** `fakeApi` for a handler that works on the URL itself. `calls` keep what was asked: `/api` prefix and query string. */
export function fakeUrlApi(handler: FakeUrlHandler) {
  const calls: FakeCall[] = [];
  fakeApi((method, _path, body, { url }) => {
    calls.push({ method, path: `${url.pathname}${url.search}`, body });
    return handler(method, url, body);
  });
  return calls;
}
export const problem = (status: number, detail: string, code?: string) => new Response(JSON.stringify({ detail, code }), { status });

export function testContext(overrides: Partial<SettingsContext> = {}): SettingsContext {
  return {
    userId: "u-admin",
    isWikiAdmin: true,
    spaces: [],
    backupWarning: null,
    version: undefined,
    imported: vi.fn(),
    warning: vi.fn(),
    openPage: vi.fn(),
    newTemplate: vi.fn(),
    newPage: vi.fn(),
    spaceChanged: vi.fn(),
    spaceTrashed: vi.fn(),
    spacesChanged: vi.fn(),
    accessChanged: vi.fn(),
    ...overrides,
  };
}

/** What a settings surface provides to its pages (route, context, dirty guard, flash). Call it in the setup of a test host. */
export function provideSettingsSurface(options: { context: SettingsContext; guard: DirtyGuard; base?: string }) {
  const router = provideSettingsRoute(options.base ?? "#/settings");
  provideSettingsContext(options.context);
  provideDirtyGuard(options.guard);
  const flash = createSettingsFlash(router);
  provideSettingsFlash(flash);
  return { router, flash };
}

/** Mounts `component` as a page of the global settings surface at `hash`. */
/** `feed` stands in for /hubs/live (an idle fake by default: a page never reaches the real hub in a test). */
export function mountPage(component: Component, props: Record<string, unknown> = {}, options: { hash?: string; context?: SettingsContext; feed?: LiveFeedSource } = {}) {
  history.replaceState(null, "", `/${options.hash ?? "#/settings"}`);
  const context = options.context ?? testContext();
  const guard = createDirtyGuard();
  let flash: SettingsFlash | undefined;
  const Harness = defineComponent({
    setup() {
      flash = provideSettingsSurface({ context, guard }).flash;
      provide(liveFeedSourceKey, options.feed ?? createFakeLiveFeed().source);
      return () => h("div", { class: "settings-ui" }, [h(component, props)]);
    },
  });
  const wrapper = mount(Harness, { attachTo: document.body });
  return { wrapper, guard, context, flash: () => flash! };
}
