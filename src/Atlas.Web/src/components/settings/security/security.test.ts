import { describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import { mount } from "@vue/test-utils";
import { ApiError, clearCsrf } from "../../../api";
import { protectionApi, reauthWindow, type ConnectionView, type ProtectionLevelView, type ReauthStart } from "../../../protection";
import { settingsIssues } from "../global/issues";
import { createAccessRequestFeed } from "../requests/accessRequestFeed";
import { createApiReauthEnvironment } from "./apiReauth";
import { connectionSteps, testerSteps } from "./connection";
import { inheritedFromText, inheritedMode, originChain } from "./protection";
import { createSecurityState } from "./securityState";

const start = (method: "password" | "oidc", redirectUrl: string | null = null): ReauthStart => ({ id: "c-1", method, providerId: method === "oidc" ? "hub" : null, providerName: "Hub", expiresAt: new Date(Date.now() + 60_000).toISOString(), redirectUrl });
const status = (state: string, failure: string | null = null) => ({ id: "c-1", operation: "security", targetKind: "instance", targetId: null, method: "oidc", state, failure, expiresAt: "", proofExpiresAt: null }) as never;
function security(freshAuth: "supported" | "unknown" | "unsupported", method: "password" | "oidc" | null) {
  const state = createSecurityState(async () => ({ globalSecurityAdmin: true, managesAllVaults: true, vaultManagerSpaces: [], vaults: [], freshAuth, freshAuthMethod: method }));
  return state.load().then(() => state);
}

describe("fresh sign-in on the V4 API", () => {
  it("proves a local password for the change's scope and answers the proof id; a wrong password is a failure, not unsupported", async () => {
    const api = { start: vi.fn(async () => start("password")), password: vi.fn(async () => status("verified")), status: vi.fn() };
    const environment = createApiReauthEnvironment(await security("supported", "password"), api);
    expect(environment.method).toBe("local");
    expect(await environment.verifyPassword("heslo", { operation: "security", target: { vaultId: "v-1" } })).toEqual({ ok: true, proofId: "c-1" });
    expect(api.start).toHaveBeenCalledWith("security", { vaultId: "v-1" });
    api.password.mockRejectedValueOnce(new ApiError(403, "Heslo nesouhlasí.", "reauth_password"));
    expect(await environment.verifyPassword("spatne", { operation: "security", target: {} })).toEqual({ ok: false, reason: "password" });
    api.start.mockRejectedValueOnce(new ApiError(403, "Neumí doložit.", "fresh_auth_unsupported"));
    expect(await environment.verifyPassword("heslo", { operation: "security", target: {} })).toMatchObject({ ok: false, reason: "unsupported" });
  });

  it("opens the ten-minute window with a verification or from security/me, and a reauth_required refusal closes it", async () => {
    const until = new Date(Date.now() + 600_000).toISOString();
    reauthWindow.close();
    const api = { start: vi.fn(async () => start("password")), password: vi.fn(async () => ({ ...(status("verified") as object), windowExpiresAt: until }) as never), status: vi.fn() };
    const environment = createApiReauthEnvironment(await security("supported", "password"), api);
    expect(environment.windowUntil).toBeNull();
    await environment.verifyPassword("heslo", { operation: "security", target: { vaultId: "v-1" } });
    expect(reauthWindow.isOpen()).toBe(true);
    expect(environment.windowUntil).toBeLessThan(Date.parse(until));
    // The server stays the judge: its refusal of a change closes the window here.
    clearCsrf();
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(Response.json({ token: "csrf" }))
      .mockResolvedValueOnce(Response.json({ title: "Nové přihlášení.", code: "reauth_required" }, { status: 403 })));
    await expect(protectionApi.set("instance", undefined, "always", "1", null)).rejects.toMatchObject({ status: 403, code: "reauth_required" });
    expect(reauthWindow.isOpen()).toBe(false);
    vi.unstubAllGlobals();
    // A reload (or another tab of the session) learns the open window from security/me.
    const reloaded = createSecurityState(async () => ({ globalSecurityAdmin: true, managesAllVaults: true, vaultManagerSpaces: [], vaults: [], freshAuth: "supported", freshAuthMethod: "password", reauthWindowExpiresAt: until }));
    await reloaded.load();
    expect(reauthWindow.isOpen()).toBe(true);
    reauthWindow.close();
  });

  it("opens the Hub popup inside the click, and a missing auth_time makes it unsupported", async () => {
    const popup = { closed: false, location: { href: "" }, close: vi.fn(() => (popup.closed = true)) };
    const open = vi.fn(() => popup as unknown as Window);
    const api = { start: vi.fn(async () => start("oidc", "https://hub.test/auth")), password: vi.fn(), status: vi.fn(async () => status("failed", "auth_time_missing")) };
    const environment = createApiReauthEnvironment(await security("unknown", "oidc"), api, open);
    expect(environment.method).toBe("hub");
    expect(environment.notice).toContain("nebyla ověřena");
    const outcome = environment.verifyHub({ operation: "security", target: {} });
    expect(open).toHaveBeenCalledTimes(1);
    await vi.waitFor(() => expect(popup.location.href).toBe("https://hub.test/auth"));
    popup.closed = true;
    expect(await outcome).toMatchObject({ ok: false, reason: "unsupported" });
    const blocked = createApiReauthEnvironment(await security("supported", "oidc"), api, () => null);
    expect(await blocked.verifyHub({ operation: "security", target: {} })).toEqual({ ok: false, reason: "popup" });
  });

  it("blocks when security/me says unsupported, with the reason", async () => {
    const environment = createApiReauthEnvironment(await security("unsupported", null), { start: vi.fn(), password: vi.fn(), status: vi.fn() });
    expect(environment.supported).toBe(false);
    expect(environment.unsupportedReason).toContain("nemá způsob přihlášení");
  });
});

