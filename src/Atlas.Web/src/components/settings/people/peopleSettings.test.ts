import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, onMounted, provide, watch } from "vue";
import { clearCsrf } from "../../../api";
import { reauthWindow } from "../../../protection";
import type { DirectoryUser, Group, Member } from "../../../administration";
import type { AccessAuditEvent } from "../auditActivity";
import { createDirtyGuard } from "../dirtyGuard";
import { provideReauthEnvironment } from "../useReauth";
import type { AdminApiToken, PrincipalAccess } from "../access/accessModel";
import { fakeApi as stubApi, problem, provideSettingsSurface, testContext } from "../general/testing";
import AccessHistoryPage from "../global/AccessHistoryPage.vue";
import AccountsPage from "./AccountsPage.vue";
import PersonPeek from "./PersonPeek.vue";
import { useWorkspacePanel } from "../../../composables/useWorkspacePanel";
import { createDirectorySummary, directorySummaryKey } from "./directorySummary";
import ApiTokensPage from "./ApiTokensPage.vue";
import GroupsPage from "./GroupsPage.vue";

const user = (id: string, extra: Partial<DirectoryUser> = {}): DirectoryUser => ({
  id, username: id.replace("u-", ""), displayName: id.replace("u-", "").replace(/^./, (c) => c.toUpperCase()), isActive: true, isWikiAdmin: false, version: `${id}-v1`,
  hasBuiltinCredential: true, source: "builtin", credentialState: "active", ...extra,
});
const event = (id: string, extra: Partial<AccessAuditEvent> = {}): AccessAuditEvent => ({
  id, occurredAt: "2026-09-26T10:00:00Z", actorId: "u-admin", actorName: "Admin", action: "account.updated", userId: "u-jana", userName: "Jana", groupId: null, groupName: null,
  spaceId: null, spaceName: null, role: null, isActive: true, isWikiAdmin: false, canIssueTokens: null, ...extra,
});
const token = (id: string, extra: Partial<AdminApiToken> = {}): AdminApiToken => ({
  id, userId: "u-jana", userName: "Jana Nováková", name: `Skript ${id}`, scopes: ["read"], createdAt: "2026-09-01T10:00:00Z", expiresAt: "2030-01-01T10:00:00Z", lastUsedAt: null, revokedAt: null, ...extra,
});
function janaAccess(): PrincipalAccess {
  return {
    principal: { kind: "user", id: "u-jana", name: "Jana Nováková", detail: "jana", isActive: true, isWikiAdmin: false, lastSignInAt: "2026-09-29T07:12:00Z", groups: [{ id: "g-ops", name: "Provoz tým" }] },
    scope: "wiki",
    canManage: true,
    entries: [
      { kind: "space", resourceId: "provoz", title: "Provoz", spaceId: "provoz", spaceName: "Provoz", level: "write", sources: [{ via: "direct", label: "", level: "write", grantId: "gr-1", version: "v-provoz", startAt: null, expiresAt: null, effective: true }], note: "41 stránek bez vlastního přístupu" },
      { kind: "vault", resourceId: "v-1", title: "Servery a síť", spaceId: "provoz", spaceName: "Provoz", level: "read", sources: [{ via: "group", label: "Provoz tým", groupId: "g-ops", level: "read", effective: true }] },
    ],
  };
}

/** What the server's lists do: by display name, `?q=` contains over name or username, `?id=` one account, `?offset=` the page from there, 1000 at most. */
function page<T extends { id: string; username: string; displayName: string }>(rows: T[], url: URL) {
  const text = (url.searchParams.get("q") ?? "").toLowerCase();
  const id = url.searchParams.get("id");
  return [...rows]
    .sort((a, b) => (a.displayName < b.displayName ? -1 : a.displayName > b.displayName ? 1 : 0))
    .filter((row) => (!text || `${row.displayName} ${row.username}`.toLowerCase().includes(text)) && (!id || row.id === id))
    .slice(Number(url.searchParams.get("offset") ?? 0), Number(url.searchParams.get("offset") ?? 0) + 1000);
}

