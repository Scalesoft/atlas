import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { clearCsrf } from "../../../api";
import ModulesPage from "./ModulesPage.vue";
import { provideReauthEnvironment } from "../useReauth";
import { providerSlug } from "./oidc";
import { fakeApi, mountPage, problem, type FakeCall, type FakeHandler } from "./testing";

const modules = [
  { id: "wiki-js-import", title: "Import Wiki.js", description: "Přeneste obsah ze ZIP exportu.", settingsUi: "wiki-js-import", order: 30, showInSetup: true },
  { id: "oidc", title: "Firemní přihlášení", description: "Připojte poskytovatele OpenID Connect.", settingsUi: "oidc", order: 10, showInSetup: true },
  { id: "youtrack", title: "YouTrack", description: "Propojte články s úkoly.", settingsUi: "youtrack", order: 20, showInSetup: true },
  { id: "editor-assist", title: "Návrhy při psaní", description: "Pravidlové nápovědy v editoru.", settingsUi: "editor-assist", order: 25, showInSetup: false },
];
const oidc = () => ({
  version: "v1",
  publicOrigin: "https://wiki.example",
  restartRequired: false,
  builtinEnabled: true,
  providers: [{ id: "hub", displayName: "Hub", issuer: "https://hub.example", clientId: "client", hasSecret: true, scopes: ["openid", "profile"], allowSignup: false, callbackUrl: "https://wiki.example/signin-oidc/hub", adminMappings: [{ claimType: "groups", claimValue: "admins" }] }],
});
const youtrack = () => ({
  moduleEnabled: true,
  enabled: true,
  baseUrl: "https://tracker.example",
  hasToken: true,
  version: "observed",
  spaces: [{ id: "space", name: "Engineering" }],
  spaceProjects: [{ spaceId: "space", projectIds: ["0-7"] }],
  status: "configured",
});

let wrapper: VueWrapper | undefined;
let calls: FakeCall[] = [];
function api(extra?: FakeHandler) {
  calls = fakeApi((method, path, body) => {
    const custom = extra?.(method, path, body);
    if (custom !== undefined) return custom;
    if (path === "/admin/modules") return { items: modules };
    if (path === "/admin/modules/oidc" && method === "GET") return oidc();
    if (path === "/admin/integrations/youtrack" && method === "GET") return youtrack();
    if (path === "/admin/modules/editor-assist" && method === "GET") return { enabled: true, version: null };
    return problem(404, `unexpected ${method} ${path}`);
  });
}
async function open(sub?: string) {
  const Harness = defineComponent({ setup() {
    provideReauthEnvironment({ method: "local", supported: true, verifyPassword: async () => ({ ok: true, proofId: "proof-1" }), verifyHub: async () => false });
    return () => h(ModulesPage, { sub });
  } });
  const mounted = mountPage(Harness, {}, { hash: `#/settings/general/modules${sub ? `/${sub}` : ""}` });
  wrapper = mounted.wrapper;
  await flushPromises();
  await vi.dynamicImportSettled();
  await flushPromises();
  if (sub === "oidc" && wrapper.find('[data-origin-settings] button').exists()) {
    await wrapper.get('[data-origin-settings] button').trigger('click');
  }
  return mounted;
}
const button = (view: VueWrapper, text: string) => view.findAll("button").find((item) => item.text().includes(text))!;
/** The fresh sign-in a security change asks for (SEC-A-1), in its dialog. */
async function reauthenticate() {
  const input = document.querySelector<HTMLInputElement>('dialog[open] input[type="password"]')!;
  input.value = "synthetic-password";
  input.dispatchEvent(new Event("input"));
  await flushPromises();
  document.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-reauth]")!.click();
  await flushPromises();
}

beforeEach(() => clearCsrf());
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

