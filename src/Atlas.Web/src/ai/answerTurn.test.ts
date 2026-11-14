// @vitest-environment node
import { expect, it, vi } from "vitest";
import { ApiError } from "../api";
import { aiUnavailableText } from "./aiAction";
import { aiLimitText, runAnswerTurn, type AnswerTurnUpdate } from "./answerTurn";
import type { AiMessageView, AiSourceView, AiTurnPrepared } from "./assistantApi";

const source = (number: number): AiSourceView => ({ number, type: "page", pageId: `p${number}`, spaceId: "s", spaceName: "IT", title: `Stránka ${number}`, cited: true, changed: false });
const prepared = (state: AiTurnPrepared["state"] = "preparing"): AiTurnPrepared => ({ conversationId: "c1", messageId: "m1", state, sources: state === "preparing" ? [source(1), source(2)] : [] });
const message = (state: AiMessageView["state"], extra: Partial<AiMessageView> = {}): AiMessageView => ({ id: "m1", conversationId: "c1", role: "assistant", state, sentences: [{ text: "Věta.", sources: [1] }], sources: [source(1), source(2)], createdAt: "2026-10-05T10:00:00Z", ...extra });
const states = () => { const list: AnswerTurnUpdate[] = []; return { list, onUpdate: (u: AnswerTurnUpdate) => { list.push(u); }, names: () => list.map(u => u.state) }; };

it("goes from retrieving to preparing to done and aborts the completion on stop", async () => {
  const seen = states();
  const done = await runAnswerTurn({ prepare: async () => prepared(), complete: async () => message("answered"), onUpdate: seen.onUpdate });
  expect(seen.names()).toEqual(["retrieving", "preparing", "done"]);
  expect(seen.list[1]!.sources.map(s => s.number)).toEqual([1, 2]);
  expect(done).toMatchObject({ state: "done", messageId: "m1", conversationId: "c1" });
  expect(done.message?.sentences).toHaveLength(1);

  const stopper = new AbortController();
  let completionSignal: AbortSignal | undefined;
  const stopped = states();
  const turn = runAnswerTurn({
    prepare: async () => prepared(),
    complete: (_id, signal) => new Promise<AiMessageView>((_, reject) => { completionSignal = signal; signal.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError"))); }),
    signal: stopper.signal, onUpdate: stopped.onUpdate,
  });
  await vi.waitFor(() => expect(completionSignal).toBeDefined());
  stopper.abort();
  expect(await turn).toMatchObject({ state: "canceled" });
  expect(completionSignal!.aborted).toBe(true);
  expect(stopped.names()).toEqual(["retrieving", "preparing", "canceled"]);
});

it("ends without sources, withheld or failed in the matching state", async () => {
  expect((await runAnswerTurn({ prepare: async () => prepared("no_sources"), complete: vi.fn() })).state).toBe("no_sources");
  expect((await runAnswerTurn({ prepare: async () => prepared(), complete: async () => message("no_sources", { sources: [], sentences: [] }) })).state).toBe("no_sources");
  expect((await runAnswerTurn({ prepare: async () => prepared(), complete: async () => message("withheld", { sentences: [], sources: [] }) })).state).toBe("withheld");
  expect((await runAnswerTurn({ prepare: async () => prepared(), complete: async () => message("failed", { errorCode: "other" }) })).state).toBe("error");
});

it("maps ai_unavailable to the neutral sentence, 429 to the limit and retries sources_changed once", async () => {
  const unavailable = await runAnswerTurn({ prepare: async () => { throw new ApiError(503, "x", "ai_unavailable"); }, complete: vi.fn() });
  expect(unavailable).toMatchObject({ state: "unavailable", text: aiUnavailableText() });
  const midway = await runAnswerTurn({ prepare: async () => prepared(), complete: async () => { throw new ApiError(503, "x", "ai_unavailable"); } });
  expect(midway).toMatchObject({ state: "unavailable", text: aiUnavailableText() });

  expect(await runAnswerTurn({ prepare: async () => { throw new ApiError(429, "x", "ai_limit"); }, complete: vi.fn() })).toMatchObject({ state: "limit", text: aiLimitText() });
  expect(await runAnswerTurn({ prepare: async () => prepared(), complete: async () => { throw new ApiError(502, "x", "ai_failed"); } })).toMatchObject({ state: "error" });
  expect(await runAnswerTurn({ prepare: async () => { throw new Error("network"); }, complete: vi.fn() })).toMatchObject({ state: "error" });

  const prepare = vi.fn(async () => prepared());
  const complete = vi.fn().mockResolvedValueOnce(message("failed", { errorCode: "sources_changed" })).mockResolvedValueOnce(message("answered"));
  const seen = states();
  expect((await runAnswerTurn({ prepare, complete, onUpdate: seen.onUpdate })).state).toBe("done");
  expect(prepare).toHaveBeenCalledTimes(2);
  expect(seen.names()).toEqual(["retrieving", "preparing", "retrieving", "preparing", "done"]);

  const again = vi.fn(async () => message("failed", { errorCode: "sources_changed" }));
  const second = vi.fn(async () => prepared());
  expect((await runAnswerTurn({ prepare: second, complete: again })).state).toBe("error");
  expect(second).toHaveBeenCalledTimes(2);
});
