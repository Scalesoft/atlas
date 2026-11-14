import { beforeEach, describe, expect, it } from "vitest";
import { defineComponent, h } from "vue";
import { flushPromises, mount } from "@vue/test-utils";
import { buildOutdated, lazyComponent, preloadComponent } from "./lazyComponent";

const Panel = defineComponent({ render: () => h("p", "panel") });
const host = (component: ReturnType<typeof lazyComponent>) =>
  mount(defineComponent({ render: () => h(component) }));

describe("lazyComponent", () => {
  beforeEach(() => { buildOutdated.value = false; });

  it("renders the chunk once it arrives", async () => {
    const wrapper = host(lazyComponent(() => Promise.resolve(Panel)));
    await flushPromises();
    expect(wrapper.text()).toContain("panel");
    expect(buildOutdated.value).toBe(false);
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
    expect(buildOutdated.value).toBe(false);
  });

  // A deploy replaces every hashed chunk, so a tab opened before it asks for a file the
  // server no longer has. Vue would swallow that and render nothing: the action looks
  // dead. The shell must learn about it and offer a reload.
  it("flags the build as outdated when the chunk stays gone", async () => {
    let attempts = 0;
    host(lazyComponent(() => {
      attempts++;
      return Promise.reject(new Error("Failed to fetch dynamically imported module"));
    }));
    await flushPromises();
    expect(attempts).toBe(2);
    expect(buildOutdated.value).toBe(true);
  });
});
