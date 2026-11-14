import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { ApiError } from "../api";
import { createAtlasSecretsServices, inertSecretsServices, SecretReleaseError } from "./secretsHost";
import { nextTick, ref } from "vue";
import { createSecretAccessEvents, type SecretRequestChange } from "./secretEvents";
import { createSecretReauth } from "./secretReauth";
import type { SecretReferenceView, SecretsApi } from "./secretsApi";
import type { ReleaseRequest, ReleaseResult } from "../secretRelease";
import SecretPicker from "./SecretPicker.vue";
import { createSecretsPlugin } from "@atlas/markdown/plugins/secrets";

const CANARY = "CANARY-value-9f2";
const secretId = "d847b3af-4a87-4e97-aab8-a74a4cf48c89";
const reference = (level: SecretReferenceView["level"] = "read"): SecretReferenceView => ({
  id: secretId, title: "Router", path: "Provoz / Síť", vaultId: "v", vaultName: "Provoz", spaceId: "s",
  fields: ["title", "path", "userName", "password", "url"], level,
});

type ReleaseFake = ReturnType<typeof vi.fn<(secretId: string, request: ReleaseRequest, signal?: AbortSignal) => Promise<ReleaseResult>>>;
/** The V5 release client: every requested field comes back as `${CANARY}-${field}`. */
const released = (expiresInSeconds = 60) => vi.fn(async (_id: string, request: ReleaseRequest): Promise<ReleaseResult> => ({
  kind: "released", values: Object.fromEntries(request.fields.map(field => [field, `${CANARY}-${field}`])), expiresInSeconds,
}));
const reauthAnswer = (supported = true): ReleaseResult => ({ kind: "reauth", reauth: { mode: "always", modeSource: "secret", supported } });

function fakeApi(overrides: Partial<SecretsApi & { release: ReleaseFake }> = {}): SecretsApi & { [K in keyof SecretsApi]: ReturnType<typeof vi.fn> } & { release: ReleaseFake } {
  return {
    search: vi.fn(async () => [reference()]),
    reference: vi.fn(async () => reference()),
    release: released(),
    requestAccess: vi.fn(async () => {}),
    startReauth: vi.fn(async () => ({ id: "challenge-1", method: "password" as const, expiresAt: "", redirectUrl: null })),
    verifyPassword: vi.fn(async () => true),
    reauthStatus: vi.fn(async () => ({ id: "challenge-1", state: "verified" as const })),
    ...overrides,
  } as never;
}
function fakeEvents() {
  const revokes = new Map<string, () => void>();
  const requestListeners = new Set<(change: SecretRequestChange) => void>();
  return {
    revokes,
    requestChanged: (secretId: string | null, state: string | null = "approved") => requestListeners.forEach(listener => listener({ secretId, state })),
    subscribe: vi.fn((id: string, revoke: () => void) => { revokes.set(id, revoke); return () => revokes.delete(id); }),
    onRequestChanged: vi.fn((listener: (change: SecretRequestChange) => void) => { requestListeners.add(listener); return () => requestListeners.delete(listener); }),
    revokeAll: vi.fn(),
  };
}
/** A stand-in for the `createSecretsPlugin` return value. */
const fakePlugin = () => ({ conceal: vi.fn(() => 1), refresh: vi.fn(() => 1) });
const signal = () => new AbortController().signal;

function services(api = fakeApi(), reauth = vi.fn(async () => "proof-7"), events = fakeEvents(), clipboard = vi.fn(async () => {})) {
  return { api, reauth, events, clipboard, host: createAtlasSecretsServices({ api, release: api.release, reauth, events, clipboard, reason: () => "Ze stránky „Síť“." }) };
}

