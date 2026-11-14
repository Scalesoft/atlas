import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../api";
import ModuleSettingsHost from "../../admin/ModuleSettingsHost.vue";
import { fakeApi, problem, type FakeCall } from "./testing";

// The first-run setup mounts the real module forms outside the settings surface: no settings route,
// context or flash, only the host's own dirty guard.
const modules = [
  { id: "oidc", title: "Firemní přihlášení", description: "OIDC", settingsUi: "oidc", order: 10, showInSetup: true },
  { id: "youtrack", title: "YouTrack", description: "Úkoly", settingsUi: "youtrack", order: 20, showInSetup: true },
  { id: "wiki-js-import", title: "Import Wiki.js", description: "ZIP", settingsUi: "wiki-js-import", order: 30, showInSetup: true },
];
let wrapper: VueWrapper | undefined;
let calls: FakeCall[] = [];
beforeEach(() => {
  clearCsrf();
  calls = fakeApi((method, path) => {
    if (path === "/admin/modules") return { items: modules };
    if (path === "/admin/modules/oidc") return { version: null, publicOrigin: "", providers: [], restartRequired: false, builtinEnabled: true };
    if (path === "/admin/integrations/youtrack") return { moduleEnabled: false, enabled: false, baseUrl: "", hasToken: false, version: null, spaces: [], spaceProjects: [], status: "disabled" };
    if (path === "/admin/setup/complete") return undefined;
    return problem(404, `unexpected ${method} ${path}`);
  });
});
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
});
async function settle() {
  await flushPromises();
  await vi.dynamicImportSettled();
  await flushPromises();
}
const next = () => wrapper!.get("[data-setup-next]");

it("runs the setup steps on the real module forms and keeps a draft from being skipped", async () => {
  wrapper = mount(ModuleSettingsHost, { props: { isWikiAdmin: true, setup: true }, attachTo: document.body });
  await settle();
  expect(wrapper.text()).toContain("Firemní přihlášení zatím není připojené");
  expect(wrapper.text()).toContain("Krok 1 z 4");

  expect(wrapper.find("[data-oidc-origin]").exists()).toBe(false);
  expect(wrapper.text()).toContain(location.origin);
  await wrapper.get('[data-origin-settings] button').trigger('click');
  await wrapper.get("[data-oidc-origin]").setValue("https://wiki.example");
  expect(next().attributes("disabled")).toBeDefined();
  expect(wrapper.emitted("dirty")?.at(-1)).toEqual([true]);
  await wrapper.get("[data-save-bar-cancel]").trigger("click");
  expect(next().attributes("disabled")).toBeUndefined();

  await next().trigger("click");
  await settle();
  expect(wrapper.get(".module-step-heading h2").text()).toBe("YouTrack");
  expect(wrapper.text()).toContain("Modul vypnutý");
  await next().trigger("click");
  await settle();
  expect(wrapper.get(".module-step-heading h2").text()).toBe("Import Wiki.js");
  expect(wrapper.find("[data-import-source]").exists()).toBe(true);
  await wrapper.get("[data-import-source]").setValue("wiki");
  expect(next().attributes("disabled")).toBeDefined();
  await wrapper.get("[data-flow-cancel]").trigger("click");
  await next().trigger("click");
  await settle();
  expect(wrapper.get(".module-step-heading h2").text()).toBe("Obnovit ze zálohy");
  expect(wrapper.find("[data-backup-import]").text()).toContain("až po vytvoření a přihlášení prvního správce");
  expect(next().text()).toBe("Otevřít wiki");
  await next().trigger("click");
  await flushPromises();
  expect(calls.some((call) => call.method === "POST" && call.path === "/admin/setup/complete")).toBe(true);
  expect(wrapper.emitted("complete")).toHaveLength(1);
});
