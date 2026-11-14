import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import SpacePinToggle from "./SpacePinToggle.vue";

describe("SpacePinToggle", () => {
  it("keeps the pending toggle focusable but rejects repeated activation until ready", async () => {
    const wrapper = mount(SpacePinToggle, {
      props: { space: { id: "space", name: "Prostor", pinned: false, pinPolicy: "None" }, pending: true },
      global: { stubs: { Tooltip: { template: '<div><slot /></div>' }, TooltipTrigger: { props: ['asChild'], template: '<slot />' }, TooltipContent: true } },
    });
    const button = wrapper.get("button");
    expect(button.attributes("disabled")).toBeUndefined();
    expect(button.attributes("aria-disabled")).toBe("true");
    await button.trigger("click");
    await button.trigger("click");
    expect(wrapper.emitted("toggle")).toBeUndefined();
    await wrapper.setProps({ pending: false });
    await button.trigger("click");
    expect(wrapper.emitted("toggle")).toHaveLength(1);
  });
});