describe("module overview", () => {
  it("shows integration modules in server order and keeps authentication outside the landing", async () => {
    api((_, path) => path === "/admin/modules" ? { items: [...modules, { id: "hub", title: "Skupiny Hubu", description: "Členství", settingsUi: "hub", order: 15, showInSetup: false }, { id: "ai", title: "AI", description: "AI", settingsUi: "ai", order: 40, showInSetup: false }] } : undefined);
    const { wrapper: view } = await open();
    const cards = view.findAll("[data-module]");
    expect(cards.map((card) => card.attributes("data-module"))).toEqual(["youtrack", "editor-assist", "wiki-js-import"]);
    expect(cards[0]!.attributes("href")).toBe("#/settings/general/modules/youtrack");
    expect(view.get('[data-module-state="youtrack"]').text()).toBe("Nakonfigurováno");
    expect(view.get('[data-module-state="editor-assist"]').text()).toBe("Zapnuto");
    expect(view.get('[data-module-state="wiki-js-import"]').text()).toBe("Jednorázový import");
  });

  it("marks a module whose form this client does not have, and explains an unknown link", async () => {
    api((_, path) => (path === "/admin/modules" ? { items: [...modules, { id: "future", title: "Budoucí", description: "", settingsUi: "future-ui", order: 99, showInSetup: false }] } : undefined));
    const { wrapper: view } = await open();
    expect(view.get('[data-module="future"]').text()).toContain("Nedostupné v této verzi");
    wrapper!.unmount();
    const unknown = await open("nothing");
    expect(unknown.wrapper.text()).toContain("Modul nenalezen");
  });

  it("offers a reload when the module list fails", async () => {
    api((_, path) => (path === "/admin/modules" ? problem(500, "Server neodpovídá.") : undefined));
    const { wrapper: view } = await open();
    expect(view.get("[role=alert]").text()).toContain("Server neodpovídá.");
    expect(button(view, "Zkusit znovu").exists()).toBe(true);
  });
});

