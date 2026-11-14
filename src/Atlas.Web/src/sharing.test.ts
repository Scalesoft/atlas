import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, expect, it, vi } from "vitest";
import { clearCsrf } from "./api";
import { parseSharedToken, shareLinkMaxDays, sharingApi } from "./sharing";

afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); clearCsrf(); });

// The link flow offers exactly what the server accepts; a longer choice would fail only with a 400.
it("limits new links to the server's longest validity", () => {
  const server = readFileSync(resolve(__dirname, "../../Atlas.Server/Features/ExternalSharing/Domain/SharingEntities.cs"), "utf8");
  expect(server).toContain(`const int MaxValidityDays = ${shareLinkMaxDays};`);
});

it("recognizes only a single encoded public-share path segment", () => {
  expect(parseSharedToken("/s/sas_abc-123")).toBe("sas_abc-123");
  expect(parseSharedToken("/s/sas_%C4%8Desk%C3%BD")).toBe("sas_český");
  expect(parseSharedToken("/s/one/more")).toBeUndefined();
  expect(parseSharedToken("/pages/sas_abc")).toBeUndefined();
});

it("keeps the issued secret only in the create response and sends management mutations through CSRF", async () => {
  const storageWrite = vi.spyOn(window.Storage.prototype, "setItem");
  const fetchMock = vi.fn()
    .mockResolvedValueOnce(new Response(JSON.stringify({ token: "csrf" }), { status: 200, headers: { "Content-Type": "application/json" } }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ link: { id: "link-1" }, url: "/s/sas_secret" }), { status: 200, headers: { "Content-Type": "application/json" } }));
  vi.stubGlobal("fetch", fetchMock);
  const created = await sharingApi.createLink("page-1", { name: "Revize pro klienta", expiresAt: "2026-09-20T10:00:00.000Z" });
  expect(created.url).toBe("/s/sas_secret");
  expect(fetchMock.mock.calls[1]?.[0]).toBe("/api/pages/page-1/share-links");
  expect(fetchMock.mock.calls[1]?.[1]).toMatchObject({ method: "POST", credentials: "same-origin" });
  expect(new Headers(fetchMock.mock.calls[1]?.[1]?.headers).get("X-CSRF-TOKEN")).toBe("csrf");
  expect(storageWrite).not.toHaveBeenCalled();
});
