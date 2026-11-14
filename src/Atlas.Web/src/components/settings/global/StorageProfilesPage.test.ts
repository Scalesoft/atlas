import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import StorageProfilesPage from "./StorageProfilesPage.vue";
import { clearCsrf } from "../../../api";
import { fakeApi, mountPage, problem } from "../general/testing";

let wrapper: VueWrapper | undefined;
const profile = { id: "store", name: "Dokumentace", destination: "smb://server/docs", username: "atlas", domain: "OFFICE", hasPassword: true, enabled: true, allowPageExports: true, allowBackups: true, allSpaces: true, spaceIds: [], version: 3 };
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); clearCsrf(); document.body.innerHTML = ""; });

it("keeps credentials and draft through failed save and never tests access on save", async () => {
  let fail = true;
  const calls = fakeApi((method, path, body) => path.endsWith("/pdf-exports") ? [] : method === "GET" ? [profile] : method === "PUT" ? fail ? problem(409, "Profil mezitím někdo změnil.") : { ...profile, ...body as object, version: 4 } : undefined);
  const mounted = mountPage(StorageProfilesPage); wrapper = mounted.wrapper; await flushPromises();
  await wrapper.get('[data-edit-profile="store"]').trigger("click");
  expect(wrapper.get("[data-profile-password]").element).toHaveProperty("value", "");
  await wrapper.get("[data-profile-name]").setValue("Nový název");
  await wrapper.get("[data-profile-password]").setValue("replacement");
  await wrapper.get("[data-save-bar-save]").trigger("click"); await flushPromises();
  expect(wrapper.text()).toContain("Profil mezitím někdo změnil.");
  expect(wrapper.get("[data-profile-password]").element).toHaveProperty("value", "replacement");
  expect(mounted.guard.dirty.value).toBe(true);
  fail = false;
  await wrapper.get("[data-save-bar-save]").trigger("click"); await flushPromises();
  expect(calls.filter(call => call.method === "PUT").at(-1)?.body).toMatchObject({ name: "Nový název", password: "replacement", expectedVersion: 3, allSpaces: true, spaceIds: [] });
  expect(calls.some(call => call.path.endsWith("/test"))).toBe(false);
  expect(mounted.guard.dirty.value).toBe(false);
});

it("tests only saved profiles and leaves a referenced profile visible when deletion fails", async () => {
  const calls = fakeApi((method, path) => path.endsWith("/pdf-exports") ? [] : method === "GET" ? [profile] : path.endsWith("/test") ? { message: "Přístup ověřen." } : problem(409, "Profil používá export stránky."));
  wrapper = mountPage(StorageProfilesPage).wrapper; await flushPromises();
  await wrapper.get('[data-test-profile="store"]').trigger("click"); await flushPromises();
  expect(wrapper.text()).toContain("Přístup ověřen.");
  await wrapper.get('[data-remove-profile="store"]').trigger("click");
  await wrapper.get("[data-confirm-action]").trigger("click"); await flushPromises();
  expect(wrapper.text()).toContain("Profil používá export stránky.");
  expect(wrapper.find('[data-profile="store"]').exists()).toBe(true);
  expect(calls.find(call => call.method === "DELETE")?.path).toBe("/storage-profiles/store");
});

it("lists every automatic PDF with its path, mode, last update and error, opens its settings and repeats it", async () => {
  const handbook = { pageId: "p1", spaceName: "Identity", pageTitle: "MPI – Příručka správce", profileName: "Dokumentace", path: "\\\\server\\docs\\Modelsoft Identity - MPI\\MPI – Příručka správce.pdf",
    enabled: true, publishedOnly: false, keepVersions: false, status: "error", lastExportedAt: "2026-10-16T08:00:00Z", lastError: "PDF se nepodařilo uložit. Export se automaticky zopakuje." };
  let rows = [handbook];
  const calls = fakeApi((method, path) => path.endsWith("/pdf-exports") ? rows : path.endsWith("/run") ? undefined : [profile]);
  const mounted = mountPage(StorageProfilesPage); wrapper = mounted.wrapper; await flushPromises();
  const row = wrapper.get('[data-pdf-export-row="p1"]');
  expect(row.text()).toContain("\\server\\docs\\Modelsoft Identity - MPI\\MPI – Příručka správce.pdf");
  expect(row.text()).toContain("Každá uložená verze");
  expect(row.text()).toContain("Poslední úspěšná aktualizace");
  expect(row.text()).toContain("PDF se nepodařilo uložit.");
  expect(row.text()).toContain("Export selhal");
  await wrapper.get('[data-open-pdf-export="p1"]').trigger("click");
  expect(mounted.context.openPage).toHaveBeenCalledWith("p1", true);
  rows = [{ ...handbook, status: "pending", lastError: "" }];
  await wrapper.get('[data-retry-pdf-export="p1"]').trigger("click"); await flushPromises();
  expect(calls.find(call => call.method === "POST")?.path).toBe("/storage-profiles/pdf-exports/p1/run");
  expect(wrapper.get('[data-pdf-export-row="p1"]').text()).toContain("Čeká na export");
});
