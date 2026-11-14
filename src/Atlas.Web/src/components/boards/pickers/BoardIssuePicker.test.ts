import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { DOMWrapper, mount, type VueWrapper } from "@vue/test-utils";
import { nextTick } from "vue";
import BoardIssuePicker from "./BoardIssuePicker.vue";

let wrapper: VueWrapper | undefined;
/** The picker's panel is portalled to the body. */
const body = () => new DOMWrapper(document.body);
beforeEach(() => vi.useFakeTimers());
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.useRealTimers(); });

const issue = (key: string, title: string, state?: string) => ({ id: `id-${key}`, key, title, url: `https://yt.example/issue/${key}`, ...(state ? { state } : {}) });
const catalog = [issue("ATL-1", "Oprava přihlášení", "Open"), issue("ATL-2", "Nový export"), issue("OPS-7", "Záloha", "Done")];

async function show(fetchIssues = vi.fn(async (query: string) => catalog.filter(item => `${item.key} ${item.title}`.toLowerCase().includes(query.toLowerCase())))) {
  wrapper = mount(BoardIssuePicker, { attachTo: document.body, props: { open: true, spaceId: "s1", spaceName: "Provoz", fetchIssues } });
  await nextTick();
  return fetchIssues;
}
/** Types into the search field and lets the debounce and the answer through. */
async function search(text: string) {
  await body().get("input[type=search]").setValue(text);
  await vi.advanceTimersByTimeAsync(300);
  await nextTick();
}
const box = (key: string) => body().get(`[data-pick="${key}"]`);
const rows = () => body().findAll(".pick-item").map(row => row.text());

it("asks the integration for the space of the board, after the typing has paused, and lists what it answers", async () => {
  const fetchIssues = await show();
  expect(body().text()).toContain("Napište ID úkolu nebo slovo z jeho názvu.");
  await body().get("input[type=search]").setValue("ATL");
  await vi.advanceTimersByTimeAsync(200);
  expect(fetchIssues).not.toHaveBeenCalled();
  await vi.advanceTimersByTimeAsync(100);
  expect(fetchIssues).toHaveBeenCalledWith("ATL", "s1", expect.any(AbortSignal));
  expect(rows()).toEqual([expect.stringContaining("Oprava přihlášení"), expect.stringContaining("Nový export")]);
  expect(rows()[0]).toContain("ATL-1 · Open");
});

it("does not search for a single character", async () => {
  const fetchIssues = await show();
  await search("A");
  expect(fetchIssues).not.toHaveBeenCalled();
});

it("says so when nothing matches", async () => {
  await show();
  await search("zzz");
  expect(body().text()).toContain("Žádný úkol neodpovídá.");
});

it("emits one youtrackIssue node of the editor per picked issue, in the order picked, with the snapshot the search returned", async () => {
  await show();
  await search("a");
  await search("ATL");
  await box("ATL-2").setValue(true);
  await box("ATL-1").setValue(true);
  const confirm = body().get("[data-board-issue-confirm]");
  expect(confirm.text()).toBe("Přidat 2 úkoly");
  await confirm.trigger("click");
  expect(wrapper!.emitted("pick")).toEqual([[[
    { type: "youtrackIssue", attrs: { id: "id-ATL-2", key: "ATL-2", title: "Nový export", url: "https://yt.example/issue/ATL-2", state: null } },
    { type: "youtrackIssue", attrs: { id: "id-ATL-1", key: "ATL-1", title: "Oprava přihlášení", url: "https://yt.example/issue/ATL-1", state: "Open" } },
  ]]]);
});

it("keeps a pick when the next search shows other issues", async () => {
  await show();
  await search("ATL");
  await box("ATL-1").setValue(true);
  await search("OPS");
  expect(rows()).toEqual([expect.stringContaining("Záloha")]);
  await box("OPS-7").setValue(true);
  expect(body().get("[data-board-issue-confirm]").text()).toBe("Přidat 2 úkoly");
  await body().get("[data-board-issue-confirm]").trigger("click");
  const nodes = (wrapper!.emitted("pick")![0]![0] as { attrs: { key: string } }[]).map(node => node.attrs.key);
  expect(nodes).toEqual(["ATL-1", "OPS-7"]);
});

