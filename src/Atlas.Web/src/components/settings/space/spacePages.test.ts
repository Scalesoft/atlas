import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, type Component } from "vue";
import { clearCsrf, type Space } from "../../../api";
import type { ResourceAccess } from "../access/accessModel";
import type { ShareLink } from "../../../sharing";
import type { SettingsContext } from "../context";
import { localInput } from "../grantValidity";
import { createDirtyGuard, type DirtyGuard } from "../dirtyGuard";
import { fakeUrlApi, provideSettingsSurface, testContext, type FakeCall, type FakeUrlHandler } from "../general/testing";
import SpaceAccessPage from "./SpaceAccessPage.vue";
import SpaceGeneralPage from "./SpaceGeneralPage.vue";
import SpaceSharingPage from "./SpaceSharingPage.vue";

const spaces: Space[] = [{ id: "provoz", name: "Provoz", icon: "book-open", color: "blue", role: "Admin", description: "Servery", pinPolicy: "None", pinned: false, pageCount: 0, lastChangedAt: null }];

let calls: FakeCall[] = [];
function fakeApi(handler: FakeUrlHandler) {
  calls = fakeUrlApi(handler);
}
const conflict = () => new Response(JSON.stringify({ detail: "Změněno jinde" }), { status: 409, headers: { "Content-Type": "application/json" } });
const puts = () => calls.filter((call) => call.method === "PUT");

let wrapper: VueWrapper | undefined;
let context: SettingsContext;
let guard: DirtyGuard;
async function page(component: Component, props: Record<string, unknown>, hash: string, options: { routed?: boolean; isWikiAdmin?: boolean } = {}) {
  history.replaceState(null, "", `/${hash}`);
  context = testContext({ isWikiAdmin: options.isWikiAdmin ?? true, spaces });
  guard = createDirtyGuard();
  const Host = defineComponent({
    setup() {
      const { router, flash } = provideSettingsSurface({ base: "#/spaces/provoz/settings", context, guard });
      // Like SpaceSettings: the flash line above the page, `access[/…]` routed through `part`.
      return () => h("div", { class: "settings-ui" }, [
        flash.message.value ? h("p", { "data-flash": "" }, flash.message.value.text) : null,
        h(component, options.routed ? { ...props, key: router.route.value.path, part: router.route.value.segments.slice(1) } : props),
      ]);
    },
  });
  wrapper = mount(Host, { attachTo: document.body });
  await flushPromises();
  return wrapper;
}
async function settle() {
  await new Promise((resolve) => setTimeout(resolve, 0));
  await flushPromises();
}
const confirm = async () => {
  (document.querySelector("dialog[open] [data-confirm-action]") as HTMLButtonElement).click();
  await settle();
};

beforeEach(() => clearCsrf());
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