describe("protection chain and connection diagnostics", () => {
  it("names what a level would inherit and marks each level of the chain", () => {
    const chain: ProtectionLevelView[] = [{ level: "instance", targetId: null, name: null, mode: "always" }, { level: "vault", targetId: "v", name: "Servery", mode: "never" }, { level: "secret", targetId: "s", name: "Router", mode: null }];
    expect(inheritedMode(chain)).toEqual({ mode: "never", level: "vault" });
    expect(inheritedFromText(chain)).toBe("trezoru Servery");
    expect(originChain(chain).map((step) => [step.level, step.explicit, step.value])).toEqual([["Instance", true, "Vždy"], ["Trezor", true, "Nevyžadovat"], ["Heslo", false, undefined]]);
  });

  it("explains address → derivation → rule → decision, and never trusts a header from an unknown peer", () => {
    const view: ConnectionView = { address: "203.0.113.9", source: "direct", reason: "untrustedForwarding", peer: "203.0.113.9", peerIsProxy: false, hops: 0, forwardedFor: ["10.5.3.24"], network: null, networkException: false, instanceMode: "outsideTrustedNetworks", reauthRequired: true, version: "4" };
    const steps = connectionSteps(view, 1);
    expect(steps.map((step) => step.value)).toEqual(["203.0.113.9", "Hlavička ignorována", "Žádné aktivní pravidlo", "Jiná síť, nové přihlášení"]);
    expect(testerSteps({ address: "10.5.8.1", addressError: null, isProxy: false, network: null, inactiveMatches: [{ id: "n", name: "Stará VPN", kind: "vpn", range: "10.5.0.0/16" }], draft: null })[1]).toMatchObject({ tone: "warning", detail: expect.stringContaining("Stará VPN") });
  });

  it("adds pending requests, a mode without networks and unproven fresh sign-in to K řešení", () => {
    const issues = settingsIssues([], [{ id: "l" } as never], null, true, { pendingRequests: 3, freshAuth: "unknown", outsideWithoutNetworks: true });
    expect(issues.map((issue) => issue.id)).toEqual(["requests", "no-networks", "fresh-auth"]);
    expect(issues[0]!.title).toBe("3 žádosti o přístup čekají na vyřízení");
  });
});

describe("access request feed", () => {
  it("reloads on accessRequestChanged and drops a decided request at once", async () => {
    let listener: (() => void) | undefined;
    const list = vi.fn(async () => ({ items: [{ id: "r-1", spaceId: "p", vaultId: "v", state: "pending" }, { id: "r-2", spaceId: "q", vaultId: "w", state: "pending" }] as never[], hasMore: false }));
    let feed!: ReturnType<typeof createAccessRequestFeed>;
    const view = mount(defineComponent({ setup() { feed = createAccessRequestFeed({ list, events: () => ({ subscribe: () => () => {}, onRequestChanged: (callback) => { listener = () => callback({ secretId: null, state: "approved" }); return () => (listener = undefined); }, revokeAll: () => {} }) }); return () => h("i"); } }));
    await feed.load();
    feed.listen();
    expect([feed.count.value, feed.inSpace("p").length, feed.inVault("w").length]).toEqual([2, 1, 1]);
    feed.settle({ id: "r-1" } as never);
    expect(feed.count.value).toBe(1);
    listener!();
    await vi.waitFor(() => expect(list).toHaveBeenCalledTimes(2));
    view.unmount();
    expect(listener).toBeUndefined();
  });
});