// A small in-memory administration and access API behind a fake fetch.
interface State {
  users: DirectoryUser[];
  groups: Group[];
  members: Record<string, Member[]>;
  api: Record<string, { canIssueTokens: boolean; version: string | null; level: string }>;
  levelsReview: boolean;
  audit: { items: AccessAuditEvent[]; hasMore: boolean; nextBeforeId: string | null }[];
  principals: Record<string, PrincipalAccess>;
  tokens: AdminApiToken[];
  conflict?: string;
  reauth?: string;
}
let state: State;
let calls: { method: string; path: string; body: unknown; proof: string | null }[] = [];
function fakeApi() {
  calls = [];
  stubApi((method, path, body: any, { url, headers }) => {
    calls.push({ method, path: `${path}${url.search}`, body, proof: headers.get("X-Atlas-Reauth") });
    if (state.conflict && method !== "GET" && path.startsWith(state.conflict)) return problem(409, "Změněno");
    if (state.reauth && method !== "GET" && path.startsWith(state.reauth) && !headers.get("X-Atlas-Reauth")) return problem(403, "Potvrďte změnu novým přihlášením.", "reauth_required");
    if (path === "/admin/users" && method === "GET") return Response.json(page(state.users, url));
    if (path === "/admin/users/summary") {
      const pending = state.users.filter((item) => item.isActive && item.credentialState === "pending").length;
      return Response.json({ total: state.users.length, pending, deactivated: state.users.filter((item) => !item.isActive).length, admins: state.users.filter((item) => item.isWikiAdmin).length });
    }
    if (path === "/admin/users" && method === "POST") {
      const created = user(`u-${body.username}`, { displayName: body.displayName, isWikiAdmin: body.isWikiAdmin, credentialState: "pending" });
      state.users.push(created);
      return Response.json({ user: created, token: "ACT-1234-CODE", expiresAt: "2026-10-01T10:00:00Z", purpose: "invitation" }, { status: 201 });
    }
    let match = path.match(/^\/admin\/users\/([^/]+)$/);
    if (match && method === "PUT") {
      const saved = { ...state.users.find((item) => item.id === match![1])!, displayName: body.displayName, isActive: body.isActive, isWikiAdmin: body.isWikiAdmin, version: "saved-v2" };
      state.users = state.users.map((item) => (item.id === saved.id ? saved : item));
      return Response.json(saved);
    }
    match = path.match(/^\/admin\/users\/([^/]+)\/reset-password$/);
    if (match) return Response.json({ token: "RESET-5678-CODE", expiresAt: "2026-10-01T10:00:00Z", purpose: "reset" });
    match = path.match(/^\/admin\/users\/([^/]+)\/api-access$/);
    if (match && method === "GET") return Response.json(state.api[match[1]!] ?? { canIssueTokens: false, version: null, level: "none" });
    if (match && method === "PUT") return Response.json((state.api[match[1]!] = { canIssueTokens: body.level !== "none", version: "api-v2", level: body.level }));
    if (/^\/admin\/users\/[^/]+\/ai-policy$/.test(path)) return Response.json({ enabled: method === "PUT" ? body.enabled : null, searchAnswerDailyLimit: method === "PUT" ? body.searchAnswerDailyLimit : null, chatAnswerDailyLimit: method === "PUT" ? body.chatAnswerDailyLimit : null, pageSummaryDailyLimit: method === "PUT" ? body.pageSummaryDailyLimit : null, toolsEnabled: method === "PUT" ? body.toolsEnabled : null, agentDailyLimit: method === "PUT" ? body.agentDailyLimit : null, version: method === "PUT" ? "ai-v2" : null, resolved: { enabled: true, limits: [] } });
    if (path === "/admin/groups" && method === "GET") return Response.json(state.groups);
    if (path === "/admin/groups" && method === "POST") {
      const created: Group = { id: "g-new", name: body.name, version: "g-new-v1", source: "local" };
      state.groups.push(created);
      state.members[created.id] = [];
      return Response.json(created);
    }
    match = path.match(/^\/admin\/groups\/([^/]+)$/);
    if (match && method === "PUT") {
      const saved = { ...state.groups.find((item) => item.id === match![1])!, name: body.name, version: "g-v2" };
      state.groups = state.groups.map((item) => (item.id === saved.id ? saved : item));
      return Response.json(saved);
    }
    match = path.match(/^\/admin\/groups\/([^/]+)\/members$/);
    if (match) return Response.json(page(state.members[match[1]!] ?? [], url));
    match = path.match(/^\/admin\/groups\/([^/]+)\/members\/([^/]+)$/);
    if (match) {
      const [, groupId, userId] = match;
      const list = state.members[groupId!] ?? [];
      const account = state.users.find((item) => item.id === userId)!;
      state.members[groupId!] = body.isMember ? [...list, { id: account.id, username: account.username, displayName: account.displayName, isActive: account.isActive, version: "m-v1" }] : list.filter((item) => item.id !== userId);
      return new Response(null, { status: 204 });
    }
    if (path === "/admin/access-audit") {
      const page = url.searchParams.get("beforeId") ? state.audit[1] ?? state.audit[0]! : state.audit[0]!;
      return Response.json({ ...page, coverageStartedAt: "2026-09-01T00:00:00Z" });
    }
    if (path === "/admin/activity-summary") {
      return Response.json({
        from: url.searchParams.get("days") ? "2026-08-30T10:00:00Z" : null, revisions: 42, aiAssistedRevisions: 5,
        revisionChannels: [{ key: "web", count: 30 }, { key: "mcp", count: 12 }], topEditors: [{ userId: "u-jana", name: "Jana Nováková", count: 25 }, { userId: "u-petr", name: "Petr Svoboda", count: 17 }],
        pageOpens: [{ key: "web", count: 300 }, { key: "mcp", count: 40 }], toolCalls: 19, toolFailures: 3, toolChannels: [{ key: "mcp", count: 14 }, { key: "assistant", count: 5 }],
        topTools: [{ channel: "mcp", operation: "atlas_update_section", calls: 10, failures: 3 }], topToolUsers: [{ userId: "u-petr", name: "Petr Svoboda", count: 14 }],
      });
    }
    if (path === "/admin/api-tokens") return Response.json(state.tokens);
    if (path === "/admin/api-tokens/summary") return Response.json({ levelsReview: state.levelsReview });
    if (path === "/admin/api-tokens/levels-review" && method === "POST") return Response.json({ levelsReview: (state.levelsReview = false) });
    match = path.match(/^\/admin\/api-tokens\/([^/]+)$/);
    if (match && method === "DELETE") {
      state.tokens = state.tokens.map((item) => (item.id === match![1] ? { ...item, revokedAt: "2026-09-29T10:00:00Z" } : item));
      return new Response(null, { status: 204 });
    }
    if (path === "/access/kinds") return Response.json([]);
    match = path.match(/^\/access\/principals\/(user|group)\/([^/]+)$/);
    if (match) {
      const found = state.principals[match[2]!];
      if (found) return Response.json(found);
      const group = state.groups.find((item) => item.id === match![2]);
      return Response.json({ principal: { kind: match[1], id: match[2], name: group?.name ?? match[2], detail: "1 člen", groups: [] }, scope: "wiki", entries: [], canManage: true });
    }
    match = path.match(/^\/access\/principals\/user\/([^/]+)\/revoke-all$/);
    if (match && method === "GET") return Response.json({ grants: 3, groups: 1, tokens: 2 });
    if (match && method === "POST") return new Response(null, { status: 204 });
    if (path === "/access/resources" && method === "GET") {
      const q = url.searchParams.get("q") ?? "";
      return Response.json([{ kind: "space", id: "eng", title: "Engineering", note: "128 stránek" }, { kind: "space", id: "provoz", title: "Provoz" }].filter((item) => item.title.toLowerCase().includes(q.toLowerCase())));
    }
    match = path.match(/^\/access\/resources\/([^/]+)\/([^/]+)\/grants(?:\/(user|group)\/([^/]+))?$/);
    if (match) return new Response(null, { status: 204 });
    return problem(404, `unexpected ${method} ${path}`);
  });
}

let flashText = () => "";
let wrapper: VueWrapper | undefined;
async function settle() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await flushPromises();
}
/** The pages send a search 250 ms after the typing stops. */
async function typingPause() {
  await new Promise((resolve) => setTimeout(resolve, 300));
  await settle();
}
/** `summary`: as inside Nastavení wiki, whose directory summary (the rail's counts) the pages share. */
async function open(hash: string, isWikiAdmin = true, summary = false) {
  history.replaceState(null, "", `/${hash}`);
  // What GlobalSettings and SettingsHost do for these pages: the providers and the section switch.
  const Harness = defineComponent({
    setup() {
      const guard = createDirtyGuard();
      const { router, flash } = provideSettingsSurface({ context: testContext({ isWikiAdmin }), guard });
      if (summary) { const directory = createDirectorySummary(); provide(directorySummaryKey, directory); onMounted(() => void directory.load()); }
      // A local account signing in again: the password gives the proof the change carries.
      provideReauthEnvironment({ method: "local", supported: true, get windowUntil() { return reauthWindow.until.value; }, verifyPassword: async () => ({ ok: true, proofId: "proof-1" }), verifyHub: async () => false });
      flashText = () => flash.message.value?.text ?? "";
      watch(() => router.route.value.path, () => guard.release());
      return () => {
        const segments = router.route.value.segments;
        const section = segments.slice(0, 2).join("/");
        const page =
          section === "people/accounts" ? h(AccountsPage, { sub: segments[2], part: segments[3] })
          : section === "people/groups" ? h(GroupsPage, { sub: segments[2], part: segments[3] })
          : section === "people/tokens" ? h(ApiTokensPage)
          : section === "security/audit" ? h(AccessHistoryPage) : null;
        return h("div", { class: "settings-ui" }, [page]);
      };
    },
  });
  wrapper = mount(Harness, { attachTo: document.body });
  await settle();
  return wrapper;
}
async function go(hash: string) {
  location.hash = hash;
  await settle();
}
async function click(selector: string) {
  await wrapper!.get(selector).trigger("click");
  await settle();
}
// FilterMenu items are portalled into the document once the menu opens.
async function pick(trigger: string, value: string) {
  await click(trigger);
  await new Promise((resolve) => setTimeout(resolve, 40));
  document.querySelector<HTMLElement>(`.filter-menu-panel [data-value="${value}"]`)!.click();
  await settle();
}
// Every ConfirmDialog of a page is in the document; only the open one counts.
const bodyButton = (selector: string) => document.querySelector(`dialog[open] ${selector}`) as HTMLButtonElement;
async function reauth(password = "heslo") {
  const input = document.querySelector<HTMLInputElement>('dialog[open] input[type="password"]')!;
  input.value = password;
  input.dispatchEvent(new Event("input"));
  await settle();
  bodyButton("[data-confirm-reauth]").click();
  await settle();
}

