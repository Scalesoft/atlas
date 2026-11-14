// @vitest-environment node
import { afterEach, expect, it, vi } from "vitest";
import { ApiError, api, clearCsrf } from "../api";
import { aiUnavailableText, runAiAction } from "./aiAction";
import { onAiUnavailable } from "./capabilities";

afterEach(() => { vi.unstubAllGlobals(); clearCsrf(); });

it("turns ai_unavailable into the neutral sentence and writes nothing", async () => {
  expect(aiUnavailableText()).toBe("Tahle funkce teď není k dispozici.");
  const fetchMock = vi.fn(async () => new Response(JSON.stringify({ title: aiUnavailableText(), code: "ai_unavailable" }), { status: 503, headers: { "Content-Type": "application/json" } }));
  vi.stubGlobal("fetch", fetchMock);
  const heard = vi.fn();
  const off = onAiUnavailable(heard);
  const write = vi.fn();
  const result = await runAiAction(async () => { await api("/ai/anything"); write(); return "done"; });
  off();
  expect(result).toEqual({ state: "unavailable", text: aiUnavailableText() });
  expect(write).not.toHaveBeenCalled();
  expect(heard).toHaveBeenCalledTimes(1);
});

it("passes a result through and lets other failures reach the caller", async () => {
  expect(await runAiAction(async () => 42)).toEqual({ state: "done", value: 42 });
  const failure = new ApiError(500, "Nejde to.");
  await expect(runAiAction(async () => { throw failure; })).rejects.toBe(failure);
  // A 503 with another code is not an AI outage.
  const other = new ApiError(503, "Služba neběží.", "maintenance");
  await expect(runAiAction(async () => { throw other; })).rejects.toBe(other);
});
