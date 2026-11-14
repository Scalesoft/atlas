import { afterEach, describe, expect, it, vi } from "vitest";
import { mount, flushPromises, type VueWrapper } from "@vue/test-utils";
import PersonalApiTokens from "./PersonalApiTokens.vue";
import { api, post } from "../../api";
import { reauthWindow } from "../../protection";
vi.mock("../../api", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  api: vi.fn(),
  post: vi.fn(),
}));
let wrapper: VueWrapper | undefined;
const token = {
  id: "token-id",
  name: "Automation",
  prefix: "atlas_test",
  scopes: ["read"],
  activeFrom: "2026-01-01T00:00:00Z",
  expiresAt: "2099-01-01T00:00:00Z",
  activeTo: null,
};
const catalog = (canIssueTokens: boolean, tokens: object[] = [token], extra: object = {}) => ({
  canIssueTokens, permissionVersion: canIssueTokens ? "v1" : null, tokens, apiBaseUrl: "/api/v1", openApiUrl: "/api/v1/openapi.json",
  level: canIssueTokens ? "write" : "none", mcpEnabled: false, mcpUrl: null, ...extra,
});
const rights = (extra: object) => ({ ...token, channels: ["rest"], effectiveScopes: ["read"], usable: true, ...extra });
/** The catalog for every read, `created` for the issuing POST (which goes out as a security change, through api). */
function answer(read: () => unknown, created: unknown = { token, secret: "s" }) {
  vi.mocked(api).mockImplementation(async (_path, options) => (options?.method === "POST" ? created : read()) as never);
}
/** The bodies of the issuing POSTs. */
const issued = () => vi.mocked(api).mock.calls.filter(([path, options]) => path === "/profile/api-tokens" && options?.method === "POST").map(([, options]) => JSON.parse(String(options!.body)));
/** Confirms the summary inside the re-authentication window this session already has (SEC-A-2). */
async function confirmIssue(view: VueWrapper) {
  reauthWindow.sync(new Date(Date.now() + 600_000).toISOString());
  await flushPromises();
  await view.get("[data-confirm-reauth]").trigger("submit");
  await flushPromises();
}
async function startCreate(level: string, extra: object = {}, created?: unknown) {
  answer(() => catalog(true, [], { level, ...extra }), created);
  wrapper = mount(PersonalApiTokens, { attachTo: document.body });
  await flushPromises();
  await wrapper.get("[data-token-new]").trigger("click");
  await wrapper.get("[data-token-name]").setValue("Automation");
  await flushPromises();
  return wrapper;
}
afterEach(() => {
  wrapper?.unmount();
  vi.resetAllMocks();
  reauthWindow.close();
});
describe("personal API permission boundaries", () => {
  it("shows persistent MCP connection steps without exposing a stored token", async () => {
    vi.mocked(api).mockResolvedValue(catalog(true, [], { mcpEnabled: true, mcpUrl: "https://wiki.example/mcp" }));
    wrapper = mount(PersonalApiTokens, { attachTo: document.body });
    await flushPromises();
    expect(wrapper.get("[data-mcp-profile-guide]").text()).toContain("HTTP");
    expect(wrapper.get("[data-mcp-profile-url]").text()).toBe("https://wiki.example/mcp");
    expect(wrapper.get("[data-mcp-profile-guide]").text()).toContain("Authorization: Bearer");
    expect(wrapper.get("[data-mcp-profile-guide]").text()).toContain("kanálem MCP");
    expect(wrapper.find("[data-token-new]").exists()).toBe(true);
  });

  it("explains denied issuance on the MCP page without showing empty token controls", async () => {
    vi.mocked(api).mockResolvedValue(catalog(false, [], { mcpEnabled: true, mcpUrl: "https://wiki.example/mcp" }));
    wrapper = mount(PersonalApiTokens, { attachTo: document.body });
    await flushPromises();
    expect(wrapper.get("[data-mcp-profile-guide]").text()).toContain("Správce wiki");
    expect(wrapper.find("[data-token-new]").exists()).toBe(false);
    expect(wrapper.find("[data-api-docs]").exists()).toBe(false);
    expect(wrapper.find("[data-token-revoke]").exists()).toBe(false);
    expect(wrapper.find("[data-mcp-profile-url]").exists()).toBe(true);
  });
  it("revokes through the owner endpoint after a destructive confirmation and keeps a failure in the dialog", async () => {
    vi.mocked(api).mockResolvedValueOnce(catalog(false)).mockRejectedValueOnce(new Error("Server je dočasně nedostupný."));
    wrapper = mount(PersonalApiTokens, { attachTo: document.body });
    await flushPromises();
    await wrapper.get("[data-token-revoke]").trigger("click");
    const confirm = wrapper.get("[data-confirm-action]");
    expect(confirm.attributes("data-variant")).toBe("destructive");
    await confirm.trigger("click");
    await flushPromises();
    expect(api).toHaveBeenLastCalledWith("/profile/api-tokens/token-id", { method: "DELETE" });
    expect(wrapper.get("dialog [role=alert]").text()).toContain("nedostupný");
    expect(wrapper.get("dialog").attributes("open")).toBeDefined();
    expect(wrapper.find("[data-result-banner]").exists()).toBe(false);
  });
  it("does not offer creation without opt-in, and cancelling a revocation sends nothing", async () => {
    vi.mocked(api).mockResolvedValue(catalog(false));
    wrapper = mount(PersonalApiTokens, { attachTo: document.body });
    await flushPromises();
    expect(wrapper.find("[data-token-new]").exists()).toBe(false);
    expect(wrapper.text()).toContain("ani správce wiki");
    await wrapper.get("[data-token-revoke]").trigger("click");
    await wrapper.get("[data-confirm-cancel]").trigger("click");
    expect(api).toHaveBeenCalledTimes(1);
    expect(post).not.toHaveBeenCalled();
  });
  it("creates a read-only 90-day token in place and forgets the one-time value after acknowledgment", async () => {
    let reads = 0;
    answer(() => catalog(true, reads++ ? [token] : []), { token, secret: "one-time-secret" });
    wrapper = mount(PersonalApiTokens, { attachTo: document.body });
    await flushPromises();
    await wrapper.get("[data-token-new]").trigger("click");
    await wrapper.get("[data-token-name]").setValue("Automation");
    await flushPromises();
    await confirmIssue(wrapper);
    expect(issued()).toEqual([{ name: "Automation", scopes: ["read"], channels: ["rest"], expiresInDays: 90 }]);
    expect(post).not.toHaveBeenCalled();
    expect(wrapper.find("[data-one-time-value]").exists()).toBe(true);
    // The confirm button left with the summary; the keyboard is on the secret, not on <body> (P7).
    expect(wrapper.get("[data-token-secret]").element.contains(document.activeElement)).toBe(true);
    await wrapper.get('[data-token-secret] input[type="checkbox"]').setValue(true);
    await wrapper.get("[data-one-time-done]").trigger("click");
    await flushPromises();
    expect(wrapper.html()).not.toContain("one-time-secret");
    expect(wrapper.get("[data-result-banner]").text()).toContain("Automation");
    expect(wrapper.get('[data-token="token-id"]').attributes("data-new")).toBeDefined();
  });

  it("offers scopes only up to the account's level and sends the cumulative scopes with the channel", async () => {
    const read = await startCreate("read");
    expect(read.findAll("[data-token-access] [data-value]").map((tile) => tile.attributes("data-value"))).toEqual(["read"]);
    read.unmount();
    vi.resetAllMocks();
    const full = await startCreate("write", {}, { token: rights({ scopes: ["read", "write"] }), secret: "s" });
    // 3.6: just Čtení and Zápis, each with its one-line explanation.
    const tiles = full.findAll("[data-token-access] [data-value]");
    expect(tiles.map((tile) => tile.attributes("data-value"))).toEqual(["read", "write"]);
    expect(tiles[1]!.text()).toContain("Zápis");
    expect(tiles[1]!.text()).toContain("co uvidí čtenáři, určuje publikace stránky");
    expect(full.text()).not.toMatch(/Koncept|Přímá publikace/);
    await full.get('[data-token-access] [data-value="write"]').trigger("click");
    await full.get('[data-token-channels] [data-value="mcp"] [role=checkbox]').trigger("click");
    await flushPromises();
    expect(full.get("[data-token-mcp-off]").text()).toContain("MCP server zatím není zapnutý");
    await confirmIssue(full);
    expect(issued()).toEqual([{ name: "Automation", scopes: ["read", "write"], channels: ["rest", "mcp"], expiresInDays: 90 }]);
  });

  it("requires at least one channel", async () => {
    const view = await startCreate("read");
    await view.get('[data-token-channels] [data-value="rest"] [role=checkbox]').trigger("click");
    await flushPromises();
    expect(view.get('[data-step="channels"]').text()).toContain("Vyberte alespoň jeden kanál.");
    expect(view.find("[data-confirm-reauth]").attributes("disabled")).toBeDefined();
    expect(issued()).toEqual([]);
  });

  it("shows the MCP address after issuing an MCP token once the module is on", async () => {
    const view = await startCreate("read", { mcpEnabled: true, mcpUrl: "https://wiki.example/mcp" }, { token: rights({ channels: ["mcp"] }), secret: "one-time-secret" });
    await view.get('[data-token-channels] [data-value="mcp"] [role=checkbox]').trigger("click");
    await view.get('[data-token-channels] [data-value="rest"] [role=checkbox]').trigger("click");
    await flushPromises();
    await confirmIssue(view);
    expect(issued()).toEqual([{ name: "Automation", scopes: ["read"], channels: ["mcp"], expiresInDays: 90 }]);
    expect(view.get("[data-token-mcp-url]").text()).toBe("https://wiki.example/mcp");
  });

  it("words a migrated token by its scopes, a trimmed token by the ceiling and an MCP-only token while the module is off", async () => {
    vi.mocked(api).mockResolvedValue(catalog(true, [
      rights({ id: "migrated", name: "Nasazení", scopes: ["read", "write"], effectiveScopes: ["read", "write"] }),
      rights({ id: "trimmed", name: "Ořezaný", scopes: ["read", "write"], effectiveScopes: ["read"] }),
      rights({ id: "mcp", name: "Claude", channels: ["mcp"], usable: false }),
    ], { level: "read" }));
    wrapper = mount(PersonalApiTokens, { attachTo: document.body });
    await flushPromises();
    const row = (id: string) => wrapper!.get(`[data-token="${id}"]`);
    expect(row("migrated").get("[data-token-scope]").text()).toBe("Zápis");
    expect(row("migrated").text()).toContain("Čte · Zapisuje · REST API");
    expect(row("migrated").find("[data-token-state]").exists()).toBe(false);
    expect(row("trimmed").get("[data-token-state]").text()).toBe("Omezeno správcem na čtení.");
    expect(row("mcp").get("[data-token-state]").text()).toContain("Nepoužitelný, dokud správce nezapne MCP server");
    expect(row("mcp").text()).toContain("MCP");
    expect(wrapper.text()).not.toContain("Pouze čtení");
  });
});

