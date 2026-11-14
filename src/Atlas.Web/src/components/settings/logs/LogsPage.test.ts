import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import type { LogApi, LogEntry, LogFilter, LogLevel, LogPage } from "../../../logs/logApi";
import { mountPage } from "../general/testing";
import { createFakeLiveFeed } from "../../../live/fakeLiveFeed";
import LogsPage from "./LogsPage.vue";

let wrapper: VueWrapper | undefined;
/** /hubs/live as the page hears it: `stored()` is a LogsWritten. */
let feed = createFakeLiveFeed();
const stored = async (level: LogLevel = "information") => { feed.emit("logs", { level }); await flushPromises(); };
/** The elements the page watches with an IntersectionObserver, by their data attribute; `see` tells the page whether one is in view. */
const watched = new Map<string, (visible: boolean) => void>();
beforeEach(() => {
  vi.stubGlobal("IntersectionObserver", class {
    constructor(private callback: (items: Array<{ isIntersecting: boolean }>) => void) {}
    observe(element: Element) { const key = ["log-top", "log-bottom"].find((name) => element.hasAttribute(`data-${name}`))!; watched.set(key, (visible) => this.callback([{ isIntersecting: visible }])); }
    disconnect() {}
  });
});
afterEach(() => { feed = createFakeLiveFeed(); wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); vi.useRealTimers(); vi.unstubAllGlobals(); watched.clear(); });
const see = async (key: string, visible: boolean) => { watched.get(key)!(visible); await flushPromises(); };

const entry = (id: number, level: LogLevel = "information", over: Partial<LogEntry> = {}): LogEntry => ({
  id, at: new Date(Date.now() - (100 - id) * 1000).toISOString(), level, category: "Atlas.Server.Features.Backups.ZipBackupJob", eventId: null, message: `Zpráva ${id}`,
  exceptionType: null, exceptionMessage: null, exception: null, traceId: null, userId: null, jobRunId: null, jobId: null, jobTitle: null, ...over,
});
const stack = "System.IO.IOException: Úložiště neodpovídá.\n   at System.IO.File.Write()\n   at Atlas.Server.Backups.Run() in Run.cs:line 8";
const summary = { errorsHour: 2, warningsHour: 1, errorsDay: 5, warningsDay: 9, dropped: 0 };

/** A log of `entries` (newest first) behind the LogApi; `calls` keep the filter and cursor of every list call. */
function fakeApi(entries: LogEntry[], options: { tail?: () => LogPage; pageSize?: number } = {}) {
  const calls: Array<{ filter: LogFilter; cursor?: { before?: number; after?: number } }> = [];
  const size = options.pageSize ?? 100;
  const api: LogApi = {
    async list(filter, cursor) {
      calls.push({ filter, cursor });
      if (cursor?.after !== undefined) return options.tail?.() ?? { entries: [], more: false, summary: null };
      const found = entries.filter((item) => cursor?.before === undefined || item.id < cursor.before);
      return { entries: found.slice(0, size), more: found.length > size, summary: cursor ? null : summary };
    },
    exportUrl: (filter) => `/export?q=${filter.q ?? ""}`,
  };
  return { api, calls };
}
const mount = (api: LogApi, query = "") => {
  wrapper = mountPage(LogsPage, { api }, { hash: `#/settings/general/logs${query}`, feed: feed.source }).wrapper as unknown as VueWrapper;
  return wrapper;
};
const ids = (view: VueWrapper) => view.findAll("[data-log-row]").map((row) => Number(row.attributes("data-log-row")));
const newest = (from: number, to: number) => Array.from({ length: to - from + 1 }, (_, index) => entry(to - index));

