import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import FormField from "./FormField.vue";

describe("FormField", () => {
  it("shows its error under the control and hands the control the id to describe itself by", () => {
    const wrapper = mount(FormField, { props: { label: "Name", error: "Required" }, slots: { default: `<template #default="{ describedBy }"><input :aria-describedby="describedBy" /></template>` } });
    const alert = wrapper.get('[role="alert"]');
    expect(alert.text()).toBe("Required");
    expect(wrapper.get("input").attributes("aria-describedby")).toBe(alert.attributes("id"));
  });
  it("renders no alert without an error", () => {
    expect(mount(FormField, { props: { label: "Name" }, slots: { default: "<input />" } }).find('[role="alert"]').exists()).toBe(false);
  });
});
