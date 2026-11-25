import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PageHistoryPanel from "./PageHistoryPanel.vue";
import { downloadContentExport } from "../contentExport";
vi.mock("../contentExport", () => ({ downloadContentExport: vi.fn(), rememberedCoverDetails: () => ["revision"] }));
import { ApiError, type Page } from "../api";
import { historyApi, type PageHistory, type PageRevision, type PageRevisionSummary, type RevisionChanges } from "../history";

vi.mock("../history", () => ({ historyApi: { list: vi.fn(), steps: vi.fn(), revision: vi.fn(), restore: vi.fn(), prefetch: vi.fn(), forget: vi.fn() } }));
vi.mock("./history/HistoryActivityChart.vue", () => ({ __esModule: true, default: { props: ["items"], emits: ["pick"], template: "<div data-activity />" } }));
vi.mock("./HistoryRevisionPreview.vue", () => ({ __esModule: true, default: { props: ["contentJson"], template: '<div data-preview="readonly">{{ contentJson }}</div>' } }));
let wrapper: VueWrapper | undefined;
const page: Page = { id: "page", spaceId: "space", parentId: null, title: "Příručka", version: 3, isPublished: true, publishedVersion: 2, contentJson: "live", activeFrom: "2026-01-01T08:00:00Z", canEdit: true };
const summary = (version: number): PageRevisionSummary => ({ id: `revision-${version}`, version, title: `Příručka ${version}`, authorId: "author", authorName: "Jana", activeFrom: "2026-09-06T12:00:00Z", activeTo: version === 3 ? null : "2026-09-06T13:00:00Z", previousRevisionId: version > 1 ? `revision-${version - 1}` : null, isCurrent: version === 3, isPublished: version === 2, lastPublishedAt: version === 2 ? "2026-09-06T12:30:00Z" : null });
const changes = (override: Partial<RevisionChanges> = {}): RevisionChanges => ({ added: 4, removed: 1, blocks: 2, sections: [], states: [], requirementsAdded: 0, requirementsRemoved: 0, contactsAdded: [], contactsRemoved: [], attachmentsAdded: 0, attachmentsRemoved: 0, titleBefore: null, contributors: [], ...override });
const revision = (version: number): PageRevision => ({ ...summary(version), pageId: page.id, contentJson: `historical-${version}`, markdown: `historical-${version}` });
const history = (versions = [3, 2], currentVersion = 3, nextBeforeVersion: number | null = null): PageHistory => ({ pageId: page.id, currentVersion, items: versions.map(summary), nextBeforeVersion });
const props = () => ({ page, spaceRole: "Writer" as const, isWikiAdmin: false, dark: false, accent: "blue" as const, beforeRestore: vi.fn().mockResolvedValue(true) });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
async function open(override: Record<string, unknown> = {}, fullVersion = true) {
  vi.mocked(historyApi.list).mockImplementation(async (_, before) => before ? history([1]) : history());
  vi.mocked(historyApi.revision).mockImplementation(async (_, id) => revision(Number(id.split("-")[1])));
  wrapper = mount(PageHistoryPanel, { props: { ...props(), ...override }, attachTo: document.body });
  await flushPromises();
  if (fullVersion && (override.page as Page | undefined)?.canEdit !== false) {
    await wrapper.findAll("button").find(b => b.text() === "Celá verze")!.trigger("click"); await flushPromises();
    await wrapper.get(".history-restore-toggle").trigger("click");
  }
  return wrapper;
}
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.resetAllMocks(); document.body.innerHTML = ""; });