beforeEach(() => {
  clearCsrf();
  reauthWindow.close();
  state = {
    users: [
      user("u-admin", { displayName: "Admin", isWikiAdmin: true }),
      user("u-jana", { displayName: "Jana Nováková" }),
      user("u-petr", { displayName: "Petr Svoboda", credentialState: "pending" }),
      user("u-eva", { displayName: "Eva Malá", isActive: false }),
      user("u-hub", { displayName: "Hub User", source: "external", hasBuiltinCredential: false, credentialState: "none" }),
    ],
    groups: [{ id: "g-ops", name: "Provoz tým", version: "g-ops-v1", source: "local" }],
    members: { "g-ops": [{ id: "u-jana", username: "jana", displayName: "Jana Nováková", isActive: true, version: "m-jana" }] },
    api: { "u-jana": { canIssueTokens: true, version: "api-v1", level: "write" } },
    levelsReview: false,
    audit: [{ items: [event("3"), event("2", { action: "membership.added", groupId: "g-ops", groupName: "Provoz tým", isActive: null, isWikiAdmin: null }), event("1", { userId: "u-petr", userName: "Petr" })], hasMore: false, nextBeforeId: null }],
    principals: { "u-jana": janaAccess() },
    tokens: [token("t-1", { lastUsedAt: "2026-09-29T06:02:00Z" }), token("t-2", { userId: "u-petr", userName: "Petr Svoboda", scopes: ["read", "write"], channels: ["rest", "mcp"] }), token("t-3", { revokedAt: "2026-09-10T10:00:00Z" })],
  };
  fakeApi();
});
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

describe("people", () => {
  it("lists people with badges only where attention is needed, filters in one line in the URL, and never reads for a non-administrator", async () => {
    const view = await open("#/settings/people/accounts");
    expect(view.findAll("[data-account]").map((row) => row.attributes("data-account"))).toEqual(["u-admin", "u-eva", "u-hub", "u-jana", "u-petr"]);
    const petr = view.get('[data-account="u-petr"]');
    expect(petr.get("[data-account-status]").text()).toBe("Čeká na aktivaci");
    expect(petr.text()).not.toContain("Aktivní");
    // An active account carries no state badge: nothing to attend to.
    expect(view.find('[data-account="u-jana"] [data-account-status]').exists()).toBe(false);
    expect(view.get('[data-account="u-eva"] [data-account-status]').text()).toBe("Deaktivovaný");
    expect(view.get('[data-account="u-hub"]').text()).toContain("Externí přihlášení");
    expect(view.get('[data-account="u-admin"]').text()).toContain("Správce wiki");
    expect(view.get('[data-account="u-admin"]').text()).toContain("Vy");
    await pick("[data-account-filter]", "pending");
    expect(location.hash).toBe("#/settings/people/accounts?stav=pending");
    expect(view.findAll("[data-account]").map((row) => row.attributes("data-account"))).toEqual(["u-petr"]);
    await pick("[data-account-filter]", "all");
    await view.get("[data-account-search]").setValue("nov");
    await typingPause();
    expect(calls.some((call) => call.method === "GET" && call.path === "/admin/users?q=nov")).toBe(true);
    expect(view.findAll("[data-account]").map((row) => row.attributes("data-account"))).toEqual(["u-jana"]);
    expect(view.get("[data-open-account]").attributes("href")).toBe("#/settings/people/accounts/u-jana");
    wrapper!.unmount();
    fakeApi();
    const reader = await open("#/settings/people/accounts", false);
    expect(reader.text()).toContain("Správa účtů je dostupná pouze správci wiki.");
    expect(calls.some((call) => call.path.startsWith("/admin"))).toBe(false);
  });

  it("shows a person in the shared dock: a click on the name sets ?osoba=, a deep link opens it, closing the dock clears it", async () => {
    const panel = useWorkspacePanel();
    const view = await open("#/settings/people/accounts");
    await view.get('[data-account="u-jana"] [data-open-account]').trigger("click");
    await settle();
    expect(location.hash).toBe("#/settings/people/accounts?osoba=u-jana");
    expect(panel.open.value && panel.content.value === PersonPeek && panel.contentProps.value.account).toMatchObject({ id: "u-jana" });
    panel.hide();
    await settle();
    expect(location.hash).toBe("#/settings/people/accounts");
    wrapper!.unmount();
    await open("#/settings/people/accounts?osoba=u-petr");
    expect(panel.open.value && panel.contentProps.value.account).toMatchObject({ id: "u-petr" });
    wrapper!.unmount();
    expect(panel.open.value).toBe(false);
    panel.reset();
  });

  it("counts one set while searching and reads the directory's tallies once beside Nastavení wiki's", async () => {
    const view = await open("#/settings/people/accounts", true, true);
    expect(calls.filter((call) => call.path === "/admin/users/summary")).toHaveLength(1);
    expect(view.get(".people-count").text().replace(/\s/g, " ")).toBe("5 lidí · 1 čeká na aktivaci");
    // The search shows one person: the line counts that set, not the directory's waiting accounts beside it.
    await view.get("[data-account-search]").setValue("nov");
    await typingPause();
    expect(view.get(".people-count").text().replace(/\s/g, " ")).toBe("1 člověk");
  });

  it("reaches accounts beyond the first page: Načíst další appends the next offset, a card is read by its id, the tallies are the server's", async () => {
    // 1 000 accounts fill the first page exactly; the one added next sorts last and waits behind it.
    state.users = [user("u-admin", { displayName: "Admin", isWikiAdmin: true }), user("u-wait", { displayName: "Adam Čeká", credentialState: "pending" }),
      ...Array.from({ length: 998 }, (_, index) => user(`u-n${index}`, { displayName: `Novák ${String(index).padStart(4, "0")}` }))];
    const view = await open("#/settings/people/accounts");
    expect(view.findAll("[data-account]")).toHaveLength(1000);
    expect(view.get(".people-count").text().replace(/\s/g, " ")).toBe("Zobrazeno 1 000 lidí · 1 čeká na aktivaci");
    expect(calls.some((call) => call.path === "/admin/users/summary")).toBe(true);
    state.users.push(user("u-last", { displayName: "Zkušební Poslední" }));
    await click("[data-more-accounts]");
    expect(calls.some((call) => call.path === "/admin/users?offset=1000")).toBe(true);
    expect(view.findAll("[data-account]")).toHaveLength(1001);
    expect(view.find("[data-more-accounts]").exists()).toBe(false);
    // The button went with the last page; the keyboard is on the first row it brought (P7).
    expect(document.activeElement?.getAttribute("data-open-account")).toBe("u-last");
    wrapper!.unmount();
    fakeApi();
    await open("#/settings/people/accounts/u-last");
    expect(calls.some((call) => call.path === "/admin/users?id=u-last")).toBe(true);
    expect(wrapper!.text()).not.toContain("Účet nenalezen");
  });

  it("invites a person through the wizard up to the one-time code and returns to the highlighted row", async () => {
    const view = await open("#/settings/people/accounts");
    expect(view.get("[data-invite]").attributes("href")).toBe("#/settings/people/accounts/new");
    await go("#/settings/people/accounts/new");
    await view.get("[data-invite-username]").setValue("jana");
    await view.get("[data-invite-display-name]").setValue("Nová Jana");
    expect(view.text()).toContain("Účet s tímto uživatelským jménem už existuje.");
    expect(view.find("[data-invite-role]").exists()).toBe(false);
    await view.get("[data-invite-username]").setValue("novak");
    expect(view.find("[data-invite-role]").exists()).toBe(true);
    expect(view.get("[data-invite-username]").element).toHaveProperty("value", "novak");
    await click('[data-invite-role] [data-value="admin"] [role="radio"]');
    expect(view.text()).toContain("Správce wiki vidí všechny prostory");
    expect(view.get('[data-summary="role"]').text()).toContain("Správce wiki");
    expect(calls.some((call) => call.method === "POST")).toBe(false);
    // A new wiki administrator is confirmed with a fresh sign-in instead of a plain Další.
    expect(view.find("[data-flow-next]").exists()).toBe(false);
    await view.get('.confirm-reauth input[type="password"]').setValue("heslo");
    await click("[data-confirm-reauth]");
    const created = calls.find((call) => call.method === "POST" && call.path === "/admin/users");
    expect(created?.body).toEqual({ username: "novak", displayName: "Nová Jana", isWikiAdmin: true });
    expect(created?.proof).toBe("proof-1");
    const code = view.get("[data-invite-code]");
    expect(code.text()).toContain("Jednorázový aktivační kód");
    expect(code.text()).toContain("Kód platí 7 dní (do ");
    expect(code.get("[data-one-time-value]").text()).not.toContain("ACT-1234-CODE");
    await code.findAll("button").find((button) => button.text() === "Zobrazit")!.trigger("click");
    expect(code.get("[data-one-time-value]").text()).toBe("ACT-1234-CODE");
    expect(code.get("[data-one-time-done]").attributes("disabled")).toBeDefined();
    await code.get('input[type="checkbox"]').setValue(true);
    await code.get("[data-one-time-done]").trigger("click");
    await settle();
    expect(location.hash).toBe("#/settings/people/accounts");
    expect(flashText()).toContain("Účet Nová Jana je založený");
    expect(view.get('[data-account="u-novak"]').attributes("data-new")).toBeDefined();
    expect(view.text()).not.toContain("ACT-1234-CODE");
  });
});

