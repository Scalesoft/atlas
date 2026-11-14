import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PageAccessPanel from "./PageAccessPanel.vue";
import { sharingApi, type PageAccess } from "../../sharing";
import { accessApi } from "../../accessApi";
import type { ResourceAccess } from "../settings/access/accessModel";
import { accessPaths, explanationLine, lastActivity, pageRoleLabel, sourceLabel } from "./pageAccessText";

vi.mock("../../sharing", async original => ({ ...await original<typeof import("../../sharing")>(),
  sharingApi: { pageAccess: vi.fn(), pageLinks: vi.fn(), accessHistory: vi.fn(), createLink: vi.fn(), revokeLink: vi.fn(), policy: vi.fn(),
    setAccessMode: vi.fn(), explainAccess: vi.fn(), accessCandidates: vi.fn() } }));
vi.mock("../../accessApi", () => ({ accessApi: { resource: vi.fn(), candidates: vi.fn(), grant: vi.fn(), revoke: vi.fn() } }));

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.clearAllMocks(); vi.useRealTimers(); document.body.replaceChildren(); });

const thisYear = new Date().getFullYear();
const me = { userId: "eva", displayName: "Eva", username: "eva", role: "Admin", isWikiAdmin: false, canReadPublished: true, canReadDraft: true, canEdit: true,
  lastViewedAt: null, lastEditedAt: null, sources: [{ kind: "spaceAdmin" as const, name: "", role: "Admin", effective: true }] };
function state(mode: "inherit" | "custom", extra: Partial<PageAccess> = {}): PageAccess {
  return {
    spaceId: "space", spaceName: "Vývoj", isPublished: true, canManage: true, myAccess: me, hasMore: false, pageTitle: "Plán", mode, modeVersion: "m1", items: [me], groups: [],
    counts: { manage: 2, edit: 1, read: 2, hidden: mode === "custom" ? 7 : 0, loseOnReset: 2, downgradeOnReset: 1, defaultGrants: 3, losing: ["Jan", "Ola"] },
    shareLinks: { active: 2, nextExpiry: `${thisYear}-10-10T10:00:00Z` }, ...extra,
  };
}
/** GET /api/access/resources/page/page: a group and Jan have grants on the page, Ola comes through the group, Petr from his space role. */
function pageResource(): ResourceAccess {
  return {
    resource: { kind: "page", id: "page", title: "Plán", spaceId: "space", spaceName: "Vývoj" },
    canManage: true,
    automatic: [
      { label: "Správci prostoru", detail: "Spravují všechny stránky prostoru Vývoj", level: "manage", count: 1 },
      { label: "Správci wiki", detail: "Ve všech prostorech", level: "manage", count: 1 },
    ],
    entries: [
      { subject: { kind: "group", id: "grp", name: "Podpora", memberCount: 4 }, level: "read", sources: [{ via: "direct", label: "Přímý přístup", level: "read", version: "v-grp", effective: true }] },
      { subject: { kind: "user", id: "jan", name: "Jan", detail: "jan" }, level: "write", sources: [{ via: "direct", label: "Přímý přístup", level: "write", version: "v-jan", expiresAt: `${thisYear}-12-31T12:00:00Z`, effective: true }] },
      { subject: { kind: "user", id: "ola", name: "Ola", detail: "ola" }, level: "read", sources: [{ via: "group", label: "Podpora", groupId: "grp", level: "read", effective: true }] },
      { subject: { kind: "user", id: "petr", name: "Petr", detail: "petr" }, level: "read", sources: [{ via: "derived", label: "Z role v prostoru", level: "read", effective: true }] },
    ],
    hasMore: false,
  };
}
beforeEach(() => {
  vi.mocked(accessApi.resource).mockImplementation(async () => pageResource());
});
async function mountPanel(access: PageAccess) {
  wrapper = mount(PageAccessPanel, { attachTo: document.body, props: { pageId: "page", access, linksHref: "#/pages/page/access/links" } });
  await flushPromises();
  return wrapper;
}
const entry = (view: VueWrapper, key: string) => view.get(`[data-access-entry="${key}"]`);
const dialog = () => document.querySelector<HTMLElement>("dialog[open]");
const confirm = () => document.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-action]")!.click();

