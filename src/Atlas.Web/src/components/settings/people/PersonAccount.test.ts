import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { DirectoryUser } from "../../../administration";
import type { PrincipalAccess } from "../access/accessModel";
import InviteAccountWizard from "./InviteAccountWizard.vue";
import PersonAccount, { type PersonAccountSource } from "./PersonAccount.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

const account: DirectoryUser = { id: "acc-1", username: "eva", displayName: "Eva Dvořáková", isActive: true, isWikiAdmin: false, version: "v1",
  hasBuiltinCredential: true, source: "builtin", credentialState: "active", contactId: "c-1" };
const access = { principal: { kind: "user", id: "acc-1", name: "Eva Dvořáková", detail: "eva", isActive: true, isWikiAdmin: false, lastSignInAt: null,
  groups: [{ id: "g-dev", name: "Vývojáři" }] }, entries: [] } as unknown as PrincipalAccess;
function source(admin: boolean): PersonAccountSource & { [K in keyof PersonAccountSource]: ReturnType<typeof vi.fn> } {
  return {
    me: vi.fn(async () => ({ id: "me", isWikiAdmin: admin })),
    account: vi.fn(async () => account),
    principal: vi.fn(async () => access),
    create: vi.fn(async () => ({ token: "code", expiresAt: "2026-12-01T00:00:00Z", purpose: "invitation" as const })),
  };
}
const show = (props: { accountId?: string; contact: { id: string; name: string }; source: PersonAccountSource }) => {
  wrapper = mount(PersonAccount, { props, attachTo: document.body });
  return flushPromises().then(() => wrapper!);
};

describe("the account part of a person's profile", () => {
  it("is nothing and asks the admin endpoints nothing for anyone but a wiki administrator", async () => {
    const api = source(false);
    const view = await show({ accountId: "acc-1", contact: { id: "c-1", name: "Eva Dvořáková" }, source: api });
    expect(view.find("[data-person-account]").exists()).toBe(false);
    expect(api.account).not.toHaveBeenCalled();
    expect(api.principal).not.toHaveBeenCalled();
  });

  it("shows a wiki administrator the account, its groups and the way into its full card", async () => {
    const api = source(true);
    const view = await show({ accountId: "acc-1", contact: { id: "c-1", name: "Eva Dvořáková" }, source: api });
    expect(api.account).toHaveBeenCalledWith("acc-1");
    expect(view.get("[data-person-account]").text()).toContain("Vývojáři");
    expect(view.get("[data-person-account-open]").attributes("href")).toBe("#/settings/people/accounts/acc-1");
  });

  it("makes a wiki administrator an account of a contact without one, prefilled and linked to that contact", async () => {
    const api = source(true);
    const view = await show({ contact: { id: "c-2", name: "Ota Novák" }, source: api });
    expect(api.account).not.toHaveBeenCalled();
    await view.get("[data-person-account-create]").trigger("click");
    await flushPromises();
    const wizard = view.findComponent(InviteAccountWizard);
    expect(wizard.props("initial")).toEqual({ displayName: "Ota Novák" });
    await wizard.props("submit")({ username: "ota", displayName: "Ota Novák", isWikiAdmin: false }, null);
    expect(api.create).toHaveBeenCalledWith({ username: "ota", displayName: "Ota Novák", isWikiAdmin: false, contactId: "c-2" }, null);
    wizard.vm.$emit("done", { token: "code", expiresAt: "", purpose: "invitation" });
    expect(view.emitted("created")).toHaveLength(1);
  });
});
