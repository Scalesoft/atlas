import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import ModulesExample from "./ModulesExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  document.body.innerHTML = "";
});

it("shows the module and backup composites with local sample data", async () => {
  wrapper = mount(ModulesExample, { attachTo: document.body });
  await flushPromises();
  expect(wrapper.findAll('.progressive-flow[aria-label="Přidat poskytovatele"]')).toHaveLength(1);
  expect(wrapper.find('.progressive-flow[aria-label="Upravit poskytovatele"]').exists()).toBe(true);
  for (const label of ["Probíhá", "Dokončeno", "Beze změny", "Selhalo", "Stáhnout ZIP", "Jednorázový import"]) expect(wrapper.text()).toContain(label);
  expect(wrapper.find("[data-copy-value]").text()).toBe("https://wiki.example.cz/signin-oidc/hub");

  // The new-provider wizard stays inside the gallery: its result is a banner, nothing is saved.
  const wizard = wrapper.findAll(".progressive-flow")[0]!;
  await wizard.get("[data-provider-name]").setValue("Firemní účet");
  expect((wizard.get("[data-provider-id]").element as HTMLInputElement).value).toBe("firemni-ucet");
  await wizard.get("[data-provider-issuer]").setValue("https://login.example.cz");
  await wizard.get("[data-provider-client]").setValue("atlas");
  expect(wizard.get('[aria-label="Souhrn poskytovatele"]').text()).toContain("https://wiki.example.cz/signin-oidc/firemni-ucet");
  await wizard.get("[data-flow-next]").trigger("click");
  await flushPromises();
  expect(wrapper.get("[data-result-banner]").text()).toContain("Firemní účet");
});