describe("Firemní přihlášení", () => {
  it("adds a provider through the wizard into the draft and saves it with the SaveBar in one PUT", async () => {
    const saved = { ...oidc(), version: "v2", restartRequired: true };
    api((method, path, body) => {
      if (path === "/admin/modules/oidc" && method === "PUT") {
        const request = body as { providers: { id: string; displayName: string; issuer: string; clientId: string; scopes: string[]; allowSignup: boolean; adminMappings: unknown[] }[] };
        saved.providers = request.providers.map((provider) => ({ ...provider, hasSecret: true, callbackUrl: "", adminMappings: provider.adminMappings as never }));
        return saved;
      }
      return undefined;
    });
    const { wrapper: view, guard } = await open("oidc");
    expect(view.get("[data-auth-legacy] a").attributes("href")).toBe("#/settings/general/authentication/login");
    expect(view.get(".settings-page-back").attributes("href")).toBe("#/settings/general/authentication");
    expect(view.text()).toContain("Firemní přihlášení");
    expect(view.get('[data-provider="hub"]').text()).toContain("Tajný klíč uložen");
    await view.get("[data-add-provider]").trigger("click");

    await view.get("[data-provider-name]").setValue("Firemní účet");
    expect((view.get("[data-provider-id]").element as HTMLInputElement).value).toBe("firemni-ucet");
    await view.get("[data-provider-issuer]").setValue("https://login.example.cz");
    await view.get("[data-provider-client]").setValue("atlas");
    await view.get("[data-provider-secret]").setValue("synthetic-secret");
    expect(view.get("[data-copy-value]").text()).toBe("https://wiki.example/signin-oidc/firemni-ucet");
    await view.get("[data-provider-signup]").trigger("click");
    await view.get("[data-add-mapping]").trigger("click");
    await view.get('input[aria-label="Hodnota skupiny nebo role"]').setValue("atlas-admins");
    expect(view.get('[aria-label="Souhrn poskytovatele"]').text()).toContain("groups = atlas-admins");
    await view.get("[data-provider-issuer]").setValue("invalid");
    expect(view.find('[aria-label="Souhrn poskytovatele"]').exists()).toBe(true);
    expect(view.get("[data-flow-next]").attributes("disabled")).toBeDefined();
    await view.get("[data-provider-issuer]").setValue("https://login.example.cz");
    expect(view.get('[aria-label="Souhrn poskytovatele"]').text()).toContain("groups = atlas-admins");
    expect(guard.dirty.value).toBe(true);
    // Leaving now can only discard: the wizard's draft is not part of the configuration yet.
    expect(guard.canSave.value).toBe(false);
    await view.get("[data-flow-next]").trigger("click");
    await flushPromises();

    expect(view.get('[data-provider="firemni-ucet"]').text()).toContain("Nový · neuložený");
    expect(guard.canSave.value).toBe(true);
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    // Who signs in, and who becomes a wiki administrator, is a security change: nothing goes out before the fresh sign-in.
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
    await reauthenticate();

    const put = calls.find((call) => call.method === "PUT")!;
    expect(put.path).toBe("/admin/modules/oidc");
    expect(put.body).toMatchObject({
      expectedVersion: "v1",
      publicOrigin: "https://wiki.example",
      providers: [
        { id: "hub" },
        { id: "firemni-ucet", displayName: "Firemní účet", issuer: "https://login.example.cz", clientId: "atlas", clientSecret: "synthetic-secret", allowSignup: true, scopes: ["openid", "profile"], adminMappings: [{ claimType: "groups", claimValue: "atlas-admins" }] },
      ],
    });
    expect((put.body as { providers: Record<string, unknown>[] }).providers[0]).not.toHaveProperty("clientSecret");
    expect(guard.dirty.value).toBe(false);
    expect(view.get("[data-oidc-restart]").text()).toContain("čeká na restart");
    expect(view.text()).toContain("Přihlášení ho použije po restartu serveru.");
  });

  it("keeps a conflicted draft, clears the submitted secret and says why the save failed", async () => {
    api((method, path) => (path === "/admin/modules/oidc" && method === "PUT" ? problem(409, "Nastavení změnil jiný správce.") : undefined));
    const { wrapper: view, guard } = await open("oidc");
    await view.get("[data-oidc-origin]").setValue("https://new-wiki.example");
    await view.get("[data-edit-provider]").trigger("click");
    await view.get("[data-provider-secret]").setValue("typed-secret");
    await view.get("[data-flow-next]").trigger("click");
    await flushPromises();
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    await reauthenticate();
    expect(calls.find((call) => call.method === "PUT")!.body).toMatchObject({ publicOrigin: "https://new-wiki.example", providers: [{ id: "hub", clientSecret: "typed-secret" }] });
    expect(view.get(".save-bar").text()).toContain("jiný správce");
    expect((view.get("[data-oidc-origin]").element as HTMLInputElement).value).toBe("https://new-wiki.example");
    expect(guard.dirty.value).toBe(true);
    expect(view.find("[data-module-conflict] [data-conflict-overwrite]").exists()).toBe(false);
    // The secret went out with the failed request and is not kept for a retry.
    await view.get("[data-edit-provider]").trigger("click");
    expect((view.get("[data-provider-secret]").element as HTMLInputElement).value).toBe("");
    // Načíst znovu is the explicit way out: the draft goes, the stored settings come back.
    await view.get("[data-module-conflict] [data-conflict-reload]").trigger("click");
    await flushPromises();
    expect((view.get("[data-oidc-origin]").element as HTMLInputElement).value).not.toBe("https://new-wiki.example");
    expect(guard.dirty.value).toBe(false);
    expect(view.find("[data-module-conflict]").exists()).toBe(false);
  });

  it("tests only the saved provider and shows the result next to it", async () => {
    api((method, path, body) => (path === "/admin/modules/oidc/test" ? { ok: (body as { id: string }).id === "hub", message: "Discovery dokument je dostupný." } : undefined));
    const { wrapper: view } = await open("oidc");
    await view.get("[data-provider-test]").trigger("click");
    await flushPromises();
    expect(calls.filter((call) => call.path === "/admin/modules/oidc/test")).toEqual([{ method: "POST", path: "/admin/modules/oidc/test", body: { id: "hub" } }]);
    expect(view.get("[data-provider-test-result]").text()).toBe("Discovery dokument je dostupný.");
    expect(view.get("[data-provider-test-result]").attributes("data-tone")).toBe("success");
    expect(view.get("[data-provider-test-mapping]").attributes("href")).toBe("/api/auth/oidc/hub/test-mapping");

    await view.get("[data-oidc-origin]").setValue("https://other.example");
    expect(view.get("[data-provider-test]").attributes("disabled")).toBeDefined();
    expect(view.find("[data-provider-test-mapping]").exists()).toBe(false);
    await view.get("[data-save-bar-cancel]").trigger("click");
    expect((view.get("[data-oidc-origin]").element as HTMLInputElement).value).toBe("https://wiki.example");
    expect(view.get("[data-provider-test]").attributes("disabled")).toBeUndefined();
  });

  it("removes a provider only from the draft until saved, after saying who signs in through it, and requires the public origin", async () => {
    api((_, path) => (path === "/admin/modules/oidc" ? { ...oidc(), publicOrigin: "", providers: [{ ...oidc().providers[0]!, linkedAccounts: 3 }] } : undefined));
    const { wrapper: view } = await open("oidc");
    await view.get("[data-oidc-origin]").setValue("");
    await view.get("[data-remove-provider]").trigger("click");
    await flushPromises();
    // UX-N-12: removing a saved provider asks first and names its impact.
    expect(view.find('[data-provider="hub"]').exists()).toBe(true);
    expect(document.querySelector("dialog[open] [data-remove-provider-impact]")!.textContent).toContain("přihlašuje 3 účty");
    document.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-action]")!.click();
    await flushPromises();
    expect(view.text()).toContain("Firemní přihlášení zatím není připojené");
    await view.get("[data-save-bar-cancel]").trigger("click");
    expect(view.find('[data-provider="hub"]').exists()).toBe(true);
    await view.get("[data-oidc-origin]").setValue(" ");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
    expect(view.text()).toContain("Zadejte adresu, na které uživatelé wiki otevírají.");
  });

  it("keeps the restart and local sign-in notes", async () => {
    api((_, path) => (path === "/admin/modules/oidc" ? { ...oidc(), restartRequired: true, builtinEnabled: false } : undefined));
    const { wrapper: view } = await open("oidc");
    expect(view.get("[data-oidc-restart]").text()).toContain("čeká na restart serveru");
    expect(view.get("[data-oidc-builtin]").text()).toContain("vypnuté");
    // A pending restart makes the mapping test meaningless until the new configuration runs.
    expect(view.find("[data-provider-test-mapping]").exists()).toBe(false);
  });

  it("suggests a stable id from the button name", () => {
    expect(providerSlug("Firemní účet")).toBe("firemni-ucet");
    expect(providerSlug("123 Hub")).toBe("p-123-hub");
    expect(providerSlug("")).toBe("");
  });
});

