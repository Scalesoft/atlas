import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import EditLockToggle from "./EditLockToggle.vue";

describe("EditLockToggle", () => {
  it("keeps one name, says the state with aria-pressed and asks the page to toggle", async () => {
    const wrapper = mount(EditLockToggle, { props: { locked: true } });
    const button = wrapper.get("[data-edit-lock]");
    expect(button.attributes("aria-pressed")).toBe("true");
    expect(button.attributes("aria-label")).toBe("Jen pro čtení");
    // Locked it is a badge with words, so the state shows at a glance (owner 2026-11-07).
    expect(button.text()).toBe("Jen pro čtení");
    await button.trigger("click");
    expect(wrapper.emitted("toggle")).toHaveLength(1);
    await wrapper.setProps({ locked: false });
    expect(button.attributes("aria-pressed")).toBe("false");
    expect(button.attributes("aria-label")).toBe("Jen pro čtení");
    expect(button.text()).toBe("");
  });

  it("offers a primary Upravit beside the locked badge as the same unlock, and only the lock while unlocked", async () => {
    const wrapper = mount(EditLockToggle, { props: { locked: true } });
    const edit = wrapper.get("[data-edit-unlock]");
    expect(edit.element.tagName).toBe("BUTTON");
    expect(edit.text()).toBe("Upravit");
    // The badge first, Upravit after it: both stand before the publish controls the page puts behind them.
    const order = wrapper.findAll("button").map(item => (item.attributes("data-edit-unlock") === undefined ? "lock" : "edit"));
    expect(order).toEqual(["lock", "edit"]);
    // Not a second toggle: the lock keeps the state, so the browser suite's [data-edit-lock] stays one control.
    expect(edit.attributes("data-edit-lock")).toBeUndefined();
    expect(edit.attributes("aria-pressed")).toBeUndefined();
    await edit.trigger("click");
    expect(wrapper.emitted("toggle")).toHaveLength(1);
    await wrapper.setProps({ locked: false });
    expect(wrapper.find("[data-edit-unlock]").exists()).toBe(false);
    expect(wrapper.find("[data-edit-lock]").exists()).toBe(true);
  });

  it("pulses the badge and Upravit together on request, announces it and starts a new pulse only after the previous one ends", async () => {
    const wrapper = mount(EditLockToggle, { props: { locked: true, pulse: 0 } });
    expect(wrapper.find(".edit-lock-ring").exists()).toBe(false);
    await wrapper.setProps({ pulse: 1 });
    expect(wrapper.find(".edit-lock-ring").exists()).toBe(true);
    expect(wrapper.get("[data-edit-lock]").classes()).toContain("is-pulsing");
    expect(wrapper.get("[data-edit-unlock]").classes()).toContain("is-pulsing");
    expect(wrapper.get("[role=status]").text()).toContain("Jen pro čtení");
    // A burst of keystrokes pulses once: a pulse asked for while one runs is dropped.
    const ring = wrapper.get(".edit-lock-ring").element;
    await wrapper.setProps({ pulse: 2 });
    expect(wrapper.get(".edit-lock-ring").element).toBe(ring);
    await wrapper.get(".edit-lock-ring").trigger("animationend");
    expect(wrapper.find(".edit-lock-ring").exists()).toBe(false);
    expect(wrapper.get("[data-edit-unlock]").classes()).not.toContain("is-pulsing");
    await wrapper.setProps({ pulse: 3 });
    expect(wrapper.find(".edit-lock-ring").exists()).toBe(true);
  });
});
