import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { config, flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import { clearCsrf, type Space } from "../../api";
import type { Vault } from "../../vaults";
import { useDraftGuard } from "./dirtyGuard";
import SettingsHost from "./SettingsHost.vue";
import { auditActivity, loadVaultAudit, vaultAuditSource, type AccessAuditEvent } from "./auditActivity";
import { provideSettingsRoute } from "./useSettingsRoute";
import { fakeUrlApi, type FakeCall, type FakeUrlHandler } from "./general/testing";
import { settingsIssues } from "./global/issues";
import type { ResourceAccess, ResourceAccessView } from "./access/accessModel";
import { automaticRows, directSource, groupSource, resourceRows, sourcesSummary } from "./access/accessView";
import VaultWizard from "./vaults/VaultWizard.vue";

// The heavy older panels are not under test here.
// A test can give the stub an unsaved draft with a save of its own through `accountsPage.install`.
const accountsPage = vi.hoisted(() => ({ install: undefined as (() => void) | undefined }));
vi.mock("./people/AccountsPage.vue", () => ({ __esModule: true, default: defineComponent({ props: { sub: String }, setup() { accountsPage.install?.(); }, render() { return h("p", { "data-users": this.sub ?? "" }, "Účty"); } }) }));
// Live access request events without a SignalR connection.
vi.mock("../../secrets/secretEvents", () => ({ createSecretAccessEvents: () => ({ subscribe: () => () => {}, onRequestChanged: () => () => {}, revokeAll: () => {} }) }));
vi.mock("./space/SpaceAccessPage.vue", () => ({ __esModule: true, default: defineComponent({ props: { spaceId: String, part: Array }, render() { return h("p", { "data-space-access": `${this.spaceId}:${((this.part ?? []) as string[]).join("/")}` }); } }) }));

const spaces: Space[] = [
  { id: "provoz", name: "Provoz", icon: "book-open", color: "blue", role: "Admin", description: "Servery", pinPolicy: "None", pinned: false, pageCount: 0, lastChangedAt: null },
  { id: "vyvoj", name: "Vývoj", icon: "code-xml", color: "green", role: "Reader", pinPolicy: "None", pinned: false, pageCount: 0, lastChangedAt: null },
];
const vault = (id: string, extra: Partial<Vault> = {}): Vault => ({
  id, spaceId: "provoz", spaceName: "Provoz", name: `Trezor ${id}`, locationId: "loc-1", locationName: "FS01", fileName: `${id}.kdbx`, status: "unlocked",
  lastFailure: null, lastRefreshAt: "2026-09-27T08:00:00Z", snapshotSha256: "abcdef0123456789abcdef", entryCount: 3, createdAt: "2026-09-01T08:00:00Z", level: "manage", ...extra,
});
/** GET /api/access/resources/vault/v-1: Jana and the group Provoz tým have grants here, Petr comes in through the group. */
function vaultAccess(view: ResourceAccessView = "effective"): ResourceAccess {
  return {
    resource: { kind: "vault", id: "v-1", title: "Trezor v-1", spaceId: "provoz", spaceName: "Provoz" },
    canManage: true,
    automatic: [
      { label: "Správci prostoru", detail: "Mění se v přístupech prostoru Provoz", level: "manage", count: 2 },
      { label: "Správci wiki", detail: "Ve všech prostorech", level: "manage", count: 1 },
    ],
    entries: [
      { subject: { kind: "user", id: "u-jana", name: "Jana", detail: "jana" }, level: "read", sources: [{ via: "direct", label: "Přímý přístup", level: "read", version: "v-jana", effective: true }] },
      {
        subject: { kind: "group", id: "g-ops", name: "Provoz tým", memberCount: 3 },
        level: "discover",
        sources: [{ via: "direct", label: "Přímý přístup", level: "discover", version: "v-ops", expiresAt: "2026-12-31T23:00:00Z", effective: true }],
      },
      ...(view === "effective"
        ? [{ subject: { kind: "user" as const, id: "u-petr", name: "Petr", detail: "petr" }, level: "discover" as const, sources: [{ via: "group" as const, label: "Provoz tým", groupId: "g-ops", level: "discover" as const, effective: true }] }]
        : []),
    ],
    hasMore: false,
  };
}

let calls: FakeCall[] = [];
function fakeApi(handler: FakeUrlHandler) {
  calls = fakeUrlApi(handler);
}
const baseApi = (extra?: FakeUrlHandler): FakeUrlHandler => (method, url, body) => {
  const custom = extra?.(method, url, body);
  if (custom !== undefined) return custom;
  if (url.pathname === "/api/vaults") return [vault("v-1"), vault("v-2", { status: "needsPassword", lastFailure: "Heslo nesedí." })];
  if (url.pathname === "/api/vault-locations") return [{ id: "loc-1", name: "FS01", kind: "smb", root: "smb://fs01/kp", username: "svc", domain: "", hasPassword: true, vaultCount: 2 }];
  if (url.pathname === "/api/admin/access-audit") return { items: [], hasMore: false, nextBeforeId: null, coverageStartedAt: "2026-09-01T00:00:00Z" };
  if (url.pathname === "/api/access/resources/vault/v-1") return vaultAccess(url.searchParams.get("view") === "direct" ? "direct" : "effective");
  if (url.pathname.endsWith("/grants") || url.pathname.includes("/grants/") || url.pathname.endsWith("/archive")) return new Response(null, { status: 204 });
  if (url.pathname === "/api/security/me") return { globalSecurityAdmin: true, managesAllVaults: true, vaultManagerSpaces: [], vaults: [], freshAuth: "supported", freshAuthMethod: "password" };
  if (url.pathname === "/api/security/reauth") return { id: "proof-1", method: "password", providerId: null, providerName: null, expiresAt: "2026-09-27T10:05:00Z", redirectUrl: null };
  if (url.pathname === "/api/security/reauth/proof-1/password") return { id: "proof-1", operation: "security", targetKind: "vault", targetId: "v-1", method: "password", state: "verified", failure: null, expiresAt: "2026-09-27T10:05:00Z", proofExpiresAt: "2026-09-27T10:02:00Z" };
  if (url.pathname === "/api/security/protection") return protectionState("instance", null, "always");
  if (url.pathname === "/api/vaults/v-1/protection") return protectionState("vault", "v-1", null);
  if (url.pathname === "/api/security/protection/overrides") return [];
  if (url.pathname === "/api/security/networks") return { version: "3", items: [] };
  if (url.pathname === "/api/security/reauth/self-test") return [];
  if (url.pathname === "/api/access-requests") return { items: [], hasMore: false };
  if (url.pathname === "/api/vaults/v-1/audit") return { items: [], hasMore: false, nextBeforeId: null };
  return new Response(JSON.stringify({ detail: `unexpected ${url.pathname}` }), { status: 404 });
};

function protectionState(level: "instance" | "vault", targetId: string | null, explicit: string | null) {
  const chain = [{ level: "instance", targetId: null, name: null, mode: "always" }, ...(level === "vault" ? [{ level: "vault", targetId, name: "Trezor v-1", mode: explicit }] : [])];
  return { level, targetId, explicit: level === "instance" ? "always" : explicit, effective: explicit ?? "always", source: explicit ? level : "instance", chain, canManage: true, version: explicit ? "p-1" : null,
    impact: level === "instance" ? { vaults: 2, secrets: 6 } : { vaults: 0, secrets: 3 }, preview: { reauthRequired: true, networkException: false, freshAuth: "supported", blocked: false }, configurationVersion: "3" };
}
/** Confirms the open fresh sign-in (ReauthDialog or an inline ConfirmWithReauth) with the account password. */
async function reauth() {
  const input = document.querySelector<HTMLInputElement>('.confirm-reauth input[type="password"]')!;
  input.value = "heslo";
  input.dispatchEvent(new Event("input"));
  await flushPromises();
  input.closest("form")!.dispatchEvent(new Event("submit", { cancelable: true }));
  await flushPromises();
  await flushPromises();
}

let wrapper: VueWrapper | undefined;
async function host(hash: string, props: Record<string, unknown> = {}) {
  history.replaceState(null, "", `/${hash}`);
  wrapper = mount(SettingsHost, {
    props: { scope: hash.startsWith("#/settings") ? "global" : "space", spaceId: hash.startsWith("#/spaces/") ? hash.split("/")[2] : undefined, userId: "u-admin", isWikiAdmin: true, spaces, backupWarning: null, ...props },
    attachTo: document.body,
  });
  await flushPromises();
  await vi.dynamicImportSettled();
  await flushPromises();
  return wrapper;
}
async function navigate(hash: string) {
  location.hash = hash;
  // jsdom delivers hashchange as a task, like a browser after a clicked link.
  await new Promise((resolve) => setTimeout(resolve, 0));
  await flushPromises();
  await vi.dynamicImportSettled();
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

describe("access rows of a vault", () => {
  it("shows the automatic administrators as locked rows and the grants of the vault as editable ones", () => {
    const access = vaultAccess();
    const locked = automaticRows(access);
    expect(locked.map((row) => [row.key, row.title, row.level, row.locked])).toEqual([
      ["automatic:0", "Správci prostoru", "manage", true],
      ["automatic:1", "Správci wiki", "manage", true],
    ]);
    expect(locked[0]!.detail).toBe("2 lidé · Mění se v přístupech prostoru Provoz");
    const rows = resourceRows(access);
    expect(rows.map((row) => [row.key, row.level])).toEqual([
      ["user:u-jana", "read"],
      ["user:u-petr", "discover"],
      ["group:g-ops", "discover"],
    ]);
    // A change or revoke goes out with the version of the grant set here.
    expect(directSource(rows[0]!.sources)?.version).toBe("v-jana");
    expect(rows[2]!.detail).toBe("Skupina · 3 členové");
  });

  it("explains a right through a group by the group, which is where it changes", () => {
    const petr = resourceRows(vaultAccess()).find((row) => row.key === "user:u-petr")!;
    expect(directSource(petr.sources)).toBeUndefined();
    expect(groupSource(petr.sources)?.groupId).toBe("g-ops");
    expect(sourcesSummary(petr.sources)).toBe("Přes skupinu Provoz tým");
    // Jen přidělené lists only the grants set on the vault.
    expect(resourceRows(vaultAccess("direct")).map((row) => row.key)).toEqual(["user:u-jana", "group:g-ops"]);
  });
});

describe("audit and issues", () => {
  it("explains Hub authority revocation and identity transitions with stable external identifiers", () => {
    const event: AccessAuditEvent = { id: "hub-1", occurredAt: "2026-10-01T10:00:00Z", actorId: "admin", actorName: "Správce", action: "hub.binding.changed", userId: null, userName: null, groupId: "group", groupName: "Vývoj", spaceId: null, spaceName: null, role: null, isActive: null, isWikiAdmin: null, canIssueTokens: null,
      hubConnectionId: "connection", externalGroupId: "opaque/group", hubReason: "binding.updated", hubBindingEnabled: false, hubGrantsWikiAdmin: false };
    const binding = auditActivity(event);
    expect(binding).toMatchObject({ action: "změnil(a) propojení skupiny s Hubem", target: "Vývoj", category: "hub" });
    expect(binding.details).toEqual(expect.arrayContaining([{ label: "ID skupiny v Hubu", value: "opaque/group" }, { label: "Členství z Hubu", value: "Vypnuto" }, { label: "Správce wiki přes Hub", value: "Vypnuto" }]));
    const identity = auditActivity({ ...event, action: "hub.identity.changed", actorId: null, actorName: "Synchronizace Hubu", userId: "person", userName: "Jana", groupId: null, groupName: null, externalGroupId: null, externalUserId: "opaque/user", hubReason: "identity.observed", hubBindingEnabled: null, hubGrantsWikiAdmin: null, hubPreviousState: "Active", hubCurrentState: "Disabled" });
    expect(identity).toMatchObject({ actor: "Synchronizace Hubu", action: "ověřil(a) stav účtu v Hubu", target: "Jana", category: "hub" });
    expect(identity.details).toEqual(expect.arrayContaining([{ label: "ID účtu v Hubu", value: "opaque/user" }, { label: "Změna stavu", value: "Aktivní → Deaktivovaný" }]));
  });

  it("reads a vault grant event as kdo co kdy with the level", () => {
    const event: AccessAuditEvent = { id: "1", occurredAt: "2026-09-26T10:00:00Z", actorId: "u-admin", actorName: "Admin", action: "resource_grant.added", userId: "u-jana", userName: "Jana", groupId: null, groupName: null, spaceId: "provoz", spaceName: "Provoz", role: "read", isActive: null, isWikiAdmin: null, canIssueTokens: null };
    expect(auditActivity(event)).toMatchObject({ actor: "Admin", action: "přidělil(a) přístup k trezoru", context: "účet Jana · prostor Provoz · úroveň Čte", category: "vault" });
    expect(auditActivity({ ...event, action: "group.created", role: null, userId: null, userName: null, groupId: "g", groupName: "Provoz tým" })).toMatchObject({ action: "založil(a) skupinu", target: "Provoz tým", category: "group" });
    expect(auditActivity({ ...event, action: "secret.release_denied", role: null, result: "forbidden", field: "password" })).toMatchObject({ action: "nedostal(a) heslo", category: "release", outcome: "denied" });
  });

  it("reads a password release with its technical details, never a value, and the client report as no proof of reading", () => {
    const base: AccessAuditEvent = { id: "7", occurredAt: "2026-09-27T08:12:04Z", actorId: "u-jana", actorName: "Jana", action: "secret.released", userId: null, userName: null, groupId: null, groupName: null, spaceId: "provoz", spaceName: "Provoz", role: null, isActive: null, isWikiAdmin: null, canIssueTokens: null,
      vaultId: "v-1", secretId: "s-1", vaultName: "Servery", secretTitle: "Router", result: "released", field: "password", operation: "copy", mode: "outsideTrustedNetworks", modeSource: "vault", matchedNetwork: "Firemní VPN", reauthUsed: false, clientIp: "10.6.2.17", sessionId: "abcdef123456", requestId: "0HN:12" };
    const released = auditActivity(base);
    expect(released).toMatchObject({ action: "zkopíroval(a)", target: "heslo · Router · trezor Servery", category: "release", outcome: "allowed", context: "prostor Provoz · Mimo důvěryhodné sítě" });
    const details = Object.fromEntries(released.details!.map((detail) => [detail.label, detail.value]));
    expect(details).toMatchObject({ "Adresa klienta": "10.6.2.17", "Síťové pravidlo a verze": "Firemní VPN", "Účinný režim a zdroj": "Mimo důvěryhodné sítě · nastavení trezoru", "Síťová výjimka": "Použita — Firemní VPN", "Nové přihlášení": "Nevyžadováno", "Relace": "…123456", "Request ID": "0HN:12" });
    const report = auditActivity({ ...base, action: "secret.client_report", result: "shown", operation: "reveal" });
    expect(report.details!.find((detail) => detail.label === "Klient hlásí")).toMatchObject({ value: "Zobrazeno na obrazovce", hint: "Hlášení prohlížeče, není důkaz přečtení." });
    expect(auditActivity({ ...base, action: "secret.release_denied", result: "reauth_required" })).toMatchObject({ outcome: "denied", badge: { label: "Vyžádáno přihlášení" } });
    expect(auditActivity({ ...base, action: "trusted_network.created", trustedAddressName: "Kancelář", trustedAddressRange: "10.5.0.0/16", configurationVersion: "4" })).toMatchObject({ action: "přidal(a) důvěryhodnou síť", target: "Kancelář", context: "10.5.0.0/16", category: "network" });
    expect(auditActivity({ ...base, action: "protection.set", secretId: null, secretTitle: null, protectionMode: "always" })).toMatchObject({ target: "trezor Servery", category: "protection", context: "prostor Provoz · Vždy" });
  });

  it("reads the vault audit from the V5 endpoint for every Spravuje holder", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (input: string) => {
      urls.push(input);
      return new Response(JSON.stringify({ items: [], hasMore: false, nextBeforeId: null }), { headers: { "Content-Type": "application/json" } });
    }));
    try {
      expect(vaultAuditSource.available({ isWikiAdmin: false, level: "manage" })).toBe(true);
      expect(vaultAuditSource.available({ isWikiAdmin: false, level: "read" })).toBe(false);
      expect(vaultAuditSource.available({ isWikiAdmin: true, level: null })).toBe(true);
      await vaultAuditSource.load("v-1", { limit: 20, beforeId: "c-9" });
      await loadVaultAudit({ secretId: "s-1" });
      expect(urls).toEqual(["/api/vaults/v-1/audit?limit=20&beforeId=c-9", "/api/secrets/s-1/audit?limit=50"]);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("lists what needs attention from vault states, locations and backups", () => {
    const issues = settingsIssues([vault("v-1"), vault("v-2", { status: "unavailable", lastFailure: "Sdílení neodpovídá." })], [], "Záloha selhala.", true);
    expect(issues.map((issue) => issue.id)).toEqual(["backups", "vault-v-2", "locations"]);
    expect(issues[1]!.target).toEqual({ hash: "#/spaces/provoz/settings/vaults/v-2/status" });
  });
});

describe("settings router guard", () => {
  it("puts the address back and asks instead of leaving a dirty page, and visit continues", async () => {
    history.replaceState(null, "", "/#/settings/people/accounts");
    let allow = false;
    const asked: string[] = [];
    let router!: ReturnType<typeof provideSettingsRoute>;
    wrapper = mount(defineComponent({
      setup() {
        router = provideSettingsRoute("#/settings", { guard: (target) => (asked.push(target), allow) });
        return () => h("p", router.route.value.path);
      },
    }));
    router.navigate("general/files");
    await flushPromises();
    expect(asked).toEqual(["#/settings/general/files"]);
    expect(location.hash).toBe("#/settings/people/accounts");
    expect(wrapper.text()).toBe("people/accounts");
    // Filters of the same page never ask.
    router.setQuery({ kdo: "u-1" });
    expect(asked).toHaveLength(1);
    router.visit("#/settings/general/files");
    await flushPromises();
    expect(wrapper.text()).toBe("general/files");
    allow = true;
    router.navigate("people/groups");
    await flushPromises();
    expect(wrapper.text()).toBe("people/groups");
  });
});

describe("global settings", () => {
  it("recounts Lidé in the rail on a move among the people pages, so an invited person counts at once", async () => {
    const person = (id: string) => ({ id, username: id, displayName: id, isActive: true, isWikiAdmin: false, version: "1", hasBuiltinCredential: true, source: "builtin", credentialState: "active" });
    let people = [person("u-admin")];
    fakeApi(baseApi((_method, url) => {
      if (url.pathname === "/api/admin/users/summary") return { total: people.length, pending: 0, deactivated: 0, admins: 1 };
      if (url.pathname === "/api/admin/groups") return [];
      return undefined;
    }));
    const view = await host("#/settings/security/overview");
    const count = () => view.get('a[href="#/settings/people/accounts"] .settings-nav-count').text();
    expect(count()).toContain("1");
    people = [person("u-admin"), person("u-jana")];
    await navigate("#/settings/people/groups");
    expect(count()).toContain("2");
  });
  it("shows the overview with K řešení from the vault states and the real protection and network tiles", async () => {
    fakeApi(baseApi());
    const view = await host("#/settings/security/overview");
    expect(view.get("h1").text()).toBe("Správa wiki");
    const nav = view.get('nav[aria-label="Správa wiki"]');
    expect(nav.findAll("h2").map((heading) => heading.text())).toEqual(["Lidé", "Prostory", "Trezory a hesla", "AI a integrace", "Provoz", "Server a úložiště"]);
    expect(nav.findAll("[data-path]").map((link) => link.attributes("data-path"))).toEqual([
      // Přehled, Audit · Lidé · Prostory · Trezory a hesla · AI a integrace · Provoz · Server a úložiště. The module list fails to load here, so its overview stands between AI and Import z Wiki.js.
      "security/overview", "security/audit",
      "people/accounts", "people/groups", "general/authentication",
      "access/spaces", "spaces/trash", "spaces/contacts",
      "security/vaults", "security/requests", "security/protection",
      "general/modules/ai", "general/modules", "general/modules/wiki-js-import", "people/tokens",
      "general/jobs", "general/logs", "general/backups",
      "general/storage", "general/files", "security/networks",
    ]);
    expect(view.get('[data-issue="vault-v-2"]').text()).toContain("Heslo nesedí.");
    expect(view.get('[data-issue="vault-v-2"] a').attributes("href")).toBe("#/spaces/provoz/settings/vaults/v-2/status");
    expect(view.get('[data-stat="protection"]').text()).toContain("Vždy");
    expect(view.get('[data-stat="networks"]').text()).toContain("0 aktivních sítí");
    expect(view.find("[data-soon]").exists()).toBe(false);
    expect(view.get('[data-stat="vaults"]').text()).toContain("1 vyžaduje pozornost");
  });

  it("confirms a retry of dead background messages and keeps the keyboard there when the row goes (UX3-5)", async () => {
    let dead = 1;
    fakeApi(baseApi((method, url) => {
      if (url.pathname === "/api/admin/outbox") return { pending: 0, dead, oldestPendingAt: null, lastFailedAt: null, lastError: null };
      if (url.pathname === "/api/admin/outbox/retry" && method === "POST") return { retried: (dead = 0, 1) };
      return undefined;
    }));
    const view = await host("#/settings/security/overview");
    const retry = view.get<HTMLButtonElement>('[data-issue-run="outbox"]');
    retry.element.focus();
    await retry.trigger("click");
    await flushPromises();
    expect(view.find('[data-issue="outbox"]').exists()).toBe(false);
    const done = view.get("[data-issue-done]");
    expect(done.attributes("role")).toBe("status");
    expect(done.text()).toContain("se zpracují znovu");
    expect(document.activeElement).toBe(done.element);
  });

  it("lists an integration module between AI and Import z Wiki.js, in place of the module overview", async () => {
    fakeApi(baseApi((_method, url) => {
      if (url.pathname === "/api/admin/modules") return { items: [{ id: "youtrack", title: "YouTrack", description: "Úkoly", settingsUi: "youtrack", order: 20, showInSetup: true }] };
      return undefined;
    }));
    const view = await host("#/settings/security/overview");
    await flushPromises();
    const paths = view.get('nav[aria-label="Správa wiki"]').findAll("[data-path]").map((link) => link.attributes("data-path"));
    expect(paths.slice(paths.indexOf("general/modules/ai"), paths.indexOf("people/tokens") + 1)).toEqual(["general/modules/ai", "general/modules/youtrack", "general/modules/wiki-js-import", "people/tokens"]);
  });

  it("routes authentication tabs and protects an unsaved login draft", async () => {
    fakeApi(baseApi((method, url) => {
      if (url.pathname === "/api/admin/modules") return { items: [
        { id: "oidc", title: "Firemní přihlášení", description: "Přihlášení", settingsUi: "oidc", order: 10, showInSetup: true },
        { id: "hub", title: "Skupiny Hubu", description: "Členství", settingsUi: "hub", order: 30, showInSetup: false },
      ] };
      if (url.pathname === "/api/admin/modules/oidc" && method === "GET") return { version: "v1", publicOrigin: "https://wiki.example", restartRequired: false, builtinEnabled: true, providers: [] };
      return undefined;
    }));
    const view = await host("#/settings/general/authentication/login");
    expect(view.get('[data-auth-panel="login"]').text()).toContain("Firemní přihlášení");
    await view.get('[data-origin-settings] button').trigger("click");
    await view.get("[data-oidc-origin]").setValue("https://new-wiki.example");
    await navigate("#/settings/general/authentication/hub");
    expect(location.hash).toBe("#/settings/general/authentication/login");
    expect(view.find('[data-auth-panel="hub"]').exists()).toBe(false);
    // The open one: the login panel also holds its closed „Uložit přihlášení“ confirmation (fresh sign-in, V4).
    expect(view.get("dialog[open]").text()).toContain("Zahodit");
  });
  it("routes the people sections to their pages and lists vaults across spaces with links into their space", async () => {
    fakeApi(baseApi());
    const view = await host("#/settings/people/accounts/new");
    expect(view.get("[data-users]").attributes("data-users")).toBe("new");
    await navigate("#/settings/security/vaults");
    expect(view.get('[data-open-vault="v-1"]').attributes("href")).toBe("#/spaces/provoz/settings/vaults/v-1/status");
    await navigate("#/settings/does/not-exist");
    expect(view.text()).toContain("Tato sekce neexistuje");
  });

  it("lists the spaces under Prostory, opens one with its tabs and names every enabled plugin kind in the menu", async () => {
    fakeApi(baseApi((method, url) => {
      if (url.pathname === "/api/access/kinds")
        return [
          { id: "space", one: "Prostor", many: "Prostory", icon: "book-open", parent: null, plugin: null, grantable: true, levels: [{ level: "read", description: "Čte" }] },
          { id: "youtrack", one: "YouTrack projekt", many: "YouTrack", icon: "blocks", parent: "space", plugin: "YouTrack", grantable: false, levels: [{ level: "read", description: "Vidí úkoly" }] },
        ];
      if (url.pathname === "/api/access/resources/space/provoz") return { resource: { kind: "space", id: "provoz", title: "Provoz" }, canManage: true, automatic: [], entries: [], hasMore: false, exceptions: [] };
      if (url.pathname === "/api/access/resources" && url.searchParams.get("kind") === "youtrack") return [{ kind: "youtrack", id: "yt-ops", title: "OPS", spaceName: "Provoz" }];
      return undefined;
    }));
    const view = await host("#/settings/access/spaces");
    await flushPromises();
    const paths = view.get('nav[aria-label="Správa wiki"]').findAll("[data-path]").map((link) => link.attributes("data-path"));
    expect(paths[paths.indexOf("access/spaces") + 1]).toBe("access/youtrack");
    expect(view.get('[data-open-space="provoz"]').attributes("href")).toBe("#/settings/access/spaces/provoz");
    await navigate("#/settings/access/spaces/provoz");
    expect(view.findAll(".settings-tabs a").map((tab) => tab.attributes("href"))).toEqual([
      "#/settings/access/spaces/provoz", "#/settings/access/spaces/provoz/exceptions", "#/settings/access/spaces/provoz/links", "#/settings/access/spaces/provoz/activity",
    ]);
    expect(view.get("[data-space-settings]").attributes("href")).toBe("#/spaces/provoz/settings/general");
    expect(view.get("[data-add-access]").attributes("href")).toBe("#/settings/access/spaces/provoz/new");
    await navigate("#/settings/access/youtrack");
    await new Promise((resolve) => setTimeout(resolve, 0));
    await flushPromises();
    expect(view.get('[data-kind-resource="yt-ops"]').text()).toContain("OPS");
  });

  it("adds a location through the location wizard and returns to the list with the result", async () => {
    fakeApi(baseApi((method, url, body) => {
      if (url.pathname === "/api/vault-locations/test") return { message: "Složka je dostupná." };
      if (url.pathname === "/api/vault-locations" && method === "POST") return { id: "loc-2", ...(body as object), hasPassword: false, vaultCount: 0 };
      return undefined;
    }));
    const view = await host("#/settings/general/files/new");
    await view.get('[data-value="local"] [role="radio"]').trigger("click");
    await flushPromises();
    await view.get("[data-location-name]").setValue("Disk serveru");
    await view.get("[data-location-root]").setValue("D:\\Atlas\\trezory");
    await view.get("[data-location-test]").trigger("click");
    await flushPromises();
    expect(view.get("[data-location-test-result]").text()).toContain("Složka je dostupná.");
    await view.get("form.location-form").trigger("submit");
    await flushPromises();
    expect(calls.find((call) => call.method === "POST" && call.path === "/api/vault-locations")?.body).toEqual({ name: "Disk serveru", kind: "local", root: "D:\\Atlas\\trezory", username: "", domain: "", password: "" });
    expect(location.hash).toBe("#/settings/general/files");
    expect(view.get("[data-result-banner]").text()).toContain("Disk serveru");
    expect(view.find('[data-location="loc-2"]').exists()).toBe(true);
  });

  it("asks about unsaved changes of a panel instead of blocking the navigation", async () => {
    fakeApi(baseApi());
    const view = await host("#/settings/general/files/new");
    await view.get('[data-value="smb"] [role="radio"]').trigger("click");
    await view.get("[data-location-name]").setValue("Rozepsané");
    await flushPromises();
    expect(view.emitted("dirty")?.at(-1)).toEqual([true]);
    await navigate("#/settings/people/groups");
    expect(location.hash).toBe("#/settings/general/files/new");
    expect(document.body.textContent).toContain("Máte rozepsané změny");
    const discard = [...document.querySelectorAll("button")].find((button) => button.textContent === "Zahodit")!;
    discard.click();
    await flushPromises();
    expect(location.hash).toBe("#/settings/people/groups");
  });

  describe("Uložit in that question, when the page's save throws", () => {
    // useSettingsDocument rejects save() when fill or onSaved throws after the answer is stored. The error
    // still goes to Vue's handler; whether the leave goes on depends on what the page left unsaved.
    async function saveAfterThrow(stored: boolean) {
      const dirty = ref(false);
      const reported: unknown[] = [];
      config.global.config.errorHandler = (error) => { reported.push(error); };
      accountsPage.install = () => useDraftGuard(dirty, {
        save: async () => {
          dirty.value = !stored;
          throw new Error("onSaved failed");
        },
        discard: () => { dirty.value = false; },
      });
      try {
        fakeApi(baseApi());
        await host("#/settings/people/accounts/new");
        dirty.value = true;
        await flushPromises();
        await navigate("#/settings/general/files");
        expect(document.body.textContent).toContain("Máte rozepsané změny");
        [...document.querySelectorAll("button")].find((button) => button.textContent === "Uložit")!.click();
        await flushPromises();
        expect(reported.map((error) => (error as Error).message)).toEqual(["onSaved failed"]);
      } finally {
        accountsPage.install = undefined;
        delete config.global.config.errorHandler;
      }
    }

    it("leaves when the document was stored first", async () => {
      await saveAfterThrow(true);
      expect(location.hash).toBe("#/settings/general/files");
      // The question stays mounted, closed, so it can leave with motion (UX cycle 1, SH-2).
      expect([...document.querySelectorAll("dialog[open]")].map(dialog => dialog.textContent).join()).not.toContain("Máte rozepsané změny");
    });

    it("keeps the question open while the draft is still unsaved", async () => {
      await saveAfterThrow(false);
      expect(location.hash).toBe("#/settings/people/accounts/new");
      expect(document.body.textContent).toContain("Máte rozepsané změny");
    });
  });
});

describe("space settings", () => {
  it("shows the space's vaults and routes its sections to their pages", async () => {
    fakeApi(baseApi());
    const view = await host("#/spaces/provoz/settings/vaults");
    expect(view.get("h1").text()).toBe("Provoz");
    expect(view.findAll("[data-vault]").map((card) => card.attributes("data-vault"))).toEqual(["v-1", "v-2"]);
    expect(view.get('[data-vault="v-1"]').attributes("href")).toBe("#/spaces/provoz/settings/vaults/v-1/status");
    await navigate("#/spaces/provoz/settings/access/new");
    expect(view.get("[data-space-access]").attributes("data-space-access")).toBe("provoz:new");
  });

  it("changes a grant level with its version, revokes after a confirmation and reloads on a conflict", async () => {
    let conflict = false;
    fakeApi(baseApi((method, url) => {
      if (url.pathname === "/api/access/resources/vault/v-1/grants" && conflict) return new Response(JSON.stringify({ detail: "Změněno" }), { status: 409 });
      return undefined;
    }));
    const view = await host("#/spaces/provoz/settings/vaults/v-1/access");
    const jana = () => view.get('[data-access-entry="user:u-jana"]');
    expect(view.find('[data-resource-access="vault"]').exists()).toBe(true);
    expect(view.get('[data-access-entry="automatic:1"]').text()).toContain("Správci wiki");
    // A right through a group is changed on the group, not here.
    expect(view.get('[data-access-entry="user:u-petr"]').find("[data-change-access]").exists()).toBe(false);
    await jana().get("[data-change-access]").trigger("click");
    await jana().get('[role="radio"][data-value="manage"]').trigger("click");
    await jana().get("[data-save-access]").trigger("click");
    await flushPromises();
    // A grant change is a security change: nothing is sent before the fresh sign-in for this vault.
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
    expect(document.querySelector("[data-access-change]")?.textContent).toContain("Jana: Čte → Spravuje");
    await reauth();
    // Grant changes bind their fresh sign-in to the global security target (docs/access-model.md §3).
    expect(calls.find((call) => call.path === "/api/security/reauth")?.body).toEqual({ operation: "security" });
    expect(calls.find((call) => call.method === "PUT")).toMatchObject({
      path: "/api/access/resources/vault/v-1/grants",
      body: { subjectKind: "user", subjectId: "u-jana", level: "manage", startAt: null, expiresAt: null, expectedVersion: "v-jana" },
    });
    expect(view.get("[data-result-banner]").text()).toContain("Přístup pro Jana je uložený: Spravuje");

    await view.get('[data-access-entry="group:g-ops"] [data-revoke-access]').trigger("click");
    await flushPromises();
    expect(document.body.textContent).toContain("Provoz tým přijde o přímý přístup Vidí");
    await reauth();
    expect(calls.some((call) => call.method === "DELETE" && call.path === "/api/access/resources/vault/v-1/grants/group/g-ops?version=v-ops")).toBe(true);

    conflict = true;
    const reads = calls.filter((call) => call.path.startsWith("/api/access/resources/vault/v-1?")).length;
    await jana().get("[data-change-access]").trigger("click");
    await jana().get('[role="radio"][data-value="discover"]').trigger("click");
    await jana().get("[data-save-access]").trigger("click");
    await flushPromises();
    await reauth();
    expect(view.get("[data-access-conflict]").text()).toContain("mezitím změnil někdo jiný");
    expect(calls.filter((call) => call.path.startsWith("/api/access/resources/vault/v-1?")).length).toBe(reads + 1);
  });

  it("reads the vault audit from V5 for a space administrator too and shows the vault's protection with its origin", async () => {
    fakeApi(baseApi());
    const admin = await host("#/spaces/provoz/settings/vaults/v-1/audit", { isWikiAdmin: false });
    expect(calls.some((call) => call.path.startsWith("/api/vaults/v-1/audit?"))).toBe(true);
    expect(calls.some((call) => call.path.startsWith("/api/admin/access-audit"))).toBe(false);
    expect(admin.text()).not.toContain("připravujeme");
    await navigate("#/spaces/provoz/settings/vaults/v-1/protection");
    expect(admin.get(".effective-value").text()).toContain("Zděděno z úrovně instance");
    expect(admin.get(".effective-current").text()).toBe("Vždy");
    expect(admin.text()).toContain("Pro vás teď: nové přihlášení před vydáním");
  });

  it("archives a vault only after a destructive confirmation", async () => {
    fakeApi(baseApi());
    const view = await host("#/spaces/provoz/settings/vaults/v-1/status");
    await view.get("[data-vault-archive]").trigger("click");
    await flushPromises();
    expect(calls.some((call) => call.path.endsWith("/archive"))).toBe(false);
    (document.querySelector("[data-confirm-action]") as HTMLButtonElement).click();
    await flushPromises();
    expect(calls.some((call) => call.method === "POST" && call.path === "/api/vaults/v-1/archive")).toBe(true);
    expect(location.hash).toBe("#/spaces/provoz/settings/vaults");
    expect(view.find('[data-vault="v-1"]').exists()).toBe(false);
  });
});

describe("VaultWizard on the real API shape", () => {
  it("reveals valid sections immediately, keeps edits available, and creates only after explicit confirmation", async () => {
    const submit = vi.fn(async () => ({ id: "v", name: "Servery", entryCount: 0, generatedPassword: "one-time-generated-password" }));
    wrapper = mount(VaultWizard, { props: { spaces: [{ value: "provoz", label: "Provoz" }], spaceId: "provoz", locations: [{ id: "loc-1", name: "FS01", kind: "smb", root: null, vaultCount: 1 }], canAddLocation: false, submit }, attachTo: document.body });
    await wrapper.get('[data-value="create"] [role="radio"]').trigger("click");
    expect(wrapper.find("[data-flow-next]").exists()).toBe(false);
    expect(wrapper.find('[data-value="generate"]').exists()).toBe(false);
    await wrapper.get("[data-vault-name]").setValue("Servery");
    await flushPromises();
    expect(wrapper.find('[data-value="generate"]').exists()).toBe(true);
    expect(wrapper.get("[data-flow-next]").text()).toContain("Založit trezor");
    await wrapper.get("[data-vault-name]").setValue("");
    expect(wrapper.find('[data-value="generate"]').exists()).toBe(true);
    expect(wrapper.get('[data-value="generate"] button').element.closest("fieldset")?.disabled).toBe(true);
    expect(wrapper.get("[data-flow-next]").attributes("disabled")).toBeDefined();
    await wrapper.get("[data-vault-name]").setValue("Servery");
    await flushPromises();
    expect(submit).not.toHaveBeenCalled();
    await wrapper.get("[data-flow-next]").trigger("click");
    await flushPromises();
    expect(submit).toHaveBeenCalledWith({ mode: "create", vault: { spaceId: "provoz", locationId: "loc-1", name: "Servery", fileName: "servery.kdbx" }, password: null, newLocation: undefined });
    await wrapper.get('.one-time-secret button[aria-pressed]').trigger("click");
    expect(wrapper.get("[data-one-time-value]").text()).toBe("one-time-generated-password");
    expect(wrapper.find("[data-vault-name]").exists()).toBe(false);
  });

  it("clears a selected attachment when its location changes and ignores late listings", async () => {
    let resolveOld!: (result: { files: { name: string; sizeBytes: number; lastWriteAt: string; registered: boolean; usedBy: null }[]; truncated: boolean }) => void;
    let oldSignal: AbortSignal | undefined;
    const file = (name: string) => ({ name, sizeBytes: 1, lastWriteAt: "2026-09-21T10:00:00Z", registered: false, usedBy: null });
    const listFiles = vi.fn((location: string, query: string, signal?: AbortSignal) => {
      if (query) { oldSignal = signal; return new Promise<{ files: ReturnType<typeof file>[]; truncated: boolean }>((resolve) => { resolveOld = resolve; }); }
      return Promise.resolve({ files: [file(location === "loc-1" ? "old.kdbx" : "new.kdbx")], truncated: false });
    });
    wrapper = mount(VaultWizard, { props: { spaces: [{ value: "provoz", label: "Provoz" }], spaceId: "provoz", locations: ["loc-1", "loc-2"].map((id) => ({ id, name: id, kind: "smb" as const, root: null, vaultCount: 0 })), canAddLocation: false, submit: vi.fn(), listFiles }, attachTo: document.body });
    await wrapper.get('[data-value="attach"] [role="radio"]').trigger("click");
    await wrapper.get('[data-value="loc-1"] [role="radio"]').trigger("click");
    await flushPromises();
    await wrapper.get('[role="radio"][data-value="old.kdbx"]').trigger("click");
    await wrapper.get("[data-vault-attach-password]").setValue("old-secret");
    vi.useFakeTimers();
    await wrapper.get('input[type="search"]').setValue("old");
    await vi.advanceTimersByTimeAsync(300);
    vi.useRealTimers();
    await wrapper.get('[data-value="loc-2"] [role="radio"]').trigger("click");
    await flushPromises();
    expect(oldSignal?.aborted).toBe(true);
    resolveOld({ files: [file("stale.kdbx")], truncated: false });
    await flushPromises();
    expect(wrapper.find('[data-value="stale.kdbx"]').exists()).toBe(false);
    expect(wrapper.find('[data-value="new.kdbx"]').exists()).toBe(true);
    expect((wrapper.get("[data-vault-attach-password]").element as HTMLInputElement).value).toBe("");
    expect(wrapper.get("[data-flow-next]").attributes("disabled")).toBeDefined();
  });

  it("picks the file to attach from the folder listing, registered files disabled, typing only after a failed listing", async () => {
    let fail = false;
    const listFiles = vi.fn(async (_location: string, query: string) => {
      if (fail) throw new Error("Umístění je nedostupné.");
      const all = [
        { name: "provoz.kdbx", sizeBytes: 2048, lastWriteAt: "2026-09-20T10:00:00Z", registered: true, usedBy: "Provoz" },
        { name: "zaloha.kdbx", sizeBytes: 4096, lastWriteAt: "2026-09-21T10:00:00Z", registered: false, usedBy: null },
        { name: "záloha.kdbx", sizeBytes: 4096, lastWriteAt: "2026-09-21T10:00:00Z", registered: false, usedBy: null },
      ];
      return { files: all.filter((file) => file.name.includes(query)), truncated: false };
    });
    const submit = vi.fn(async () => ({ id: "v", name: "Záloha", entryCount: 4, generatedPassword: null }));
    const view = mount(VaultWizard, {
      props: { spaces: [{ value: "provoz", label: "Provoz" }], spaceId: "provoz", locations: [{ id: "loc-1", name: "FS01", kind: "smb", root: null, vaultCount: 1 }], canAddLocation: false, submit, listFiles },
      attachTo: document.body,
    });
    await view.get('[data-value="attach"] [role="radio"]').trigger("click");
    await flushPromises();
    expect(listFiles).toHaveBeenCalledWith("loc-1", "", expect.any(AbortSignal));
    const registered = view.get('[role="radio"][data-value="provoz.kdbx"]');
    expect(registered.attributes("data-disabled")).toBeDefined();
    expect(view.text()).toContain("Už je připojený jako trezor „Provoz“");
    expect(view.find("[data-vault-attach-file]").exists()).toBe(false);
    await view.get('[role="radio"][data-value="zaloha.kdbx"]').trigger("click");
    await flushPromises();
    expect((view.get("[data-vault-name]").element as HTMLInputElement).value).toBe("Zaloha");
    await view.get("[data-vault-attach-password]").setValue("old-password");
    await view.get('[role="radio"][data-value="záloha.kdbx"]').trigger("click");
    expect((view.get("[data-vault-attach-password]").element as HTMLInputElement).value).toBe("");
    await view.get("[data-vault-attach-password]").setValue("tajne");
    expect(view.get("[data-flow-next]").attributes("disabled")).toBeUndefined();
    // A failed listing offers a retry and, only then, typing the name.
    fail = true;
    vi.useFakeTimers();
    await view.get('input[type="search"]').setValue("zal");
    await vi.advanceTimersByTimeAsync(300);
    vi.useRealTimers();
    await flushPromises();
    expect(view.get("[data-vault-files-error]").text()).toContain("Umístění je nedostupné.");
    (view.get("[data-vault-manual-file]").element as HTMLButtonElement).click();
    await flushPromises();
    expect(view.find("[data-vault-attach-file]").exists()).toBe(true);
    view.unmount();
  });

  it("asks for the file name when the location lists no files and shows a refused attach on the summary", async () => {
    const submit = vi.fn(async () => {
      throw new Error("Heslo souboru nesedí.");
    });
    const view = mount(VaultWizard, {
      props: { spaces: [{ value: "provoz", label: "Provoz" }], spaceId: "provoz", locations: [{ id: "loc-1", name: "FS01", kind: "smb", root: null, vaultCount: 1 }], canAddLocation: false, submit },
      attachTo: document.body,
    });
    const next = async () => {
      await view.get("[data-flow-next]").trigger("click");
      await flushPromises();
    };
    await view.get('[data-value="attach"] [role="radio"]').trigger("click");
    await flushPromises();
    // A single location is preselected for someone who cannot add one; roots stay hidden.
    expect(view.get('[data-value="loc-1"] [role="radio"]').attributes("aria-checked")).toBe("true");
    expect(view.text()).not.toContain("smb://");
    await view.get("[data-vault-attach-file]").setValue("../ven.kdbx");
    await view.get("[data-vault-name]").setValue("Starý provoz");
    expect(view.text()).toContain("Zadejte název souboru .kdbx bez lomítek.");
    expect(view.find("[data-vault-attach-password]").exists()).toBe(false);
    await view.get("[data-vault-attach-file]").setValue("provoz.kdbx");
    await view.get("[data-vault-attach-password]").setValue("tajne");
    expect(submit).not.toHaveBeenCalled();
    await next();
    expect(submit).toHaveBeenCalledWith({ mode: "attach", vault: { spaceId: "provoz", locationId: "loc-1", name: "Starý provoz", fileName: "provoz.kdbx" }, password: "tajne", newLocation: undefined });
    expect(view.get("[data-vault-wizard-error]").text()).toBe("Heslo souboru nesedí.");
    view.unmount();
  });
});
