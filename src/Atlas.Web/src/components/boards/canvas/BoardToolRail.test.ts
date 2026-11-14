import { afterEach, describe, expect, it } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import BoardToolRail from "./BoardToolRail.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

describe("BoardToolRail", () => {
  it("asks the canvas for the components (its search beside the rail) instead of opening a menu of its own", async () => {
    wrapper = mount(BoardToolRail, { attachTo: document.body, props: { tool: "select", canEdit: true, canUndo: false, canRedo: false, canPickPage: false } });
    await wrapper.get("[data-board-tool=component]").trigger("click");
    expect(wrapper.emitted("components")).toHaveLength(1);
    expect(document.querySelector("[role=menu]")).toBeNull();
  });
});
