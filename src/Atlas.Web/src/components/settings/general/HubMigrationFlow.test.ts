import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { clearCsrf } from "../../../api";
import { provideReauthEnvironment } from "../useReauth";
import { fakeApi, mountPage, problem } from "./testing";
import HubMigrationFlow from "./HubMigrationFlow.vue";
import type { HubMigrationInventory } from "../../../hub";

const endpoint = "/admin/modules/hub/migration";
const inventory: HubMigrationInventory = {
  users: [{ id: "person", displayName: "Jana", isActive: true, isWikiAdmin: false, oidcAdminIssuer: null, hasPassword: false }],
  groups: [{ id: "local", name: "Vývoj" }, { id: "managed", name: "Řízená skupina" }],
  members: [], effectiveMembers: [], grants: [], spaces: [{ id: "space", name: "Portál" }], pages: [], identities: [],
  bindings: [{ groupId: "managed", externalGroupId: "old-id", enabled: false, grantsWikiAdmin: false }], effectiveWikiAdmins: [],
};
const preview = () => ({ ticket: "reviewed-ticket", expiresAt: "2099-01-01T12:00:00Z", configurationVersion: "v1", inventory,
  groupDiffs: [{ localGroupId: "local", externalGroupId: "opaque/b", convertsLocalGroup: true, beforeUserIds: [], afterUserIds: ["person"] }],
  removedGrants: [], removedWikiAdminUserIds: [], pageBoundaries: [], copiedGrantsRemoved: [], beforeRoutes: [], observedIdentities: [],
  afterRoutes: [{ userId: "person", resourceKind: "space", resourceId: "space", role: 1, source: "personal", grantId: "personal-grant", groupId: null }], warnings: ["Nezávislé přístupy zůstávají zachované."],
});
let wrapper: VueWrapper;
beforeEach(clearCsrf);
afterEach(() => { wrapper?.unmount(); vi.unstubAllGlobals(); document.body.innerHTML = ""; });
async function open(conflict = false, data = inventory) {
  const calls = fakeApi((method, path) => {
    if (path === `${endpoint}/inventory`) return data;
    if (path === "/admin/modules/hub/groups/search") return { groups: [{ id: "opaque/a", name: "Vývoj" }, { id: "opaque/b", name: "Vývoj" }], hasMore: false };
    if (path === `${endpoint}/preview`) return { ...preview(), inventory: data };
    if (path === `${endpoint}/apply`) return conflict ? problem(409, "Náhled již neodpovídá přístupům.") : { convertedGroups: 1, removedGrants: 0, removedWikiAdmins: 0, isolatedPages: 0 };
    throw new Error(`Unexpected ${method} ${path}`);
  });
  wrapper = mountPage(defineComponent({ setup() {
    provideReauthEnvironment({ method: "local", supported: true, verifyPassword: async () => ({ ok: true, proofId: "proof" }), verifyHub: async () => false });
    return () => h(HubMigrationFlow, { version: "v1" });
  } })).wrapper;
  await flushPromises();
  return calls;
}
async function selectAndPreview() {
  await wrapper.get('[data-migration-local] [data-value="local"]').trigger("click");
  await wrapper.get('[data-migration-search]').trigger("click");
  await flushPromises();
  await wrapper.get('[data-migration-hub] [data-value="opaque/b"]').trigger("click");
  await wrapper.get('[data-migration-add-group]').trigger("click");
  await wrapper.get('[data-flow-next]').trigger("click");
  await flushPromises();
}
async function apply() {
  await wrapper.get('[data-flow-next]').trigger("click");
  await flushPromises();
  await wrapper.get('input[type="password"]').setValue("synthetic");
  await wrapper.get('[data-confirm-reauth]').trigger("click");
  await flushPromises();
}
it("reviews explicit IDs and membership changes before applying only the reviewed ticket with fresh authentication", async () => {
  const calls = await open();
  expect(wrapper.find('[data-migration-local] [data-value="managed"]').exists()).toBe(false);
  await selectAndPreview();
  expect(calls.find(call => call.path === `${endpoint}/preview`)?.body).toEqual({ expectedVersion: "v1", groups: [{ localGroupId: "local", externalGroupId: "opaque/b" }], removeSpaceGrantIds: [], removePageGrantIds: [], removeWikiAdminUserIds: [], pageBoundaries: [] });
  expect(wrapper.text()).toContain("Jana");
  expect(wrapper.text()).toContain("Nezávislé přístupy zůstávají zachované.");
  await wrapper.findAll("button").find(button => button.text().includes("Všechny cesty po změně"))!.trigger("click");
  expect(wrapper.text()).toContain("Osobní grant");
  expect(wrapper.text()).toContain("personal-grant");
  expect(calls.some(call => call.path.endsWith("/apply"))).toBe(false);
  await apply();
  expect(calls.find(call => call.path.endsWith("/apply"))?.body).toEqual({ ticket: "reviewed-ticket" });
  const request = vi.mocked(fetch).mock.calls.find(([path]) => String(path).endsWith("/apply"))!;
  expect(new Headers(request[1]?.headers).get("X-Atlas-Reauth")).toBe("proof");
});
it("preserves the selected groups after a conflict and cannot apply the stale ticket again", async () => {
  const calls = await open(true);
  await selectAndPreview();
  await apply();
  expect(wrapper.text()).toContain("Náhled již neodpovídá přístupům.");
  expect(wrapper.text()).toContain("opaque/b");
  expect(wrapper.find('[data-confirm-reauth]').exists()).toBe(false);
  expect(calls.filter(call => call.path.endsWith("/apply"))).toHaveLength(1);
});
it("reviews only selected grant IDs and the exact page boundary without inferring other pages", async () => {
  const calls = await open(false, { ...inventory,
    pages: [
      { id: "page-parent", spaceId: "space", title: "Projekt", ownAccess: false, isTrashed: false, isTemplate: false },
      { id: "page-child", spaceId: "space", title: "Podstránka", ownAccess: false, isTrashed: false, isTemplate: false },
    ],
    grants: [{ id: "selected-grant", spaceId: "space", pageId: null, userId: "person", groupId: null, role: 2, startAt: null, expiresAt: null, activeFrom: "2020-01-01T00:00:00Z", activeTo: null }],
  });
  await wrapper.findAll("button").find(button => button.text().includes("Odebrání zkontrolovaných přístupů"))!.trigger("click");
  await wrapper.get('input[value="selected-grant"]').setValue(true);
  await wrapper.findAll("button").find(button => button.text().includes("Vlastní přístupy konkrétních stránek"))!.trigger("click");
  await wrapper.get('[data-migration-page] [data-value="page-parent"]').trigger("click");
  await wrapper.get('[data-migration-page-group] [data-value="local"]').trigger("click");
  await wrapper.findAll("button").find(button => button.text() === "Přidat skupinu do přístupů stránky")!.trigger("click");
  await wrapper.findAll("button").find(button => button.text() === "Přidat hranici stránky")!.trigger("click");
  await wrapper.get('[data-flow-next]').trigger("click");
  await flushPromises();
  expect(calls.find(call => call.path.endsWith("/preview"))?.body).toEqual({ expectedVersion: "v1", groups: [], removeSpaceGrantIds: ["selected-grant"], removePageGrantIds: [], removeWikiAdminUserIds: [], pageBoundaries: [{ pageId: "page-parent", grants: [{ groupId: "local", role: "Reader" }] }] });
});