describe("person card", () => {
  it("keeps personal AI inherited until edited and saves a tri-state exception and quotas with the account SaveBar", async () => {
    const view = await open("#/settings/people/accounts/u-jana/account");
    const section = view.get("[data-account-ai]");
    expect(section.text()).toContain("Podle skupin");
    await section.get(".settings-section-header.is-toggle").trigger("click");
    await section.get("[data-ai-personal-enabled] [data-value=\"deny\"]").trigger("click");
    await section.get('[data-quota="chatAnswerDailyLimit"] [data-value="unlimited"]').trigger("click");
    await view.get("[data-save-bar-save]").trigger("click");
    await settle();
    expect(calls.find(call => call.method === "PUT" && call.path.endsWith("/ai-policy"))!.body).toEqual({ enabled: false, toolsEnabled: null, searchAnswerDailyLimit: null, chatAnswerDailyLimit: 0, pageSummaryDailyLimit: null, agentDailyLimit: null, expectedVersion: null });
    expect(view.findAll(".save-bar")).toHaveLength(1);
    await section.get("[data-ai-personal-enabled] [data-value=\"inherit\"]").trigger("click");
    await section.get('[data-quota="chatAnswerDailyLimit"] [data-value="inherit"]').trigger("click");
    await view.get("[data-save-bar-save]").trigger("click");
    await settle();
    expect(calls.filter(call => call.method === "PUT" && call.path.endsWith("/ai-policy")).at(-1)!.body).toMatchObject({ enabled: null, chatAnswerDailyLimit: null, expectedVersion: "ai-v2" });
  });

  it("reveals the AI with tools choice under access only while AI is not personally denied, and saves it", async () => {
    const view = await open("#/settings/people/accounts/u-jana/account");
    const section = view.get("[data-account-ai]");
    await section.get(".settings-section-header.is-toggle").trigger("click");
    expect(section.find("[data-ai-personal-tools]").exists()).toBe(true);
    await section.get("[data-ai-personal-enabled] [data-value=\"deny\"]").trigger("click");
    await settle();
    expect(section.find("[data-ai-personal-tools]").exists()).toBe(false);
    await section.get("[data-ai-personal-enabled] [data-value=\"allow\"]").trigger("click");
    await settle();
    await section.get("[data-ai-personal-tools] [data-value=\"deny\"]").trigger("click");
    expect(section.text()).toContain("Bez nástrojů");
    await view.get("[data-save-bar-save]").trigger("click");
    await settle();
    expect(calls.find(call => call.method === "PUT" && call.path.endsWith("/ai-policy"))!.body).toMatchObject({ enabled: true, toolsEnabled: false });
  });

  it("opens on Přístupy: everything the person reaches, grouped by space, with the card's tabs and actions", async () => {
    state.principals["u-jana"]!.entries.push({ kind: "youtrack", resourceId: "provoz", title: "77-16", spaceId: "provoz", spaceName: "Provoz", level: "read", sources: [] });
    const view = await open("#/settings/people/accounts/u-jana");
    expect(view.get("h2").text()).toBe("Jana Nováková");
    expect(calls.some((call) => call.path === "/access/principals/user/u-jana")).toBe(true);
    expect(view.get("[data-person-card]").text()).toContain("skupiny Provoz tým");
    expect(view.findAll(".settings-tabs a").map((tab) => tab.attributes("href"))).toEqual([
      "#/settings/people/accounts/u-jana", "#/settings/people/accounts/u-jana/account", "#/settings/people/accounts/u-jana/groups",
      "#/settings/people/accounts/u-jana/tokens", "#/settings/people/accounts/u-jana/activity",
    ]);
    const space = view.get('[data-access-entry="space:provoz"]');
    expect(space.text()).toContain("Celý prostor");
    expect(space.text()).toContain("Přímý přístup");
    expect(space.get(".level-badge").attributes("data-level")).toBe("write");
    expect(space.get(".access-entry-link").attributes("href")).toBe("#/settings/access/spaces/provoz");
    // A right through a group is changed on the group: the row offers the group, not Odebrat.
    await view.get('[data-access-group="provoz"] .access-details-toggle').trigger('click');
    const youtrack = view.get('[data-access-entry="youtrack:provoz"]');
    expect(youtrack.text()).toContain("ID projektů v YouTracku: 77-16");
    expect(youtrack.get('.access-entry-link').text()).toBe("Úkoly YouTracku v prostoru");
    expect(youtrack.get('.access-entry-link').attributes('href')).toBe("#/settings/access/youtrack");
    const vault = view.get('[data-access-entry="vault:v-1"]');
    expect(vault.find("[data-revoke-access]").exists()).toBe(false);
    expect(vault.get("[data-open-group]").attributes("href")).toBe("#/settings/people/groups/g-ops");
    expect(view.get("[data-person-grant]").attributes("href")).toBe("#/settings/people/accounts/u-jana/new");
  });

  it("changes a level inline with the grant's version and revokes after a confirmation", async () => {
    const view = await open("#/settings/people/accounts/u-jana");
    const space = () => view.get('[data-access-entry="space:provoz"]');
    await space().get("[data-change-access]").trigger("click");
    await settle();
    expect(space().get("[data-save-access]").attributes("disabled")).toBeDefined();
    await space().get('[data-access-level-picker] [data-value="read"]').trigger("click");
    await space().get("[data-save-access]").trigger("click");
    await settle();
    const put = calls.find((call) => call.method === "PUT" && call.path === "/access/resources/space/provoz/grants");
    expect(put?.body).toEqual({ subjectKind: "user", subjectId: "u-jana", level: "read", startAt: null, expiresAt: null, expectedVersion: "v-provoz" });
    expect(put?.proof).toBeNull();
    expect(flashText()).toContain("Přístup pro Jana Nováková je uložený: Čte");

    await space().get("[data-revoke-access]").trigger("click");
    await settle();
    expect(calls.some((call) => call.method === "DELETE")).toBe(false);
    expect(document.body.textContent).toContain("Jana Nováková přijde o přímý přístup Upravuje · prostor Provoz");
    bodyButton("[data-confirm-action]").click();
    await settle();
    expect(calls.find((call) => call.method === "DELETE")?.path).toBe("/access/resources/space/provoz/grants/user/u-jana?version=v-provoz");
  });

  it("raising a space grant to Spravuje asks for a fresh sign-in and carries its proof", async () => {
    const view = await open("#/settings/people/accounts/u-jana");
    const space = () => view.get('[data-access-entry="space:provoz"]');
    await space().get("[data-change-access]").trigger("click");
    await settle();
    await space().get('[data-access-level-picker] [data-value="manage"]').trigger("click");
    await space().get("[data-save-access]").trigger("click");
    await settle();
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
    expect(document.querySelector("dialog[open] [data-reauth-dialog]")).not.toBeNull();
    await reauth();
    const put = calls.find((call) => call.method === "PUT" && call.path === "/access/resources/space/provoz/grants");
    expect(put?.body).toMatchObject({ level: "manage", expectedVersion: "v-provoz" });
    expect(put?.proof).toBe("proof-1");
  });

  it("adds access from the card through K čemu → Který → Úroveň and returns to the card", async () => {
    const view = await open("#/settings/people/accounts/u-jana/new");
    expect(view.get("h2").text()).toBe("Přidat přístup");
    await click('[data-grant-kind] [data-value="space"] [role="radio"]');
    await new Promise((resolve) => setTimeout(resolve, 20));
    await settle();
    expect(calls.some((call) => call.path === "/access/resources?kind=space")).toBe(true);
    await click('[data-grant-target] [role="radio"][data-value="eng"]');
    await click('[data-grant-level] [data-value="read"]');
    expect(view.get('[data-summary="target"]').text()).toContain("Engineering");
    await click("[data-flow-next]");
    const put = calls.find((call) => call.method === "PUT" && call.path === "/access/resources/space/eng/grants");
    expect(put?.body).toEqual({ subjectKind: "user", subjectId: "u-jana", level: "read", startAt: null, expiresAt: null, expectedVersion: null });
    expect(location.hash).toBe("#/settings/people/accounts/u-jana");
    expect(flashText()).toContain("Jana Nováková má přístup Čte · prostor Engineering");
  });

  it("Odebrat vše shows what it would close and confirms with a fresh sign-in", async () => {
    const view = await open("#/settings/people/accounts/u-jana");
    await click("[data-revoke-all]");
    expect(calls.some((call) => call.method === "GET" && call.path === "/access/principals/user/u-jana/revoke-all")).toBe(true);
    const impact = document.querySelector("dialog[open] [data-revoke-all-impact]")!;
    expect(impact.textContent).toContain("Zavře se 3 přímá přidělení");
    expect(impact.textContent).toContain("Zneplatní se 2 osobní API tokeny");
    expect(impact.textContent).toContain("účet zůstane aktivní");
    expect(calls.some((call) => call.method === "POST")).toBe(false);
    await reauth();
    const post = calls.find((call) => call.method === "POST" && call.path === "/access/principals/user/u-jana/revoke-all");
    expect(post?.proof).toBe("proof-1");
    expect(flashText()).toContain("přišel(a) o všechny přímé přístupy");
    expect(view.find("[data-revoke-all]").exists()).toBe(true);
  });

  it("says why Odebrat vše was refused when the person holds the last permanent Spravuje of a space", async () => {
    const view = await open("#/settings/people/accounts/u-jana");
    state.conflict = "/access/principals/user/u-jana/revoke-all";
    await click("[data-revoke-all]");
    await reauth();
    expect(document.querySelector("dialog[open] [data-reauth-dialog]")).toBeNull();
    expect(view.get("[data-revoke-all-conflict]").text()).toContain("Změněno");
  });

  it("saves the account with its version and keeps the draft on a conflict until Načíst znovu", async () => {
    const view = await open("#/settings/people/accounts/u-jana/account");
    expect(view.get("h2").text()).toBe("Jana Nováková");
    expect(view.find("[data-save-bar-save]").exists()).toBe(false);
    await view.get("[data-account-display-name]").setValue("Jana Svobodová");
    await click("[data-save-bar-save]");
    expect(calls.find((call) => call.method === "PUT" && call.path === "/admin/users/u-jana")?.body).toEqual({ displayName: "Jana Svobodová", isActive: true, isWikiAdmin: false, expectedVersion: "u-jana-v1" });
    expect(view.get("h2").text()).toBe("Jana Svobodová");

    state.conflict = "/admin/users/u-jana";
    state.users = state.users.map((item) => (item.id === "u-jana" ? { ...item, displayName: "Jana od kolegy", version: "other-v3" } : item));
    const reads = calls.filter((call) => call.method === "GET" && call.path === "/admin/users").length;
    await view.get("[data-account-display-name]").setValue("Moje změna");
    await click("[data-save-bar-save]");
    expect(view.get("[data-account-conflict]").text()).toContain("mezitím změnil někdo jiný");
    expect(calls.filter((call) => call.method === "PUT" && call.path === "/admin/users/u-jana").at(-1)?.body).toMatchObject({ expectedVersion: "saved-v2" });
    // Never replaced silently: the draft stays until the user chooses Načíst znovu.
    expect(calls.filter((call) => call.method === "GET" && call.path === "/admin/users").length).toBe(reads);
    expect((view.get("[data-account-display-name]").element as HTMLInputElement).value).toBe("Moje změna");
    expect(view.find("[data-conflict-overwrite]").exists()).toBe(false);
    await click("[data-conflict-reload]");
    expect(calls.filter((call) => call.method === "GET" && call.path === "/admin/users").length).toBe(reads + 1);
    expect((view.get("[data-account-display-name]").element as HTMLInputElement).value).toBe("Jana od kolegy");
    expect(view.find("[data-account-conflict]").exists()).toBe(false);
  });

  it("turns Správce wiki on only after a fresh sign-in and sends the proof", async () => {
    const view = await open("#/settings/people/accounts/u-jana/account");
    await click("[data-account-admin]");
    await click("[data-save-bar-save]");
    expect(calls.some((call) => call.method === "PUT" && call.path === "/admin/users/u-jana")).toBe(false);
    expect(document.querySelector("dialog[open] [data-admin-change]")?.textContent).toContain("bude spravovat celou wiki");
    await reauth();
    const put = calls.find((call) => call.method === "PUT" && call.path === "/admin/users/u-jana");
    expect(put?.body).toEqual({ displayName: "Jana Nováková", isActive: true, isWikiAdmin: true, expectedVersion: "u-jana-v1" });
    expect(put?.proof).toBe("proof-1");
    expect(view.find("[data-save-bar-save]").exists()).toBe(false);
  });

  it("resets a password with a fresh sign-in and shows the new code once; never for one's own account", async () => {
    const view = await open("#/settings/people/accounts/u-jana/account");
    await click("[data-reset-password]");
    expect(calls.some((call) => call.path.endsWith("/reset-password"))).toBe(false);
    expect(document.body.textContent).toContain("přestanou ihned platit");
    expect(document.querySelector('dialog[open] input[type="password"]')).not.toBeNull();
    await reauth();
    expect(calls.find((call) => call.path === "/admin/users/u-jana/reset-password")?.body).toEqual({ expectedVersion: "u-jana-v1" });
    expect(calls.find((call) => call.path === "/admin/users/u-jana/reset-password")?.proof).toBe("proof-1");
    expect(view.get("[data-reset-code]").text()).toContain("Nový jednorázový kód");
    expect(view.get("[data-reset-code]").text()).toContain("Kód platí 30 minut (do ");
    await go("#/settings/people/accounts/u-admin/account");
    expect(view.find("[data-reset-password]").exists()).toBe(false);
    expect(view.text()).toContain("Vlastní heslo změníte v profilu.");
    await go("#/settings/people/accounts/u-hub/account");
    expect(view.find("[data-reset-password]").exists()).toBe(false);
  });

  it("uses the current reset reauth window and asks again if the server has expired it", async () => {
    reauthWindow.sync(new Date(Date.now() + 600_000).toISOString());
    const view = await open("#/settings/people/accounts/u-jana/account");
    state.reauth = "/admin/users/u-jana/reset-password";
    await click("[data-reset-password]");
    expect(document.querySelector('dialog[open] input[type="password"]')).toBeNull();
    expect(bodyButton("[data-confirm-reauth]")).not.toBeNull();
    bodyButton("[data-confirm-reauth]").click();
    await settle();
    expect(calls.find((call) => call.path === state.reauth)?.proof).toBeNull();
    expect(view.find("[data-reset-code]").exists()).toBe(false);
    expect(document.querySelector('dialog[open] input[type="password"]')).not.toBeNull();
    await reauth();
    expect(calls.filter((call) => call.path === state.reauth).at(-1)?.proof).toBe("proof-1");
    expect(view.find("[data-reset-code]").exists()).toBe(true);
    expect(document.querySelector("dialog[open]")).toBeNull();
  });

  it("changes the personal API token permission; revoking asks first and sends the observed version", async () => {
    const view = await open("#/settings/people/accounts/u-jana/account");
    const level = (value: string) => `[data-api-access] [data-value="${value}"]`;
    expect(view.get(level("write")).attributes("aria-checked")).toBe("true");
    expect(view.findAll("[data-api-access] [role=radio]").map((pill) => pill.text())).toEqual(["Bez API", "Čtení", "Zápis"]);
    expect(view.get("[data-api-level-help]").text()).toContain("Čte i mění obsah jako člověk");
    await click(level("none"));
    expect(view.get("[data-api-revoke-warning]").text()).toContain("trvale zneplatní");
    await click("[data-save-bar-cancel]");
    expect(view.get(level("write")).attributes("aria-checked")).toBe("true");
    await click(level("none"));
    await click("[data-save-bar-save]");
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
    expect(document.body.textContent).toContain("ihned a trvale přestanou platit");
    bodyButton("[data-confirm-action]").click();
    await settle();
    expect(calls.find((call) => call.method === "PUT" && call.path === "/admin/users/u-jana/api-access")?.body).toEqual({ level: "none", expectedVersion: "api-v1" });
    expect(calls.some((call) => call.method === "PUT" && call.path === "/admin/users/u-jana")).toBe(false);
    expect(flashText()).toContain("už nesmí vydávat osobní API tokeny");
  });

  it("lowers the level without a confirmation and says the tokens stay valid but trimmed", async () => {
    const view = await open("#/settings/people/accounts/u-jana/account");
    await click('[data-api-access] [data-value="read"]');
    expect(view.get("[data-api-lower-warning]").text()).toContain("rozsah se ořízne");
    expect(view.find("[data-api-revoke-warning]").exists()).toBe(false);
    await click("[data-save-bar-save]");
    expect(calls.find((call) => call.method === "PUT" && call.path === "/admin/users/u-jana/api-access")?.body).toEqual({ level: "read", expectedVersion: "api-v1" });
    expect(flashText()).toContain("úroveň osobního API čtení");
    expect(view.get('[data-api-access] [data-value="read"]').attributes("aria-checked")).toBe("true");
  });

  it("shows the account's groups, its tokens and its activity narrowed on the server", async () => {
    const view = await open("#/settings/people/accounts/u-jana/groups");
    expect(view.get('[data-account-group="g-ops"] a').attributes("href")).toBe("#/settings/people/groups/g-ops");
    await go("#/settings/people/accounts/u-jana/tokens");
    // Only Jana's tokens; the revoked one waits collapsed under Neplatné tokeny.
    expect(view.findAll("[data-api-token]").map((row) => row.attributes("data-api-token"))).toEqual(["t-1"]);
    expect(view.text()).toContain("1 zneplatněných nebo vypršelých");
    await go("#/settings/people/accounts/u-jana/activity");
    expect(calls.some((call) => call.path === "/admin/access-audit?limit=50&userId=u-jana")).toBe(true);
    expect(view.findAll(".activity-item").length).toBeGreaterThan(0);
    expect(view.get("[data-audit-history]").attributes("href")).toBe("#/settings/security/audit?kdo=u-jana");
  });
});

