import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { REDUCED_MOTION_PREVIEW } from "../../motion/aiMotion";
import ReleaseMark from "./ReleaseMark.vue";

describe("ReleaseMark", () => {
  it("draws the bundled mark inline as decoration, with ids of its own, and stands still under reduced motion", async () => {
    document.body.innerHTML = `<div ${REDUCED_MOTION_PREVIEW}><div id="host"></div></div>`;
    const first = mount(ReleaseMark, { props: { mark: "pear", variant: "watermark" }, attachTo: "#host" });
    const second = mount(ReleaseMark, { props: { mark: "pear" } });
    await vi.waitFor(() => expect(first.find("svg").exists() && second.find("svg").exists()).toBe(true));
    const svg = first.find("svg");
    expect(first.attributes()).toMatchObject({ "aria-hidden": "true", "data-variant": "watermark", "data-still": "true" });
    expect(svg.classes()).toContain("wm-pear");
    const gradient = svg.find("linearGradient").attributes("id")!;
    expect(gradient).toMatch(/^pear-gradient-.+/);
    expect(svg.html()).toContain(`url(#${gradient})`);
    expect(second.find("linearGradient").attributes("id")).not.toBe(gradient);
    expect(second.attributes("data-still")).toBeUndefined();
    first.unmount();
    second.unmount();
  });

  it("leaves an unknown mark empty", async () => {
    const wrapper = mount(ReleaseMark, { props: { mark: "none", variant: "icon" } });
    await flushPromises();
    expect(wrapper.find("svg").exists()).toBe(false);
  });
});
