import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../api";
import AuthenticationPage from "./AuthenticationPage.vue";
import { fakeApi, mountPage, problem, type FakeCall } from "./testing";

const modules = [
  { id: "oidc", title: "Firemní přihlášení", description: "Přihlášení OpenID Connect", settingsUi: "oidc", order: 10, showInSetup: true },
  { id: "hub", title: "Skupiny Hubu", description: "Členství", settingsUi: "hub", order: 30, showInSetup: false },
];
const oidc = {
  version: "v1", publicOrigin: "https://wiki.example", restartRequired: false, builtinEnabled: true, providers: [],
};
const hub = {
  connectionId: "connection", version: "v1", issuer: "https://hub.example/hub", baseUrl: "https://hub.example/hub",
  hasToken: true, mode: "Preview", providers: [], bindings: [], identities: [], pollSeconds: 60, maxValiditySeconds: 300,
};
let wrapper: VueWrapper | undefined;
let calls: FakeCall[];
async function open(part: string, available = modules) {
  calls = fakeApi((method, path) => {
    if (path === "/admin/modules") return { items: available };
    if (path === "/admin/modules/oidc" && method === "GET") return oidc;
    if (path === "/admin/modules/hub" && method === "GET") return hub;
    return problem(404, `Unexpected ${method} ${path}`);
  });
  const mounted = mountPage(AuthenticationPage, { part }, { hash: `#/settings/general/authentication/${part}` });
  wrapper = mounted.wrapper;
  await flushPromises();
  await vi.dynamicImportSettled();
  await flushPromises();
  return mounted;
}
beforeEach(() => clearCsrf());
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); document.body.innerHTML = ""; history.replaceState(null, "", "/"); });

describe("authentication settings", () => {
  it("groups login and Hub membership as links, mounting only the selected form and keeping its dirty guard", async () => {
    const { wrapper: view, guard } = await open("login");
    expect(view.get('[data-tab="login"]').attributes("href")).toBe("#/settings/general/authentication/login");
    expect(view.get('[data-tab="hub"]').attributes("href")).toBe("#/settings/general/authentication/hub");
    expect(view.get('[data-auth-panel="login"]').text()).toContain("Firemní přihlášení");
    expect(view.find('[data-auth-panel="hub"]').exists()).toBe(false);
    expect(calls.filter(call => call.path === "/admin/modules/hub")).toHaveLength(0);
    await view.get('[data-origin-settings] button').trigger("click");
    await view.get("[data-oidc-origin]").setValue("https://new-wiki.example");
    expect(guard.dirty.value).toBe(true);
    expect(guard.canSave.value).toBe(true);
  });

  it("shows only authentication sections supplied by the module catalog", async () => {
    const { wrapper: view } = await open("login", modules.slice(0, 1));
    expect(view.find('[data-tab="hub"]').exists()).toBe(false);
    expect(view.find('[data-auth-panel="login"]').exists()).toBe(true);
  });

  it("opens Hub membership directly without mounting the OIDC login form", async () => {
    const { wrapper: view } = await open("hub");
    expect(view.get('[data-auth-panel="hub"]').text()).toContain("Spojení s Hubem");
    expect(view.find('[data-auth-panel="login"]').exists()).toBe(false);
    expect(calls.filter(call => call.path === "/admin/modules/oidc")).toHaveLength(0);
  });
});