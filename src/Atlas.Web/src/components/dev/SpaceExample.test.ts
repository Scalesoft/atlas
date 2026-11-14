import { afterEach, expect, it } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import SpaceExample from "./SpaceExample.vue";

let wrapper: ReturnType<typeof mount>;
afterEach(() => {
  wrapper?.unmount();
  document.body.innerHTML = "";
});

it("shows the space settings composites with local sample data", async () => {
  wrapper = mount(SpaceExample, { attachTo: document.body });
  await flushPromises();
  expect(wrapper.get('[data-access-entry="automatic:0"]').text()).toContain("Správci wiki");
  expect(wrapper.get('[data-access-entry="automatic:0"]').text()).toContain("Automaticky");
  expect(wrapper.findAll("[data-access-entry]")).toHaveLength(4);
  expect(wrapper.find("[data-conflict-note]").exists()).toBe(true);
  // A level change stays inside the gallery.
  const petr = () => wrapper.get('[data-access-entry="user:u-2"]');
  expect(petr().text()).toContain("od ");
  await petr().get("[data-change-access]").trigger("click");
  await flushPromises();
  await petr().get('[data-access-level-picker] [data-value="write"]').trigger("click");
  await petr().get("[data-save-access]").trigger("click");
  await flushPromises();
  expect(wrapper.get("[data-result-banner]").text()).toContain("Petr Svoboda: Upravuje");
  expect(petr().get(".level-badge").attributes("data-level")).toBe("write");
});
