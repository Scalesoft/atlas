// @vitest-environment node
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearCsrf } from "../api";
import { notifyAiUnavailable } from "../ai/capabilities";
import { createFakeLiveFeed } from "../live/fakeLiveFeed";
import { useAiCapabilities } from "./useAiCapabilities";

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });
const ALL_FALSE = { semanticSearch: false, related: false, editorLinks: false, createSuggestions: false, answer: false, assistant: false, editorIntent: false, editorTransform: false, editorOutline: false, publishCheck: false, changeSummary: false, pageSummary: false, tagSuggestions: false, agent: false, arrangePages: false };
let fetchMock: ReturnType<typeof vi.fn>;
/** /hubs/live as the composable hears it: `feed.emit("ai", {})` is an AiChanged. */
let feed = createFakeLiveFeed();
const capabilityCalls = () => fetchMock.mock.calls.filter(([url]) => url === "/api/ai/capabilities").length;

beforeEach(() => {
  vi.useFakeTimers();
  feed = createFakeLiveFeed();
  fetchMock = vi.fn(async () => json({ semanticSearch: true, related: true, editorLinks: false, createSuggestions: false }));
  vi.stubGlobal("fetch", fetchMock);
});
afterEach(() => { useAiCapabilities().stop(); vi.unstubAllGlobals(); vi.useRealTimers(); clearCsrf(); });

describe("useAiCapabilities", () => {
  it("loads with the session and treats a failure or an unknown field as false", async () => {
    const ai = useAiCapabilities();
    expect(ai.capabilities.value).toEqual(ALL_FALSE);
    expect(ai.any.value).toBe(false);
    ai.start(feed.source);
    await vi.advanceTimersByTimeAsync(0);
    expect(ai.capabilities.value).toEqual({ ...ALL_FALSE, semanticSearch: true, related: true });
    expect(ai.any.value).toBe(true);

    // A newer field the client does not know is ignored; a missing or non-boolean one is false.
    fetchMock.mockResolvedValueOnce(json({ semanticSearch: true, related: "yes", chat: true }));
    await ai.refresh();
    expect(ai.capabilities.value).toEqual({ ...ALL_FALSE, semanticSearch: true });
    expect("chat" in ai.capabilities.value).toBe(false);

    // Any failure silently turns everything off: no throw, no message.
    fetchMock.mockRejectedValueOnce(new TypeError("offline"));
    await expect(ai.refresh()).resolves.toBeUndefined();
    expect(ai.capabilities.value).toEqual(ALL_FALSE);
    fetchMock.mockResolvedValueOnce(json({ detail: "x" }, 500));
    await ai.refresh();
    expect(ai.capabilities.value).toEqual(ALL_FALSE);
  });

  it("reads again when the server tells AI changed and after a reconnect, never on a timer", async () => {
    const ai = useAiCapabilities();
    ai.start(feed.source);
    await vi.advanceTimersByTimeAsync(0);
    expect(feed.count("ai")).toBe(1);
    // The read at start stands for the first subscription's: one request on an open.
    expect(capabilityCalls()).toBe(1);
    const settled = capabilityCalls();
    await vi.advanceTimersByTimeAsync(30 * 60_000);
    expect(capabilityCalls()).toBe(settled);

    // A profile recovered after an outage: the server's probe tells it, the flag comes back.
    fetchMock.mockImplementation(async () => json(ALL_FALSE));
    await ai.refresh();
    expect(ai.capabilities.value.related).toBe(false);
    fetchMock.mockImplementation(async () => json({ semanticSearch: true, related: true }));
    feed.emit("ai", {});
    await vi.advanceTimersByTimeAsync(0);
    expect(ai.capabilities.value.related).toBe(true);
    feed.resubscribe("ai");
    await vi.advanceTimersByTimeAsync(0);
    expect(capabilityCalls()).toBe(settled + 3);
    // A lost feed keeps the flags as last read.
    feed.fail("ai");
    await vi.advanceTimersByTimeAsync(30 * 60_000);
    expect(ai.capabilities.value.related).toBe(true);
    expect(capabilityCalls()).toBe(settled + 3);
  });

  it("refreshes at once after an ai_unavailable problem", async () => {
    const ai = useAiCapabilities();
    ai.start(feed.source);
    await vi.advanceTimersByTimeAsync(0);
    expect(ai.capabilities.value.related).toBe(true);
    const before = capabilityCalls();
    fetchMock.mockResolvedValueOnce(json(ALL_FALSE));
    notifyAiUnavailable();
    await vi.advanceTimersByTimeAsync(0);
    expect(capabilityCalls()).toBe(before + 1);
    expect(ai.capabilities.value).toEqual(ALL_FALSE);
  });

  it("does not storm the server: a burst of 503s or of signals is one request and one trailing one, and one start is one feed", async () => {
    const ai = useAiCapabilities();
    ai.start(feed.source); ai.start(feed.source);
    await vi.advanceTimersByTimeAsync(0);
    expect(feed.count("ai")).toBe(1);
    const settled = capabilityCalls();
    let release!: () => void;
    fetchMock.mockImplementationOnce(() => new Promise<Response>(resolve => { release = () => resolve(json(ALL_FALSE)); }));
    for (let i = 0; i < 20; i++) { notifyAiUnavailable(); feed.emit("ai", {}); }
    await vi.advanceTimersByTimeAsync(0);
    expect(capabilityCalls()).toBe(settled + 1);
    release();
    await vi.advanceTimersByTimeAsync(0);
    expect(capabilityCalls()).toBe(settled + 2);
  });

  it("leaves the feed and resets to all false on stop", async () => {
    const ai = useAiCapabilities();
    ai.start(feed.source);
    await vi.advanceTimersByTimeAsync(0);
    ai.stop();
    expect(feed.count()).toBe(0);
    expect(ai.capabilities.value).toEqual(ALL_FALSE);
    const before = capabilityCalls();
    notifyAiUnavailable();
    feed.emit("ai", {});
    await vi.advanceTimersByTimeAsync(5 * 60_000);
    expect(capabilityCalls()).toBe(before);
  });

  it("drops an answer that arrives after stop", async () => {
    const ai = useAiCapabilities();
    let release!: () => void;
    fetchMock.mockImplementationOnce(() => new Promise<Response>(resolve => { release = () => resolve(json({ semanticSearch: true })); }));
    ai.start(feed.source);
    await vi.advanceTimersByTimeAsync(0);
    ai.stop();
    release();
    await vi.advanceTimersByTimeAsync(0);
    expect(ai.capabilities.value).toEqual(ALL_FALSE);
  });
});