describe("page history", () => {
  it("opens the version selected in the article dock instead of the latest revision", async () => {
    const view = await open({ initialRevision: summary(2) }, false);
    expect(view.get('[data-revision="revision-2"]').attributes("aria-current")).toBe("true");
    expect(view.get(".history-preview-identity").text()).toContain("v2");
  });
  it("shows the channel after the author, the token in the preview and nothing for web or legacy rows", async () => {
    const tokenRef = { id: "t", name: "Claude Code – notebook", revoked: true };
    vi.mocked(historyApi.list).mockResolvedValue({ ...history(), items: [{ ...summary(3), channel: "mcp", apiToken: tokenRef, publicationChannel: "api", publicationApiToken: null }, { ...summary(2), channel: "web" }] });
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => ({ ...revision(Number(id.split("-")[1])), channel: "mcp", apiToken: tokenRef, publicationChannel: "api", publicationApiToken: null }));
    wrapper = mount(PageHistoryPanel, { props: props(), attachTo: document.body }); await flushPromises();
    const rows = wrapper.findAll(".history-version");
    expect(rows[0]!.text()).toContain("přes MCP");
    expect(rows[1]!.find("[data-channel-badge]").exists()).toBe(false);
    await rows[0]!.trigger("click"); await flushPromises();
    expect(wrapper.get("[data-preview-channel]").text()).toContain("token ‚Claude Code – notebook‘ (odvolaný)");
    expect(wrapper.get("[data-preview-publication-channel]").text()).toContain("přes API");
    expect(wrapper.get("[data-preview-publication-channel]").text()).not.toContain("token");
  });

  it("finds changes by MCP and API in the author filter", async () => {
    vi.mocked(historyApi.list).mockResolvedValue({ ...history(), items: [{ ...summary(3), channel: "mcp" }, { ...summary(2), channel: "web" }] });
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => revision(Number(id.split("-")[1])));
    wrapper = mount(PageHistoryPanel, { props: props(), attachTo: document.body }); await flushPromises();
    await wrapper.get("input[type=search]").setValue("mcp"); await flushPromises();
    expect(wrapper.findAll(".history-version")).toHaveLength(1);
  });

  it("shows the channel after the author, the token in the preview and nothing for web or legacy rows", async () => {
    const tokenRef = { id: "t", name: "Claude Code – notebook", revoked: true };
    vi.mocked(historyApi.list).mockResolvedValue({ ...history(), items: [{ ...summary(3), channel: "mcp", apiToken: tokenRef, publicationChannel: "api", publicationApiToken: null }, { ...summary(2), channel: "web" }] });
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => ({ ...revision(Number(id.split("-")[1])), channel: "mcp", apiToken: tokenRef, publicationChannel: "api", publicationApiToken: null }));
    wrapper = mount(PageHistoryPanel, { props: props(), attachTo: document.body }); await flushPromises();
    const rows = wrapper.findAll(".history-version");
    expect(rows[0]!.text()).toContain("přes MCP");
    expect(rows[1]!.find("[data-channel-badge]").exists()).toBe(false);
    await rows[0]!.trigger("click"); await flushPromises();
    expect(wrapper.get("[data-preview-channel]").text()).toContain("token ‚Claude Code – notebook‘ (odvolaný)");
    expect(wrapper.get("[data-preview-publication-channel]").text()).toContain("přes API");
    expect(wrapper.get("[data-preview-publication-channel]").text()).not.toContain("token");
  });

  it("finds changes by MCP and API in the author filter", async () => {
    vi.mocked(historyApi.list).mockResolvedValue({ ...history(), items: [{ ...summary(3), channel: "mcp" }, { ...summary(2), channel: "web" }] });
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => revision(Number(id.split("-")[1])));
    wrapper = mount(PageHistoryPanel, { props: props(), attachTo: document.body }); await flushPromises();
    await wrapper.get("input[type=search]").setValue("mcp"); await flushPromises();
    expect(wrapper.findAll(".history-version")).toHaveLength(1);
  });

  it("shows the change summary of a published revision and none for a saved one", async () => {
    const long = "Doplněn postup obnovy zálohy včetně kontroly integrity a ".repeat(6).trim();
    vi.mocked(historyApi.list).mockResolvedValue({ ...history(), items: [{ ...summary(3), changeSummary: null }, { ...summary(2), changeSummary: long }] });
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => ({ ...revision(Number(id.split("-")[1])), changeSummary: id === "revision-2" ? long : null }));
    wrapper = mount(PageHistoryPanel, { props: props() }); await flushPromises();
    const rows = wrapper.findAll(".history-version");
    expect(rows[0]!.find(".history-version-summary").exists()).toBe(false);
    expect(wrapper.find(".history-preview-summary").exists()).toBe(false);
    const line = rows[1]!.get(".history-version-summary");
    expect(line.text()).toBe(long);
    expect(line.attributes("title")).toBe(long);
    await rows[1]!.trigger("click"); await flushPromises();
    expect(wrapper.get(".history-preview-summary").text()).toBe(long);
  });
  it("opens on changes against the immediate predecessor, with an explicitly historical full-version view", async () => {
    await open({}, false);
    expect(wrapper!.find(".history-preview-heading [aria-checked=\"true\"]").text()).toBe("Změny");
    expect(wrapper!.text()).toContain("Historická verze");
    expect(wrapper!.get("del").text()).toContain("2");
    expect(wrapper!.get("ins").text()).toContain("3");
    expect(wrapper!.find("[data-preview]").exists()).toBe(false);
    await wrapper!.findAll("button").find(b => b.text() === "Celá verze")!.trigger("click");
    await flushPromises();
    expect(wrapper!.get("[data-preview]").text()).toBe("historical-3");
    expect(page.contentJson).toBe("live");
  });
  it("does not request history or expose restore to Readers", async () => {
    await open({ spaceRole: "Reader", page: { ...page, canEdit: false } });
    expect(historyApi.list).not.toHaveBeenCalled();
    expect(historyApi.revision).not.toHaveBeenCalled();
    expect(wrapper!.find(".history-restore").exists()).toBe(false);
  });

  it("drops out-of-order preview responses and never changes the active page while browsing", async () => {
    vi.mocked(historyApi.list).mockResolvedValue(history([3, 2, 1]));
    const old = deferred<PageRevision>(), current = deferred<PageRevision>();
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => id === "revision-3" ? old.promise : id === "revision-2" ? current.promise : revision(1));
    wrapper = mount(PageHistoryPanel, { props: props(), attachTo: document.body }); await flushPromises();
    await wrapper!.findAll(".history-version")[1]!.trigger("click");
    current.resolve(revision(2)); await flushPromises();
    old.resolve(revision(3)); await flushPromises();
    await wrapper.findAll("button").find(b => b.text() === "Celá verze")!.trigger("click"); await flushPromises();
    expect(wrapper!.get("[data-preview]").text()).toBe("historical-2");
    expect(wrapper!.emitted("restored")).toBeUndefined();
    expect(historyApi.restore).not.toHaveBeenCalled();
    expect(page.contentJson).toBe("live");
  });

  it("loads older revisions with a keyset cursor and keeps the selected preview", async () => {
    vi.mocked(historyApi.list).mockResolvedValueOnce(history([3, 2], 3, 2)).mockResolvedValueOnce(history([1]));
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => revision(Number(id.split("-")[1])));
    wrapper = mount(PageHistoryPanel, { props: props() }); await flushPromises();
    await wrapper.findAll("button").find(b => b.text() === "Celá verze")!.trigger("click"); await flushPromises();
    await wrapper.get(".history-load-more").trigger("click"); await flushPromises();
    expect(historyApi.list).toHaveBeenLastCalledWith("page", 2, expect.any(AbortSignal));
    expect(wrapper.findAll(".history-version")).toHaveLength(3);
    expect(wrapper.get("[data-preview]").text()).toBe("historical-3");
    expect(wrapper.find(".history-load-more").exists()).toBe(false);
    expect(wrapper.text()).toContain("Publikovaná");
    expect(wrapper.get("time").attributes("datetime")).toBe("2026-09-06T12:00:00Z");
  });

  it("gives Změny | Celá verze one thumb that is placed first and then follows the choice", async () => {
    // jsdom has no layout: Změny sits at 3 px (54 px wide), Celá verze at 59 px (72 px wide).
    const left = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetLeft");
    const width = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetWidth");
    const isFull = (element: HTMLElement) => element.textContent === "Celá verze";
    Object.defineProperty(HTMLElement.prototype, "offsetLeft", { configurable: true, get() { return isFull(this) ? 59 : 3; } });
    Object.defineProperty(HTMLElement.prototype, "offsetWidth", { configurable: true, get() { return this.tagName !== "BUTTON" ? 0 : isFull(this) ? 72 : 54; } });
    const frame = () => new Promise<void>(done => requestAnimationFrame(() => done()));
    try {
      const view = await open({}, false);
      await flushPromises(); await frame(); await frame(); await flushPromises();
      const track = view.get(".history-preview-heading .option-pills-group");
      expect(track.attributes("data-segmented")).toBeDefined();
      expect(track.attributes("style")).toContain("--thumb-x: 3px");
      expect(track.attributes("style")).toContain("--thumb-w: 54px");
      expect(track.attributes("data-thumb")).toBe("ready");
      await view.findAll("button").find(b => b.text() === "Celá verze")!.trigger("click"); await flushPromises(); await frame();
      expect(track.attributes("style")).toContain("--thumb-x: 59px");
      expect(track.attributes("style")).toContain("--thumb-w: 72px");
    } finally {
      if (left) Object.defineProperty(HTMLElement.prototype, "offsetLeft", left); else delete (HTMLElement.prototype as { offsetLeft?: number }).offsetLeft;
      if (width) Object.defineProperty(HTMLElement.prototype, "offsetWidth", width); else delete (HTMLElement.prototype as { offsetWidth?: number }).offsetWidth;
    }
  });
  it("unfolds the restore confirmation in place instead of inserting it at once under the preview", async () => {
    await open();
    await wrapper!.findAll(".history-version")[1]!.trigger("click"); await flushPromises();
    expect(wrapper!.find(".history-restore-confirm").exists()).toBe(false);
    await wrapper!.get(".history-restore-toggle").trigger("click");
    expect(wrapper!.get(".history-restore [data-inline-reveal]").attributes("data-state")).toBe("open");
    expect(wrapper!.get(".history-restore [data-inline-reveal] .history-restore-confirm").text()).toContain("Tohle je publikovaná verze");
    await wrapper!.get(".history-restore-toggle").trigger("click");
    expect(wrapper!.get(".history-restore [data-inline-reveal]").attributes("data-state")).toBe("closed");
  });

  it("keeps the historical selection on CAS conflict and retries only after explicit refresh", async () => {
    await open();
    await wrapper!.findAll(".history-version")[1]!.trigger("click"); await flushPromises();
    await wrapper!.get(".history-restore-toggle").trigger("click");
    vi.mocked(historyApi.restore).mockRejectedValueOnce(new ApiError(409, "Conflict"));
    await wrapper!.get(".history-restore [data-variant=default]").trigger("click"); await flushPromises();
    expect(wrapper!.get("[data-preview]").text()).toBe("historical-2");
    expect(wrapper!.get(".history-restore [data-variant=default]").attributes("disabled")).toBeDefined();
    expect(wrapper!.text()).toContain("Stránku mezitím někdo změnil");
    vi.mocked(historyApi.list).mockResolvedValue(history([4, 3, 2], 4));
    await wrapper!.get(".history-restore [data-variant=outline]").trigger("click"); await flushPromises();
    expect(wrapper!.get("[data-preview]").text()).toBe("historical-2");
    const restored = { ...page, version: 5, contentJson: "historical-2" };
    vi.mocked(historyApi.restore).mockResolvedValue(restored);
    await wrapper!.get(".history-restore [data-variant=default]").trigger("click"); await flushPromises();
    expect(historyApi.restore).toHaveBeenLastCalledWith("page", "revision-2", 4);
    expect(wrapper!.emitted("restored")).toEqual([[restored]]);
    expect(restored.publishedVersion).toBe(2);
  });

  it("shows others' unsaved live edits as the server's message and allows a retry without refreshing", async () => {
    await open();
    vi.mocked(historyApi.restore).mockRejectedValueOnce(new ApiError(409, "Na stránce jsou neuložené živé úpravy jiných uživatelů.", "collaboration_unsaved"));
    await wrapper!.get(".history-restore [data-variant=default]").trigger("click"); await flushPromises();
    expect(wrapper!.text()).toContain("neuložené živé úpravy jiných uživatelů");
    expect(wrapper!.text()).not.toContain("Stránku mezitím někdo změnil");
    expect(wrapper!.get(".history-restore [data-variant=default]").attributes("disabled")).toBeUndefined();
  });

  it("honors the host's unsaved-change guard without submitting a restore", async () => {
    const guard = vi.fn().mockResolvedValue(false);
    await open({ beforeRestore: guard });
    await wrapper!.get(".history-restore [data-variant=default]").trigger("click"); await flushPromises();
    expect(guard).toHaveBeenCalledOnce(); expect(historyApi.restore).not.toHaveBeenCalled();
    expect(wrapper!.get("[data-preview]").text()).toBe("historical-3");
  });

  it("cancels pending work and clears the preview when permission is lost", async () => {
    const pending = deferred<PageRevision>();
    vi.mocked(historyApi.list).mockResolvedValue(history());
    vi.mocked(historyApi.revision).mockReturnValue(pending.promise);
    wrapper = mount(PageHistoryPanel, { props: props() }); await flushPromises();
    const signal = vi.mocked(historyApi.revision).mock.calls.at(-1)![2];
    await wrapper!.setProps({ spaceRole: "Reader", page: { ...page, canEdit: false } });
    pending.resolve(revision(2)); await flushPromises();
    expect(signal.aborted).toBe(true); expect(wrapper!.find("[data-preview]").exists()).toBe(false);
    expect(wrapper!.find(".history-restore").exists()).toBe(false);
  });

  it("finds an author beyond the first page of history", async () => {
    await open({}, false);
    vi.mocked(historyApi.list).mockResolvedValueOnce(history([3, 2], 3, 2)).mockResolvedValueOnce({ ...history([1]), items: [{ ...summary(1), authorName: "Petr" }] });
    await wrapper!.get('[aria-label="Aktualizovat historii"]').trigger("click"); await flushPromises();
    await wrapper!.get('input[type="search"]').setValue("Petr"); await flushPromises();
    expect(wrapper!.findAll(".history-version")).toHaveLength(1);
    expect(wrapper!.get(".history-version").text()).toContain("Petr");
    expect(wrapper!.get(".history-preview-heading").text()).toContain("Jana");
    vi.mocked(historyApi.list).mockResolvedValueOnce(history([3, 2], 3, 2)).mockResolvedValueOnce({ ...history([1]), items: [{ ...summary(1), authorName: "Petr" }] });
    await wrapper!.get('[aria-label="Aktualizovat historii"]').trigger("click"); await flushPromises();
    expect(wrapper!.findAll(".history-version")).toHaveLength(1);
    expect(wrapper!.get(".history-version").text()).toContain("Petr");
  });

  it("keeps the author field usable while older pages load", async () => {
    vi.mocked(historyApi.list).mockResolvedValueOnce(history([3, 2], 3, 2));
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => revision(Number(id.split("-")[1])));
    wrapper = mount(PageHistoryPanel, { props: props() }); await flushPromises();
    const older = deferred<PageHistory>();
    vi.mocked(historyApi.list).mockReturnValueOnce(older.promise);
    await wrapper.get('input[type="search"]').setValue("P");
    expect(wrapper.get('input[type="search"]').attributes("disabled")).toBeUndefined();
    await wrapper.get('input[type="search"]').setValue("Petr");
    older.resolve({ ...history([1]), items: [{ ...summary(1), authorName: "Petr" }] }); await flushPromises();
    expect(wrapper.get(".history-version").text()).toContain("Petr");
  });

  it("resumes an exhaustive filter entered during a manual load of older versions", async () => {
    vi.mocked(historyApi.list).mockResolvedValueOnce(history([5, 4], 5, 4));
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => revision(Number(id.split("-")[1])));
    wrapper = mount(PageHistoryPanel, { props: props() }); await flushPromises();
    const older = deferred<PageHistory>();
    vi.mocked(historyApi.list).mockReturnValueOnce(older.promise).mockResolvedValueOnce({ ...history([1], 5), items: [{ ...summary(1), authorName: "Petr" }] });
    await wrapper.get(".history-load-more").trigger("click");
    await wrapper.get('input[type="search"]').setValue("Petr");
    older.resolve(history([3, 2], 5, 2)); await flushPromises();
    expect(wrapper.findAll(".history-version")).toHaveLength(1);
    expect(wrapper.get(".history-version").text()).toContain("Petr");
    expect(wrapper.find(".history-load-more").exists()).toBe(false);
  });

  it("filters versions with removed lines from their stored summaries without downloading any revision", async () => {
    vi.mocked(historyApi.list).mockResolvedValue({ ...history([3, 2], 3, 2), items: [{ ...summary(3), changes: changes({ removed: 0 }) }, { ...summary(2), changes: changes({ removed: 2 }) }] });
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => revision(Number(id.split("-")[1])));
    wrapper = mount(PageHistoryPanel, { props: props() }); await flushPromises();
    vi.mocked(historyApi.revision).mockClear();
    vi.mocked(historyApi.list).mockResolvedValue({ ...history([1], 3), items: [{ ...summary(1), changes: null }] });
    await wrapper.get('input[type="checkbox"]').setValue(true); await flushPromises();
    expect(wrapper.findAll(".history-version")).toHaveLength(1);
    expect(wrapper.get(".history-version").attributes("data-revision")).toBe("revision-2");
    expect(historyApi.revision).not.toHaveBeenCalled();
  });

  it("compares with previousRevisionId and asks for no other list", async () => {
    vi.mocked(historyApi.list).mockResolvedValue({ ...history([3]), items: [{ ...summary(3), previousRevisionId: "older-x" }] });
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => id === "older-x" ? { ...revision(2), id } : revision(Number(id.split("-")[1])));
    wrapper = mount(PageHistoryPanel, { props: props() }); await flushPromises();
    expect(historyApi.revision).toHaveBeenCalledWith("page", "older-x", expect.anything());
    expect(historyApi.list).toHaveBeenCalledTimes(1);
  });

  it("summarises a version from its stored changes and shows nothing for an old row", async () => {
    vi.mocked(historyApi.list).mockResolvedValue({ ...history(), items: [
      { ...summary(3), label: "Před auditem", checkpoints: 12, isAi: true, channel: "mcp", changes: changes({ added: 12, removed: 3, states: [{ title: "A", from: "planned", to: "done" }, { title: "B", from: "planned", to: "done" }], contactsAdded: ["Jana Nováková"], contributors: [{ id: "1", name: "Jana", checkpoints: 3 }, { id: "2", name: "Petr", checkpoints: 1 }] }) },
      { ...summary(2), changes: null }] });
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => revision(Number(id.split("-")[1])));
    wrapper = mount(PageHistoryPanel, { props: props() }); await flushPromises();
    const [first, old] = wrapper.findAll(".history-version");
    expect(first!.text()).toContain("+12");
    expect(first!.text()).toContain("−3");
    expect(first!.get("[data-history-label]").text()).toBe("Před auditem");
    expect(first!.text()).toContain("12 uložení");
    expect(first!.text()).toContain("2 požadavky → Hotovo");
    expect(first!.text()).toContain("Jana Nováková");
    expect(first!.text()).toContain("75 %");
    expect(first!.get("[data-history-ai]").text()).toBe("AI");
    expect(old!.find(".hv-size").exists()).toBe(false);
    expect(old!.find("[data-history-ai]").exists()).toBe(false);
    expect(wrapper.get("[data-preview-ai]").text()).toContain("Změna s AI");
    expect(wrapper.find(".history-ai-diff").exists()).toBe(true);
  });

  it("filters to published, named and AI versions", async () => {
    vi.mocked(historyApi.list).mockResolvedValue({ ...history([3, 2, 1], 3), items: [{ ...summary(3), label: "Pojmenovaná" }, { ...summary(2), isAi: true }, summary(1)] });
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => revision(Number(id.split("-")[1])));
    wrapper = mount(PageHistoryPanel, { props: props() }); await flushPromises();
    const choose = async (label: string) => { await wrapper!.findAll(".history-kind-switch button").find(b => b.text() === label)!.trigger("click"); await flushPromises(); };
    await choose("Pojmenované"); expect(wrapper.findAll(".history-version").map(b => b.attributes("data-revision"))).toEqual(["revision-3"]);
    await choose("Od AI"); expect(wrapper.findAll(".history-version").map(b => b.attributes("data-revision"))).toEqual(["revision-2"]);
    await choose("Publikované"); expect(wrapper.findAll(".history-version").map(b => b.attributes("data-revision"))).toEqual(["revision-2"]);
    await choose("Vše"); expect(wrapper.findAll(".history-version")).toHaveLength(3);
  });

  it("loads the steps of a version by group on expanding and compares a step with the next older one", async () => {
    vi.mocked(historyApi.list).mockResolvedValue({ ...history([3]), items: [{ ...summary(3), groupId: "g3", checkpoints: 3, previousRevisionId: "base" }] });
    // As the server sends steps: each names the next older step, the oldest the version's base.
    const step = (id: string, previousRevisionId: string) => ({ ...summary(3), id, groupId: "g3", previousRevisionId });
    vi.mocked(historyApi.steps).mockResolvedValue({ pageId: "page", currentVersion: 3, items: [step("s3", "s2"), step("s2", "s1"), step("s1", "base")], nextBeforeVersion: null });
    vi.mocked(historyApi.revision).mockImplementation(async (_, id) => ({ ...revision(3), id, markdown: id }));
    wrapper = mount(PageHistoryPanel, { props: props(), attachTo: document.body }); await flushPromises();
    await wrapper.get(".history-steps-toggle").trigger("click"); await flushPromises();
    expect(historyApi.steps).toHaveBeenCalledWith("page", "g3", null);
    expect(wrapper.get(".history-steps-toggle").attributes("aria-expanded")).toBe("true");
    const steps = wrapper.findAll(".history-step");
    expect(steps).toHaveLength(3);
    vi.mocked(historyApi.revision).mockClear();
    await steps[0]!.trigger("click"); await flushPromises();
    expect(historyApi.revision).toHaveBeenCalledWith("page", "s3", expect.anything());
    expect(historyApi.revision).toHaveBeenCalledWith("page", "s2", expect.anything());
    vi.mocked(historyApi.revision).mockClear();
    await steps[2]!.trigger("click"); await flushPromises();
    expect(historyApi.revision).toHaveBeenCalledWith("page", "base", expect.anything());
  });

  it("shows one title, asks to close on Escape and restores focus to its opener", async () => {
    const opener = document.createElement("button"); opener.textContent = "Historie"; document.body.appendChild(opener); opener.focus();
    await open();
    expect(wrapper!.get(".app-dialog-header h2").text()).toBe("Historie stránky");
    expect(wrapper!.find(".app-dialog-header .eyebrow").exists()).toBe(false);
    // The logical place is the shown version in the list, where the arrows step through the history (keyboard pass).
    expect(document.activeElement).toBe(wrapper!.get('.history-version[aria-current="true"]').element);
    await wrapper!.get("dialog").trigger("cancel");
    expect(wrapper!.emitted("close")).toHaveLength(1);
    wrapper!.unmount(); wrapper = undefined;
    expect(document.activeElement).toBe(opener);
  });
});


it("downloads the explicitly selected historical PDF and keeps its preview on failure", async () => {
  await open();
  await wrapper!.findAll(".history-version")[1]!.trigger("click"); await flushPromises();
  vi.mocked(downloadContentExport).mockRejectedValueOnce(new Error("PDF není dostupné."));
  await wrapper!.get("[data-history-pdf]").trigger("click"); await flushPromises();
  expect(downloadContentExport).toHaveBeenCalledWith("pages", "page", "pdf", expect.objectContaining({ revisionId: "revision-2", coverDetails: ["revision"], name: expect.stringContaining("revize 2") }));
  expect(wrapper!.text()).toContain("PDF není dostupné.");
  expect(wrapper!.get("[data-preview]").text()).toBe("historical-2");
});
