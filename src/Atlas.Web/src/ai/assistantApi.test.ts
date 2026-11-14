// @vitest-environment node
import { afterEach, expect, it, vi } from "vitest";
import { loadSearchAnswerPreference } from "./assistantApi";

afterEach(() => vi.unstubAllGlobals());

it.each([
  [{}, "auto"],
  [{ searchAnswer: "ask" }, "ask"],
  [{ searchAnswer: "never" }, "never"],
])("uses automatic answers for a missing preference and keeps an explicit choice: %j", async (body, expected) => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } })));
  expect(await loadSearchAnswerPreference()).toBe(expected);
});