describe("YouTrack", () => {
  it("saves the connection and projects with the CAS version, then clears the token", async () => {
    api((method, path, body) => (path === "/admin/integrations/youtrack" && method === "PUT" ? { ...youtrack(), baseUrl: (body as { baseUrl: string }).baseUrl, version: "next" } : undefined));
    const { wrapper: view, guard } = await open("youtrack");
    expect(view.get('[data-module-header-state="youtrack"]').text()).toBe("Nakonfigurováno");
    await view.get("[data-youtrack-url]").setValue("https://new.example");
    await view.get("[data-youtrack-token]").setValue("fixture-only");
    await view.get('[data-youtrack-space="space"]').setValue("0-7, 0-12 0-7");
    expect(guard.dirty.value).toBe(true);
    expect(view.get("[data-youtrack-test]").attributes("disabled")).toBeDefined();
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.find((call) => call.method === "PUT")!.body).toEqual({
      enabled: true,
      baseUrl: "https://new.example",
      token: "fixture-only",
      clearToken: false,
      expectedVersion: "observed",
      spaceProjects: [{ spaceId: "space", projectIds: ["0-7", "0-12"] }],
    });
    expect((view.get("[data-youtrack-token]").element as HTMLInputElement).value).toBe("");
    expect(guard.dirty.value).toBe(false);
  });

  it("answers a conflict with the note and the message, keeps the address, drops the typed token and keeps the note up while the next save runs", async () => {
    api((method, path) => (path === "/admin/integrations/youtrack" && method === "PUT" ? problem(409, "Nastavení změnil jiný správce.") : undefined));
    // The second save is held back so that the note can be looked at while its request is pending.
    const answer = fetch;
    let hold = false;
    let release = () => {};
    vi.stubGlobal("fetch", vi.fn(async (input: string, init?: RequestInit) => {
      if (hold && init?.method === "PUT") await new Promise<void>((resolve) => (release = resolve));
      return answer(input, init);
    }));
    const { wrapper: view } = await open("youtrack");
    await view.get("[data-youtrack-url]").setValue("https://new.example");
    await view.get("[data-youtrack-token]").setValue("fixture-only");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.find((call) => call.method === "PUT")!.body).toMatchObject({ token: "fixture-only", expectedVersion: "observed" });
    expect(view.find("[data-module-conflict]").exists()).toBe(true);
    expect(view.get(".save-bar").text()).toContain("jiný správce");
    expect((view.get("[data-youtrack-url]").element as HTMLInputElement).value).toBe("https://new.example");
    expect((view.get("[data-youtrack-token]").element as HTMLInputElement).value).toBe("");

    hold = true;
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(view.get("[data-save-bar-save]").attributes("disabled")).toBeDefined();
    expect(view.find("[data-module-conflict]").exists()).toBe(true);
    release();
    await flushPromises();
    expect(calls.filter((call) => call.method === "PUT")).toHaveLength(2);
    expect(view.find("[data-module-conflict]").exists()).toBe(true);
  });

  it("tests only the stored connection and reports an upstream failure beside the button", async () => {
    api((method, path) => (path === "/admin/integrations/youtrack/test" ? problem(502, "YouTrack není dostupný.") : undefined));
    const { wrapper: view } = await open("youtrack");
    await view.get("[data-youtrack-test]").trigger("click");
    await flushPromises();
    expect(calls.filter((call) => call.path.endsWith("/test"))).toHaveLength(1);
    expect(view.get("[data-youtrack-test-error]").text()).toContain("není dostupný");
    expect(view.find("[data-youtrack-test-result]").exists()).toBe(false);
  });

  it("cancel discards token and project edits without a request", async () => {
    api();
    const { wrapper: view } = await open("youtrack");
    await view.get("[data-youtrack-token]").setValue("fixture-only");
    await view.get('[data-youtrack-space="space"]').setValue("0-99");
    await view.get("[data-save-bar-cancel]").trigger("click");
    expect((view.get("[data-youtrack-token]").element as HTMLInputElement).value).toBe("");
    expect((view.get('[data-youtrack-space="space"]').element as HTMLInputElement).value).toBe("0-7");
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
  });
});