/** Web storage that records every write, so a leaked value would show up in the check below. */
function memoryStorage(): Storage {
  const items = new Map<string, string>();
  return {
    get length() { return items.size; },
    clear: () => items.clear(),
    getItem: key => items.get(key) ?? null,
    key: index => [...items.keys()][index] ?? null,
    removeItem: key => { items.delete(key); },
    setItem: (key, value) => { items.set(key, String(value)); },
  };
}
const dump = (storage: Storage) => JSON.stringify(Array.from({ length: storage.length }, (_, index) => [storage.key(index), storage.getItem(storage.key(index)!)]));
beforeEach(() => { vi.stubGlobal("localStorage", memoryStorage()); vi.stubGlobal("sessionStorage", memoryStorage()); });
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe("Atlas secrets host", () => {
  it("shares one discovery request and a consumer that goes away does not fail the others (S17)", async () => {
    // Like fetch: a request that was given an aborted signal fails.
    const { api, host } = services(fakeApi({ reference: vi.fn(async (_id: string, abort?: AbortSignal) => { await Promise.resolve(); if (abort?.aborted) throw new DOMException("x", "AbortError"); return reference(); }) }));
    const first = new AbortController();
    const gone = host.discover({ secretId, signal: first.signal }).catch(() => "aborted");
    const stays = host.discover({ secretId, signal: signal() });
    first.abort();
    expect(await stays).toBe("available");
    await gone;
    expect(api.reference).toHaveBeenCalledTimes(1);
  });

  it("reveals the shown fields in one release: metadata from the reference, values from the response", async () => {
    const { api, host } = services();
    expect(await host.discover({ secretId, signal: signal() })).toBe("available");
    const release = await host.reveal({ secretId, fields: ["title", "username", "password", "notes"], signal: signal(), ownerDocument: document });
    expect(release.values.title?.textContent).toBe("Router");
    expect(release.values.username?.textContent).toBe(`${CANARY}-userName`);
    expect(release.values.password?.textContent).toBe(`${CANARY}-password`);
    // The entry has no notes: the plugin shows its dash.
    expect(release.values.notes).toBeUndefined();
    // One request for every shown value; notes is not in the reference, title and path are metadata.
    expect(api.release.mock.calls.map(call => [call[0], call[1]])).toEqual([[secretId, { fields: ["userName", "password"], operation: "reveal" }]]);
    expect(api.reference).toHaveBeenCalledTimes(1);
    release.dispose?.();
    expect(release.values.password?.textContent).toBe("");
  });

  it("asks for a fresh sign-in once on reauth_required and retries the whole reveal once with the single-use proof", async () => {
    const api = fakeApi();
    api.release.mockResolvedValueOnce(reauthAnswer());
    const { reauth, host } = services(api);
    const release = await host.reveal({ secretId, fields: ["username", "password", "url"], signal: signal(), ownerDocument: document });
    expect(reauth).toHaveBeenCalledOnce();
    expect(reauth).toHaveBeenCalledWith(expect.objectContaining({ secretId, operation: "reveal", requirement: { mode: "always", modeSource: "secret", supported: true } }));
    const fields = ["userName", "password", "url"];
    expect(api.release.mock.calls.map(call => call[1])).toEqual([{ fields, operation: "reveal" }, { fields, operation: "reveal", proofId: "proof-7" }]);
    expect(release.values.username?.textContent).toBe(`${CANARY}-userName`);
    expect(release.values.password?.textContent).toBe(`${CANARY}-password`);
    expect(release.values.url?.textContent).toBe(`${CANARY}-url`);
  });

  it("names the page the reference is shown on in every release, also the retry after a fresh sign-in", async () => {
    const api = fakeApi();
    api.release.mockResolvedValueOnce(reauthAnswer());
    const reauth = vi.fn(async () => "proof-7");
    const host = createAtlasSecretsServices({ api, release: api.release, reauth, events: fakeEvents(), clipboard: vi.fn(async () => {}), reason: () => "", pageId: () => "page-1" });
    await host.reveal({ secretId, fields: ["password"], signal: signal(), ownerDocument: document });
    await host.copy({ secretId, field: "password", signal: signal() });
    expect(api.release.mock.calls.map(call => call[1])).toEqual([
      { fields: ["password"], operation: "reveal", pageId: "page-1" },
      { fields: ["password"], operation: "reveal", proofId: "proof-7", pageId: "page-1" },
      { fields: ["password"], operation: "copy", pageId: "page-1" },
    ]);
  });

  it("does not ask again when the retry with the proof is refused", async () => {
    const api = fakeApi();
    api.release.mockResolvedValue(reauthAnswer());
    const { reauth, host } = services(api);
    await expect(host.reveal({ secretId, fields: ["password"], signal: signal(), ownerDocument: document })).rejects.toBeInstanceOf(SecretReleaseError);
    expect(reauth).toHaveBeenCalledOnce();
    expect(api.release).toHaveBeenCalledTimes(2);
  });

  it("leaves a field that is empty in KeePass out of the reveal", async () => {
    const api = fakeApi({ release: vi.fn(async () => ({ kind: "released", values: { password: CANARY }, expiresInSeconds: 60 }) as ReleaseResult) });
    const { host } = services(api);
    const release = await host.reveal({ secretId, fields: ["username", "password"], signal: signal(), ownerDocument: document });
    expect(release.values.username).toBeUndefined();
    expect(release.values.password?.textContent).toBe(CANARY);
  });

  it("fails closed when the sign-in provider cannot prove a fresh sign-in", async () => {
    const api = fakeApi();
    api.release.mockResolvedValue(reauthAnswer(false));
    const { reauth, host } = services(api);
    await expect(host.reveal({ secretId, fields: ["password"], signal: signal(), ownerDocument: document })).rejects.toBeInstanceOf(SecretReleaseError);
    await expect(host.copy({ secretId, field: "password", signal: signal() })).rejects.toBeInstanceOf(SecretReleaseError);
    expect(reauth).not.toHaveBeenCalled();
    // Unsupported never retries.
    expect(api.release).toHaveBeenCalledTimes(2);
  });

  it("turns a 403 into a requestable reference and sends the access request", async () => {
    const api = fakeApi();
    api.release.mockRejectedValueOnce(new ApiError(403, "K této operaci potřebujete oprávnění číst heslo."));
    const { host } = services(api);
    await expect(host.reveal({ secretId, fields: ["password"], signal: signal(), ownerDocument: document })).rejects.toMatchObject({ status: 403 });
    api.reference.mockResolvedValue(reference("discover"));
    expect(await host.discover({ secretId, signal: signal() })).toEqual({ access: "requestable", requestPending: false, requestRefused: false });
    await host.requestAccess({ secretId, signal: signal(), reason: "" });
    expect(api.requestAccess).toHaveBeenCalledWith(secretId, "Ze stránky „Síť“.", expect.anything());
    expect(await host.discover({ secretId, signal: signal() })).toEqual({ access: "requestable", requestPending: true, requestRefused: false });
    api.reference.mockResolvedValue(null);
  });

  it("forwards the reason typed in the request panel before the page context", async () => {
    const { api, host } = services();
    await host.requestAccess({ secretId, signal: signal(), reason: "  Nasazení routeru  " });
    expect(api.requestAccess).toHaveBeenCalledWith(secretId, "Nasazení routeru\n\nZe stránky „Síť“.", expect.anything());
  });

  it("refreshes the reference in place after a 403 instead of remounting", async () => {
    vi.useFakeTimers();
    const api = fakeApi();
    api.release.mockRejectedValueOnce(new ApiError(403, "K této operaci potřebujete oprávnění číst heslo."));
    const { host } = services(api);
    const plugin = fakePlugin();
    host.attach(plugin);
    await expect(host.reveal({ secretId, fields: ["password"], signal: signal(), ownerDocument: document })).rejects.toMatchObject({ status: 403 });
    expect(plugin.refresh).not.toHaveBeenCalled();
    vi.runAllTimers();
    expect(plugin.refresh).toHaveBeenCalledExactlyOnceWith(secretId);
    expect(plugin.conceal).not.toHaveBeenCalled();
  });

  it("rediscovers once per secret when an access request changes (approved)", async () => {
    vi.useFakeTimers();
    const { api, events, host } = services();
    const plugin = fakePlugin();
    const detach = host.attach(plugin);
    api.reference.mockResolvedValue(reference("discover"));
    await host.discover({ secretId, signal: signal() });
    // A new pending request repaints nothing.
    events.requestChanged(secretId, "pending");
    vi.runAllTimers();
    expect(plugin.refresh).not.toHaveBeenCalled();
    events.requestChanged(secretId);
    events.requestChanged(secretId);
    vi.runAllTimers();
    expect(plugin.refresh).toHaveBeenCalledExactlyOnceWith(secretId);
    // The cached level was dropped, so the refresh asks the server again.
    api.reference.mockResolvedValue(reference("read"));
    expect(await host.discover({ secretId, signal: signal() })).toBe("available");
    expect(api.reference).toHaveBeenCalledTimes(2);
    // An approved vault-wide request rediscovers every reference.
    events.requestChanged(null);
    vi.runAllTimers();
    expect(plugin.refresh).toHaveBeenLastCalledWith(undefined);
    detach();
    events.requestChanged(secretId);
    vi.runAllTimers();
    expect(plugin.refresh).toHaveBeenCalledTimes(2);
  });

  it.each(["reveal", "copy"] as const)("revocation cancels a pending %s before rediscovery and permits a later authorized release", async (operation) => {
    vi.useFakeTimers();
    const media = vi.spyOn(window, "matchMedia").mockImplementation(query => ({ matches: true, media: query, onchange: null, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => true }));
    const canvas = vi.spyOn(HTMLCanvasElement.prototype, "getContext").mockReturnValue(null);
    let resolveRelease!: (value: ReleaseResult) => void;
    const pending = new Promise<ReleaseResult>(resolve => { resolveRelease = resolve; });
    let resolveRediscovery!: (value: SecretReferenceView | null) => void;
    const rediscovery = new Promise<SecretReferenceView | null>(resolve => { resolveRediscovery = resolve; });
    const api = fakeApi();
    api.reference.mockResolvedValueOnce(reference()).mockImplementationOnce(() => rediscovery);
    api.release.mockImplementationOnce(() => pending);
    const { events, host, clipboard } = services(api);
    const plugin = createSecretsPlugin({ services: host });
    host.attach(plugin);
    const controller = new AbortController();
    const root = document.createElement("div");
    const placeholder = document.createElement("span");
    placeholder.dataset.mdSecretInline = "";
    root.append(placeholder);
    document.body.append(root);
    const cleanup = plugin.create({ profile: "rich", signal: controller.signal }).onRender!({
      container: root, ownerDocument: document, signal: controller.signal,
      document: { type: "doc", content: [{ type: "secretInlineReference", attrs: { secretId, field: "password", showCopy: true, autoHideSeconds: 60 } }] },
    });
    const selector = operation === "reveal" ? '[data-concealed-action="reveal"]' : "[data-concealed-copy]";
    try {
      await flushPromises();
      await vi.advanceTimersByTimeAsync(100);
      root.querySelector<HTMLButtonElement>(selector)!.click();
      await flushPromises();
      expect(api.release).toHaveBeenCalledOnce();
      events.revokes.get(secretId)!();
      await vi.advanceTimersByTimeAsync(0);
      // The old authorized response arrives while the new access lookup is still pending.
      resolveRelease({ kind: "released", values: { password: CANARY }, expiresInSeconds: 60 });
      await flushPromises();
      await vi.advanceTimersByTimeAsync(400);
      expect(root.textContent).not.toContain(CANARY);
      expect(clipboard).not.toHaveBeenCalled();

      resolveRediscovery(reference());
      await flushPromises();
      await vi.advanceTimersByTimeAsync(100);
      root.querySelector<HTMLButtonElement>(selector)!.click();
      await flushPromises();
      await vi.advanceTimersByTimeAsync(400);
      if (operation === "reveal") expect(root.textContent).toContain(`${CANARY}-password`);
      else expect(clipboard).toHaveBeenCalledExactlyOnceWith(`${CANARY}-password`);
    } finally {
      controller.abort();
      if (typeof cleanup === "function") cleanup();
      host.dispose();
      root.remove();
      media.mockRestore();
      canvas.mockRestore();
    }
  });

  it("revokes immediately and refreshes on secretAccessRevoked when a plugin is attached", async () => {
    vi.useFakeTimers();
    const { events, host } = services();
    const plugin = fakePlugin();
    host.attach(plugin);
    const revoke = vi.fn();
    host.subscribeRevocation(secretId, revoke);
    const release = await host.reveal({ secretId, fields: ["password"], signal: signal(), ownerDocument: document });
    events.revokes.get(secretId)!();
    expect(release.values.password?.textContent).toBe("");
    expect(revoke).toHaveBeenCalledOnce();
    vi.runAllTimers();
    expect(plugin.refresh).toHaveBeenCalledExactlyOnceWith(secretId);
  });

  it("hides every surface and clears released values on secretAccessRevoked", async () => {
    const { events, host } = services();
    const revoke = vi.fn();
    host.subscribeRevocation(secretId, revoke);
    const release = await host.reveal({ secretId, fields: ["password"], signal: signal(), ownerDocument: document });
    events.revokes.get(secretId)!();
    expect(revoke).toHaveBeenCalledTimes(1);
    expect(release.values.password?.textContent).toBe("");
  });

  it("copies through the host clipboard after the release, never through the plugin", async () => {
    const { api, clipboard, host } = services();
    await host.copy({ secretId, field: "username", signal: signal() });
    expect(api.release).toHaveBeenCalledExactlyOnceWith(secretId, { fields: ["userName"], operation: "copy" }, expect.anything());
    expect(clipboard).toHaveBeenCalledWith(`${CANARY}-userName`);
  });

  it.each(["title", "path"] as const)("does not copy revoked %s metadata when a cached reference resolves late", async (field) => {
    let resolveReference!: (value: SecretReferenceView) => void;
    const pending = new Promise<SecretReferenceView>(resolve => { resolveReference = resolve; });
    const api = fakeApi();
    api.reference.mockImplementationOnce(() => pending);
    const { host, events, clipboard } = services(api);
    // Discovery owns the fetch signal; the copy awaits its cached promise with its own signal.
    const discovery = host.discover({ secretId, signal: signal() });
    const controller = new AbortController();
    const stop = host.subscribeRevocation(secretId, () => controller.abort());
    const copying = host.copy({ secretId, field, signal: controller.signal });
    events.revokes.get(secretId)!();
    resolveReference(reference());
    await expect(copying).rejects.toMatchObject({ name: "AbortError" });
    expect(clipboard).not.toHaveBeenCalled();
    await discovery;
    expect(api.reference).toHaveBeenCalledOnce();
    stop();
    host.dispose();
  });

  it("keeps a value only in memory and only for the server lifetime", async () => {
    vi.useFakeTimers();
    const logs = [vi.spyOn(console, "log"), vi.spyOn(console, "info"), vi.spyOn(console, "warn"), vi.spyOn(console, "error"), vi.spyOn(console, "debug")];
    const api = fakeApi({ release: vi.fn(async () => ({ kind: "released", values: { password: CANARY }, expiresInSeconds: 5 }) as ReleaseResult) });
    const { host } = services(api);
    const release = await host.reveal({ secretId, fields: ["password"], signal: signal(), ownerDocument: document });
    expect(release.values.password?.textContent).toBe(CANARY);
    vi.advanceTimersByTime(5000);
    expect(release.values.password?.textContent).toBe("");
    host.dispose();
    const stored = dump(localStorage) + dump(sessionStorage) + document.cookie + document.body.innerHTML;
    expect(stored).not.toContain(CANARY);
    for (const log of logs) expect(JSON.stringify(log.mock.calls)).not.toContain(CANARY);
    logs.forEach(log => log.mockRestore());
  });

  it("history and public views never offer a release", async () => {
    expect(await inertSecretsServices.discover({ secretId, signal: signal() })).toBe("hidden");
    await expect(inertSecretsServices.reveal({ secretId, fields: ["password"], signal: signal(), ownerDocument: document })).rejects.toThrow();
    await expect(inertSecretsServices.copy({ secretId, field: "password", signal: signal() })).rejects.toThrow();
  });
});

