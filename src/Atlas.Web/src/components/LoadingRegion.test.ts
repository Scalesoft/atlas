import { afterEach, describe, expect, it } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import LoadingRegion from "./LoadingRegion.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => wrapper?.unmount());
const region = (props: { loading: boolean; ready: boolean }) => (wrapper = mount(LoadingRegion, { props, slots: { default: "<p>Obsah</p>" } }));

describe("LoadingRegion content", () => {
  it("is marked as arriving when it takes the place of the skeleton", async () => {
    const view = region({ loading: true, ready: false });
    expect(view.find(".loading-skeleton").exists()).toBe(true);
    expect(view.find(".loading-region-content").exists()).toBe(false);
    await view.setProps({ loading: false, ready: true });
    expect(view.find(".loading-skeleton").exists()).toBe(false);
    expect(view.get(".loading-region-content").attributes("data-arrived")).toBeDefined();
  });

  it("stands at once when it was ready from the start and stays put through a refresh", async () => {
    const view = region({ loading: false, ready: true });
    const content = view.get(".loading-region-content");
    expect(content.attributes("data-arrived")).toBeUndefined();
    await view.setProps({ loading: true });
    expect(view.get(".loading-region").attributes("data-refreshing")).toBeDefined();
    expect(view.get(".loading-region-content").element).toBe(content.element);
    expect(view.get(".loading-region-content").attributes("data-arrived")).toBeUndefined();
  });
});