describe("the list", () => {
  it("shows the entries newest first, a line each with the level as its label", async () => {
    const { api } = fakeApi([entry(5, "critical"), entry(4, "error"), entry(3, "warning"), entry(2, "information"), entry(1, "debug")]);
    const view = mount(api);
    await flushPromises();
    expect(ids(view)).toEqual([5, 4, 3, 2, 1]);
    expect(view.get('[data-log-row="4"]').attributes("data-level")).toBe("error");
    expect(view.get('[data-log-row="3"]').attributes("data-level")).toBe("warning");
    expect(view.findAll("[data-log-level]").map((label) => label.text())).toEqual(["CRIT", "ERROR", "WARN", "INFO", "DEBUG"]);
    expect(view.get('[data-log-row="4"]').text()).toContain("Backups.ZipBackupJob");
    expect(view.get('[data-log-row="4"] .log-category').attributes("title")).toBe("Atlas.Server.Features.Backups.ZipBackupJob");
  });

  it("says so when nothing matches and when the load fails", async () => {
    const view = mount(fakeApi([]).api);
    await flushPromises();
    expect(view.text()).toContain("Žádné záznamy");
    view.unmount();
    wrapper = undefined;
    const failing: LogApi = { list: async () => { throw new Error("Server neodpovídá."); }, exportUrl: () => "" };
    const broken = mount(failing);
    await flushPromises();
    expect(broken.text()).toContain("Server neodpovídá.");
    expect(broken.find(".loading-skeleton").exists()).toBe(false);
  });

  it("loads the older page from „Načíst starší“ and when the end of the list comes into view", async () => {
    const { api, calls } = fakeApi(newest(1, 250), { pageSize: 100 });
    const view = mount(api);
    await flushPromises();
    expect(ids(view)).toHaveLength(100);
    await view.get("[data-log-older]").trigger("click");
    await flushPromises();
    expect(calls.at(-1)?.cursor).toEqual({ before: 151 });
    expect(ids(view)).toHaveLength(200);
    await see("log-bottom", true);
    expect(ids(view)).toHaveLength(250);
    expect(view.find("[data-log-older]").exists()).toBe(false);
  });

  it("writes the exception under its line, a line each, the frames indented", async () => {
    const thrown = entry(2, "error", { exceptionType: "System.IO.IOException", exceptionMessage: "Úložiště neodpovídá.", exception: stack });
    const view = mount(fakeApi([thrown, entry(1)]).api);
    await flushPromises();
    const lines = view.findAll('[data-log-row="2"] .log-exception-line');
    expect(lines.map((line) => line.text())).toEqual(["System.IO.IOException: Úložiště neodpovídá.", "at\u00a0System.IO.File.Write()", "at\u00a0Atlas.Server.Backups.Run() in Run.cs:line 8"]);
    expect(lines.map((line) => line.classes("is-frame"))).toEqual([false, true, true]);
    expect(view.find('[data-log-row="1"] [data-log-exception]').exists()).toBe(false);
  });

  it("names the job run a line was logged in with a chip that opens the run", async () => {
    const view = mount(fakeApi([entry(2, "error", { jobRunId: 7, jobId: "backups.zip", jobTitle: "Záloha ZIP" }), entry(1)]).api);
    await flushPromises();
    const chip = view.get('[data-log-row="2"] [data-log-job]');
    expect(chip.text()).toBe("Záloha ZIP");
    expect(chip.attributes("href")).toBe("#/settings/general/jobs/backups.zip/runs/7");
    expect(view.find('[data-log-row="1"] [data-log-job]').exists()).toBe(false);
  });

  it("copies every loaded entry at once, oldest first, a line each", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { clipboard: { writeText } });
    const view = mount(fakeApi([entry(2, "error"), entry(1)]).api);
    await flushPromises();
    expect(view.get("[data-log-copy-all]").text()).toBe("Kopírovat 2 záznamy");
    await view.get("[data-log-copy-all]").trigger("click");
    const lines = (writeText.mock.calls[0]![0] as string).split("\n");
    expect(lines[0]).toContain("[information]");
    expect(lines.at(-1)).toContain("[error]");
  });

});

describe("the filters", () => {
  it("reads them from the URL on arrival", async () => {
    const { api, calls } = fakeApi([entry(1)]);
    const view = mount(api, "?uroven=warning&cas=1h");
    await flushPromises();
    expect(document.activeElement?.getAttribute("data-filter")).toBe("level");
    const filter = calls[0]!.filter;
    expect(filter.level).toBe("warning");
    const ago = Date.now() - new Date(filter.from!).getTime();
    expect(ago).toBeGreaterThanOrEqual(3_600_000 - 1000);
    expect(ago).toBeLessThan(3_600_000 + 5000);
    expect(view.get("[data-log-export]").attributes("href")).toBe("/export?q=");
  });

  it("filter by the request id of an entry, and the chip removes it", async () => {
    const { api, calls } = fakeApi([entry(2, "error", { traceId: "trace-abc" }), entry(1)]);
    const view = mount(api);
    await flushPromises();
    await view.get('[data-log-row="2"] [data-log-trace]').trigger("click");
    await flushPromises();
    expect(window.location.hash).toContain("pozadavek=trace-abc");
    expect(calls.at(-1)?.filter.traceId).toBe("trace-abc");
    await view.get("[data-log-trace-chip]").trigger("click");
    await flushPromises();
    expect(calls.at(-1)?.filter.traceId).toBeUndefined();
  });
});