describe("secret access events", () => {
  function fakeLive() {
    const listeners = new Map<string, { message?: (event: string, payload: unknown) => void; revoked?: () => void }>();
    const close = vi.fn();
    const live = {
      route: vi.fn(),
      subscribe: vi.fn((name: string, listener: { message?: (event: string, payload: unknown) => void; revoked?: () => void }) => {
        listeners.set(name, listener);
        return { close, presence: vi.fn(), join: vi.fn() };
      }),
    };
    return {
      live, close,
      hear: (name: string, event: string, payload: unknown) => listeners.get(name)!.message!(event, payload),
      revoke: (name: string) => listeners.get(name)!.revoked!(),
    };
  }

  // Deactivation, a new security stamp or an ended sign-in all reach the client as the server ending user:{id} (SEC2-1), maybe before
  // the secretAccessRevoked it would have sent: every revealed value hides, and the channel is asked for again.
  it("hides every revealed value when the server ends the account's channel and subscribes it again", () => {
    const { live, close, revoke } = fakeLive();
    const account = ref<string | undefined>("u1");
    const events = createSecretAccessEvents({ live: () => live as never, account });
    const first = vi.fn();
    const other = vi.fn();
    events.subscribe(secretId, first);
    const stopOther = events.subscribe("other", other);
    revoke("user:u1");
    expect(first).toHaveBeenCalledTimes(1);
    expect(other).toHaveBeenCalledTimes(1);
    expect(live.subscribe).toHaveBeenCalledTimes(2);
    expect(live.subscribe).toHaveBeenLastCalledWith("user:u1", expect.anything());
    // The ended subscription is not closed again.
    stopOther();
    expect(close).not.toHaveBeenCalled();
  });

  it("holds the account's live channel from the first reference to the last and follows the signed-in account", async () => {
    const { live, close, hear } = fakeLive();
    const account = ref<string | undefined>("u1");
    const events = createSecretAccessEvents({ live: () => live as never, account });
    const first = vi.fn();
    const other = vi.fn();
    const stopFirst = events.subscribe(secretId, first);
    const stopOther = events.subscribe("other", other);
    // One subscription of the account's own channel on the shared connection: no second hub.
    expect(live.subscribe).toHaveBeenCalledTimes(1);
    expect(live.subscribe).toHaveBeenCalledWith("user:u1", expect.anything());
    expect(live.route).toHaveBeenCalledWith("secretAccessRevoked", expect.any(Function));
    hear("user:u1", "secretAccessRevoked", { secretId });
    hear("user:u1", "NotificationsChanged", { channel: "user:u1" });
    expect(first).toHaveBeenCalledTimes(1);
    expect(other).not.toHaveBeenCalled();
    const changes: SecretRequestChange[] = [];
    const stopChanges = events.onRequestChanged(change => changes.push(change));
    hear("user:u1", "accessRequestChanged", { requestId: "r", secretId, vaultId: "v", state: "approved" });
    hear("user:u1", "accessRequestChanged", { requestId: "r2", secretId: null, vaultId: "v", state: "rejected" });
    expect(changes).toEqual([{ secretId, state: "approved" }, { secretId: null, state: "rejected" }]);

    account.value = "u2";
    await nextTick();
    expect(close).toHaveBeenCalledTimes(1);
    expect(live.subscribe).toHaveBeenLastCalledWith("user:u2", expect.anything());
    stopChanges();
    stopFirst();
    stopOther();
    expect(close).toHaveBeenCalledTimes(2);
  });

  it("lets a request feed alone hold the channel, and subscribes nothing while nobody is signed in", async () => {
    const { live, close } = fakeLive();
    const account = ref<string | undefined>();
    const events = createSecretAccessEvents({ live: () => live as never, account });
    const stop = events.onRequestChanged(() => {});
    expect(live.subscribe).not.toHaveBeenCalled();
    account.value = "u1";
    await nextTick();
    expect(live.subscribe).toHaveBeenCalledWith("user:u1", expect.anything());
    stop();
    expect(close).toHaveBeenCalledTimes(1);
    account.value = "u3";
    await nextTick();
    expect(live.subscribe).toHaveBeenCalledTimes(1);
  });
});

