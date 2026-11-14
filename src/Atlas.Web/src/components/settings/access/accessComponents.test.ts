import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { ApiError } from "../../../api";
import { provideReauthEnvironment } from "../useReauth";
import AccessEntryRow from "./AccessEntryRow.vue";
import PrincipalAccessView from "./PrincipalAccessView.vue";
import ResourceAccess from "./ResourceAccess.vue";
import type { AccessSource, PrincipalAccess, ResourceAccess as ResourceAccessState } from "./accessModel";

let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  document.body.innerHTML = "";
});
async function settle() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await flushPromises();
}
const direct = (level: AccessSource["level"], extra: Partial<AccessSource> = {}): AccessSource => ({ via: "direct", label: "", level, version: `v-${level}`, startAt: null, expiresAt: null, effective: true, ...extra });

describe("AccessEntryRow", () => {
  it("explains a renewable Hub lease without calling it the end of the permission", async () => {
    wrapper = mount(AccessEntryRow, { props: { row: { key: "space:hub", kind: "space", resourceId: "hub", title: "Projekt", level: "read", sources: [{ via: "group", label: "Vývoj", groupId: "group", groupSource: "hub", level: "read", effective: true, verificationLimited: true, expiresAt: "2099-01-01T12:05:00Z", grantExpiresAt: null }] } } });
    expect(wrapper.find("[data-access-end]").exists()).toBe(false);
    expect(wrapper.get("[data-hub-verification]").text()).toBe("Ověřuje Hub");
    await wrapper.get(".access-sources-toggle").trigger("click");
    expect(wrapper.text()).toContain("Členství ověřeno do");
    expect(wrapper.text()).toContain("Členství se mění v Hubu");
  });
  it("shows the clock time of a short-lived group access in the visible badge", () => {
    wrapper = mount(AccessEntryRow, { props: { row: { key: "space:short", kind: "space", resourceId: "short", title: "Projekt", level: "read", sources: [{ via: "group", label: "Projekt", groupId: "group", groupSource: "hub", level: "read", effective: true, expiresAt: new Date(2099, 0, 1, 12, 5).toISOString() }] } } });
    expect(wrapper.get("[data-access-end]").text()).toContain("12:05");
    expect(wrapper.text()).toContain("Přes skupinu Hubu Projekt");
  });

  it("draws the sentence with the level last, tints the icon with the level and offers Změnit and Odebrat for a direct grant", async () => {
    wrapper = mount(AccessEntryRow, { props: { row: { key: "vault:v-1", kind: "vault", resourceId: "v-1", title: "Servery", detail: "Trezor", level: "read", sources: [direct("read", { expiresAt: "2099-01-01T00:00:00Z" })] } }, attachTo: document.body });
    expect(wrapper.text()).toContain("Trezor · Přímý přístup");
    expect(wrapper.get(".access-entry-icon").attributes("data-level-color")).toBe("read");
    const meta = wrapper.get(".settings-list-row-meta").element.children;
    expect(meta[meta.length - 1]!.classList.contains("access-entry-level")).toBe(true);
    expect(wrapper.get("[data-access-end]").text()).toContain("Přístup končí ");
    await wrapper.get("[data-change-access]").trigger("click");
    expect(wrapper.emitted("change")).toHaveLength(1);
    await wrapper.get("[data-revoke-access]").trigger("click");
    expect(wrapper.emitted("revoke")![0]![1]).toMatchObject({ via: "direct", version: "v-read" });
  });

  it("offers the group for a right through a group and nothing on a locked or readonly row", async () => {
    wrapper = mount(AccessEntryRow, {
      props: { row: { key: "space:p", kind: "space", resourceId: "p", title: "Jana", subject: { kind: "user", id: "u", name: "Jana" }, level: "write", sources: [{ via: "group", label: "Ops", groupId: "g-1", level: "write", effective: true }] }, groupHref: (id: string) => `#/g/${id}` },
    });
    expect(wrapper.find("[data-revoke-access]").exists()).toBe(false);
    expect(wrapper.get("[data-open-group]").attributes("href")).toBe("#/g/g-1");
    expect(wrapper.text()).toContain("Skupina Ops");
    await wrapper.setProps({ readonly: true });
    expect(wrapper.find(".settings-list-row-actions").exists()).toBe(false);
    await wrapper.setProps({ readonly: false, row: { key: "a", kind: "space", resourceId: "p", title: "Správci wiki", level: "manage", locked: true, sources: [{ via: "automatic", label: "Automaticky", level: "manage", effective: true }] } });
    expect(wrapper.text()).toContain("Automaticky");
    expect(wrapper.find(".settings-list-row-actions").exists()).toBe(false);
  });

  it("says when a grant waits for its start or has ended", () => {
    wrapper = mount(AccessEntryRow, { props: { row: { key: "k", kind: "space", resourceId: "p", title: "Eva", level: "read", sources: [direct("read", { startAt: "2099-01-01T00:00:00Z", effective: false })] } } });
    expect(wrapper.get("[data-access-pending]").text()).toContain("od ");
    wrapper.unmount();
    wrapper = mount(AccessEntryRow, { props: { row: { key: "k", kind: "space", resourceId: "p", title: "Eva", level: "read", sources: [direct("read", { expiresAt: "2020-01-01T00:00:00Z", effective: false })] } } });
    expect(wrapper.get("[data-access-expired]").text()).toBe("Platnost skončila");
  });
});

