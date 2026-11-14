import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";

// The icon pack is shared module state and its drawings are mocked, so every case starts from a pack nobody has
// requested; a throwing factory runs again on each import (see iconPack.test.ts).
const pack = { loads: 0, failures: 0 };
const mountIcon = async (props: { name: string; size?: number }) =>
  mount((await import("./AtlasIcon.vue")).default, { props });

beforeEach(() => {
  vi.resetModules();
  pack.loads = 0;
  pack.failures = 0;
  // Every part of the pack answers the same drawing; a failing part throws on each call.
  vi.doMock("virtual:icon-pack/parts", () => ({
    default: Array.from({ length: 64 }, () => async () => {
      pack.loads++;
      if (pack.failures > 0) {
        pack.failures--;
        throw new Error("Failed to fetch dynamically imported module");
      }
      return { default: { dog: '<path d="M1 1"/>' } };
    }),
  }));
});
afterEach(() => vi.restoreAllMocks());

describe("AtlasIcon", () => {
  it("renders registry markup with the shared geometry", async () => {
    const wrapper = await mountIcon({ name: "link", size: 18 });
    const svg = wrapper.get("svg");
    expect(svg.attributes("data-icon")).toBe("link");
    expect(svg.attributes("stroke-width")).toBe("1.75");
    expect(svg.attributes("width")).toBe("18");
    expect(pack.loads).toBe(0);
  });

  it("defaults to size 18 when no size is given", async () => {
    const wrapper = await mountIcon({ name: "search" });
    expect(wrapper.get("svg").attributes("width")).toBe("18");
  });

  it("resolves a wiki-local name through the shared map", async () => {
    const wrapper = await mountIcon({ name: "trash" });
    expect(wrapper.get("svg").attributes("data-icon")).toBe("trash-2");
  });

  it("passes through a name that is already a registry key", async () => {
    const wrapper = await mountIcon({ name: "book-open" });
    expect(wrapper.get("svg").attributes("data-icon")).toBe("book-open");
  });

  it("renders the settings drawings from the registry (editor 0.6.0)", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const wrapper = await mountIcon({ name: "network", size: 16 });
    const svg = wrapper.get("svg");
    expect(svg.attributes("data-icon")).toBe("network");
    expect(svg.attributes("stroke-width")).toBe("1.75");
    expect(svg.attributes("width")).toBe("16");
    expect(warn).not.toHaveBeenCalled();
  });

  it("draws a pack icon as an empty box of the same size until the pack has arrived", async () => {
    const wrapper = await mountIcon({ name: "dog", size: 26 });
    const pending = wrapper.get("svg");
    expect(pending.attributes("data-icon")).toBe("dog");
    expect(pending.attributes("data-icon-state")).toBe("pending");
    expect(pending.attributes("width")).toBe("26");
    expect(pending.element.childElementCount).toBe(0);

    await vi.waitFor(() => expect(wrapper.get("svg").find("path").exists()).toBe(true));
    const drawn = wrapper.get("svg");
    expect(drawn.attributes("data-icon")).toBe("dog");
    expect(drawn.attributes("data-icon-state")).toBeUndefined();
    expect(drawn.attributes("stroke-width")).toBe("1.75");
    expect(drawn.attributes("width")).toBe("26");
  });

  it("falls back to circle-help while the pack stays unavailable, and a later mount tries again", async () => {
    pack.failures = 2;
    const first = await mountIcon({ name: "dog" });
    await vi.waitFor(() => expect(first.get("svg").attributes("data-icon")).toBe("circle-help"));

    const second = await mountIcon({ name: "dog" });
    await vi.waitFor(() => expect(second.get("svg").attributes("data-icon")).toBe("dog"));
    // The icon that failed earlier is drawn too, as soon as the pack is there.
    expect(first.get("svg").attributes("data-icon")).toBe("dog");
    expect(pack.loads).toBe(3);
  });

  it("warns about an unknown name only after the pack has arrived, then falls back", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const wrapper = await mountIcon({ name: "nonsense" });
    expect(wrapper.get("svg").attributes("data-icon-state")).toBe("pending");
    expect(warn).not.toHaveBeenCalled();

    await vi.waitFor(() => expect(wrapper.get("svg").attributes("data-icon")).toBe("circle-help"));
    expect(warn).toHaveBeenCalledTimes(1);
  });
});