describe("secret re-authentication", () => {
  it("starts a challenge for the secret and resolves its id as the proof once confirmed", async () => {
    const api = fakeApi();
    const reauth = createSecretReauth(api);
    const proof = reauth.prompt({ secretId, operation: "copy", requirement: { mode: "always", modeSource: "vault", supported: true }, signal: signal() });
    await flushPromises();
    expect(api.startReauth).toHaveBeenCalledWith(secretId, "copy", expect.any(String));
    const pending = reauth.pending.value!;
    expect(pending.environment.method).toBe("local");
    expect(await pending.environment.verifyPassword("heslo")).toBe(true);
    expect(api.verifyPassword).toHaveBeenCalledWith("challenge-1", "heslo");
    pending.confirm();
    await expect(proof).resolves.toBe("challenge-1");
    expect(reauth.pending.value).toBeNull();
  });

  it("rejects when the reader cancels", async () => {
    const reauth = createSecretReauth(fakeApi());
    const proof = reauth.prompt({ secretId, operation: "reveal", requirement: { mode: "always", modeSource: "secret", supported: true }, signal: signal() });
    await flushPromises();
    reauth.pending.value!.cancel();
    await expect(proof).rejects.toMatchObject({ name: "AbortError" });
  });
});

describe("secret picker", () => {
  it("searches the space's discoverable secrets and inserts the chosen one", async () => {
    vi.useFakeTimers();
    const api = fakeApi();
    const wrapper = mount(SecretPicker, { props: { open: true, spaceId: "space-1", api }, attachTo: document.body });
    await flushPromises();
    expect(api.search).toHaveBeenCalledWith("", "space-1", expect.anything());
    await wrapper.get('input[type="search"]').setValue("rou");
    vi.advanceTimersByTime(250);
    await flushPromises();
    expect(api.search).toHaveBeenLastCalledWith("rou", "space-1", expect.anything());
    await wrapper.get('.option-pill[data-value="inline"]').trigger("click");
    await wrapper.get(`[role="radio"][data-value="${secretId}"]`).trigger("click");
    expect(wrapper.emitted("insert")).toEqual([[{ secretId, inline: true, vaultName: "Provoz" }]]);
    wrapper.unmount();
  });
});