describe("API tokeny", () => {
  it("lists every valid token with its owner and revokes one after a confirmation", async () => {
    const view = await open("#/settings/people/tokens");
    const valid = view.findAll("[data-api-token]").map((row) => row.attributes("data-api-token"));
    expect(valid).toEqual(["t-1", "t-2"]);
    expect(view.get('[data-api-token="t-2"]').text()).toContain("Petr Svoboda");
    expect(view.get('[data-api-token="t-2"] [data-token-scope]').text()).toBe("Zápis");
    expect(view.get('[data-api-token="t-1"] [data-token-scope]').text()).toBe("Čtení");
    expect(view.findAll('[data-api-token="t-2"] [data-token-channel]').map((badge) => badge.text())).toEqual(["REST API", "MCP"]);
    expect(view.findAll('[data-api-token="t-1"] [data-token-channel]').map((badge) => badge.text())).toEqual(["REST API"]);
    expect(view.find("[data-levels-review]").exists()).toBe(false);
    expect(view.get('[data-api-token="t-2"] a').attributes("href")).toBe("#/settings/people/accounts/u-petr/tokens");
    await click('[data-api-token="t-1"] [data-token-revoke]');
    expect(calls.some((call) => call.method === "DELETE")).toBe(false);
    expect(document.body.textContent).toContain("přestane platit hned");
    bodyButton("[data-confirm-action]").click();
    await settle();
    expect(calls.find((call) => call.method === "DELETE")?.path).toBe("/admin/api-tokens/t-1");
    expect(flashText()).toContain("Token Skript t-1 (Jana Nováková) je zneplatněný.");
  });
});