const reference = vi.hoisted(() => ({ mount: undefined as undefined | ((...args: unknown[]) => () => void) }));
vi.mock("@atlas/markdown/plugins/openapi", () => ({ mountOpenApiReference: (...args: unknown[]) => reference.mount!(...args) }));

describe("the API documentation section", () => {
  it("unfolds with its frame mounted at the start and disposes the frame only after the fold", async () => {
    // jsdom has no Web Animations: a stand-in that finishes when the test says so.
    const finishers: (() => void)[] = [];
    const animate = vi.fn(() => ({ addEventListener: (type: string, callback: () => void) => { if (type === "finish") finishers.push(callback); }, finished: Promise.resolve(), cancel: vi.fn() }));
    Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, writable: true, value: animate });
    const dispose = vi.fn();
    reference.mount = vi.fn(() => dispose);
    try {
      vi.mocked(api).mockImplementation((async (path: string) => (String(path).includes("api-document") ? { openapi: "3.1.0" } : catalog(true, [token]))) as never);
      wrapper = mount(PersonalApiTokens, { attachTo: document.body });
      await flushPromises();
      expect(wrapper.find("[data-api-docs-reference]").exists()).toBe(false);
      await wrapper.get("[data-api-docs]").trigger("click");
      await flushPromises();
      // The section is a reveal that opens by animating its height; the frame is mounted into the host right away.
      expect(animate).toHaveBeenCalled();
      expect(reference.mount).toHaveBeenCalledTimes(1);
      finishers.splice(0).forEach((finish) => finish());
      await flushPromises();
      await wrapper.get("[data-api-docs-close]").trigger("click");
      await flushPromises();
      // Folding: the section is still there and its frame still alive, so it folds with its content instead of an empty box.
      expect(wrapper.find("[data-api-docs-reference]").exists()).toBe(true);
      expect(dispose).not.toHaveBeenCalled();
      finishers.splice(0).forEach((finish) => finish());
      await flushPromises();
      expect(wrapper.find("[data-api-docs-reference]").exists()).toBe(false);
      expect(dispose).toHaveBeenCalledTimes(1);
    } finally {
      delete (HTMLElement.prototype as { animate?: unknown }).animate;
    }
  });
});
