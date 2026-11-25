import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PageSharingView from "./PageSharingView.vue";
import { sharingApi, type ShareLink } from "../../sharing";
import { accessApi } from "../../accessApi";
import ResourceAccess from "../settings/access/ResourceAccess.vue";

vi.mock("../../sharing", async original => ({ ...await original<typeof import("../../sharing")>(),
  sharingApi: { pageAccess: vi.fn(), pageLinks: vi.fn(), accessHistory: vi.fn(), createLink: vi.fn(), revokeLink: vi.fn(), policy: vi.fn(),
    setAccessMode: vi.fn(), explainAccess: vi.fn(), accessCandidates: vi.fn() } }));
vi.mock("../../accessApi", () => ({ accessApi: { resource: vi.fn(), candidates: vi.fn(), grant: vi.fn(), revoke: vi.fn() } }));
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.clearAllMocks(); vi.unstubAllGlobals(); document.body.replaceChildren(); });
const person = { userId: "me", displayName: "Já", username: "me", role: "Admin", isWikiAdmin: false,
  canReadPublished: true, canReadDraft: true, canEdit: true, lastViewedAt: null, lastEditedAt: null, sources: [{ kind: "page" as const, name: "", role: "Admin", grantId: "g-me", version: "v1", effective: true }] };
const access = { spaceId: "space", spaceName: "Vývoj", isPublished: true, canManage: true, myAccess: person, items: [person], hasMore: false,
  pageTitle: "Stránka", mode: "inherit" as const, modeVersion: "m1", groups: [], shareLinks: { active: 0, nextExpiry: null },
  counts: { manage: 1, edit: 0, read: 0, hidden: 0, loseOnReset: 0, downgradeOnReset: 0, defaultGrants: 0, losing: [] } };
const link = (id: string, name: string, extra: Partial<ShareLink> = {}): ShareLink => ({ id, pageId: "page", pageTitle: "Stránka", name, revisionVersion: 7, createdByName: "Já",
  createdAt: "2026-09-13T10:00:00Z", expiresAt: "2026-09-30T10:00:00Z", revokedAt: null, lastOpenedAt: null, status: "active", ...extra });
function manager() {
  vi.mocked(sharingApi.pageAccess).mockResolvedValue(access);
  vi.mocked(sharingApi.policy).mockResolvedValue({ enabled: true, version: "v1" });
  vi.mocked(sharingApi.pageLinks).mockResolvedValue({ items: [], hasMore: false });
  vi.mocked(accessApi.resource).mockResolvedValue({ resource: { kind: "page", id: "page", title: "Stránka" }, canManage: true, automatic: [], hasMore: false,
    entries: [{ subject: { kind: "user", id: "me", name: "Já" }, level: "manage", sources: [{ via: "direct", label: "Přímý přístup", level: "manage", version: "v1", effective: true }] }] });
}
async function view(props: Record<string, unknown>) {
  wrapper = mount(PageSharingView, { attachTo: document.body, props: { pageId: "page", ...props } });
  await flushPromises();
  return wrapper;
}

it("is a page of its own with linked parts, a way back and the settings styles", async () => {
  manager();
  const page = await view({});
  expect(page.get("[data-page-sharing]").classes()).toContain("settings-ui");
  expect(page.get("h2").text()).toBe("Přístup ke stránce");
  expect(page.get(".settings-page-back").attributes("href")).toBe("#/pages/page");
  expect(page.findAll(".settings-tabs a").map(tab => [tab.text(), tab.attributes("href")])).toEqual([
    ["Přístup", "#/pages/page/access"], ["Anonymní odkazy", "#/pages/page/access/links"], ["Historie otevření", "#/pages/page/access/history"]]);
  expect(page.get('[data-tab="access"]').attributes("aria-current")).toBe("page");
  expect(page.find("dialog[open]").exists()).toBe(false);
  expect(sharingApi.pageAccess).toHaveBeenCalledWith("page");
  expect(page.get('[data-resource-access="page"] [data-access-entry="user:me"]').text()).toContain("Já");
  // Links and history load only when their part opens.
  expect(sharingApi.pageLinks).not.toHaveBeenCalled();
  expect(sharingApi.accessHistory).not.toHaveBeenCalled();
});