describe("Kontrola úrovní API po upgradu", () => {
  it("shows the notice only while a review waits and confirms it once", async () => {
    state.levelsReview = true;
    const view = await open("#/settings/people/tokens");
    expect(view.get("[data-levels-review]").text()).toContain("úroveň Zápis");
    await click("[data-levels-review-confirm]");
    expect(calls.filter((call) => call.method === "POST" && call.path === "/admin/api-tokens/levels-review")).toHaveLength(1);
    expect(view.find("[data-levels-review]").exists()).toBe(false);
    expect(flashText()).toContain("Úrovně osobního API jsou potvrzené");
  });
});

describe("groups", () => {
  it("creates a group through the wizard and opens it to add members", async () => {
    const view = await open("#/settings/people/groups");
    expect(view.findAll("[data-group]").map((row) => row.attributes("data-group"))).toEqual(["g-ops"]);
    await go("#/settings/people/groups/new");
    await view.get("[data-group-name]").setValue("provoz tým");
    expect(view.text()).toContain("Skupina s tímto názvem už existuje.");
    expect(view.find('[data-summary="name"]').exists()).toBe(false);
    await view.get("[data-group-name]").setValue("Externisté");
    expect(view.get('[data-summary="name"]').text()).toContain("Externisté");
    await view.get("[data-group-name]").setValue("");
    expect(view.find('[data-summary="name"]').exists()).toBe(true);
    expect(view.get("[data-flow-next]").attributes("disabled")).toBeDefined();
    expect(calls.some((call) => call.method === "POST" && call.path === "/admin/groups")).toBe(false);
    await view.get("[data-group-name]").setValue("Externisté");
    await click("[data-flow-next]");
    expect(calls.find((call) => call.method === "POST" && call.path === "/admin/groups")?.body).toEqual({ name: "Externisté" });
    expect(view.get("[data-group-created]").text()).toContain("Externisté");
    await click("[data-group-open]");
    expect(location.hash).toBe("#/settings/people/groups/g-new/members");
    expect(flashText()).toContain("Přidejte do ní členy");
    expect(view.text()).toContain("Skupina zatím nemá žádné členy");
  });

  it("opens the group's card on Přístupy with its own grants", async () => {
    state.principals["g-ops"] = {
      principal: { kind: "group", id: "g-ops", name: "Provoz tým", detail: "1 člen" },
      scope: "wiki",
      canManage: true,
      entries: [{ kind: "space", resourceId: "provoz", title: "Provoz", spaceId: "provoz", spaceName: "Provoz", level: "write", sources: [{ via: "direct", label: "", level: "write", version: "v-g", effective: true }] }],
    };
    const view = await open("#/settings/people/groups/g-ops");
    expect(view.get("h2").text()).toBe("Provoz tým");
    expect(calls.some((call) => call.path === "/access/principals/group/g-ops")).toBe(true);
    expect(view.get('[data-access-entry="space:provoz"]').text()).toContain("Celý prostor");
    expect(view.text()).toContain("platí pro všechny její členy");
    expect(view.get("[data-group-grant]").attributes("href")).toBe("#/settings/people/groups/g-ops/new");
  });

  it("finds who to add by asking the server as the admin types, and loads the next page of a large group", async () => {
    state.members["g-ops"] = Array.from({ length: 1001 }, (_, index) => {
      const id = `u-m${String(index).padStart(4, "0")}`;
      return { id, username: id.replace("u-", ""), displayName: `Člen ${id}`, isActive: true, version: `${id}-v1` };
    });
    const view = await open("#/settings/people/groups/g-ops/members");
    expect(view.findAll("[data-member]")).toHaveLength(1000);
    await click("[data-more-members]");
    expect(calls.some((call) => call.path === "/admin/groups/g-ops/members?offset=1000")).toBe(true);
    expect(view.findAll("[data-member]")).toHaveLength(1001);
    expect(view.find("[data-more-members]").exists()).toBe(false);

    await click("[data-add-member]");
    await view.get("[data-member-candidates] input").setValue("pet");
    await typingPause();
    expect(calls.some((call) => call.method === "GET" && call.path === "/admin/users?q=pet")).toBe(true);
    expect(view.get("[data-member-candidates]").findAll('[role="radio"]').map((item) => item.attributes("data-value"))).toEqual(["u-petr"]);
  });

  it("adds a member from the searchable list and removes one after a confirmation", async () => {
    const view = await open("#/settings/people/groups/g-ops/members");
    expect(view.findAll("[data-member]").map((row) => row.attributes("data-member"))).toEqual(["u-jana"]);
    await click("[data-add-member]");
    // The search takes the keyboard at once (P7).
    expect(document.activeElement).toBe(view.get("[data-member-candidates] input").element);
    expect(calls.some((call) => call.method === "GET" && call.path === "/admin/users")).toBe(true);
    const candidates = view.get("[data-member-candidates]").findAll('[role="radio"]').map((item) => item.attributes("data-value"));
    // Active accounts outside the group only.
    expect(candidates).toEqual(["u-admin", "u-hub", "u-petr"]);
    expect(view.get("[data-add-member-confirm]").attributes("disabled")).toBeDefined();
    await click('[data-member-candidates] [role="radio"][data-value="u-petr"]');
    await click("[data-add-member-confirm]");
    expect(calls.find((call) => call.method === "PUT" && call.path === "/admin/groups/g-ops/members/u-petr")?.body).toEqual({ isMember: true, expectedVersion: null });
    expect(view.get('[data-member="u-petr"]').attributes("data-new")).toBeDefined();
    expect(flashText()).toContain("Petr Svoboda je členem skupiny Provoz tým");
    // The closed panel took the confirm button; the keyboard stays on Přidat člena, and the card's header count is asked again.
    expect(document.activeElement).toBe(view.get("[data-add-member]").element);
    expect(calls.filter((call) => call.path.startsWith("/access/principals/group/g-ops")).length).toBeGreaterThan(1);

    await click('[data-remove-member="u-jana"]');
    expect(calls.some((call) => call.path === "/admin/groups/g-ops/members/u-jana")).toBe(false);
    expect(document.body.textContent).toContain("přestane být členem skupiny");
    bodyButton("[data-confirm-action]").click();
    await settle();
    expect(calls.find((call) => call.path === "/admin/groups/g-ops/members/u-jana")?.body).toEqual({ isMember: false, expectedVersion: "m-jana" });
    expect(view.find('[data-member="u-jana"]').exists()).toBe(false);
  });

  it.each(["add", "remove"] as const)("retries a refused member %s with a fresh sign-in and the intended version", async (action) => {
    const view = await open("#/settings/people/groups/g-ops/members");
    state.reauth = "/admin/groups/g-ops/members";
    if (action === "add") {
      await click("[data-add-member]");
      await click('[data-member-candidates] [role="radio"][data-value="u-petr"]');
      await click("[data-add-member-confirm]");
    } else {
      await click('[data-remove-member="u-jana"]');
      bodyButton("[data-confirm-action]").click();
      await settle();
    }
    const path = `${state.reauth}/${action === "add" ? "u-petr" : "u-jana"}`;
    expect(calls.filter((call) => call.path === path)).toHaveLength(1);
    expect(calls.find((call) => call.path === path)?.proof).toBeNull();
    expect(state.members["g-ops"]!.map((member) => member.id)).toEqual(["u-jana"]);
    expect(flashText()).toBe("");
    expect(document.querySelectorAll("dialog[open]")).toHaveLength(1);
    expect(document.querySelector('dialog[open] input[type="password"]')).not.toBeNull();
    await reauth();
    expect(calls.filter((call) => call.path === path).at(-1)).toMatchObject({
      body: { isMember: action === "add", expectedVersion: action === "add" ? null : "m-jana" }, proof: "proof-1",
    });
    expect(document.querySelector("dialog[open]")).toBeNull();
    if (action === "add") {
      expect(view.get('[data-member="u-petr"]').attributes("data-new")).toBeDefined();
      expect(flashText()).toContain("Petr Svoboda je členem skupiny Provoz tým");
      expect(view.find("[data-member-candidates]").exists()).toBe(false);
    } else {
      expect(view.find('[data-member="u-jana"]').exists()).toBe(false);
      expect(flashText()).toContain("Jana Nováková už není členem skupiny Provoz tým");
    }
  });

  it("renames with the version, keeps the name draft on a conflict and shows the group's changes", async () => {
    const view = await open("#/settings/people/groups/g-ops/members");
    await view.get("[data-group-rename]").setValue("Provoz");
    await settle();
    await click("[data-save-bar-save]");
    expect(calls.find((call) => call.method === "PUT" && call.path === "/admin/groups/g-ops")?.body).toEqual({ name: "Provoz", expectedVersion: "g-ops-v1" });
    expect(view.get("h2").text()).toBe("Provoz");
    state.conflict = "/admin/groups/g-ops";
    await view.get("[data-group-rename]").setValue("Jinak");
    await settle();
    await click("[data-save-bar-save]");
    expect(view.get("[data-group-conflict]").text()).toContain("mezitím změnil někdo jiný");
    expect((view.get("[data-group-rename]").element as HTMLInputElement).value).toBe("Jinak");
    await click("[data-conflict-reload]");
    expect((view.get("[data-group-rename]").element as HTMLInputElement).value).toBe("Provoz");
    state.conflict = undefined;
    await go("#/settings/people/groups/g-ops/activity");
    expect(calls.some((call) => call.path === "/admin/access-audit?limit=50&groupId=g-ops")).toBe(true);
    expect(view.findAll(".activity-item").length).toBeGreaterThan(0);
  });

  it("shows one actionable empty state without groups", async () => {
    state.groups = [];
    const view = await open("#/settings/people/groups");
    expect(view.text()).toContain("Zatím nemáte žádnou skupinu");
    expect(view.find("[data-group]").exists()).toBe(false);
  });
});

