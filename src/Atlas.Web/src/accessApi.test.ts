import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { accessApi, apiTokenAdminApi } from "./accessApi";
import { ApiError, clearCsrf } from "./api";
import { reauthWindow } from "./protection";

let calls: { method: string; url: string; body: unknown; headers: Headers }[] = [];
let answer: (url: URL, method: string) => Response = () => Response.json({});
beforeEach(() => {
  clearCsrf();
  calls = [];
  vi.stubGlobal("fetch", vi.fn(async (input: string, init?: RequestInit) => {
    const url = new URL(String(input), "http://atlas.test");
    const method = init?.method ?? "GET";
    if (url.pathname === "/api/auth/csrf") return Response.json({ token: "csrf-1" });
    calls.push({ method, url: `${url.pathname}${url.search}`, body: init?.body ? JSON.parse(String(init.body)) : undefined, headers: new Headers(init?.headers) });
    return answer(url, method);
  }));
});
afterEach(() => {
  vi.unstubAllGlobals();
  answer = () => Response.json({});
});

describe("access API", () => {
  it("reads kinds, the caller's own access, a principal's card (optionally one space) and a resource", async () => {
    await accessApi.kinds();
    await accessApi.me();
    await accessApi.principal("user", "u-1");
    await accessApi.principal("group", "g 1", "provoz");
    await accessApi.resource("vault", "v-1");
    await accessApi.resource("space", "provoz", "direct");
    await accessApi.resource("space", "provoz", "effective", " 50 % ");
    expect(calls.map((call) => call.url)).toEqual([
      "/api/access/kinds",
      "/api/access/me",
      "/api/access/principals/user/u-1",
      "/api/access/principals/group/g%201?spaceId=provoz",
      "/api/access/resources/vault/v-1?view=effective",
      "/api/access/resources/space/provoz?view=direct",
      "/api/access/resources/space/provoz?view=effective&q=50+%25",
    ]);
  });

  it("searches grant targets and candidates on the server with the trimmed query", async () => {
    answer = () => Response.json([]);
    await accessApi.resources("youtrack", "  OPS ");
    await accessApi.resources("space");
    await accessApi.candidates("user", "ja na");
    expect(calls.map((call) => call.url)).toEqual(["/api/access/resources?kind=youtrack&q=OPS", "/api/access/resources?kind=space", "/api/access/candidates?kind=user&q=ja+na"]);
  });

  it("sends a grant and a revoke with CSRF and, when there is one, the fresh sign-in proof", async () => {
    answer = () => new Response(null, { status: 204 });
    const body = { subjectKind: "user" as const, subjectId: "u-1", level: "read" as const, startAt: null, expiresAt: "2030-01-01T00:00:00Z", expectedVersion: null };
    await accessApi.grant("space", "provoz", body);
    await accessApi.grant("vault", "v-1", body, "proof-1");
    await accessApi.revoke("page", "p-1", "group", "g-1", "2026-09-29T10:00:00.123456Z");
    expect(calls[0]).toMatchObject({ method: "PUT", url: "/api/access/resources/space/provoz/grants", body });
    expect(calls[0]!.headers.get("X-CSRF-TOKEN")).toBe("csrf-1");
    expect(calls[0]!.headers.get("X-Atlas-Reauth")).toBeNull();
    expect(calls[1]!.headers.get("X-Atlas-Reauth")).toBe("proof-1");
    expect(calls[2]).toMatchObject({ method: "DELETE", url: "/api/access/resources/page/p-1/grants/group/g-1?version=2026-09-29T10%3A00%3A00.123456Z" });
  });

  it("closes the reauth window on reauth_required and keeps the code on the error", async () => {
    reauthWindow.sync(new Date(Date.now() + 5 * 60_000).toISOString());
    expect(reauthWindow.isOpen()).toBe(true);
    answer = () => new Response(JSON.stringify({ detail: "Potvrďte změnu novým přihlášením.", code: "reauth_required" }), { status: 403 });
    const failure = await accessApi.revokeAll("u-1").catch((error: unknown) => error);
    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).code).toBe("reauth_required");
    expect(reauthWindow.isOpen()).toBe(false);
    expect(calls[0]).toMatchObject({ method: "POST", url: "/api/access/principals/user/u-1/revoke-all" });
  });

  it("previews revoke-all and lists and revokes the wiki's personal API tokens", async () => {
    answer = (url, method) => (method === "DELETE" ? new Response(null, { status: 204 }) : url.pathname.endsWith("revoke-all") ? Response.json({ grants: 1, groups: 2, tokens: 3 }) : Response.json([]));
    expect(await accessApi.revokeAllPreview("u-1")).toEqual({ grants: 1, groups: 2, tokens: 3 });
    await apiTokenAdminApi.list();
    await apiTokenAdminApi.revoke("t-1");
    expect(calls.map((call) => `${call.method} ${call.url}`)).toEqual(["GET /api/access/principals/user/u-1/revoke-all", "GET /api/admin/api-tokens", "DELETE /api/admin/api-tokens/t-1"]);
  });
});
