// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, clearCsrf } from "./api";
import { pageSourceApi, pageSourceFailure } from "./pageSource";

afterEach(() => { vi.unstubAllGlobals(); clearCsrf(); });

describe("page source endpoint", () => {
  it("replaces the page with PUT /api/pages/{id}/source", async () => {
    const fetch = vi.fn(async (input: unknown) => String(input) === "/api/auth/csrf"
      ? new Response(JSON.stringify({ token: "csrf" }))
      : new Response(JSON.stringify({ id: "page 1", version: 3 })));
    vi.stubGlobal("fetch", fetch);
    const signal = new AbortController().signal;
    await expect(pageSourceApi.replace("page 1", { contentJson: "{}", markdown: "# A", expectedVersion: 2 }, signal)).resolves.toMatchObject({ version: 3 });
    const [path, init] = fetch.mock.calls.at(-1)! as unknown as [string, RequestInit];
    expect(path).toBe("/api/pages/page%201/source");
    expect(init.method).toBe("PUT");
    expect(init.signal).toBe(signal);
    expect(JSON.parse(init.body as string)).toEqual({ contentJson: "{}", markdown: "# A", expectedVersion: 2 });
    expect(new Headers(init.headers).get("X-CSRF-TOKEN")).toBe("csrf");
  });
  it("maps server refusals to draft outcomes", () => {
    expect(pageSourceFailure(new ApiError(409, "Stránku mezitím změnil jiný uživatel."))).toEqual({ ok: false, reason: "conflict" });
    expect(pageSourceFailure(new ApiError(409, "Živé úpravy", "collaboration_unsaved"))).toEqual({ ok: false, reason: "live-edits" });
    expect(pageSourceFailure(new ApiError(409, "Přesunuto", "space_changed"))).toEqual({ ok: false, reason: "conflict" });
    expect(pageSourceFailure(new ApiError(403, "Zdroj stránky může upravit pouze správce prostoru."))).toEqual({ ok: false, reason: "error", message: "Zdroj stránky může upravit pouze správce prostoru." });
    expect(pageSourceFailure(new Error("offline"))).toEqual({ ok: false, reason: "error", message: "offline" });
  });
});