describe("PrincipalAccessView", () => {
  const access: PrincipalAccess = {
    principal: { kind: "user", id: "u-1", name: "Jana" },
    scope: "self",
    canManage: false,
    entries: [
      { kind: "space", resourceId: "provoz", title: "Provoz", spaceId: "provoz", spaceName: "Provoz", spaceIcon: "server", spaceColor: "green", level: "write", sources: [{ via: "group", label: "Ops", groupId: "g", level: "write", effective: true }] },
      { kind: "vault", resourceId: "v-1", title: "Servery", spaceId: "provoz", spaceName: "Provoz", level: "read", sources: [direct("read")] },
      { kind: "space", resourceId: "eng", title: "Engineering", spaceId: "eng", spaceName: "Engineering", level: "read", sources: [direct("read")] },
    ],
  };

  it("filters by search and by the legend's level, and stays readonly for the caller's own card", async () => {
    wrapper = mount(PrincipalAccessView, { props: { access, readonly: true }, attachTo: document.body });
    expect(wrapper.findAll("[data-access-group]").map((group) => group.attributes("data-access-group"))).toEqual(["eng", "provoz"]);
    expect(wrapper.find("[data-change-access]").exists()).toBe(false);
    expect(wrapper.get('[data-access-group="provoz"] .access-space-icon').attributes('data-color')).toBe('green');
    expect(wrapper.get('[data-access-group="provoz"] .access-space-icon svg').attributes('data-icon')).toBe('server');
    expect(wrapper.find('[data-access-entry="vault:v-1"]').exists()).toBe(false);
    await wrapper.get('[data-access-group="provoz"] .access-details-toggle').trigger('click');
    expect(wrapper.find('[data-access-entry="vault:v-1"]').exists()).toBe(true);
    const legend = wrapper.findAll(".level-legend-item");
    expect(legend.map((item) => item.attributes("aria-label"))).toEqual(["Upravuje: 1", "Čte: 2"]);
    await legend[0]!.trigger("click");
    expect(wrapper.findAll("[data-access-entry]").map((row) => row.attributes("data-access-entry"))).toEqual(["space:provoz"]);
    expect(legend[0]!.attributes("aria-pressed")).toBe("true");
    await legend[0]!.trigger("click");
    await wrapper.get('input[type="search"]').setValue("servery");
    expect(wrapper.findAll("[data-access-entry]").map((row) => row.attributes("data-access-entry"))).toEqual(["vault:v-1"]);
    await wrapper.get('input[type="search"]').setValue("nic takového");
    expect(wrapper.text()).toContain("Nic takového");
  });
});

