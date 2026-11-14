import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import DiscardChangesDialog from "./DiscardChangesDialog.vue";
import HistoryRenderedChanges from "../HistoryRenderedChanges.vue";
import { api, type Page } from "../../api";
import { historyApi, type PageRevision } from "../../history";
import { compareHistoryBlocks } from "../../historyBlocks";

vi.mock("../../api", async original => ({ ...await original<typeof import("../../api")>(), api: vi.fn() }));
vi.mock("../../history", () => ({ historyApi: { revision: vi.fn(), restore: vi.fn() } }));
vi.mock("../HistoryChanges.vue", () => ({ __esModule: true, default: { props: ["revision", "previous"], template: '<div class="changes-test">{{ previous.markdown }} -> {{ revision.markdown }}</div>' } }));
vi.mock("@atlas/markdown/vue", () => ({ MarkdownRenderer: { props: ["value"], template: '<div class="md-test" />' } }));
vi.mock("../../documentWidgets", () => ({ atlasContentPlugins: () => [] }));

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.resetAllMocks(); vi.unstubAllGlobals(); document.body.innerHTML = ""; });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; }
const revision = (id: string, markdown: string) => ({ id, pageId: "page", version: 2, title: "A", markdown, contentJson: "{}" }) as PageRevision;
const action = () => document.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-action]");

describe("DiscardChangesDialog", () => {
  it("asks at once, before the save or any answer of the server, and confirming then discards the same thing", async () => {
    const saved = deferred<void>(), published = deferred<PageRevision>();
    const order: string[] = [];
    vi.mocked(historyApi.revision).mockImplementation((_, id) => id === "rev-1" ? published.promise : Promise.resolve(revision("rev-2", "typo")));
    vi.mocked(api).mockResolvedValue({ pageId: "page", currentVersion: 2, items: [{ id: "rev-2" }], nextBeforeVersion: null });
    vi.mocked(historyApi.restore).mockImplementation(async () => { order.push("restore"); return { id: "page", version: 3 } as Page; });
    wrapper = mount(DiscardChangesDialog, { attachTo: document.body, props: {
      open: true, title: "Zahodit nepublikované změny?", pageId: "page", publishedRevisionId: "rev-1", version: 2, dark: false, accent: "blue",
      prepare: () => saved.promise, beforeRestore: async () => { order.push("flush"); return true; },
    } });
    await flushPromises();
    // Nothing has answered yet: the question, both buttons and the sized placeholder of the comparison are there.
    expect(document.querySelector("dialog[open]")!.textContent).toContain("Zahodit nepublikované změny?");
    expect(action()!.disabled).toBe(false);
    expect(document.querySelector("dialog[open] [data-confirm-cancel]")).not.toBeNull();
    expect(document.querySelector(".discard-changes .loading-skeleton")).not.toBeNull();
    // The published side was asked for without waiting for the save; the draft's is not known before it.
    expect(vi.mocked(historyApi.revision).mock.calls.map(call => call[1])).toEqual(["rev-1"]);
    expect(api).not.toHaveBeenCalled();

    saved.resolve(); published.resolve(revision("rev-1", "published")); await flushPromises();
    expect(document.querySelector(".changes-test")!.textContent).toBe("published -> typo");

    action()!.click(); await flushPromises();
    // The restore waits for the host's flush of the last keystrokes.
    expect(order).toEqual(["flush", "restore"]);
    expect(historyApi.restore).toHaveBeenCalledWith("page", "rev-1", 2);
    expect(wrapper.emitted("restored")![0]![1]).toContain("Změny byly zahozeny");
  });

  it("confirms while the comparison is still on its way", async () => {
    vi.mocked(historyApi.revision).mockReturnValue(new Promise(() => {}));
    vi.mocked(historyApi.restore).mockResolvedValue({ id: "page", version: 3 } as Page);
    wrapper = mount(DiscardChangesDialog, { attachTo: document.body, props: {
      open: true, title: "Zahodit nepublikované změny?", pageId: "page", publishedRevisionId: "rev-1", version: 2, dark: false, accent: "blue",
      prepare: () => new Promise(() => {}), beforeRestore: async () => true,
    } });
    await flushPromises();
    action()!.click(); await flushPromises();
    expect(historyApi.restore).toHaveBeenCalledWith("page", "rev-1", 2);
    expect(wrapper.emitted("restored")).toHaveLength(1);
  });
});

describe("HistoryRenderedChanges", () => {
  it("fills the changes in from the top, a few a frame, without moving what is shown", async () => {
    const frames: FrameRequestCallback[] = [];
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => frames.push(callback));
    vi.stubGlobal("cancelAnimationFrame", () => undefined);
    const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
    const stored = (...content: object[]) => JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content } });
    // Ten edited paragraphs, each between unchanged ones: ten changes with their context rows.
    const blocks = (edit: string) => Array.from({ length: 10 }, (_, at) => [paragraph(`Odstavec ${at} zůstává stejný`), paragraph(`Postup číslo ${at} pro obsluhu${edit}`)]).flat();
    const diff = compareHistoryBlocks(stored(...blocks("")), stored(...blocks(" serveru")));
    expect(diff.changes).toBe(10);
    wrapper = mount(HistoryRenderedChanges, { attachTo: document.body, props: { diff, first: false, dark: false, accent: "blue" } });
    await flushPromises();
    const changes = () => wrapper!.findAll("[data-change]").length;
    const first = wrapper.find("[data-change]").element;
    expect(changes()).toBeGreaterThan(0);
    expect(changes()).toBeLessThan(10);
    expect(wrapper.find("[data-filling]").text()).toContain("Načítám další změny");
    let before = changes();
    for (let turn = 0; turn < 20 && wrapper.find("[data-filling]").exists(); turn++) {
      frames.shift()!(0); await flushPromises();
      expect(changes()).toBeGreaterThanOrEqual(before);
      before = changes();
    }
    expect(changes()).toBe(10);
    expect(wrapper.find("[data-filling]").exists()).toBe(false);
    expect(wrapper.find("[data-change]").element).toBe(first);
  });
});