it("shows the mode with its summary and who reaches the page, why, with the level on the staircase", async () => {
  const view = await mountPanel(state("inherit"));
  expect(view.text()).toContain("5 lidí stránku vidí · spravovat: 2 · upravovat: 1 · jen číst: 2");
  expect(view.get("[data-access-mode] [data-value=inherit]").attributes("data-selected")).toBeDefined();
  expect(accessApi.resource).toHaveBeenCalledWith("page", "page", "effective");
  expect(view.get("[data-resource-access]").attributes("data-resource-access")).toBe("page");
  expect(entry(view, "automatic:0").text()).toContain("Správci prostoru");
  const group = entry(view, "group:grp");
  expect(group.text()).toContain("Skupina · 4 členové · Přímý přístup");
  expect(group.get(".level-badge").attributes("data-level")).toBe("read");
  expect(entry(view, "user:jan").text()).toContain("Přístup končí 31. 12.");
  expect(entry(view, "user:jan").get(".level-badge-label").text()).toBe("Upravuje");
  expect(entry(view, "user:ola").text()).toContain("Přes skupinu Podpora");
  expect(entry(view, "user:petr").text()).toContain("Z role v prostoru");
  expect(view.find("[data-hidden-members]").exists()).toBe(false);
  expect(view.text()).toContain("Čte → Upravuje → Spravuje: každá úroveň zahrnuje nižší. Při více cestách platí nejvyšší. Podstránky přebírají tento přístup při vytvoření.");
  expect(view.get("[data-share-summary]").text()).toContain("Anonymní odkazy: 2 aktivní, nejbližší vyprší 10. 10.");
  expect(view.get("[data-share-summary] a").attributes("href")).toBe("#/pages/page/access/links");
  expect(view.text()).not.toMatch(/Reader|Writer|Editor|Čtenář|Autor/);
});

it("offers Změnit and Odebrat only for grants set on the page", async () => {
  const view = await mountPanel(state("inherit"));
  expect(entry(view, "user:jan").find("[data-change-access]").exists()).toBe(true);
  expect(entry(view, "group:grp").find("[data-revoke-access]").exists()).toBe(true);
  // A right through a group is changed on the group: the row names it and offers no action of its own here (the page
  // view has no group card to open); automatic and space-role rows have no action either.
  expect(entry(view, "user:ola").find("[data-change-access]").exists()).toBe(false);
  expect(entry(view, "user:ola").find("[data-open-group]").exists()).toBe(false);
  expect(entry(view, "user:ola").text()).toContain("Přes skupinu Podpora");
  for (const key of ["automatic:0", "automatic:1", "user:petr"]) expect(entry(view, key).find("[data-change-access], [data-revoke-access]").exists()).toBe(false);
});

it("shows the struck-through row of space members hidden by own access", async () => {
  const view = await mountPanel(state("custom"));
  expect(view.get("[data-access-mode] [data-value=custom]").attributes("data-selected")).toBeDefined();
  const row = view.get("[data-hidden-members]");
  expect(row.find("s").text()).toBe("Ostatní členové prostoru (7)");
  expect(row.text()).toContain("Nevidí: stránka má vlastní přístup");
});

it("turns own access on only after the impact confirmation, with the CAS version, and reloads the list", async () => {
  vi.mocked(sharingApi.setAccessMode).mockResolvedValue({ mode: "custom", version: "m2" });
  const view = await mountPanel(state("inherit"));
  await view.get('[data-access-mode] [data-value="custom"] [role="radio"]').trigger("click");
  expect(sharingApi.setAccessMode).not.toHaveBeenCalled();
  expect(dialog()!.textContent).toContain("3 výchozích přiřazení prostoru se zkopíruje přímo na stránku");
  confirm(); await flushPromises();
  expect(sharingApi.setAccessMode).toHaveBeenCalledWith("page", { ownAccess: true, expectedVersion: "m1" });
  expect(view.emitted("changed")).toHaveLength(1);
  expect(accessApi.resource).toHaveBeenCalledTimes(2);
  expect(dialog()).toBeNull();
  expect(view.get("[data-result-banner]").text()).toContain("Stránka má vlastní přístup.");
});

it("names who gains and loses before returning to the space defaults, and keeps a failure in the confirmation", async () => {
  vi.mocked(sharingApi.setAccessMode).mockRejectedValue(new Error("Údaj se mezitím změnil. Obnovte seznam a změnu zopakujte."));
  const view = await mountPanel(state("custom"));
  await view.get('[data-access-mode] [data-value="inherit"] [role="radio"]').trigger("click");
  const impact = dialog()!.textContent!;
  expect(impact).toContain("Získají přístup: 7 lidí");
  expect(impact).toContain("Ztratí přístup: 2 lidé");
  expect(impact).toContain("Jan, Ola");
  expect(impact).toContain("Přijdou o část oprávnění: 1 člověk");
  confirm(); await flushPromises();
  expect(sharingApi.setAccessMode).toHaveBeenCalledWith("page", { ownAccess: false, expectedVersion: "m1" });
  expect(dialog()!.querySelector("[role=alert]")!.textContent).toContain("Údaj se mezitím změnil");
  expect(view.emitted("changed")).toBeUndefined();
});

