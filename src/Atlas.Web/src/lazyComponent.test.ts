import { describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { lazyComponent, preloadComponent, reloadOnNewBuild } from "./lazyComponent";

const Panel = defineComponent({ render: () => h("p", "panel") });
const host = (component: ReturnType<typeof lazyComponent>) =>
  mount(defineComponent({ render: () => h(component) }));

describe("lazyComponent", () => {
  it("renders the chunk once it arrives", async () => {
    const wrapper = host(lazyComponent(() => Promise.resolve(Panel)));
    await flushPromises();
    expect(wrapper.text()).toContain("panel");
  });

  it("draws a preloaded chunk in its first render, where a fresh one still waits", async () => {
    const preloaded = lazyComponent(() => Promise.resolve(Panel)), fresh = lazyComponent(() => Promise.resolve(Panel));
    await preloadComponent(preloaded);
    expect(host(preloaded).text()).toContain("panel");
    expect(host(fresh).text()).not.toContain("panel");
  });

  it("retries once, so a single flaky request still renders the chunk", async () => {
    let attempts = 0;
    const wrapper = host(lazyComponent(() => {
      attempts++;
      return attempts === 1 ? Promise.reject(new Error("network")) : Promise.resolve(Panel);
    }));
    await flushPromises();
    expect(attempts).toBe(2);
    expect(wrapper.text()).toContain("panel");
  });

  // The failed import itself reaches reloadOnNewBuild through Vite's vite:preloadError; the component only must not break.
  it("renders its empty fallback when the chunk stays gone after the retry", async () => {
    let attempts = 0;
    const wrapper = host(lazyComponent(() => {
      attempts++;
      return Promise.reject(new Error("Failed to fetch dynamically imported module"));
    }));
    await flushPromises();
    expect(attempts).toBe(2);
    expect(wrapper.text()).toBe("");
  });
});

// A tab opened before a deploy asks for chunks the server no longer has (Safari keeps such tabs alive for days).
describe("reloadOnNewBuild", () => {
  const page = (entry: string) => `<!doctype html><html><head><script type="module" crossorigin src="${entry}"></script></head><body></body></html>`;
  function tab(answer: () => Promise<Response>) {
    const win = new EventTarget() as EventTarget & Record<string, unknown>;
    const document = new DOMParser().parseFromString(page("/assets/index-old.js"), "text/html");
    const storage = new Map<string, string>();
    const reload = vi.fn();
    const fetch = vi.fn(answer);
    Object.assign(win, {
      document: { querySelector: (selector: string) => {
        const script = document.querySelector<HTMLScriptElement>(selector);
        return script && { src: new URL(script.getAttribute("src")!, "https://atlas.test/").href };
      } },
      location: { origin: "https://atlas.test", pathname: "/", href: "https://atlas.test/#/library", reload },
      fetch,
      sessionStorage: { getItem: (key: string) => storage.get(key) ?? null, setItem: (key: string, value: string) => storage.set(key, value) },
    });
    reloadOnNewBuild(win as unknown as Window);
    const fail = async () => { win.dispatchEvent(new Event("vite:preloadError")); await vi.waitFor(() => expect(fetch).toHaveBeenCalled()); await flushPromises(); };
    return { fail, fetch, reload, storage };
  }
  const html = (entry: string) => () => Promise.resolve(new Response(page(entry), { status: 200 }));

  it("reloads the tab once its page starts another entry: a deploy has replaced the build", async () => {
    const { fail, fetch, reload } = tab(html("/assets/index-new.js"));
    await fail();
    expect(fetch).toHaveBeenCalledWith("https://atlas.test/", { cache: "no-store" });
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it("leaves the tab alone when the page still starts this build, as after a network failure", async () => {
    const { fail, reload } = tab(html("/assets/index-old.js"));
    await fail();
    expect(reload).not.toHaveBeenCalled();
  });

  it("leaves the tab alone when the page does not answer", async () => {
    const { fail, reload } = tab(() => Promise.reject(new TypeError("Load failed")));
    await fail();
    expect(reload).not.toHaveBeenCalled();
  });

  it("reloads at most once a minute, so servers answering with different builds cannot loop", async () => {
    const { fail, reload, storage } = tab(html("/assets/index-new.js"));
    storage.set("atlas.newBuildReload", String(Date.now()));
    await fail();
    expect(reload).not.toHaveBeenCalled();
  });
});
