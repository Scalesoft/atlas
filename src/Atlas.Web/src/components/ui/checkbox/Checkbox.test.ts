import { afterEach, describe, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import { Checkbox } from "./index";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = ""; });

function render(initial: boolean) {
  const checked = ref(initial);
  wrapper = mount(defineComponent({
    render: () => h(Checkbox, { modelValue: checked.value, "onUpdate:modelValue": (value: boolean | "indeterminate") => { checked.value = value === true; } }),
  }), { attachTo: document.body });
  return { checked, box: wrapper.get('[role="checkbox"]'), indicator: () => document.querySelector('[data-slot="checkbox-indicator"]') };
}

describe("Checkbox tick", () => {
  it("stands without drawing when the box arrives checked", () => {
    const { indicator } = render(true);
    expect(indicator()).not.toBeNull();
    expect(indicator()!.hasAttribute("data-toggled")).toBe(false);
  });

  it("is marked for drawing once the reader has toggled the box, and its path is one 30-unit dash", async () => {
    const { box, indicator, checked } = render(false);
    expect(indicator()).toBeNull();
    await box.trigger("click");
    await flushPromises();
    expect(checked.value).toBe(true);
    expect(indicator()!.hasAttribute("data-toggled")).toBe(true);
    expect(indicator()!.getAttribute("data-state")).toBe("checked");
    expect(indicator()!.querySelector("path")!.getAttribute("pathLength")).toBe("30");
  });

  it("does not draw a tick the owner sets from outside", async () => {
    const { indicator, checked } = render(false);
    checked.value = true;
    await flushPromises();
    expect(indicator()!.hasAttribute("data-toggled")).toBe(false);
  });
});