it("creates a link in a flow on the same screen and shows its URL only once", async () => {
  const setItem = vi.fn();
  vi.stubGlobal("localStorage", { getItem: vi.fn(() => null), setItem });
  manager();
  vi.mocked(sharingApi.createLink).mockResolvedValue({ link: link("l", "Klient"), url: "/s/sas_once" });
  const page = await view({ tab: "links" });
  await page.get("[data-new-link]").trigger("click"); await flushPromises();
  await page.get("input[name=name]").setValue("Klient"); await flushPromises();
  expect(page.get('[data-value="7"]').attributes("data-state")).toBe("checked");
  await page.get("[data-flow-next]").trigger("click"); await flushPromises();
  expect(sharingApi.createLink).toHaveBeenCalledWith("page", { name: "Klient", expiresAt: expect.any(String) });
  const secret = page.get("[data-issued-link]");
  await secret.findAll("button").find(button => button.text() === "Zobrazit")!.trigger("click");
  expect(secret.get("[data-one-time-value]").text()).toBe(`${location.origin}/s/sas_once`);
  expect(page.get('[data-share-link="l"]').text()).toContain("Klient");
  await secret.get("input[type=checkbox]").setValue(true);
  await secret.get("[data-one-time-done]").trigger("click"); await flushPromises();
  expect(page.find("[data-issued-link]").exists()).toBe(false);
  expect(page.text()).not.toContain("sas_once");
  expect(page.get("[data-result-banner]").text()).toContain("Odkaz „Klient“ je vytvořený");
  expect(setItem).not.toHaveBeenCalled();
});

it("offers link validity only up to the server's 30 days and says so before creating", async () => {
  manager();
  const page = await view({ tab: "links" });
  await page.get("[data-new-link]").trigger("click"); await flushPromises();
  await page.get("input[name=name]").setValue("Klient"); await flushPromises();
  expect(page.findAll(".option-pill").map(pill => pill.text())).toEqual(["1 den", "7 dní", "30 dní", "Vlastní datum"]);
  await page.get('[data-value="custom"]').trigger("click"); await flushPromises();
  const date = page.get("input[name=expiresAt]");
  expect(page.get("[data-link-limit]").text()).toBe("nejvýš 30 dní");
  expect(new Date(date.attributes("max")!).getTime()).toBeLessThanOrEqual(Date.now() + 30 * 86_400_000);
  await date.setValue(new Date(Date.now() + 40 * 86_400_000).toISOString().slice(0, 16)); await flushPromises();
  expect(page.get(".page-link-date [role=alert]").text()).toContain("nejvýš 30 dní od teď");
  expect(page.get("[data-flow-next]").attributes("disabled")).toBeDefined();
  expect(sharingApi.createLink).not.toHaveBeenCalled();
});

it("zneplatní a link only after the confirmation", async () => {
  manager();
  vi.mocked(sharingApi.pageLinks).mockResolvedValueOnce({ items: [link("l", "Pro zákazníka", { lastOpenedAt: "2026-09-25T08:00:00Z" })], hasMore: false })
    .mockResolvedValueOnce({ items: [link("l", "Pro zákazníka", { status: "revoked" })], hasMore: false });
  vi.mocked(sharingApi.revokeLink).mockResolvedValue(undefined);
  const page = await view({ tab: "links" });
  const row = page.get('[data-share-link="l"]');
  expect(row.text()).toContain("Anonymní odkaz · revize 7");
  expect(row.text()).toContain("poslední zaznamenané otevření");
  await row.get("[data-revoke-link]").trigger("click");
  expect(sharingApi.revokeLink).not.toHaveBeenCalled();
  expect(document.querySelector("dialog[open]")!.textContent).toContain("přestane platit hned");
  document.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-action]")!.click(); await flushPromises();
  expect(sharingApi.revokeLink).toHaveBeenCalledWith("page", "l");
  expect(page.get('[data-share-link="l"]').text()).toContain("Zneplatněný");
});

it("shows only a reader's own access and never loads management data", async () => {
  vi.mocked(sharingApi.pageAccess).mockResolvedValue({ ...access, canManage: false, items: [], mode: null, counts: null, groups: null, shareLinks: null,
    myAccess: { ...person, role: "Reader", canReadDraft: false, canEdit: false, sources: [{ kind: "space", name: "", role: "Reader", effective: true }] } });
  const page = await view({ tab: "links" });
  const own = page.get("[data-own-access]");
  expect(own.text()).toContain("Já má úroveň Čte");
  expect(own.text()).toContain("prostoru Vývoj");
  expect(own.text()).toContain("Můžete číst publikovanou verzi.");
  expect(page.find(".settings-tabs").exists()).toBe(false);
  expect(page.find("[data-page-access]").exists()).toBe(false);
  expect(sharingApi.pageLinks).not.toHaveBeenCalled();
  expect(sharingApi.accessHistory).not.toHaveBeenCalled();
  expect(sharingApi.accessCandidates).not.toHaveBeenCalled();
  expect(accessApi.resource).not.toHaveBeenCalled();
  expect(accessApi.candidates).not.toHaveBeenCalled();
});

it("explains disabled anonymous sharing and does not offer a new link", async () => {
  manager();
  vi.mocked(sharingApi.policy).mockResolvedValue({ enabled: false, version: "v1" });
  const page = await view({ tab: "links" });
  expect(page.get("[data-links-disabled]").text()).toContain("Anonymní sdílení je v prostoru vypnuté");
  expect(page.find("[data-new-link]").exists()).toBe(false);
});

