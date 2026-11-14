import { afterEach, expect, it, vi } from "vitest";
import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import VaultLocationWizard from "./VaultLocationWizard.vue";
import type { VaultLocation } from "../../../vaults";

enableAutoUnmount(afterEach);
const location: VaultLocation = { id: "disk", name: "Disk", kind: "local", root: "/vaults", username: null, domain: null, hasPassword: false, vaultCount: 0 };

it("reveals fields on selection and a live summary without Next, saving only explicitly", async () => {
  const submit = vi.fn().mockResolvedValue(location);
  const wrapper = mount(VaultLocationWizard, { props: { submit }, attachTo: document.body });
  expect(wrapper.find("[data-location-name]").exists()).toBe(false);
  expect(wrapper.get('[aria-label="Přehled vyplnění"]').text()).toContain("Kontrola a uložení");
  expect(wrapper.get('[data-location-chip="kind"]').attributes("aria-current")).toBe("step");
  await wrapper.get('[role="radio"][value="smb"]').trigger("click");
  await flushPromises();
  await vi.waitFor(() => expect(wrapper.get("[data-location-name]").isVisible()).toBe(true));
  expect(wrapper.get('[role="radio"][value="smb"]').attributes("aria-checked")).toBe("true");
  expect(wrapper.get('[data-location-chip="kind"]').text()).toContain("SMB");
  expect(wrapper.get('[data-location-chip="folder"]').attributes("aria-current")).toBe("step");
  await wrapper.get("[data-location-name]").setValue("Sdílení");
  await wrapper.get("[data-location-root]").setValue("smb://fs01/trezory");
  await flushPromises();
  expect(wrapper.get('[aria-label="Souhrn umístění"]').text()).toContain("smb://fs01/trezory");
  expect(wrapper.get('[data-location-chip="summary"]').text()).toContain("Připraveno k uložení");
  await wrapper.get('[data-location-chip="kind"]').trigger("click");
  expect(document.activeElement).toBe(wrapper.get('[data-location-section="kind"]').element);
  expect(wrapper.get("[data-location-name]").isVisible()).toBe(true);
  expect(wrapper.get('[data-location-chip="summary"]').text()).toContain("Připraveno k uložení");
  expect(wrapper.find("[data-flow-next]").exists()).toBe(false);
  expect(submit).not.toHaveBeenCalled();
  await wrapper.get('[type="submit"]').trigger("click");
  await flushPromises();
  expect(submit).toHaveBeenCalledTimes(1);
  expect(submit).toHaveBeenCalledWith(expect.objectContaining({ name: "Sdílení", kind: "smb", root: "smb://fs01/trezory" }));
});

it("keeps the name but clears incompatible hidden SMB values when switching to local", async () => {
  const submit = vi.fn().mockResolvedValue(location);
  const wrapper = mount(VaultLocationWizard, { props: { submit, location: { ...location, kind: "smb", root: "smb://fs01/trezory", username: "svc", domain: "ACME", hasPassword: true } } });
  await wrapper.get('[type="password"]').setValue("example-secret");
  await wrapper.get('[role="radio"][value="local"]').trigger("click");
  await flushPromises();
  expect((wrapper.get("[data-location-name]").element as HTMLInputElement).value).toBe("Disk");
  expect(wrapper.find("[data-location-username]").exists()).toBe(false);
  expect(wrapper.get('[type="submit"]').attributes("disabled")).toBeDefined();
  expect(wrapper.get('[data-location-chip="folder"]').text()).toContain("Doplnit");
  expect(wrapper.get('[aria-label="Souhrn umístění"]').text()).not.toContain("smb://fs01/trezory");
  expect(wrapper.get('[aria-label="Souhrn umístění"]').text()).not.toContain("ACME");
  expect(wrapper.get('[data-location-section="summary"]').text()).toContain("Doplňte cestu ke složce");
  expect(wrapper.get('[data-location-chip="summary"]').text()).not.toContain("Připraveno k uložení");
  await wrapper.get("[data-location-root]").setValue("/vaults");
  await wrapper.get("form").trigger("submit");
  await flushPromises();
  expect(submit).toHaveBeenCalledWith({ name: "Disk", kind: "local", root: "/vaults", username: "", domain: "", password: "" });
});

it("opens existing values immediately and keeps them editable after a failed save", async () => {
  const submit = vi.fn().mockRejectedValue(new Error("Složka není dostupná."));
  const wrapper = mount(VaultLocationWizard, { props: { submit, location } });
  expect(wrapper.get('[aria-label="Souhrn umístění"]').text()).toContain("/vaults");
  await wrapper.get("form").trigger("submit");
  await flushPromises();
  expect(wrapper.get('[role="alert"]').text()).toContain("Složka není dostupná.");
  await wrapper.get("[data-location-root]").setValue("/new-vaults");
  expect(wrapper.get('[aria-label="Souhrn umístění"]').text()).toContain("/new-vaults");
  expect(wrapper.find('[role="alert"]').exists()).toBe(false);
});

it("offers three kinds and a managed location needs only a name, with no folder to enter", async () => {
  const submit = vi.fn().mockResolvedValue({ ...location, kind: "managed" });
  const wrapper = mount(VaultLocationWizard, { props: { submit, test: vi.fn().mockResolvedValue("Přístup ověřen") } });
  expect(wrapper.findAll('[role="radio"]').map((radio) => radio.attributes("value"))).toEqual(["managed", "local", "smb"]);
  await wrapper.get('[role="radio"][value="managed"]').trigger("click");
  await flushPromises();
  expect(wrapper.find("[data-location-root]").exists()).toBe(false);
  expect(wrapper.get("[data-location-test]").attributes("disabled")).toBeUndefined();
  await wrapper.get("[data-location-name]").setValue("Na serveru wiki");
  await flushPromises();
  expect(wrapper.get('[data-location-chip="summary"]').text()).toContain("Připraveno k uložení");
  await wrapper.get("form").trigger("submit");
  await flushPromises();
  expect(submit).toHaveBeenCalledWith({ name: "Na serveru wiki", kind: "managed", root: "", username: "", domain: "", password: "" });
});
