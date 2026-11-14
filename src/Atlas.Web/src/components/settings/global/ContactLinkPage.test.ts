import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { createFakeLiveFeed } from "../../../live/fakeLiveFeed";
import { liveFeedSourceKey } from "../../../live/liveFeed";
import { provideSettingsRoute } from "../useSettingsRoute";
import ContactLinkPage from "./ContactLinkPage.vue";
import type { ContactLinkReport, ContactLinkRun } from "../../../contacts/contactApi";

let wrapper: VueWrapper | undefined;
/** /hubs/live as the page hears it: `changed()` is a JobRunChanged of the job contacts.link. */
let feed = createFakeLiveFeed();
afterEach(() => { feed = createFakeLiveFeed(); wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); vi.useRealTimers(); });
const global = () => ({ provide: { [liveFeedSourceKey as symbol]: feed.source } });
const changed = async (jobId = "contacts.link") => { feed.emit("jobs", { runId: 7, jobId, state: "running", spaceId: null }); await flushPromises(); };

const item = (name: string) => ({ name, identityKey: name, occurrences: 1, pageIds: ["p"] });
const report: ContactLinkReport = { mode: "report", pages: 10, revisions: 0, linked: [item("a"), item("b")], created: [{ ...item("c"), spaceId: "s" }], unclear: [item("d")], restricted: [item("e")] };
const run = (over: Partial<ContactLinkRun> = {}): ContactLinkRun => ({ id: 7, state: "running", startedAt: "", pages: 10, pagesDone: 2, revisions: 0, report, ...over });
const api = (linkRun: () => Promise<ContactLinkRun | null>) => ({ linkReport: async () => report, linkApply: async () => run(), linkRun, linkCancel: async () => run({ state: "cancelled" }) });
const button = (w: VueWrapper, selector: string) => w.get<HTMLButtonElement>(selector).element;

it("keeps both actions off until the open fetch returns and applying until a report exists, then confirms with the numbers", async () => {
  let open!: (value: ContactLinkRun | null) => void;
  const opened = new Promise<ContactLinkRun | null>(resolve => { open = resolve; });
  wrapper = mount(ContactLinkPage, { attachTo: document.body, props: { api: api(() => opened) }, global: global() });
  expect(button(wrapper, "[data-contact-link-report]").disabled).toBe(true);
  // The page is there at once, with a placeholder for the run's state until it is known.
  expect(wrapper.find(".loading-skeleton").exists()).toBe(true);
  open(null);
  await flushPromises();
  expect(wrapper.find(".loading-skeleton").exists()).toBe(false);
  expect(button(wrapper, "[data-contact-link-report]").disabled).toBe(false);
  expect(button(wrapper, "[data-contact-link-apply]").disabled).toBe(true);
  await wrapper.get("[data-contact-link-report]").trigger("click");
  await flushPromises();
  expect(button(wrapper, "[data-contact-link-apply]").disabled).toBe(false);
  expect(wrapper.get("[data-contact-link-group=created]").text()).toContain("kontakt prostoru");
  await wrapper.get("[data-contact-link-apply]").trigger("click");
  await flushPromises();
  expect(document.body.textContent).toContain("Propojí se 2, založí se 1, nejasných 1, nepropojených 1.");
});

it("reads the run when the server tells it changed, never on a timer; a failed read shows above the progress until the next good one", async () => {
  vi.useFakeTimers();
  let calls = 0, fail = false;
  wrapper = mount(ContactLinkPage, { attachTo: document.body, props: { api: api(async () => { calls++; if (fail) throw new Error("síť"); return run({ pagesDone: calls }); }) }, global: global() });
  await flushPromises();
  expect(wrapper.find("[data-contact-link-cancel]").exists()).toBe(true);
  const settled = calls;
  await vi.advanceTimersByTimeAsync(60_000);
  expect(calls).toBe(settled);
  // Another job's run is not this page's.
  await changed("backups.zip");
  expect(calls).toBe(settled);

  fail = true;
  await changed();
  expect(wrapper.find("[role=alert]").exists()).toBe(true);
  expect(wrapper.find("[data-contact-link-cancel]").exists()).toBe(true);
  fail = false;
  await changed();
  expect(wrapper.find("[role=alert]").exists()).toBe(false);
  expect(calls).toBe(settled + 2);
});

it("once the followed run ends shows only its result, not the plan's stale tiles, and applying waits for a fresh report", async () => {
  let done = false;
  wrapper = mount(ContactLinkPage, { attachTo: document.body, props: { api: api(async () => (done ? run({ state: "succeeded", pagesDone: 10, revisions: 7 }) : run())) }, global: global() });
  await flushPromises();
  expect(wrapper.find("[data-contact-link-counts]").text()).toContain("Založí se");
  done = true;
  await changed();
  expect(wrapper.get("[data-contact-link-result]").text()).toContain("Hotovo: propojeno 2, založeno 1, nejasných 1; upraveno verzí stránek 7.");
  expect(wrapper.find("[data-contact-link-counts]").exists()).toBe(false);
  expect(button(wrapper, "[data-contact-link-apply]").disabled).toBe(true);
  await wrapper.get("[data-contact-link-report]").trigger("click");
  await flushPromises();
  expect(wrapper.find("[data-contact-link-result]").exists()).toBe(false);
  expect(wrapper.find("[data-contact-link-counts]").exists()).toBe(true);
});

it("follows a queued run like a running one, links to the run's detail and log under the jobs, and says when the feed is lost", async () => {
  let state: ContactLinkRun["state"] = "queued";
  const Host = defineComponent({
    setup() {
      provideSettingsRoute("#/settings");
      return () => h(ContactLinkPage, { api: api(async () => run({ state, pagesDone: 0 })) });
    }
  });
  wrapper = mount(Host, { attachTo: document.body, global: global() });
  await flushPromises();
  expect(wrapper.find("[data-contact-link-cancel]").exists()).toBe(true);
  expect(wrapper.get("[data-contact-link-run-detail]").attributes("href")).toBe("#/settings/general/jobs/contacts.link/runs/7");
  state = "running";
  await changed();
  expect(wrapper.find("[data-contact-link-cancel]").exists()).toBe(true);
  feed.fail("jobs");
  await flushPromises();
  expect(wrapper.get("[data-live-update-note]").text()).toContain("Živá aktualizace nedostupná");
});