it("ignores a late link response after navigation changes the page", async () => {
  let finishOld!: (value: { items: ShareLink[]; hasMore: boolean }) => void;
  manager();
  vi.mocked(sharingApi.pageLinks)
    .mockImplementationOnce(() => new Promise(resolve => { finishOld = resolve; }))
    .mockResolvedValueOnce({ items: [], hasMore: false });
  const page = await view({ pageId: "old", tab: "links" });
  await page.setProps({ pageId: "new" }); await flushPromises();
  finishOld({ items: [link("old-link", "Starý odkaz")], hasMore: false }); await flushPromises();
  expect(sharingApi.pageLinks).toHaveBeenLastCalledWith("new");
  expect(page.text()).not.toContain("Starý odkaz");
});

it("loads history only on its part and attributes anonymous use to its link", async () => {
  manager();
  vi.mocked(sharingApi.accessHistory).mockResolvedValue({ items: [{ id: "9007199254740993", action: "share.opened", occurredAt: "2026-09-25T08:00:00Z", actorId: null, actorName: null, shareLinkId: "link", shareLinkName: "Pro zákazníka", revisionVersion: 7 }], nextBeforeId: null });
  const page = await view({});
  expect(sharingApi.accessHistory).not.toHaveBeenCalled();
  await page.setProps({ tab: "history" }); await flushPromises();
  expect(sharingApi.accessHistory).toHaveBeenCalledWith("page", undefined);
  const timeline = page.get(".activity-timeline");
  expect(timeline.text()).toContain("Anonymní návštěvník otevřel anonymní odkaz „Pro zákazníka“");
  expect(timeline.text()).toContain("revize 7");
  expect(timeline.text()).not.toContain("Já");
});

it("ignores a late history response after leaving the history part", async () => {
  let finish!: (value: { items: any[]; nextBeforeId: string | null }) => void;
  manager();
  vi.mocked(sharingApi.accessHistory).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }))
    .mockResolvedValueOnce({ items: [], nextBeforeId: null });
  const page = await view({ tab: "history" });
  await page.setProps({ tab: "access" }); await flushPromises();
  finish({ items: [{ id: "1", action: "page.opened", occurredAt: "2026-09-25T08:00:00Z", actorId: "old", actorName: "Stará osoba", shareLinkId: null, shareLinkName: null, revisionVersion: 1 }], nextBeforeId: null }); await flushPromises();
  await page.setProps({ tab: "history" }); await flushPromises();
  expect(sharingApi.accessHistory).toHaveBeenCalledTimes(2);
  expect(page.text()).not.toContain("Stará osoba");
});

it("pages history with an exact string cursor and retries a failed page", async () => {
  manager();
  vi.mocked(sharingApi.accessHistory)
    .mockResolvedValueOnce({ items: [{ id: "9007199254740994", action: "page.opened", occurredAt: "2026-09-25T09:00:00Z", actorId: "me", actorName: "Já", shareLinkId: null, shareLinkName: null, revisionVersion: 8 }], nextBeforeId: "9007199254740993" })
    .mockRejectedValueOnce(new Error("Historie není dostupná"))
    .mockResolvedValueOnce({ items: [{ id: "9007199254740992", action: "share.revoked", occurredAt: "2026-09-24T09:00:00Z", actorId: null, actorName: null, shareLinkId: "link", shareLinkName: "Klient", revisionVersion: 7 }], nextBeforeId: null });
  const page = await view({ tab: "history" });
  await page.get("[data-load-older]").trigger("click"); await flushPromises();
  expect(page.get(".error-state").text()).toContain("Historie není dostupná");
  await page.get(".error-state button").trigger("click"); await flushPromises();
  expect(sharingApi.accessHistory).toHaveBeenLastCalledWith("page", "9007199254740993");
  const timeline = page.get(".activity-timeline").text();
  expect(timeline).toContain("Já otevřel(a) stránku");
  expect(timeline).toContain("Systém zneplatnil(a) anonymní odkaz „Klient“");
  expect(page.find("[data-load-older]").exists()).toBe(false);
});

it("says what failed and retries the load", async () => {
  vi.mocked(sharingApi.pageAccess).mockRejectedValueOnce(new Error("Server neodpovídá")).mockResolvedValueOnce(access);
  const page = await view({});
  expect(page.get(".error-state").text()).toContain("Server neodpovídá");
  await page.get(".error-state button").trigger("click"); await flushPromises();
  expect(page.find(".error-state").exists()).toBe(false);
  expect(page.find("[data-page-access]").exists()).toBe(true);
});

it("reloads the page state after a change in the access list, so the mode counts follow", async () => {
  manager();
  const page = await view({});
  expect(page.get("[data-access-summary]").text()).toBe("1");
  vi.mocked(sharingApi.pageAccess).mockResolvedValue({ ...access, counts: { ...access.counts, read: 2 } });
  page.getComponent(ResourceAccess).vm.$emit("changed"); await flushPromises();
  expect(sharingApi.pageAccess).toHaveBeenCalledTimes(2);
  expect(page.get("[data-access-summary]").text()).toBe("3");
  // The list itself stays in place: ResourceAccess reloads its own part.
  expect(accessApi.resource).toHaveBeenCalledTimes(1);
});
