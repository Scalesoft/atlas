// @vitest-environment node
import { afterEach, expect, it, vi } from "vitest";
import { api, ApiError } from "./api";

afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });

async function failure(headers: Record<string, string>): Promise<ApiError> {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ title: "Zkuste to později" }), { status: 429, headers: { "Content-Type": "application/json", ...headers } })));
  return api("/boards").then(() => { throw new Error("The request was expected to fail."); }, (reason: unknown) => reason as ApiError);
}

// A refused request carries what the server's Retry-After asks for, so a caller that retries can wait that long.
it("reads Retry-After as milliseconds, from seconds or from a date", async () => {
  expect((await failure({ "Retry-After": "7" })).retryAfterMs).toBe(7000);
  vi.useFakeTimers({ now: Date.parse("2026-10-26T10:00:00Z") });
  expect((await failure({ "Retry-After": "Mon, 26 Oct 2026 10:00:30 GMT" })).retryAfterMs).toBe(30_000);
});

it("has no Retry-After when the server sent none or it makes no sense", async () => {
  expect((await failure({})).retryAfterMs).toBeUndefined();
  expect((await failure({ "Retry-After": "soon" })).retryAfterMs).toBeUndefined();
  expect((await failure({ "Retry-After": "Mon, 26 Oct 2020 10:00:30 GMT" })).retryAfterMs).toBeUndefined();
});
