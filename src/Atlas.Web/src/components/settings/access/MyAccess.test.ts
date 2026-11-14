import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import MyAccess from "./MyAccess.vue";
import type { PrincipalAccess } from "./accessModel";

// The kinds registry asks the server once; the core kinds stand in here.
vi.mock("../../../accessApi", () => ({ accessApi: { kinds: vi.fn(async () => { throw new Error("offline"); }), me: vi.fn() } }));

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

const mine: PrincipalAccess = {
  principal: { kind: "user", id: "u-jana", name: "Jana", detail: "jana" },
  scope: "self",
  canManage: false,
  entries: [
    { kind: "space", resourceId: "provoz", title: "Provoz", spaceId: "provoz", spaceName: "Provoz", level: "write", sources: [{ via: "group", label: "Provoz tým", groupId: "g-ops", level: "write", effective: true }] },
    { kind: "page", resourceId: "p-plan", title: "Plán", parentTitle: "Architektura", spaceId: "provoz", spaceName: "Provoz", level: "manage", sources: [{ via: "direct", label: "Přímý přístup", level: "manage", version: "v1", effective: true }] },
    { kind: "vault", resourceId: "v-net", title: "Síťová zařízení", spaceId: "provoz", spaceName: "Provoz", level: "read", sources: [{ via: "direct", label: "Přímý přístup", level: "read", version: "v2", effective: true }] },
    { kind: "token", resourceId: "t-1", title: "Zálohy", level: "read", sources: [{ via: "direct", label: "Osobní token", level: "read", effective: true }] },
  ],
};

it("loads the caller's own access and shows it read-only, grouped by space, with links where a row lives", async () => {
  const me = vi.fn().mockResolvedValue(mine);
  wrapper = mount(MyAccess, { props: { api: { me } }, attachTo: document.body });
  expect(wrapper.text()).toContain("Načítání přístupů…");
  await flushPromises();
  expect(me).toHaveBeenCalledTimes(1);
  expect(wrapper.get('[data-access-group="provoz"]').text()).toContain("Prostor · Upravuje přes skupinu Provoz tým");
  expect(wrapper.get('[data-access-entry="space:provoz"] a').attributes("href")).toBe("#/spaces/provoz");
  expect(wrapper.find('[data-access-entry="page:p-plan"]').exists()).toBe(false);
  await wrapper.get('.access-details-toggle').trigger('click');
  expect(wrapper.get('[data-access-entry="page:p-plan"] a').attributes("href")).toBe("#/pages/p-plan");
  expect(wrapper.get('[data-access-entry="token:t-1"] a').attributes("href")).toBe("#/profile/api");
  expect(wrapper.get('[data-access-entry="vault:v-net"]').find("a").exists()).toBe(false);
  // Nothing is changed from the profile, not even an own direct grant.
  expect(wrapper.find("[data-change-access]").exists()).toBe(false);
  expect(wrapper.find("[data-revoke-access]").exists()).toBe(false);
});

it("says when there is nothing yet", async () => {
  wrapper = mount(MyAccess, { props: { api: { me: vi.fn().mockResolvedValue({ ...mine, entries: [] }) } } });
  await flushPromises();
  expect(wrapper.text()).toContain("Zatím nemáte přístup k žádnému prostoru, stránce ani trezoru.");
});

it("says what failed and loads again on retry", async () => {
  const me = vi.fn().mockRejectedValueOnce(new Error("Server neodpovídá")).mockResolvedValueOnce(mine);
  wrapper = mount(MyAccess, { props: { api: { me } } });
  await flushPromises();
  expect(wrapper.get(".error-state").text()).toContain("Server neodpovídá");
  await wrapper.get(".error-state button").trigger("click");
  await flushPromises();
  expect(wrapper.find(".error-state").exists()).toBe(false);
  expect(wrapper.find('[data-access-entry="space:provoz"]').exists()).toBe(true);
});
