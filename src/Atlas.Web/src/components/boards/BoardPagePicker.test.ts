import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { DOMWrapper, mount, type VueWrapper } from "@vue/test-utils";
import { nextTick } from "vue";

const search = vi.fn(async (text: string) => [
  { id: "p1", title: `Provozní řád ${text}`, detail: "Provoz" },
  { id: "p2", title: "Onboarding" },
]);
vi.mock("../../editorDirectories", () => ({ createPageSource: () => ({ search }) }));
const { default: BoardPagePicker } = await import("./BoardPagePicker.vue");

let wrapper: VueWrapper | undefined;
beforeEach(() => vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] }));
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.useRealTimers(); search.mockClear(); });
/** The picker's panel is portalled to the body. */
const body = () => new DOMWrapper(document.body);
async function show() {
  wrapper = mount(BoardPagePicker, { attachTo: document.body, props: { open: true, spaceId: "s1" } });
  await nextTick();
  await nextTick();
}
const field = () => body().get("[data-board-page-query]");

it("opens in the light panel with the caret in the search and a hint instead of an empty list", async () => {
  await show();
  expect(body().find("[data-board-picker]").exists()).toBe(true);
  expect(document.activeElement).toBe(field().element);
  expect(body().text()).toContain("Napište část názvu stránky.");
});

it("lists the editor's page search as the [[ list does, and Enter adds the first page", async () => {
  await show();
  await field().setValue("řád");
  await vi.advanceTimersByTimeAsync(200);
  expect(search).toHaveBeenCalledWith("řád", expect.anything());
  expect(body().findAll("[role=option]").map(row => row.text())).toEqual(["Provozní řád řádProvoz", "Onboarding"]);
  await field().trigger("keydown", { key: "Enter" });
  expect(wrapper!.emitted("pick")).toEqual([[{ id: "p1", title: "Provozní řád řád" }]]);
});

it("adds the answer's first page for an Enter pressed before the answer came", async () => {
  await show();
  await field().setValue("vpn");
  await field().trigger("keydown", { key: "Enter" });
  expect(wrapper!.emitted("pick")).toBeUndefined();
  await vi.advanceTimersByTimeAsync(200);
  expect(wrapper!.emitted("pick")).toEqual([[{ id: "p1", title: "Provozní řád vpn" }]]);
});

it("asks to cancel on Esc", async () => {
  await show();
  await field().trigger("keydown", { key: "Escape" });
  expect(wrapper!.emitted("cancel")).toHaveLength(1);
});

it("the first Esc empties the search and the panel stays; Tab adds the first page and keeps the keyboard where it is; an empty field adds nothing (P3, P8)", async () => {
  await show();
  await field().trigger("keydown", { key: "Enter" });
  expect(wrapper!.emitted("pick")).toBeUndefined();
  await field().setValue("řád");
  await vi.advanceTimersByTimeAsync(200);
  const escape = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
  field().element.dispatchEvent(escape);
  await nextTick();
  expect((field().element as HTMLInputElement).value).toBe("");
  expect(escape.defaultPrevented).toBe(true);
  await field().setValue("řád");
  await vi.advanceTimersByTimeAsync(200);
  const tab = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true });
  field().element.dispatchEvent(tab);
  expect(wrapper!.emitted("pick")).toEqual([[{ id: "p1", title: "Provozní řád řád" }]]);
  expect(tab.defaultPrevented).toBe(true);
});

it("lists the space's pages from its lexicon at once and filters them while the search is asked (P17)", async () => {
  const { refreshLexicon, setLexiconAccount } = await import("../../linking/lexicon");
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ version: "v1", truncated: false, people: [], youTrackProjects: [],
    pages: [{ id: "k1", title: "Plán vydání" }, { id: "k2", title: "Provozní příručka" }] }), { headers: { "Content-Type": "application/json" } })));
  try {
    await refreshLexicon("s1");
    await show();
    const rows = () => body().findAll("[data-testid=board-page-list] [role=option]").map(row => row.text());
    expect(rows()).toEqual([expect.stringContaining("Plán vydání"), expect.stringContaining("Provozní příručka")]);
    await field().setValue("prov");
    expect(rows()).toEqual([expect.stringContaining("Provozní příručka")]);
    expect(search).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(200);
    expect(search).toHaveBeenCalledWith("prov", expect.anything());
  } finally { setLexiconAccount("reset"); setLexiconAccount(null); vi.unstubAllGlobals(); }
});