it("offers nothing to confirm until something is picked", async () => {
  await show();
  expect(body().get("[data-board-issue-confirm]").attributes("disabled")).toBeDefined();
  expect(body().get("[data-board-issue-confirm]").text()).toBe("Přidat");
});

it("says that YouTrack is not available, with a retry, instead of an empty list", async () => {
  const fetchIssues = vi.fn<(query: string) => Promise<typeof catalog>>(async () => { throw new Error("503"); });
  await show(fetchIssues);
  await search("ATL");
  expect(body().get("[role=alert]").text()).toContain("YouTrack není dostupný");
  expect(body().find(".pick-items").exists()).toBe(false);
  fetchIssues.mockImplementation(async () => [catalog[0]!]);
  await body().get("[role=alert] button").trigger("click");
  await vi.advanceTimersByTimeAsync(300);
  await nextTick();
  expect(body().find("[role=alert]").exists()).toBe(false);
  expect(rows()).toHaveLength(1);
});

it("says it is searching, but only after the defer", async () => {
  let answer!: (issues: typeof catalog) => void;
  await show(vi.fn(() => new Promise<typeof catalog>(resolve => { answer = resolve; })));
  await body().get("input[type=search]").setValue("ATL");
  await vi.advanceTimersByTimeAsync(260);
  const note = () => body().get(".loading-note");
  expect(note().attributes("data-reserved")).toBe("active");
  answer(catalog);
  await vi.advanceTimersByTimeAsync(0);
  await nextTick();
  expect(note().attributes("data-reserved")).toBe("idle");
});

it("asks to cancel, and starts afresh when it is opened again", async () => {
  await show();
  await search("ATL");
  await box("ATL-1").setValue(true);
  await body().findAll("button").find(button => button.text() === "Zrušit")!.trigger("click");
  expect(wrapper!.emitted("cancel")).toHaveLength(1);
  await wrapper!.setProps({ open: false });
  await wrapper!.setProps({ open: true });
  expect((body().get("input[type=search]").element as HTMLInputElement).value).toBe("");
  // What was added before may be listed (the recent rows), but nothing of this session stays ticked.
  expect(body().findAll(".pick-item input:checked")).toHaveLength(0);
  expect(body().find("[data-pick-count]").exists()).toBe(false);
});

it("opens with the caret in the search field, so typing searches at once", async () => {
  wrapper = mount(BoardIssuePicker, { attachTo: document.body, props: { open: false, spaceId: "s1", fetchIssues: vi.fn(async () => []) } });
  await wrapper.setProps({ open: true });
  await vi.advanceTimersByTimeAsync(50);
  expect(document.activeElement).toBe(body().get("input[type=search]").element);
});

it("ticks the first issue on Enter, and an Enter given while the typed text is still being searched waits for that answer, never taking one of the previous search", async () => {
  await show();
  await search("ATL");
  const field = body().get("input[type=search]");
  await field.setValue("záloha");
  await field.trigger("keydown", { key: "Enter" });
  expect(body().find("[data-pick-count]").exists()).toBe(false);
  await vi.advanceTimersByTimeAsync(300);
  await nextTick();
  expect(body().findAll(".pick-tag").map(tag => tag.text())).toEqual(["Záloha"]);
});

it("opens with the issues last added in the board's space, newest first, and adds one of them without a search (P17)", async () => {
  const fetchIssues = await show();
  await search("OPS");
  await box("OPS-7").setValue(true);
  await body().get("[data-board-issue-confirm]").trigger("click");
  await wrapper!.setProps({ open: false });
  await wrapper!.setProps({ open: true });
  expect(rows()[0]).toContain("Záloha");
  fetchIssues.mockClear();
  await box("OPS-7").setValue(true);
  await body().get("[data-board-issue-confirm]").trigger("click");
  expect(fetchIssues).not.toHaveBeenCalled();
  expect((wrapper!.emitted("pick")![1]![0] as { attrs: { key: string } }[]).map(node => node.attrs.key)).toEqual(["OPS-7"]);
  await wrapper!.setProps({ open: false, spaceId: "other" });
  await wrapper!.setProps({ open: true });
  expect(rows()).toEqual([]);
});