describe("Obecné", () => {
  const settings = { id: "provoz", name: "Provoz", icon: "book-open", color: "blue", description: "Servery", pinPolicy: "None", version: "v1" };

  it("saves name, icon, colour and pin policy with the loaded version and tells the sidebar", async () => {
    fakeApi((method, url, body) => {
      if (url.pathname === "/api/spaces/provoz/settings") return method === "PUT" ? { ...settings, ...(body as object), version: "v2" } : settings;
      return undefined;
    });
    const view = await page(SpaceGeneralPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/general");
    expect(view.find("[data-save-bar-save]").exists()).toBe(false);
    await view.get("[data-space-name]").setValue("Provoz IT");
    await view.get('[data-appearance-icons] [data-value="compass"]').trigger("click");
    await view.get("[data-appearance-more-colors]").trigger("click"); await flushPromises();
    await view.get('[data-appearance-colors] [data-value="green-3"]').trigger("click");
    expect(view.get('[data-space-pin-policy] [role="radio"][aria-checked="true"]').attributes("value")).toBe("None");
    await view.get('[data-space-pin-policy] [data-value="Required"]').trigger("click");
    expect(view.get('[data-space-publication] [role="radio"][aria-checked="true"]').attributes("value")).toBe("Manual");
    await view.get('[data-space-publication] [data-value="Automatic"]').trigger("click");
    await flushPromises();
    expect(guard.dirty.value).toBe(true);
    expect(view.get("[data-appearance-preview]").text()).toContain("Provoz IT");
    expect(view.get("[data-appearance-preview] [data-color]").attributes("data-color")).toBe("green-3");
    await view.get("[data-space-save] [data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(puts()[0]!.body).toEqual({ name: "Provoz IT", icon: "compass", color: "green-3", description: "Servery", pinPolicy: "Required", commentsEnabled: true, publicationMode: "Automatic", expectedVersion: "v1" });
    expect(context.spaceChanged).toHaveBeenCalledWith(expect.objectContaining({ name: "Provoz IT", version: "v2" }));
    expect(guard.dirty.value).toBe(false);
  });

  it("keeps the draft on a conflict until the user chooses Načíst znovu", async () => {
    let current = settings;
    fakeApi((method, url) => {
      if (url.pathname !== "/api/spaces/provoz/settings") return undefined;
      if (method === "PUT") {
        current = { ...settings, name: "Provoz a sítě", version: "v2" };
        return conflict();
      }
      return current;
    });
    const view = await page(SpaceGeneralPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/general");
    await view.get("[data-space-name]").setValue("Moje úprava");
    await view.get("[data-space-save] [data-save-bar-save]").trigger("click");
    await flushPromises();
    const note = view.get("[data-space-conflict]");
    expect(note.text()).toContain("upravil někdo jiný");
    // Never silently replaced: the draft stays dirty and guarded, and there is no forced save.
    expect((view.get("[data-space-name]").element as HTMLInputElement).value).toBe("Moje úprava");
    expect(guard.dirty.value).toBe(true);
    expect(note.find("[data-conflict-overwrite]").exists()).toBe(false);
    await note.get("[data-conflict-reload]").trigger("click");
    await flushPromises();
    expect((view.get("[data-space-name]").element as HTMLInputElement).value).toBe("Provoz a sítě");
    expect(guard.dirty.value).toBe(false);
    expect(view.find("[data-space-conflict]").exists()).toBe(false);
    expect(context.spaceChanged).not.toHaveBeenCalled();
  });

  it("refuses an empty name next to the field and discards through the guard", async () => {
    fakeApi((method, url) => (url.pathname === "/api/spaces/provoz/settings" ? settings : undefined));
    const view = await page(SpaceGeneralPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/general");
    await view.get("[data-space-name]").setValue("  ");
    await view.get("[data-space-save] [data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(view.get(".settings-row-error").text()).toContain("Zadejte název prostoru");
    expect(puts()).toHaveLength(0);
    guard.discard();
    await flushPromises();
    expect((view.get("[data-space-name]").element as HTMLInputElement).value).toBe("Provoz");
  });
});

describe("Sdílení", () => {
  const link = (id: string, extra: Partial<ShareLink> = {}): ShareLink => ({
    id, pageId: "p-1", pageTitle: "Servery", name: `Odkaz ${id}`, revisionVersion: 3, createdByName: "Jana", createdAt: "2026-09-20T10:00:00Z",
    expiresAt: "2030-10-01T10:00:00Z", revokedAt: null, lastOpenedAt: null, status: "active", ...extra,
  });
  function sharingApi(state: { enabled: boolean; links: ShareLink[] }) {
    return (method: string, url: URL, body: unknown) => {
      if (url.pathname === "/api/spaces/provoz/external-sharing") {
        if (method === "PUT") {
          state.enabled = (body as { enabled: boolean }).enabled;
          if (!state.enabled) state.links = state.links.map((item) => ({ ...item, status: "revoked" as const }));
        }
        return { enabled: state.enabled, version: state.enabled ? "p1" : "p2" };
      }
      if (url.pathname === "/api/spaces/provoz/share-links") return { items: state.links, hasMore: false };
      if (method === "DELETE" && url.pathname.startsWith("/api/pages/p-1/share-links/")) {
        const id = url.pathname.split("/").at(-1);
        state.links = state.links.map((item) => (item.id === id ? { ...item, status: "revoked" as const } : item));
        return new Response(null, { status: 204 });
      }
      return undefined;
    };
  }

  it("turns anonymous links off only after the destructive confirmation", async () => {
    fakeApi(sharingApi({ enabled: true, links: [link("a"), link("b")] }));
    const view = await page(SpaceSharingPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/sharing");
    expect(view.get("[data-sharing-toggle]").attributes("aria-checked")).toBe("true");
    await view.get("[data-sharing-toggle]").trigger("click");
    await flushPromises();
    expect(puts()).toHaveLength(0);
    expect(document.body.textContent).toContain("trvale zrušíte všechny aktivní anonymní odkazy v prostoru (2)");
    await confirm();
    expect(puts()[0]).toMatchObject({ path: "/api/spaces/provoz/external-sharing", body: { enabled: false, expectedVersion: "p1" } });
    expect(view.get("[data-sharing-toggle]").attributes("aria-checked")).toBe("false");
    expect(view.text()).toContain("Anonymní sdílení je vypnuté");
    expect(view.get('[data-share-link="a"]').text()).toContain("Zneplatněný");
    // Turning it back on is not destructive and does not ask.
    await view.get("[data-sharing-toggle]").trigger("click");
    await flushPromises();
    expect(puts()[1]!.body).toEqual({ enabled: true, expectedVersion: "p2" });
  });

  it("revokes one link after a confirmation and keeps the rest", async () => {
    fakeApi(sharingApi({ enabled: true, links: [link("a"), link("b", { status: "expired" })] }));
    const view = await page(SpaceSharingPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/sharing");
    expect(view.get('[data-share-link="a"]').text()).toContain("Odkaz a · revize 3 · vytvořil(a) Jana");
    expect(view.find('[data-share-link="b"] [data-revoke-link]').exists()).toBe(false);
    await view.get('[data-share-link="a"] [data-revoke-link]').trigger("click");
    expect(document.body.textContent).toContain("přestane platit hned");
    expect(calls.some((call) => call.method === "DELETE")).toBe(false);
    await confirm();
    expect(calls.some((call) => call.method === "DELETE" && call.path === "/api/pages/p-1/share-links/a")).toBe(true);
    expect(view.get('[data-share-link="a"]').text()).toContain("Zneplatněný");
    expect(view.get("[data-flash]").text()).toContain("Odkaz a");
  });

  it("shows an empty state without links and an error with a reload", async () => {
    let fail = true;
    fakeApi((method, url) => {
      if (fail) return new Response(JSON.stringify({ detail: "Server neodpovídá." }), { status: 500, headers: { "Content-Type": "application/json" } });
      return sharingApi({ enabled: true, links: [] })(method, url, undefined);
    });
    const view = await page(SpaceSharingPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/sharing");
    expect(view.get('[role="alert"]').text()).toContain("Sdílení se nepodařilo načíst");
    fail = false;
    await view.get('[role="alert"] button').trigger("click");
    await flushPromises();
    expect(view.text()).toContain("V prostoru nejsou žádné anonymní odkazy");
  });
});

describe("Přístupy", () => {
  function accessState(): ResourceAccess {
    return {
      resource: { kind: "space", id: "provoz", title: "Provoz" },
      canManage: true,
      automatic: [{ label: "Správci wiki", detail: "automaticky ve všech prostorech", level: "manage", count: 2 }],
      entries: [
        { subject: { kind: "user", id: "u-jana", name: "Jana Nováková", detail: "jana" }, level: "write", sources: [{ via: "direct", label: "", level: "write", version: "v-jana", startAt: null, expiresAt: "2030-04-05T10:30:00.654321Z", effective: true }] },
        { subject: { kind: "group", id: "g-ops", name: "Provoz tým", memberCount: 3 }, level: "read", sources: [{ via: "direct", label: "", level: "read", version: "v-ops", startAt: null, expiresAt: null, effective: true }] },
        { subject: { kind: "user", id: "u-tomas", name: "Tomáš Král", detail: "tomas" }, level: "read", sources: [{ via: "group", label: "Provoz tým", groupId: "g-ops", level: "read", effective: true }] },
      ],
      hasMore: false,
      exceptions: [{ kind: "vault", id: "v-1", title: "Servery a síť", mode: "grants", grantCount: 2 }],
    };
  }
  function accessApi(options: { conflict?: () => boolean } = {}) {
    let state = accessState();
    return (method: string, url: URL, body: unknown) => {
      if (url.pathname === "/api/access/candidates") return url.searchParams.get("kind") === "user" ? [{ kind: "user", id: "u-petr", name: "Petr Svoboda", detail: "petr" }] : [{ kind: "group", id: "g-dev", name: "Vývojáři", memberCount: 4 }];
      if (url.pathname === "/api/access/resources/space/provoz" && method === "GET") return state;
      if (url.pathname === "/api/access/resources/vault/v-1") return { ...accessState(), resource: { kind: "vault", id: "v-1", title: "Servery a síť" }, exceptions: undefined };
      if (url.pathname === "/api/access/principals/user/u-tomas") return { principal: { kind: "user", id: "u-tomas", name: "Tomáš Král", detail: "tomas" }, scope: "space", canManage: true, entries: [] };
      const grant = url.pathname.match(/^\/api\/access\/resources\/space\/provoz\/grants(?:\/(user|group)\/([^/]+))?$/);
      if (!grant) return undefined;
      if (options.conflict?.()) return conflict();
      const change = body as { subjectKind: "user" | "group"; subjectId: string; level: "read"; startAt: string | null; expiresAt: string | null } | undefined;
      const id = change?.subjectId ?? grant[2];
      const others = state.entries.filter((entry) => entry.subject.id !== id);
      state = {
        ...state,
        entries: change
          ? [...others, { subject: { kind: change.subjectKind, id: change.subjectId, name: change.subjectId === "u-petr" ? "Petr Svoboda" : "Jana Nováková" }, level: change.level, sources: [{ via: "direct", label: "", level: change.level, version: "v-new", startAt: change.startAt, expiresAt: change.expiresAt, effective: true }] }]
          : others,
      };
      return new Response(null, { status: 204 });
    };
  }
  const row = (view: VueWrapper, key: string) => view.get(`[data-access-entry="${key}"]`);

  it("lists groups and people on the level staircase with a locked automatic row", async () => {
    fakeApi(accessApi());
    const view = await page(SpaceAccessPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/access", { routed: true });
    expect(calls.some((call) => call.path === "/api/access/resources/space/provoz?view=effective")).toBe(true);
    expect(view.findAll("[data-access-entry]").map((item) => item.attributes("data-access-entry"))).toEqual(["automatic:0", "group:g-ops", "user:u-jana", "user:u-tomas"]);
    const jana = row(view, "user:u-jana");
    expect(jana.get(".level-badge").attributes("aria-label")).toBe("Úroveň Upravuje, zahrnuje Vidí a Čte");
    expect(jana.text()).toContain("Přístup končí ");
    expect(row(view, "automatic:0").text()).toContain("Automaticky");
    // Access through a group is changed on the group; a person opens as a card limited to this space.
    expect(row(view, "user:u-tomas").find("[data-revoke-access]").exists()).toBe(false);
    expect(row(view, "user:u-tomas").get("[data-open-group]").attributes("href")).toBe("#/spaces/provoz/settings/access/groups/g-ops");
    expect(row(view, "user:u-tomas").get(".access-entry-link").attributes("href")).toBe("#/spaces/provoz/settings/access/people/u-tomas");
    expect(view.text()).toContain("Čte → Upravuje → Spravuje");
    expect(view.text()).not.toMatch(/Reader|Writer|Čtenář|Autor/);
  });

  it("adds a grant through GrantFlow at access/new with people found on the server and returns to the list", async () => {
    fakeApi(accessApi());
    const view = await page(SpaceAccessPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/access", { routed: true });
    expect(view.get("[data-add-access]").attributes("href")).toBe("#/spaces/provoz/settings/access/new");
    location.hash = "#/spaces/provoz/settings/access/new";
    await settle();
    await view.get('[data-value="user"] [role="radio"]').trigger("click");
    await flushPromises();
    // Nobody is listed before typing: never the whole directory.
    expect(view.findAll('.searchable-list [role="radio"]')).toHaveLength(0);
    await view.get('.searchable-list input[type="search"]').setValue("pe");
    await new Promise((resolve) => setTimeout(resolve, 300));
    await settle();
    expect(calls.some((call) => call.path === "/api/access/candidates?kind=user&q=pe")).toBe(true);
    await view.get('.searchable-list [data-value="u-petr"]').trigger("click");
    await flushPromises();
    await view.get('[data-access-level-picker] [data-value="write"]').trigger("click");
    await flushPromises();
    await view.get('input[name="expiresAt"]').setValue("2030-04-02T12:30");
    await flushPromises();
    expect(view.get('[data-summary="subject"]').text()).toContain("Petr Svoboda");
    await view.get("[data-flow-next]").trigger("click");
    await settle();
    expect(puts()[0]!.path).toBe("/api/access/resources/space/provoz/grants");
    expect(puts()[0]!.body).toEqual({ subjectKind: "user", subjectId: "u-petr", level: "write", expectedVersion: null, startAt: null, expiresAt: new Date("2030-04-02T12:30").toISOString() });
    expect(location.hash).toBe("#/spaces/provoz/settings/access");
    await settle();
    expect(view.find('[data-access-entry="user:u-petr"]').exists()).toBe(true);
    expect(view.get("[data-flash]").text()).toContain("Petr Svoboda má přístup Upravuje · prostor Provoz");
    expect(context.accessChanged).toHaveBeenCalled();
  });

  it("asks for a fresh sign-in before granting Spravuje", async () => {
    fakeApi(accessApi());
    const view = await page(SpaceAccessPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/access/new", { routed: true });
    await view.get('[data-value="group"] [role="radio"]').trigger("click");
    await view.get('.searchable-list input[type="search"]').setValue("v");
    await new Promise((resolve) => setTimeout(resolve, 300));
    await settle();
    await view.get('.searchable-list [data-value="g-dev"]').trigger("click");
    await flushPromises();
    await view.get('[data-access-level-picker] [data-value="manage"]').trigger("click");
    await flushPromises();
    expect(view.find("[data-confirm-reauth]").exists()).toBe(true);
    expect(view.find("[data-flow-next]").exists()).toBe(false);
    expect(puts()).toHaveLength(0);
  });

  it("changes a level with the grant's version and keeps the exact stored bounds", async () => {
    fakeApi(accessApi());
    const view = await page(SpaceAccessPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/access", { routed: true });
    const jana = () => row(view, "user:u-jana");
    await jana().get("[data-change-access]").trigger("click");
    await flushPromises();
    expect(jana().get("[data-save-access]").attributes("disabled")).toBeDefined();
    await jana().get('[data-access-level-picker] [data-value="read"]').trigger("click");
    await jana().get("[data-save-access]").trigger("click");
    await settle();
    expect(puts()[0]!.body).toEqual({ subjectKind: "user", subjectId: "u-jana", level: "read", expectedVersion: "v-jana", startAt: null, expiresAt: "2030-04-05T10:30:00.654321Z" });
    expect(view.get("[data-flash]").text()).toContain("Přístup pro Jana Nováková je uložený: Čte");
  });

  it("marks an end before the start or in the past inline and keeps Save disabled", async () => {
    fakeApi(accessApi());
    const view = await page(SpaceAccessPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/access", { routed: true });
    const ops = () => row(view, "group:g-ops");
    await ops().get("[data-change-access]").trigger("click");
    await flushPromises();
    await ops().get('input[name="startAt"]').setValue("2030-04-02T12:30");
    await ops().get('input[name="expiresAt"]').setValue("2030-04-01T12:30");
    expect(ops().get('[role="alert"]').text()).toContain("Konec platnosti musí být po začátku");
    expect(ops().get("[data-save-access]").attributes("disabled")).toBeDefined();
    await ops().get('input[name="startAt"]').setValue("");
    await ops().get('input[name="expiresAt"]').setValue("2020-04-01T12:30");
    expect(ops().get('[role="alert"]').text()).toContain("Konec platnosti už uplynul");
    expect(ops().get('input[name="expiresAt"]').attributes("aria-invalid")).toBe("true");
    expect(ops().get("[data-save-access]").attributes("disabled")).toBeDefined();
    await ops().get("[data-save-access]").trigger("click");
    await flushPromises();
    expect(puts()).toHaveLength(0);
    // The picker itself offers no end before the next minute.
    expect(ops().get('input[name="expiresAt"]').attributes("min")! > localInput(new Date())).toBe(true);
  });

  it("sets a preset end from the grant's start and clears it with Bez omezení", async () => {
    fakeApi(accessApi());
    const view = await page(SpaceAccessPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/access", { routed: true });
    const jana = () => row(view, "user:u-jana");
    await jana().get("[data-change-access]").trigger("click");
    await flushPromises();
    // A stored end of its own selects no tile.
    expect(jana().find('.grant-validity [role="radio"][aria-checked="true"]').exists()).toBe(false);
    await jana().get('input[name="startAt"]').setValue("2030-04-01T08:00");
    await jana().get('[data-value="30"] [role="radio"]').trigger("click");
    await flushPromises();
    expect((jana().get('input[name="expiresAt"]').element as HTMLInputElement).value).toBe(localInput(new Date(new Date("2030-04-01T08:00").getTime() + 30 * 86_400_000)));
    // Moving the start moves a chosen preset's end along; Bez omezení clears it.
    await jana().get('input[name="startAt"]').setValue("2030-05-01T08:00");
    await flushPromises();
    expect((jana().get('input[name="expiresAt"]').element as HTMLInputElement).value).toBe(localInput(new Date(new Date("2030-05-01T08:00").getTime() + 30 * 86_400_000)));
    await jana().get('[data-value="none"] [role="radio"]').trigger("click");
    await flushPromises();
    expect((jana().get('input[name="expiresAt"]').element as HTMLInputElement).value).toBe("");
    await jana().get("[data-save-access]").trigger("click");
    await settle();
    expect(puts()[0]!.body).toEqual({ subjectKind: "user", subjectId: "u-jana", level: "write", expectedVersion: "v-jana", startAt: new Date("2030-05-01T08:00").toISOString(), expiresAt: null });
  });

  it("removes a grant only after a confirmation", async () => {
    fakeApi(accessApi());
    const view = await page(SpaceAccessPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/access", { routed: true });
    await row(view, "group:g-ops").get("[data-revoke-access]").trigger("click");
    await flushPromises();
    expect(document.body.textContent).toContain("Provoz tým přijde o přímý přístup Čte · prostor Provoz");
    (document.querySelector("dialog[open] [data-confirm-cancel]") as HTMLButtonElement).click();
    await flushPromises();
    expect(calls.some((call) => call.method === "DELETE")).toBe(false);
    await row(view, "group:g-ops").get("[data-revoke-access]").trigger("click");
    await flushPromises();
    await confirm();
    expect(calls.find((call) => call.method === "DELETE")!.path).toBe("/api/access/resources/space/provoz/grants/group/g-ops?version=v-ops");
    expect(view.find('[data-access-entry="group:g-ops"]').exists()).toBe(false);
    expect(context.accessChanged).toHaveBeenCalledTimes(1);
  });

  it("reloads on a conflict and says so", async () => {
    fakeApi(accessApi({ conflict: () => true }));
    const view = await page(SpaceAccessPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/access", { routed: true });
    const reads = () => calls.filter((call) => call.method === "GET" && call.path.startsWith("/api/access/resources/space/provoz")).length;
    const before = reads();
    await row(view, "user:u-jana").get("[data-change-access]").trigger("click");
    await flushPromises();
    await row(view, "user:u-jana").get('[data-access-level-picker] [data-value="read"]').trigger("click");
    await row(view, "user:u-jana").get("[data-save-access]").trigger("click");
    await settle();
    expect(view.get("[data-access-conflict]").text()).toContain("mezitím změnil někdo jiný");
    expect(reads()).toBe(before + 1);
    expect(context.accessChanged).not.toHaveBeenCalled();
  });

  it("lists the exceptions, opens the same panel for one of them and a person's card limited to the space", async () => {
    fakeApi(accessApi());
    const view = await page(SpaceAccessPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/access/exceptions", { routed: true });
    await settle();
    const vault = view.get('[data-access-exception="vault:v-1"]');
    expect(vault.text()).toContain("Servery a síť");
    expect(vault.text()).toContain("2 vlastní přístupy");
    expect(vault.get("a").attributes("href")).toBe("#/spaces/provoz/settings/access/exceptions/vault/v-1");
    location.hash = "#/spaces/provoz/settings/access/exceptions/vault/v-1";
    await settle();
    expect(calls.some((call) => call.path === "/api/access/resources/vault/v-1?view=effective")).toBe(true);
    expect(view.get("[data-resource-access]").attributes("data-resource-access")).toBe("vault");
    location.hash = "#/spaces/provoz/settings/access/people/u-tomas";
    await settle();
    expect(calls.some((call) => call.path === "/api/access/principals/user/u-tomas?spaceId=provoz")).toBe(true);
    expect(view.get("[data-scoped-principal]").text()).toContain("Jen přístupy v prostoru Provoz");
  });

  it("does not load access for someone who does not manage the space", async () => {
    fakeApi(accessApi());
    spaces[0]!.role = "Reader";
    try {
      const view = await page(SpaceAccessPage, { spaceId: "provoz" }, "#/spaces/provoz/settings/access", { routed: true, isWikiAdmin: false });
      expect(view.text()).toContain("Přístupy nemůžete měnit");
      expect(calls.some((call) => call.path.startsWith("/api/access"))).toBe(false);
    } finally {
      spaces[0]!.role = "Admin";
    }
  });
});
