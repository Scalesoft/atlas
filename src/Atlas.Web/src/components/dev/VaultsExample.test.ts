import { afterEach, expect, it } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import ComponentGallery from "./ComponentGallery.vue";

let wrapper: ReturnType<typeof mount>;
afterEach(() => {
  wrapper?.unmount();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

it("shows the vault and security composites with local sample data", async () => {
  history.replaceState(null, "", "/?gallery=1#/gallery/vaults");
  wrapper = mount(ComponentGallery, { attachTo: document.body });
  await flushPromises();
  expect(wrapper.get(".gallery-nav a[aria-current]").text()).toBe("Trezory a zabezpečení");
  for (const label of ["Odemčeno", "Vyžaduje heslo", "Nedostupné", "Chyba souboru"]) expect(wrapper.text()).toContain(label);
  expect(wrapper.find('.progressive-flow[aria-label="Přidat trezor"]').exists()).toBe(true);
  const vault = wrapper.get('[data-resource-access="vault"]');
  expect(vault.get('[data-access-entry="automatic:1"]').text()).toContain("Správci wiki");
  // A level change stays inside the gallery.
  await vault.get('[data-access-entry="user:u-2"] [data-change-access]').trigger("click");
  await vault.get('[data-access-entry="user:u-2"] [role="radio"][data-value="manage"]').trigger("click");
  await vault.get('[data-access-entry="user:u-2"] [data-save-access]').trigger("click");
  await flushPromises();
  expect(wrapper.get("[data-result-banner]").text()).toContain("Petr Svoboda: přístup Spravuje");
});

it("shows the security centre pieces: fresh sign-in that blocks when unsupported, requests and the self-test verdict", async () => {
  history.replaceState(null, "", "/?gallery=1#/gallery/vaults");
  wrapper = mount(ComponentGallery, { attachTo: document.body });
  await flushPromises();
  expect(wrapper.find('[aria-label="Ukázka vyhodnocení připojení"]').text()).toContain("Kancelář Praha");
  expect(wrapper.get('[data-request="r-1"]').text()).toContain("Výměna firmware");
  await wrapper.get('[data-request="r-1"] [data-request-approve]').trigger("click");
  await flushPromises();
  expect(wrapper.get('[data-request="r-1"] [data-value="30"]').attributes("data-selected")).toBe("true");
  await wrapper.get('[role="radio"][value="unsupported"]').trigger("click");
  await wrapper.get("[data-gallery-reauth]").trigger("click");
  await flushPromises();
  expect(document.querySelector("[data-reauth-dialog]")?.textContent).toContain("neposílá čas přihlášení");
  expect((document.querySelector("[data-reauth-dialog] [data-confirm-reauth]") as HTMLButtonElement).disabled).toBe(true);
  await wrapper.get("[data-fresh-auth-run]").trigger("click");
  await new Promise((resolve) => setTimeout(resolve, 800));
  await flushPromises();
  expect(wrapper.get("[data-fresh-auth-verdict]").text()).toContain("auth_time");
});
