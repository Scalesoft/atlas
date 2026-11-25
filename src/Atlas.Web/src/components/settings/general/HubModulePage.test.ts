import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { clearCsrf } from "../../../api";
import HubModulePage from "./HubModulePage.vue";
import GroupDetail from "../people/GroupDetail.vue";
import { provideReauthEnvironment } from "../useReauth";
import { fakeApi, mountPage, problem, type FakeCall, type FakeHandler } from "./testing";
import type { SettingsFlash } from "../flash";

const endpoint = "/admin/modules/hub";
const settings = () => ({
  connectionId: "connection", version: "v1", issuer: "https://hub.example/hub", baseUrl: "https://hub.example/hub", hasToken: true, mode: "Preview",
  providers: [{ issuer: "https://hub.example/hub", displayName: "Firemní Hub" }],
  bindings: [{ groupId: "group", name: "Vývoj", externalGroupId: "opaque/original", enabled: true, grantsWikiAdmin: false, version: "b1", hubUrl: "https://hub.example/hub/groups/opaque" }],
  identities: [], pollSeconds: 60, maxValiditySeconds: 300,
});
let wrapper: VueWrapper;
let flashOf: () => SettingsFlash;
let calls: FakeCall[];
async function open(handler?: FakeHandler) {
  calls = fakeApi((method, path, body) => handler?.(method, path, body) ?? (path === endpoint && method === "GET" ? settings() : problem(404, "Neočekávaný požadavek")));
  const Harness = defineComponent({ setup() {
    provideReauthEnvironment({ method: "local", supported: true, verifyPassword: async () => ({ ok: true, proofId: "proof-1" }), verifyHub: async () => false });
    return () => h(HubModulePage);
  } });
  const mounted = mountPage(Harness);
  wrapper = mounted.wrapper;
  flashOf = mounted.flash;
  await flushPromises();
}
async function confirm() {
  const input = document.querySelector<HTMLInputElement>('dialog[open] input[type="password"]')!;
  input.value = "synthetic-password";
  input.dispatchEvent(new Event("input"));
  await flushPromises();
  (document.querySelector('dialog[open] [data-confirm-reauth]') as HTMLButtonElement).click();
  await flushPromises();
}
beforeEach(() => clearCsrf());
afterEach(() => { wrapper?.unmount(); vi.unstubAllGlobals(); document.body.innerHTML = ""; history.replaceState(null, "", "/"); });

it("shows stale connection health and counts without presenting it as healthy", async () => {
  let verified = false;
  await open((method, path) => {
    if (path === `${endpoint}/verify`) { verified = true; return { verified: 12, failed: 0, deferred: 0 }; }
    return method === "GET" && path === endpoint ? { ...settings(), health: {
      connectionId: "connection", state: verified ? "Healthy" : "Stale", known: 12, pending: verified ? 0 : 2, expired: verified ? 0 : 3, errors: verified ? 0 : 1, lastSuccessAt: "2026-10-01T12:00:00Z",
    } } : undefined;
  });
  const health = wrapper.get('[data-hub-health]');
  expect(health.text()).toContain("Ověření vypršelo");
  expect(health.text()).toContain("Známé účty: 12 · Čeká na ověření: 2 · Vypršelé: 3 · Chyby: 1");
  expect(health.get('.status-badge').attributes('data-tone')).toBe('warning');
  await wrapper.get('[data-hub-verify]').trigger('click');
  await flushPromises();
  expect(health.text()).toContain('Ověřování v pořádku');
  expect(health.text()).toContain('Vypršelé: 0 · Chyby: 0');
  expect(health.get('.status-badge').attributes('data-tone')).toBe('neutral');
});

it("keeps a saved token empty and applies Enforced only after explicit save and fresh authentication", async () => {
  await open((method, path) => method === "PUT" && path === endpoint ? { ...settings(), mode: "Enforced", version: "v2" } : undefined);
  expect((wrapper.get('[data-hub-token]').element as HTMLInputElement).value).toBe("");
  await wrapper.get('[data-hub-mode] [data-value="Enforced"]').trigger("click");
  expect(calls.filter(call => call.method === "PUT")).toHaveLength(0);
  await wrapper.get('[data-save-bar-save]').trigger("click");
  await flushPromises();
  expect(calls.filter(call => call.method === "PUT")).toHaveLength(0);
  await confirm();
  expect(calls.find(call => call.method === "PUT")?.body).toEqual({ issuer: "https://hub.example/hub", baseUrl: "https://hub.example/hub", mode: "Enforced", expectedVersion: "v1" });
  const put = vi.mocked(fetch).mock.calls.find(([, init]) => init?.method === "PUT")!;
  expect(new Headers(put[1]?.headers).get("X-Atlas-Reauth")).toBe("proof-1");
});

