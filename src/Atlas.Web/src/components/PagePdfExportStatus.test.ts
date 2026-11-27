import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../api";
import PagePdfExportStatus from "./PagePdfExportStatus.vue";
import { fakeApi } from "./settings/general/testing";
import { createFakeLiveFeed } from "../live/fakeLiveFeed";
import { liveFeedSourceKey } from "../live/liveFeed";

let wrapper: VueWrapper | undefined;
let feed = createFakeLiveFeed();
const state = (extra = {}) => ({ settings: { enabled: true }, status: "current", lastExportedAt: "2026-11-08T13:30:00Z", lastFileName: "Guide 2026-11-08 14-30.pdf", ...extra });
const mountStatus = (props = {}) => mount(PagePdfExportStatus, { props: { pageId: "p1", version: 1, dirty: false, settingsOpen: false, ...props }, global: { provide: { [liveFeedSourceKey as symbol]: feed.source } } });
afterEach(() => { feed = createFakeLiveFeed(); wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); vi.restoreAllMocks(); clearCsrf(); });

it("shows the finished PDF with its file name and always offers the manual download", async () => {
  fakeApi(() => state());
  wrapper = mountStatus(); await flushPromises();
  expect(wrapper.text()).toContain("PDF hotové");
  expect(wrapper.text()).toContain("Guide 2026-11-08 14-30.pdf");
  await wrapper.get("[data-pdf-status-download]").trigger("click");
  expect(wrapper.emitted("download")).toHaveLength(1);
});

it("waits while the writer types, is pending after a save, and offers a retry after a failure", async () => {
  fakeApi(() => state({ status: "pending" }));
  wrapper = mountStatus({ dirty: true }); await flushPromises();
  expect(wrapper.text()).toContain("až se dopíše");
  await wrapper.setProps({ dirty: false });
  expect(wrapper.text()).toContain("PDF se připravuje");
  wrapper.unmount();
  const calls = fakeApi((method) => method === "POST" ? state() : state({ status: "error" }));
  wrapper = mountStatus(); await flushPromises();
  await wrapper.get("[data-pdf-status-retry]").trigger("click"); await flushPromises();
  expect(calls.find(call => call.method === "POST")?.path).toBe("/pages/p1/pdf-export/run");
});

it("reads the export's state once on opening: the feed's subscription reads it, nothing else (perf guard: it was read twice)", async () => {
  const calls = fakeApi(() => state());
  wrapper = mountStatus(); await flushPromises();
  expect(calls.filter(call => call.method === "GET")).toHaveLength(1);
  // Without the feed (refused, offline) the state is read then, so the badge still stands.
  feed.fail(); await flushPromises();
  expect(calls.filter(call => call.method === "GET")).toHaveLength(2);
});

it("asks once for a page the server answers 404 and not again on a reconnect, a new version or closed settings", async () => {
  const calls = fakeApi(() => new Response(JSON.stringify({ status: 404 }), { status: 404, headers: { "Content-Type": "application/problem+json" } }));
  wrapper = mountStatus(); await flushPromises();
  feed.resubscribe(); feed.fail(); await wrapper.setProps({ version: 2, settingsOpen: true }); await wrapper.setProps({ settingsOpen: false }); await flushPromises();
  expect(calls.filter(call => call.method === "GET")).toHaveLength(1);
  expect(wrapper.find("[data-pdf-status]").exists()).toBe(false);
});

it("shows nothing for a page without an enabled export", async () => {
  fakeApi(() => state({ settings: { enabled: false }, status: "paused" }));
  wrapper = mountStatus(); await flushPromises();
  expect(wrapper.find("[data-pdf-status]").exists()).toBe(false);
});