describe("access history", () => {
  it("follows the opaque cursor, merges without duplicates, keeps filters in the URL and states the coverage", async () => {
    state.audit = [
      { items: [event("9007199254740999", { actorId: null, actorName: null })], hasMore: true, nextBeforeId: "9007199254740999" },
      { items: [event("9007199254740999", { actorId: null, actorName: null }), event("9007199254740998", { action: "group.created", userId: null, userName: null, groupId: "g-ops", groupName: "Provoz tým", isActive: null, isWikiAdmin: null })], hasMore: false, nextBeforeId: null },
    ];
    const view = await open("#/settings/security/audit");
    expect(view.text()).toContain("Systém");
    expect(view.get("[data-audit-coverage]").text()).toContain("Záznamy se vedou od");
    await click("[data-audit-more]");
    expect(calls.at(-1)?.path).toBe("/admin/access-audit?limit=50&beforeId=9007199254740999");
    expect(view.findAll(".activity-item")).toHaveLength(2);
    expect(view.find("[data-audit-more]").exists()).toBe(false);
    await click('[data-filter="what"]');
    await new Promise((resolve) => setTimeout(resolve, 40));
    document.querySelector<HTMLElement>('.filter-menu-panel [data-value="group"]')!.click();
    await settle();
    expect(location.hash).toBe("#/settings/security/audit?co=group");
    expect(view.findAll(".activity-item")).toHaveLength(1);
    expect(view.text()).toContain("založil(a) skupinu");
  });

  it("sums up the period on the server and leads from failures and tool users into the filtered log", async () => {
    const view = await open("#/settings/security/audit");
    expect(calls.some((call) => call.path === "/admin/activity-summary?days=30")).toBe(true);
    expect(view.get('[data-stat="top-editor"]').text()).toContain("Jana Nováková");
    expect(view.get('[data-stat="tool-calls"]').text()).toContain("MCP 14 · Asistent 5");
    expect(view.get('[data-stat="tool-failures"]').attributes("href")).toBe("#/settings/security/audit?vysledek=failed");
    expect(view.get('[data-stat="revisions"] .ring-label').text()).toBe("12 %");
    expect(view.find('[data-summary-list="tools"]').exists()).toBe(false);
    await click("[data-summary-toggle]");
    expect(view.get("[data-summary-toggle]").attributes("aria-expanded")).toBe("true");
    expect(view.get('[data-summary-list="tool-users"] a').attributes("href")).toBe("#/settings/security/audit?kdo=u-petr");
    expect(view.get('[data-summary-list="tools"]').text()).toContain("3 selhala");
    await go("#/settings/security/audit?kdy=all");
    expect(calls.at(-1)?.path).toBe("/admin/activity-summary");
    expect(view.text()).toContain("Přehled · Kdykoli");
  });

  it("shows the account flags of a change as details, an empty history and a failed load", async () => {
    const view = await open("#/settings/security/audit");
    await click("[data-activity-details]");
    expect(view.text()).toContain("Stav účtu");
    wrapper!.unmount();
    state.audit = [{ items: [], hasMore: false, nextBeforeId: null }];
    const empty = await open("#/settings/security/audit");
    expect(empty.text()).toContain("Zatím žádné události");
    wrapper!.unmount();
    vi.stubGlobal("fetch", vi.fn(async () => problem(500, "Historie není dostupná.")));
    const failed = await open("#/settings/security/audit");
    expect(failed.get('[role="alert"]:not(.st-note)').text()).toContain("Historie není dostupná.");
    expect(failed.text()).toContain("Zkusit znovu");
  });
});