describe("ResourceAccess", () => {
  function state(kind = "space"): ResourceAccessState {
    return {
      resource: { kind, id: "r-1", title: "Provoz" },
      canManage: true,
      automatic: [],
      entries: [{ subject: { kind: "user", id: "u-1", name: "Jana" }, level: "read", sources: [direct("read")] }],
      hasMore: false,
    };
  }
  function host(kind: string, api: Record<string, unknown>) {
    const Host = defineComponent({
      setup() {
        provideReauthEnvironment({ method: "local", supported: true, verifyPassword: async () => ({ ok: true, proofId: "proof-9" }), verifyHub: async () => false });
        return () => h("div", { class: "settings-ui" }, [h(ResourceAccess, { kind, id: "r-1", api: api as never })]);
      },
    });
    return mount(Host, { attachTo: document.body });
  }
  async function signIn() {
    const input = document.querySelector<HTMLInputElement>('dialog[open] input[type="password"]')!;
    input.value = "heslo";
    input.dispatchEvent(new Event("input"));
    await settle();
    document.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-reauth]")!.click();
    await settle();
  }

  it("reloads with Jen přidělené and changes a space grant at once", async () => {
    const api = { resource: vi.fn(async () => state()), candidates: vi.fn(), grant: vi.fn(async () => undefined), revoke: vi.fn() };
    wrapper = host("space", api);
    await settle();
    expect(api.resource).toHaveBeenLastCalledWith("space", "r-1", "effective");
    await wrapper.get("[data-change-access]").trigger("click");
    await settle();
    await wrapper.get('[data-access-level-picker] [data-value="write"]').trigger("click");
    await wrapper.get("[data-save-access]").trigger("click");
    await settle();
    expect(api.grant).toHaveBeenCalledWith("space", "r-1", { subjectKind: "user", subjectId: "u-1", level: "write", startAt: null, expiresAt: null, expectedVersion: "v-read" }, null);
    expect(wrapper.get("[data-result-banner]").text()).toContain("Přístup pro Jana je uložený: Upravuje");
    expect(api.resource).toHaveBeenCalledTimes(2);
  });

  it("revokes a vault grant only after a fresh sign-in and sends the proof", async () => {
    const api = { resource: vi.fn(async () => state("vault")), candidates: vi.fn(), grant: vi.fn(), revoke: vi.fn(async () => undefined) };
    wrapper = host("vault", api);
    await settle();
    await wrapper.get("[data-revoke-access]").trigger("click");
    await settle();
    expect(document.querySelector("dialog[open] [data-reauth-dialog]")?.textContent).toContain("Jana přijde o přímý přístup Čte · trezor Provoz");
    expect(api.revoke).not.toHaveBeenCalled();
    await signIn();
    expect(api.revoke).toHaveBeenCalledWith("vault", "r-1", "user", "u-1", "v-read", "proof-9");
  });

  it("asks for the fresh sign-in when the server refuses a change with reauth_required and repeats it with the proof", async () => {
    const grant = vi.fn().mockRejectedValueOnce(new ApiError(403, "Potvrďte změnu.", "reauth_required")).mockResolvedValue(undefined);
    const api = { resource: vi.fn(async () => state()), candidates: vi.fn(), grant, revoke: vi.fn() };
    wrapper = host("space", api);
    await settle();
    await wrapper.get("[data-change-access]").trigger("click");
    await settle();
    await wrapper.get('[data-access-level-picker] [data-value="write"]').trigger("click");
    await wrapper.get("[data-save-access]").trigger("click");
    await settle();
    expect(grant).toHaveBeenCalledTimes(1);
    expect(document.querySelector("dialog[open] [data-reauth-dialog]")).not.toBeNull();
    await signIn();
    expect(grant).toHaveBeenCalledTimes(2);
    expect(grant.mock.calls[1]![3]).toBe("proof-9");
  });

  it("searches a space list the server cut at 200 on the server: the loaded rows narrow at once, a stale answer is dropped, Esc clears", async () => {
    const person = (id: string, name: string) => ({ subject: { kind: "user" as const, id, name }, level: "read" as const, sources: [direct("read")] });
    const answer = (entries: ResourceAccessState["entries"], hasMore = false): ResourceAccessState => ({ ...state(), entries, hasMore });
    const pending: Record<string, (value: ResourceAccessState) => void> = {};
    const resource = vi.fn((_kind: string, _id: string, _view: string, q?: string) =>
      q ? new Promise<ResourceAccessState>((resolve) => (pending[q] = resolve)) : Promise.resolve(answer([person("u-1", "Jana"), person("u-2", "Petr")], true)));
    wrapper = host("space", { resource, candidates: vi.fn(), grant: vi.fn(), revoke: vi.fn() });
    await settle();
    const shown = () => wrapper!.findAll("[data-access-entry]").map((row) => row.attributes("data-access-entry"));
    const pause = () => new Promise((resolve) => setTimeout(resolve, 300));
    expect(wrapper.get("[data-access-more]").text()).toContain("Další najde hledání jména.");
    // A cut list has no total: its section shows no count of the first page.
    expect(wrapper.find(".settings-section-title .st-count").exists()).toBe(false);
    const input = wrapper.get<HTMLInputElement>('input[type="search"]');
    await input.setValue("pe");
    expect(shown()).toEqual(["user:u-2"]);
    expect(resource).toHaveBeenCalledTimes(1);
    await pause();
    expect(resource).toHaveBeenLastCalledWith("space", "r-1", "effective", "pe");
    await input.setValue("zo");
    await pause();
    pending.zo!(answer([person("u-9", "Žofie")]));
    await settle();
    pending.pe!(answer([person("u-2", "Petr")]));
    await settle();
    expect(shown()).toEqual(["user:u-9"]);
    // Nobody found by the server is a search that found nothing, not a space without grants.
    await input.setValue("xy");
    await pause();
    pending.xy!(answer([]));
    await settle();
    expect(wrapper.find("[data-access-empty]").exists()).toBe(false);
    expect(wrapper.text()).toContain("Nikdo neodpovídá hledání.");
    await input.trigger("keydown", { key: "Escape" });
    expect(input.element.value).toBe("");
    await pause();
    await settle();
    expect(resource).toHaveBeenLastCalledWith("space", "r-1", "effective");
    expect(shown()).toEqual(["user:u-1", "user:u-2"]);
  });

  it("keeps the search on the loaded rows when the list is whole or the kind does not search on the server", async () => {
    for (const [kind, hasMore] of [["space", false], ["vault", true]] as const) {
      const resource = vi.fn(async () => ({ ...state(kind), hasMore }));
      wrapper = host(kind, { resource, candidates: vi.fn(), grant: vi.fn(), revoke: vi.fn() });
      await settle();
      await wrapper.get('input[type="search"]').setValue("ja");
      await new Promise((resolve) => setTimeout(resolve, 300));
      expect(resource).toHaveBeenCalledTimes(1);
      wrapper.unmount();
      wrapper = undefined;
    }
  });

  it("adds a grant in place when the surface has no flow route", async () => {
    const api = { resource: vi.fn(async () => state("vault")), candidates: vi.fn(async () => [{ kind: "user", id: "u-2", name: "Petr", detail: "petr" }]), grant: vi.fn(), revoke: vi.fn() };
    wrapper = host("vault", api);
    await settle();
    await wrapper.get("[data-add-access]").trigger("click");
    await settle();
    expect(wrapper.find("[data-resource-grant-flow]").exists()).toBe(true);
    expect(wrapper.find("[data-resource-access]").exists()).toBe(false);
  });
});
