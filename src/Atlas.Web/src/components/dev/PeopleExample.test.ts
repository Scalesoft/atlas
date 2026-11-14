import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PeopleExample from "./PeopleExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  document.body.innerHTML = "";
});

it("shows the people composites with local sample data", async () => {
  wrapper = mount(PeopleExample, { attachTo: document.body });
  for (const label of ["Aktivní", "Čeká na aktivaci", "Deaktivovaný"]) expect(wrapper.text()).toContain(label);
  expect(wrapper.find('.progressive-flow[aria-label="Pozvat člověka"]').exists()).toBe(true);
  expect(wrapper.find('.progressive-flow[aria-label="Nová skupina"]').exists()).toBe(true);
  // A taken username is refused as it is typed, locally, without a Next step.
  await wrapper.get("[data-invite-username]").setValue("jana");
  await wrapper.get("[data-invite-display-name]").setValue("Jana");
  await flushPromises();
  expect(wrapper.text()).toContain("Účet s tímto uživatelským jménem už existuje.");
});
