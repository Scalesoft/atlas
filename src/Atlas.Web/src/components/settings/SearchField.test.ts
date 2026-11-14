import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import SearchField from "./SearchField.vue";

describe("SearchField availability", () => {
  it("cannot clear a disabled filter, and becomes editable when enabled", async () => {
    const wrapper = mount(SearchField, { props: { modelValue: "Provoz", label: "Prostor", disabled: true } });
    expect(wrapper.get("input").element.disabled).toBe(true);
    expect(wrapper.get("button").element.disabled).toBe(true);
    await wrapper.get("button").trigger("click");
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
    await wrapper.setProps({ disabled: false });
    await wrapper.get("button").trigger("click");
    expect(wrapper.emitted("update:modelValue")).toEqual([[""]]);
  });

  it("keeps read-only text intact and lets Escape dismiss the surrounding layer", async () => {
    const wrapper = mount(SearchField, { props: { modelValue: "Provoz", label: "Prostor", readonly: true } });
    expect(wrapper.get("input").element.readOnly).toBe(true);
    expect(wrapper.find("button").exists()).toBe(false);
    let surroundingEscape = false;
    wrapper.element.addEventListener("keydown", () => { surroundingEscape = true; });
    const escape = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
    wrapper.get("input").element.dispatchEvent(escape);
    expect(surroundingEscape).toBe(true);
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
  });
});
