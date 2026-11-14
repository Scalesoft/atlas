// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";
import { effectScope, nextTick, ref } from "vue";
import { createFakeLiveFeed } from "./fakeLiveFeed";
import { createLiveConnection, type LiveHub } from "./liveConnection";
import { jobsTopic, liveConnectionSource, logsTopic, pageTopic, useLiveFeed, type JobRunChange } from "./liveFeed";

const settle = async () => { for (let i = 0; i < 5; i++) await Promise.resolve(); await nextTick(); };
afterEach(() => { vi.useRealTimers(); });

/** Runs the composable outside a component, as a page's setup would. */
function follow<T>(run: () => T) {
  const scope = effectScope();
  const result = scope.run(run)!;
  return { ...result, stop: () => scope.stop() };
}

describe("useLiveFeed", () => {
  it("reads on every subscription and on each accepted event, one read at a time, and never on a timer", async () => {
    vi.useFakeTimers();
    const feed = createFakeLiveFeed();
    let release!: () => void;
    const read = vi.fn(() => new Promise<void>((resolve) => { release = resolve; }));
    const live = follow(() => useLiveFeed<JobRunChange>(jobsTopic, read, (change) => change.jobId === "backups.zip", feed.source));
    await settle();
    expect(read).toHaveBeenCalledTimes(1);
    // Events while a read runs coalesce into one more read after it; another job's run is not this page's.
    feed.emit("jobs", { runId: 1, jobId: "backups.zip", state: "running", spaceId: null });
    feed.emit("jobs", { runId: 1, jobId: "backups.zip", state: "succeeded", spaceId: null });
    feed.emit("jobs", { runId: 2, jobId: "retention", state: "running", spaceId: null });
    await settle();
    expect(read).toHaveBeenCalledTimes(1);
    release();
    await settle();
    expect(read).toHaveBeenCalledTimes(2);
    release();
    await settle();
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(read).toHaveBeenCalledTimes(2);
    // A reconnect reads once more (it covers what was missed).
    feed.resubscribe("jobs");
    await settle();
    expect(read).toHaveBeenCalledTimes(3);
    release();
    live.stop();
    expect(feed.count()).toBe(0);
  });

  it("says when the feed is unavailable until it is subscribed again", async () => {
    const feed = createFakeLiveFeed();
    const live = follow(() => useLiveFeed(logsTopic, () => {}, undefined, feed.source));
    await settle();
    expect(live.unavailable.value).toBe(false);
    feed.fail("logs");
    expect(live.unavailable.value).toBe(true);
    feed.resubscribe("logs");
    expect(live.unavailable.value).toBe(false);
    live.stop();
  });

  it("follows a topic getter: none closes it, another channel reopens it", async () => {
    const feed = createFakeLiveFeed();
    const page = ref<string | undefined>("A1");
    const live = follow(() => useLiveFeed(() => (page.value ? pageTopic(page.value, "PageChanged") : undefined), () => {}, undefined, feed.source));
    expect(feed.count("page:a1")).toBe(1);
    page.value = "B2";
    await nextTick();
    expect([feed.count("page:a1"), feed.count("page:b2")]).toEqual([0, 1]);
    page.value = undefined;
    await nextTick();
    expect(feed.count()).toBe(0);
    live.stop();
  });
});

describe("liveConnectionSource", () => {
  function fakeHub() {
    const handlers = new Map<string, (payload: unknown) => void>();
    const hub = {
      on: vi.fn((event: string, handler: (payload: unknown) => void) => { handlers.set(event, handler); }),
      onreconnecting: () => {}, onreconnected: () => {}, onclose: () => {},
      start: vi.fn(async () => {}), stop: vi.fn(async () => {}),
      invoke: vi.fn(async (method: string, kind: string) => {
        if (method === "SubscribeAll" && kind === "logs") throw new Error("HubException: forbidden");
        return method === "SubscribeAll" ? { sequence: 0 } : undefined;
      }),
      send: vi.fn(async () => {}),
    };
    return { hub: hub as unknown as LiveHub & typeof hub, emit: (event: string, payload: unknown) => handlers.get(event)?.(payload) };
  }

  it("asks the server for a derived feed by its kind alone, hears its events, and asks again after a revocation", async () => {
    const fake = fakeHub();
    const connection = createLiveConnection(async () => fake.hub, { failed: () => {} });
    const source = liveConnectionSource(() => connection);
    const heard = { message: vi.fn(), subscribed: vi.fn(), unavailable: vi.fn() };
    const close = source(jobsTopic, heard);
    await settle();
    expect(fake.hub.invoke).toHaveBeenCalledWith("SubscribeAll", "jobs");
    expect(heard.subscribed).toHaveBeenCalledTimes(1);
    fake.emit("JobRunChanged", { runId: 1, jobId: "x", state: "running", spaceId: null });
    expect(heard.message).toHaveBeenCalledWith({ runId: 1, jobId: "x", state: "running", spaceId: null });

    // The server revoked it (a lost right): unavailable, then SubscribeAll again, which derives what is left.
    fake.emit("Revoked", { channel: "jobs" });
    await settle();
    expect(heard.unavailable).toHaveBeenCalledTimes(1);
    expect(fake.hub.invoke.mock.calls.filter((call) => call[0] === "SubscribeAll")).toHaveLength(2);
    expect(heard.subscribed).toHaveBeenCalledTimes(2);

    // Refused (not a wiki administrator): unavailable, nothing retried.
    const logs = { message: vi.fn(), subscribed: vi.fn(), unavailable: vi.fn() };
    const closeLogs = source(logsTopic, logs);
    await settle();
    expect(logs.unavailable).toHaveBeenCalledTimes(1);
    expect(logs.subscribed).not.toHaveBeenCalled();

    close();
    closeLogs();
    await settle();
    expect(fake.hub.invoke).toHaveBeenCalledWith("UnsubscribeAll", "jobs");
  });
});