it("adds a grant through GrantFlow in place of the list, with candidates from the server as they are typed", async () => {
  vi.mocked(accessApi.candidates).mockImplementation(async (kind) => kind === "group" ? [] : [{ id: "iva", kind: "user", name: "Iva", detail: "iva" }]);
  vi.mocked(accessApi.grant).mockResolvedValue(undefined);
  const view = await mountPanel(state("custom"));
  await view.get("[data-add-access]").trigger("click"); await flushPromises();
  expect(view.find("[data-add-access]").exists()).toBe(false);
  const flow = () => view.get("[data-resource-grant-flow]");
  await flow().get('[data-value="user"] [role="radio"]').trigger("click"); await flushPromises();
  // Nobody is listed before typing: the directory is never sent whole.
  expect(accessApi.candidates).not.toHaveBeenCalled();
  await flow().get("input[type=search]").setValue("iv");
  await new Promise(resolve => setTimeout(resolve, 300)); await flushPromises();
  expect(accessApi.candidates).toHaveBeenCalledWith("user", "iv");
  await flow().get('.searchable-list [data-value="iva"]').trigger("click"); await flushPromises();
  expect(flow().findAll("[data-access-level-picker] .level-step-title").map(item => item.text())).toEqual(["Čte", "Upravuje", "Spravuje"]);
  await flow().get('[data-access-level-picker] [data-value="write"]').trigger("click"); await flushPromises();
  await flow().get('input[name="expiresAt"]').setValue("2030-12-31T12:00"); await flushPromises();
  await flow().get("[data-flow-next]").trigger("click"); await flushPromises();
  expect(accessApi.grant).toHaveBeenCalledWith("page", "page",
    { subjectKind: "user", subjectId: "iva", level: "write", startAt: null, expiresAt: new Date("2030-12-31T12:00").toISOString(), expectedVersion: null }, null);
  expect(view.emitted("changed")).toHaveLength(1);
  expect(view.get("[data-result-banner]").text()).toContain("Iva má přístup Upravuje · stránka Plán.");
  expect(view.find("[data-add-access]").exists()).toBe(true);
});

it("changes a grant in place with its version and removes one after a confirmation", async () => {
  vi.mocked(accessApi.grant).mockResolvedValue(undefined);
  vi.mocked(accessApi.revoke).mockResolvedValue(undefined);
  const view = await mountPanel(state("custom"));
  await entry(view, "user:jan").get("[data-change-access]").trigger("click");
  const editor = entry(view, "user:jan").get("[data-access-editor]");
  expect(editor.get('[data-value="write"]').attributes("data-level")).toBe("selected");
  await editor.get('[data-value="read"]').trigger("click");
  await editor.get("[data-save-access]").trigger("click"); await flushPromises();
  // Below Spravuje a page grant needs no fresh sign-in.
  expect(accessApi.grant).toHaveBeenLastCalledWith("page", "page",
    { subjectKind: "user", subjectId: "jan", level: "read", startAt: null, expiresAt: `${thisYear}-12-31T12:00:00Z`, expectedVersion: "v-jan" }, null);
  expect(view.get("[data-result-banner]").text()).toContain("Přístup pro Jan je uložený: Čte.");
  await entry(view, "group:grp").get("[data-revoke-access]").trigger("click"); await flushPromises();
  expect(accessApi.revoke).not.toHaveBeenCalled();
  expect(dialog()!.textContent).toContain("Podpora přijde o přímý přístup Čte · stránka Plán.");
  confirm(); await flushPromises();
  expect(accessApi.revoke).toHaveBeenCalledWith("page", "page", "group", "grp", "v-grp", null);
  expect(view.emitted("changed")).toHaveLength(2);
});