describe("Návrhy při psaní", () => {
  it("is on by default and saves the switch with the CAS version", async () => {
    api((method, path, body) => (path === "/admin/modules/editor-assist" && method === "PUT" ? { enabled: (body as { enabled: boolean }).enabled, version: "v1" } : undefined));
    const { wrapper: view, guard } = await open("editor-assist");
    expect(view.get("[data-editor-assist-status]").text()).toBe("Zapnuto");
    expect(view.get("[data-editor-assist-enabled]").attributes("aria-checked")).toBe("true");
    expect(guard.dirty.value).toBe(false);
    await view.get("[data-editor-assist-enabled]").trigger("click");
    expect(guard.dirty.value).toBe(true);
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.find((call) => call.method === "PUT")).toEqual({ method: "PUT", path: "/admin/modules/editor-assist", body: { enabled: false, expectedVersion: null } });
    expect(view.get("[data-editor-assist-status]").text()).toBe("Vypnuto");
    expect(guard.dirty.value).toBe(false);
  });

  it("keeps the draft on a conflict until the stored state is reloaded", async () => {
    api((method, path) => (path === "/admin/modules/editor-assist" && method === "PUT" ? problem(409, "Údaj se mezitím změnil.") : undefined));
    const { wrapper: view, guard } = await open("editor-assist");
    await view.get("[data-editor-assist-enabled]").trigger("click");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(view.find("[data-module-conflict]").exists()).toBe(true);
    expect(guard.dirty.value).toBe(true);
    await view.get("[data-module-conflict] [data-conflict-reload]").trigger("click");
    await flushPromises();
    expect(guard.dirty.value).toBe(false);
    expect(view.get("[data-editor-assist-enabled]").attributes("aria-checked")).toBe("true");
  });
});