it("retains a conflicted draft until explicit reload without resubmitting the stale version", async () => {
  await open((method, path) => method === "PUT" && path === endpoint ? problem(409, "Změněno jiným správcem") : undefined);
  await wrapper.get('[data-hub-url]').setValue("https://new-hub.example/hub");
  await wrapper.get('[data-hub-token]').setValue("synthetic-replacement");
  await wrapper.get('[data-save-bar-save]').trigger("click");
  await flushPromises();
  expect((wrapper.get('[data-hub-url]').element as HTMLInputElement).value).toBe("https://new-hub.example/hub");
  expect(wrapper.get('[data-hub-conflict]').text()).toContain("Načíst znovu");
  await wrapper.get('[data-save-bar-save]').trigger("click");
  await flushPromises();
  expect(calls.filter(call => call.method === "PUT")).toHaveLength(1);
  await wrapper.get('[data-hub-conflict] button').trigger("click");
  await flushPromises();
  expect((wrapper.get('[data-hub-url]').element as HTMLInputElement).value).toBe("https://hub.example/hub");
});

it("attaches the selected opaque group ID and saves global mapping through fresh authentication", async () => {
  await open((method, path) => {
    if (path === `${endpoint}/groups/search`) return { groups: [{ id: "opaque/a", name: "Vývoj" }, { id: "opaque/b", name: "Vývoj" }], hasMore: true };
    if (path === `${endpoint}/groups` && method === "POST") return settings();
    if (path === `${endpoint}/groups/group` && method === "PUT") return { ...settings(), bindings: [{ ...settings().bindings[0], grantsWikiAdmin: true, version: "b2" }] };
    return undefined;
  });
  await wrapper.get('[data-hub-search]').trigger("click");
  await flushPromises();
  await wrapper.get('[data-hub-groups] [data-value="opaque/b"]').trigger("click");
  await wrapper.get('[data-hub-attach]').trigger("click");
  await flushPromises();
  expect(calls.find(call => call.path === `${endpoint}/groups`)?.body).toEqual({ externalGroupId: "opaque/b", expectedVersion: "v1" });
  await wrapper.get('[data-hub-binding="group"] [data-hub-admin]').trigger("click");
  await wrapper.get('[data-hub-binding="group"] [data-save-bar-save]').trigger("click");
  await flushPromises();
  await confirm();
  expect(calls.find(call => call.path === `${endpoint}/groups/group`)?.body).toEqual({ enabled: true, grantsWikiAdmin: true, expectedVersion: "b1" });
});

it("tests the unsaved connection without persisting it and refreshes diagnostics after verification", async () => {
  let verified = false;
  await open((method, path) => {
    if (path === `${endpoint}/test`) return { ok: true, message: "Čtecí oprávnění ověřena." };
    if (path === `${endpoint}/verify`) { verified = true; return { verified: 1, failed: 0, deferred: 0 }; }
    if (path === endpoint && method === "GET") return { ...settings(), identities: [{ userId: "person", externalIdentityId: "identity", displayName: "Jana", state: verified ? "Active" : "Unverified", verifiedUntil: verified ? "2099-01-01T12:00:00Z" : null, lastSuccessAt: null, lastAttemptAt: null, lastErrorCode: null }] };
    return undefined;
  });
  await wrapper.get('[data-hub-token]').setValue("synthetic-draft-token");
  await wrapper.get('[data-hub-test]').trigger("click");
  await flushPromises();
  expect(calls.find(call => call.path === `${endpoint}/test`)?.body).toMatchObject({ token: "synthetic-draft-token", expectedVersion: "v1" });
  expect(calls.some(call => call.method === "PUT")).toBe(false);
  expect(wrapper.text()).toContain("Čtecí oprávnění ověřena.");
  await wrapper.get('[data-save-bar-cancel]').trigger("click");
  await wrapper.get('[data-hub-verify]').trigger("click");
  await flushPromises();
  expect(calls.find(call => call.path === `${endpoint}/verify`)?.body).toEqual({});
  expect(wrapper.text()).toContain("Náhled · Ověřeno");
  expect(flashOf().message.value?.text).toContain("Ověřeno: 1, neúspěšně: 0, odloženo: 0.");
});

it("shows Hub management and members while removing local rename and membership controls", async () => {
  fakeApi(() => [{ id: "person", displayName: "Jana", username: "jana", isActive: true, version: "m1" }]);
  wrapper = mountPage(GroupDetail, { group: { id: "group", name: "Vývoj", version: "g1", source: "hub", hubUrl: "https://hub.example/hub/groups/opaque" }, reload: vi.fn() }).wrapper;
  await flushPromises();
  expect(wrapper.text()).toContain("Spravováno v Hubu");
  expect(wrapper.get('[data-hub-manage]').attributes("href")).toBe("https://hub.example/hub/groups/opaque");
  expect(wrapper.text()).toContain("Jana");
  expect(wrapper.find('[data-group-rename]').exists()).toBe(false);
  expect(wrapper.find('[data-add-member]').exists()).toBe(false);
  expect(wrapper.find('[data-remove-member]').exists()).toBe(false);
});