describe("živě", () => {
  const live = "?live=1";

  it("reads what came since the list on subscribing, then prepends new entries each time the server stores some", async () => {
    vi.useFakeTimers();
    let next = 3;
    const { api, calls } = fakeApi([entry(2), entry(1)], { tail: () => ({ entries: [entry(next++)], more: false, summary: null }) });
    const view = mount(api, live);
    await flushPromises();
    expect(calls.at(-1)?.cursor).toEqual({ after: 2 });
    expect(ids(view)).toEqual([3, 2, 1]);
    await stored();
    expect(calls.at(-1)?.cursor).toEqual({ after: 3 });
    expect(ids(view)).toEqual([4, 3, 2, 1]);
    expect(view.find("[data-log-pill]").exists()).toBe(false);
  });

  it("holds the new entries back while the reader is away from the top, counts them and shows them on the pill", async () => {
    vi.useFakeTimers();
    let next = 3;
    const { api, calls } = fakeApi([entry(2), entry(1)], { tail: () => ({ entries: [entry(next++)], more: false, summary: null }) });
    const view = mount(api, live);
    await flushPromises();
    await see("log-top", false);
    await stored();
    await stored();
    expect(ids(view)).toEqual([3, 2, 1]);
    expect(view.get("[data-log-pill]").text()).toContain("2 nové");
    // The next read asks after the newest held-back entry, not after the one shown.
    expect(calls.at(-1)?.cursor).toEqual({ after: 4 });

    await view.get("[data-log-pill]").trigger("click");
    await flushPromises();
    expect(ids(view)).toEqual([5, 4, 3, 2, 1]);
    expect(view.find("[data-log-pill]").exists()).toBe(false);
  });

  it("shows the held-back entries by itself when the reader scrolls back to the top", async () => {
    vi.useFakeTimers();
    let next = 2;
    const { api } = fakeApi([entry(1)], { tail: () => ({ entries: [entry(next++)], more: false, summary: null }) });
    const view = mount(api, live);
    await flushPromises();
    await see("log-top", false);
    await stored();
    expect(ids(view)).toEqual([2, 1]);
    await see("log-top", true);
    expect(ids(view)).toEqual([3, 2, 1]);
  });

  it("replaces the list when more than a page arrived", async () => {
    vi.useFakeTimers();
    const { api } = fakeApi([entry(2), entry(1)], { tail: () => ({ entries: [entry(9), entry(8)], more: true, summary: null }) });
    const view = mount(api, live);
    await flushPromises();
    await stored();
    expect(ids(view)).toEqual([9, 8]);
    expect(view.find("[data-log-older]").exists()).toBe(true);
  });

  it("holds a large batch back like a small one while the reader is away from the top, and replaces the list only on the pill", async () => {
    vi.useFakeTimers();
    let batch = false;
    const { api } = fakeApi([entry(2), entry(1)], { tail: () => batch ? { entries: [entry(9), entry(8)], more: true, summary: null } : { entries: [], more: false, summary: null } });
    const view = mount(api, live);
    await flushPromises();
    await see("log-top", false);
    batch = true;
    await stored();
    expect(ids(view)).toEqual([2, 1]);
    expect(view.get("[data-log-pill]").text()).toContain("2 nové");
    await view.get("[data-log-pill]").trigger("click");
    await flushPromises();
    expect(ids(view)).toEqual([9, 8]);
    expect(view.find("[data-log-pill]").exists()).toBe(false);
    expect(view.find("[data-log-older]").exists()).toBe(true);
  });

  it("follows the feed only while on, never on a timer, and leaves it when the page goes", async () => {
    vi.useFakeTimers();
    const { api, calls } = fakeApi([entry(1)]);
    const view = mount(api);
    await flushPromises();
    expect(feed.count("logs")).toBe(0);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(calls).toHaveLength(1);
    await view.get("[data-log-live]").trigger("click");
    await flushPromises();
    // Subscribed: what came since the list was read, once.
    expect(feed.count("logs")).toBe(1);
    expect(calls.filter((call) => call.cursor?.after !== undefined)).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(calls.filter((call) => call.cursor?.after !== undefined)).toHaveLength(1);
    view.unmount();
    wrapper = undefined;
    expect(feed.count("logs")).toBe(0);
  });

  it("skips a signal below the filter's level and says when the feed is lost", async () => {
    const { api, calls } = fakeApi([entry(1, "error")]);
    const view = mount(api, `${live}&uroven=error`);
    await flushPromises();
    const tails = () => calls.filter((call) => call.cursor?.after !== undefined).length;
    const before = tails();
    await stored("information");
    expect(tails()).toBe(before);
    await stored("critical");
    expect(tails()).toBe(before + 1);
    feed.fail("logs");
    await flushPromises();
    expect(view.get("[data-live-update-note]").text()).toContain("Živá aktualizace nedostupná");
  });
});