it("explains the page for another person with the policy decision and its reason", async () => {
  const view = await mountPanel(state("custom"));
  vi.useFakeTimers();
  vi.mocked(sharingApi.accessCandidates).mockResolvedValue([{ id: "petr", kind: "user", name: "Petr", detail: "petr" }]);
  vi.mocked(sharingApi.explainAccess).mockResolvedValue({ userId: "petr", displayName: "Petr", canView: false, canEdit: false, role: null, sources: [], denial: "ownAccess" });
  await view.get("[data-view-as] .settings-section-header").trigger("click");
  await view.get("[data-view-as] input[type=search]").setValue("pe");
  await vi.advanceTimersByTimeAsync(300); await flushPromises();
  expect(sharingApi.accessCandidates).toHaveBeenCalledWith("page", "user", "pe");
  await view.get('[data-view-as] [data-value="petr"]').trigger("click"); await flushPromises();
  expect(sharingApi.explainAccess).toHaveBeenCalledWith("page", "petr");
  expect(view.get("[data-view-as-result]").text()).toBe("Petr nevidí, nemůže upravovat — stránka má vlastní přístup");
  expect(view.get("[data-view-as] .access-summary").text()).toBe("Petr k cíli stránka Plán přístup nemá.");
});

it("filters the list by name in the filter line", async () => {
  const view = await mountPanel(state("inherit"));
  await view.get(".resource-access-filters input[type=search]").setValue("ol");
  expect(view.find('[data-access-entry="user:ola"]').exists()).toBe(true);
  expect(view.find('[data-access-entry="user:jan"]').exists()).toBe(false);
  expect(view.find('[data-access-entry="group:grp"]').exists()).toBe(false);
});

it("explains Hub global authority with its verification deadline instead of permanent local authority", () => {
  const now = new Date(thisYear, 5, 1, 12, 0);
  const source = { kind: "hubGlobal" as const, name: "", role: "Admin", effective: true, expiresAt: new Date(thisYear, 5, 1, 12, 5).toISOString() };
  expect(sourceLabel(source, now)).toBe("Správce wiki přes Hub · ověřeno do 1. 6. 12:05");
  const [path] = accessPaths([source], { spaceName: "Vývoj", pageTitle: "Plán" }, now);
  expect(path).toMatchObject({ via: "role", label: "správce wiki přes Hub", scope: "všech stránkách wiki", locked: true });
  expect(path?.until).toBe("1. 6. 12:05");
  expect(sourceLabel({ ...source, effective: false }, now)).toContain("ověření vypršelo");
});

it("formats reasons, dates, paths and decisions in the words of roles.ts", () => {
  const now = new Date(`${thisYear}-06-01T00:00:00Z`);
  expect(pageRoleLabel("Writer")).toBe("Upravuje");
  expect(pageRoleLabel("Admin")).toBe("Spravuje");
  expect(lastActivity({ lastViewedAt: `${thisYear + 1}-01-02T12:00:00Z`, lastEditedAt: null }, now)).toBe(`zobrazeno 2. 1. ${thisYear + 1} · upraveno nikdy`);
  expect(sourceLabel({ kind: "page", name: "", role: "Reader", startAt: `${thisYear}-07-01T12:00:00Z`, effective: false }, now)).toBe("Přímo na stránce · od 1. 7.");
  expect(sourceLabel({ kind: "space", name: "", role: "Reader", effective: true }, now)).toBe("Výchozí prostoru (Čte)");
  expect(sourceLabel({ kind: "spaceAdmin", name: "Vedení", role: "Admin", groupId: "g", effective: true }, now)).toBe("Správce prostoru přes skupinu Vedení · vždy");
  expect(lastActivity({ lastViewedAt: null, lastEditedAt: null })).toBe("zobrazeno nikdy · upraveno nikdy");
  expect(accessPaths([{ kind: "page", name: "Podpora", role: "Writer", groupId: "g", effective: true }, { kind: "space", name: "", role: "Reader", effective: false }], { spaceName: "Vývoj", pageTitle: "Plán" }, now))
    .toEqual([{ via: "group", label: "Podpora", scope: "stránce Plán", level: "Writer", until: undefined }]);
  expect(explanationLine({ userId: "u", displayName: "U", canView: true, canEdit: true, role: "Writer", denial: null,
    sources: [{ kind: "page", name: "", role: "Writer", effective: true }] }, now)).toBe("vidí, může upravovat — Přímo na stránce");
});

it("keeps a finite space administrator path bounded by its expiration", () => {
  const now = new Date(thisYear, 5, 1, 12, 0);
  const source = { kind: "spaceAdmin" as const, name: "Vývoj", groupId: "hub-group", role: "Admin", expiresAt: new Date(thisYear, 5, 1, 12, 5).toISOString() };
  expect(sourceLabel(source, now)).toBe("Správce prostoru přes skupinu Vývoj · do 1. 6. 12:05");
  expect(accessPaths([source], { spaceName: "Vývoj", pageTitle: "Plán" }, now)[0]?.until).toBe("1. 6. 12:05");
  expect(sourceLabel(source, new Date(thisYear, 5, 1, 12, 6))).toContain("platnost skončila");
});
