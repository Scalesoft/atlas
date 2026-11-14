import { afterEach, expect, it, vi } from "vitest";
import { ApiError, clearCsrf } from "./api";
import { accessRequestApi, releaseSecret } from "./secretRelease";

afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); clearCsrf(); });

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

it("releases through CSRF without caching or storing the values", async () => {
  const storageWrite = vi.spyOn(window.Storage.prototype, "setItem");
  const fetchMock = vi.fn()
    .mockResolvedValueOnce(json({ token: "csrf" }))
    .mockResolvedValueOnce(json({ values: { userName: "admin", password: "value" }, expiresInSeconds: 60 }));
  vi.stubGlobal("fetch", fetchMock);
  const result = await releaseSecret("secret-1", { fields: ["userName", "password"], operation: "reveal" });
  expect(result).toEqual({ kind: "released", values: { userName: "admin", password: "value" }, expiresInSeconds: 60 });
  expect(fetchMock.mock.calls[1]?.[0]).toBe("/api/secrets/secret-1/release");
  expect(fetchMock.mock.calls[1]?.[1]).toMatchObject({ method: "POST", credentials: "same-origin", cache: "no-store" });
  expect(new Headers(fetchMock.mock.calls[1]?.[1]?.headers).get("X-CSRF-TOKEN")).toBe("csrf");
  expect(storageWrite).not.toHaveBeenCalled();
});

it("answers the shared re-authentication refusal (403 with a reauth code) with the challenge and throws other denials", async () => {
  vi.stubGlobal("fetch", vi.fn()
    .mockResolvedValueOnce(json({ token: "csrf" }))
    .mockResolvedValueOnce(json({ title: "Nové přihlášení", status: 403, code: "reauth_required", reauth: { mode: "always", modeSource: "secret", supported: true } }, 403))
    .mockResolvedValueOnce(json({ token: "csrf" }))
    .mockResolvedValueOnce(json({ title: "Nepodporováno", status: 403, code: "fresh_auth_unsupported", reauth: { mode: "always", modeSource: "instance", supported: false } }, 403))
    .mockResolvedValueOnce(json({ token: "csrf" }))
    .mockResolvedValueOnce(json({ title: "K zobrazení hesla potřebujete oprávnění číst heslo.", status: 403 }, 403))
    .mockResolvedValueOnce(json({ token: "csrf" }))
    .mockResolvedValueOnce(json({ title: "Trezor teď není dostupný.", code: "vault_unavailable" }, 409)));
  expect(await releaseSecret("s", { fields: ["password"], operation: "reveal" }))
    .toEqual({ kind: "reauth", reauth: { mode: "always", modeSource: "secret", supported: true } });
  expect(await releaseSecret("s", { fields: ["password"], operation: "copy", proofId: "p" }))
    .toEqual({ kind: "reauth", reauth: { mode: "always", modeSource: "instance", supported: false } });
  // A 403 without a re-authentication code is no Čte: an error, so the reference offers a request.
  expect(await releaseSecret("s", { fields: ["password"], operation: "copy" }).catch((e: unknown) => e)).toMatchObject({ status: 403, code: undefined });
  const error = await releaseSecret("s", { fields: ["password"], operation: "copy" }).catch((e: unknown) => e);
  expect(error).toBeInstanceOf(ApiError);
  expect(error).toMatchObject({ status: 409, code: "vault_unavailable" });
});

it("builds the request route", async () => {
  const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json({ items: [], hasMore: false, nextBeforeId: null })));
  vi.stubGlobal("fetch", fetchMock);
  await accessRequestApi.list("all", "vault-1");
  expect(fetchMock.mock.calls[0]?.[0]).toBe("/api/access-requests?state=all&vaultId=vault-1");
});
