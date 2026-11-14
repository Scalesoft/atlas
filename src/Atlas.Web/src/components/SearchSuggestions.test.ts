import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import SearchSuggestions, { noteVisit } from "./SearchSuggestions.vue";
const list = vi.hoisted(() => vi.fn());
vi.mock("../personalLibrary", () => ({ personalLibraryApi: { list } }));
afterEach(() => { vi.clearAllMocks(); });
describe("empty search suggestions", () => {
  it("loads recent and favorite pages with bounded fresh requests and real links", async () => {
    list.mockImplementation(async kind => ({ items: [{ id: kind, title: kind === "recent" ? "Nedávná stránka" : "Oblíbená stránka", spaceName: "Tým" }] }));
    const app = mount(SearchSuggestions, { props: { userId: "first" } }); await flushPromises();
    expect(list).toHaveBeenCalledWith("recent", 0, expect.any(AbortSignal), 5);
    expect(list).toHaveBeenCalledWith("favorites", 0, expect.any(AbortSignal), 5);
    expect(app.get('a[href$="#/pages/recent"]').text()).toContain("Nedávná stránka");
    await app.get('a[href$="#/pages/recent"]').trigger("click", { ctrlKey: true });
    expect(app.emitted("open")).toBeUndefined();
    await app.get('a[href$="#/pages/recent"]').trigger("click");
    expect(app.emitted("open")).toEqual([["recent"]]); app.unmount();
  });
  it("shows honest empty and retry states without retaining failed data", async () => {
    list.mockRejectedValue(new Error("Síť není dostupná"));
    const app = mount(SearchSuggestions, { props: { userId: "second" } }); await flushPromises();
    expect(app.get('[role="alert"]').text()).toContain("Síť není dostupná");
    list.mockResolvedValue({ items: [] });
    await app.get("button").trigger("click"); await flushPromises();
    expect(app.find('[role="alert"]').exists()).toBe(false);
    expect(app.text()).toContain("Zadejte alespoň dva znaky"); app.unmount();
  });
  it("selects a suggestion only when the pointer really moves over it, not when the list settles beneath a resting pointer", async () => {
    list.mockImplementation(async kind => ({ items: [{ id: kind, title: kind, spaceName: "Tým" }] }));
    const app = mount(SearchSuggestions, { props: { userId: "third" } }); await flushPromises();
    const row = app.get('a[href$="#/pages/recent"]').element;
    const move = (movementX: number, movementY: number) => {
      const event = new Event("pointermove", { bubbles: true });
      Object.defineProperties(event, { movementX: { value: movementX }, movementY: { value: movementY } });
      row.dispatchEvent(event);
    };
    move(0, 0);
    expect(app.emitted("select")).toBeUndefined();
    move(3, 0);
    expect(app.emitted("select")).toEqual([[0]]); app.unmount();
  });
  it("draws the pages of the last opening in the first frame, refines them when the answer comes and keeps them when the refresh fails (P17)", async () => {
    list.mockImplementation(async kind => ({ items: [{ id: kind, title: kind === "recent" ? "Nedávná stránka" : "Oblíbená stránka", spaceName: "Tým" }] }));
    const first = mount(SearchSuggestions, { props: { userId: "cached" } });
    await flushPromises();
    first.unmount();
    // The next opening: no skeleton, no request needed to show the rows; the parent hears of them at once.
    list.mockReturnValue(new Promise(() => {}));
    const second = mount(SearchSuggestions, { props: { userId: "cached" } });
    expect(second.findAll("a.search-result").map(row => row.text())).toEqual([expect.stringContaining("Nedávná stránka"), expect.stringContaining("Oblíbená stránka")]);
    expect(second.find(".loading-skeleton").exists()).toBe(false);
    expect(second.emitted("loaded")).toHaveLength(1);
    second.unmount();
    list.mockRejectedValue(new Error("Síť není dostupná"));
    const third = mount(SearchSuggestions, { props: { userId: "cached" } }); await flushPromises();
    expect(third.find('[role="alert"]').exists()).toBe(false);
    expect(third.findAll("a.search-result")).toHaveLength(2);
    third.unmount();
    // Another account starts from a skeleton, never from this one's pages.
    list.mockReturnValue(new Promise(() => {}));
    const other = mount(SearchSuggestions, { props: { userId: "someone-else" } });
    expect(other.findAll("a.search-result")).toHaveLength(0);
    expect(other.find(".loading-skeleton").exists()).toBe(true);
    other.unmount();
  });
  it("preselects the first row while the arrows have not chosen (activeIndex -1), so Enter opens it (F13)", async () => {
    list.mockImplementation(async kind => ({ items: [{ id: kind, title: kind, spaceName: "Tým" }] }));
    const app = mount(SearchSuggestions, { props: { userId: "idle", activeIndex: -1 } }); await flushPromises();
    expect(app.findAll(".search-result.active").map(row => row.attributes("id"))).toEqual(["suggested-result-0"]);
    expect(app.get("#suggested-result-0").attributes("aria-selected")).toBe("true");
    await app.setProps({ activeIndex: 1 });
    expect(app.findAll(".search-result.active").map(row => row.attributes("id"))).toEqual(["suggested-result-1"]);
    app.unmount();
  });
  it("draws the rows of the last opening without the rise-in, so clearing the search replays nothing (F33)", async () => {
    list.mockImplementation(async kind => ({ items: [{ id: kind, title: kind, spaceName: "Tým" }] }));
    const first = mount(SearchSuggestions, { props: { userId: "replay" } }); await flushPromises();
    expect(first.find(".search-group.atlas-stagger").exists()).toBe(true);
    first.unmount();
    const again = mount(SearchSuggestions, { props: { userId: "replay" } }); await flushPromises();
    expect(again.find(".search-group").exists()).toBe(true);
    expect(again.find(".search-group.atlas-stagger").exists()).toBe(false);
    again.unmount();
  });
  const page = (id: string) => ({ id, title: id, spaceName: "Tým" });
  it("keeps a roving tab stop on the first row while the arrows have not chosen, so the list is reachable by Tab", async () => {
    list.mockImplementation(async kind => ({ items: kind === "recent" ? [page("a"), page("b")] : [] }));
    const app = mount(SearchSuggestions, { props: { userId: "roving", activeIndex: -1 } }); await flushPromises();
    expect(app.findAll("a.search-result").map(row => row.attributes("tabindex"))).toEqual(["0", "-1"]);
    await app.setProps({ activeIndex: 1 });
    expect(app.findAll("a.search-result").map(row => row.attributes("tabindex"))).toEqual(["-1", "0"]);
    app.unmount();
  });
  it("keeps the chosen page chosen when the refresh puts another above it, and a page just opened is already first in the cache (P17)", async () => {
    list.mockImplementation(async kind => ({ items: kind === "recent" ? [page("a"), page("b")] : [] }));
    const earlier = mount(SearchSuggestions, { props: { userId: "moving" } });
    await flushPromises();
    earlier.unmount();
    // The page b was opened: the cache lists it first before the next opening asks.
    noteVisit("moving", "b");
    list.mockImplementation(async kind => ({ items: kind === "recent" ? [page("new"), page("b"), page("a")] : [] }));
    const app = mount(SearchSuggestions, { props: { userId: "moving", activeIndex: 0 } });
    expect(app.findAll("a.search-result").map(row => row.text())).toEqual([expect.stringContaining("b"), expect.stringContaining("a")]);
    await flushPromises();
    // The keyboard was on b (index 0); the refresh listed a new page above it: the choice follows b.
    expect(app.emitted("select")).toEqual([[1]]);
    app.unmount();
  });
  it("aborts pending suggestions when the search is replaced or closed", async () => {
    list.mockReturnValue(new Promise(() => {}));
    const app = mount(SearchSuggestions, { props: { userId: "fourth" } });
    const signal = list.mock.calls[0]![2] as AbortSignal;
    app.unmount(); expect(signal.aborted).toBe(true);
  });
});
