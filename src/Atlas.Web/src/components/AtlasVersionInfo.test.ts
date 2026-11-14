import { describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import AtlasVersionInfo from "./AtlasVersionInfo.vue";

describe("AtlasVersionInfo", () => {
  it("shows only the release until opened, then the commit and the bundled editor", async () => {
    const wrapper = mount(AtlasVersionInfo, {
      props: { version: "0.1.1+672459f5d4d1c1b6d2f0a1e3c9b8a7d6e5f4c3b2" },
      slots: { default: "<button>Obnovit přístup správce</button>" },
      attachTo: document.body,
    });
    const toggle = wrapper.get("button.atlas-version-toggle");
    expect(toggle.text()).toBe("Atlas 0.1.1");
    expect(toggle.attributes("aria-expanded")).toBe("false");
    expect(wrapper.get(".atlas-version-details").isVisible()).toBe(false);

    await toggle.trigger("click");
    expect(toggle.attributes("aria-expanded")).toBe("true");
    const details = wrapper.get(".atlas-version-details");
    expect(details.isVisible()).toBe(true);
    expect(details.attributes("id")).toBe(toggle.attributes("aria-controls"));
    expect(details.text()).toContain("0.1.1 · 672459f");
    // The build identifies the actual editor; source mode can differ from the installed release.
    expect(details.text()).toContain(`Markdown editor${__MARKDOWN_EDITOR_VERSION__}`);
    expect(details.text()).toContain("Obnovit přístup správce");
    wrapper.unmount();
  });

  it("with an href the release leads to Co je nového and the arrow alone unfolds the details", async () => {
    const wrapper = mount(AtlasVersionInfo, { props: { version: "3.5.1+672459f", href: "#/whats-new" }, attachTo: document.body });
    const link = wrapper.get("a.atlas-version-link");
    expect(link.text()).toBe("Atlas 3.5.1");
    expect(link.attributes("href")).toBe("#/whats-new");
    const toggle = wrapper.get("button.atlas-version-toggle");
    expect(toggle.attributes("aria-label")).toBe("Informace o verzi");
    await toggle.trigger("click");
    expect(wrapper.get(".atlas-version-details").isVisible()).toBe(true);
    wrapper.unmount();
  });

  it("folds the details with the arrow instead of cutting them away, and reopens them usable", async () => {
    // jsdom has no Web Animations: a stand-in that finishes at once records what was animated.
    const animate = vi.fn((_frames: Keyframe[]) => ({ addEventListener: (type: string, callback: () => void) => { if (type === "finish") callback(); }, finished: Promise.resolve(), cancel: vi.fn() }));
    Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, writable: true, value: animate });
    const getAnimations = vi.fn(() => []);
    Object.defineProperty(HTMLElement.prototype, "getAnimations", { configurable: true, writable: true, value: getAnimations });
    try {
      // Test Utils stubs Transition by default; the hooks are what is under test.
      const wrapper = mount(AtlasVersionInfo, { props: { version: "0.1.1+672459f" }, slots: { default: "<button>Obnovit přístup správce</button>" }, attachTo: document.body, global: { stubs: { transition: false } } });
      const toggle = wrapper.get("button.atlas-version-toggle");
      await toggle.trigger("click");
      const opening = animate.mock.calls.at(-1)![0];
      expect(opening[0]).toMatchObject({ height: "0px", opacity: 0 });
      animate.mockClear();
      await toggle.trigger("click");
      const closing = animate.mock.calls.at(-1)![0];
      expect(closing.at(-1)).toMatchObject({ height: "0px", opacity: 0 });
      await toggle.trigger("click");
      const details = wrapper.get(".atlas-version-details");
      expect(details.isVisible()).toBe(true);
      expect(details.attributes("inert")).toBeUndefined();
      expect(details.attributes("aria-hidden")).toBeUndefined();
      wrapper.unmount();
    } finally {
      delete (HTMLElement.prototype as { animate?: unknown }).animate;
      delete (HTMLElement.prototype as { getAnimations?: unknown }).getAnimations;
    }
  });

  it("stays usable when the server did not report a version", () => {
    const wrapper = mount(AtlasVersionInfo);
    expect(wrapper.get("button").text()).toBe("Atlas");
    expect(wrapper.get(".atlas-version-details").text()).toContain("neznámá verze");
  });
});
