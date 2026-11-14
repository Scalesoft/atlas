import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PersonalLibraryPanel from "./PersonalLibraryPanel.vue";
import { ApiError } from "../api";
import { personalLibraryApi, type LibraryItem, type LibraryList } from "../personalLibrary";

vi.mock("../personalLibrary", () => ({ personalLibraryApi: { list: vi.fn(), state: vi.fn(), favorite: vi.fn(), visit: vi.fn() } }));
let wrapper: VueWrapper | undefined;
const item = (id: string): LibraryItem => ({
  id, spaceId: "space", spaceName: "Vývoj", title: `Stránka ${id}`, parentId: null, version: 3,
  isPublished: true, isFavorite: true, visitedAt: "2026-09-06T12:00:00Z", publishedAt: "2026-09-06T10:00:00Z",
});
const list = (ids: string[], nextOffset: number | null = null): LibraryList => ({ items: ids.map(item), hasMore: nextOffset !== null, nextOffset });
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.resetAllMocks(); });

describe("personal library", () => {
  it("shows the publication channel on the updates tab only", async () => {
    const channelItem = { ...item("a"), publicationChannel: "mcp", publicationApiToken: { id: "t", name: "Claude Code – notebook", revoked: false } };
    vi.mocked(personalLibraryApi.list).mockResolvedValue({ items: [channelItem, { ...item("b"), publicationChannel: "web" }], hasMore: false, nextOffset: null });
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "updates" } }); await flushPromises();
    expect(wrapper.findAll("[data-channel-badge]")).toHaveLength(1);
    expect(wrapper.get("[data-channel-badge]").text()).toContain("token ‚Claude Code – notebook‘");
    wrapper.unmount();
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "recent" } }); await flushPromises();
    expect(wrapper.find("[data-channel-badge]").exists()).toBe(false);
  });

  it("shows the publication channel on the updates tab only", async () => {
    const channelItem = { ...item("a"), publicationChannel: "mcp", publicationApiToken: { id: "t", name: "Claude Code – notebook", revoked: false } };
    vi.mocked(personalLibraryApi.list).mockResolvedValue({ items: [channelItem, { ...item("b"), publicationChannel: "web" }], hasMore: false, nextOffset: null });
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "updates" } }); await flushPromises();
    expect(wrapper.findAll("[data-channel-badge]")).toHaveLength(1);
    expect(wrapper.get("[data-channel-badge]").text()).toContain("token ‚Claude Code – notebook‘");
    wrapper.unmount();
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "recent" } }); await flushPromises();
    expect(wrapper.find("[data-channel-badge]").exists()).toBe(false);
  });

  it("loads pagination without duplicates and selecting a page never records a visit before navigation", async () => {
    vi.mocked(personalLibraryApi.list).mockResolvedValueOnce(list(["a", "b"], 2)).mockResolvedValueOnce(list(["b", "c"]));
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "recent" } }); await flushPromises();
    await wrapper.get(".library-more").trigger("click"); await flushPromises();
    expect(personalLibraryApi.list).toHaveBeenLastCalledWith("recent", 2, expect.any(AbortSignal), 20);
    expect(wrapper.findAll(".library-item")).toHaveLength(3);
    await wrapper.findAll(".library-item")[1]!.trigger("click");
    expect(wrapper.emitted("select")).toEqual([["b", "page"]]);
    expect(personalLibraryApi.visit).not.toHaveBeenCalled(); expect(personalLibraryApi.favorite).not.toHaveBeenCalled();
    expect(wrapper.find(".library-more").exists()).toBe(false);
  });

  it("cancels stale tab responses and refreshKey reloads current data", async () => {
    let resolve!: (value: LibraryList) => void;
    vi.mocked(personalLibraryApi.list).mockReturnValueOnce(new Promise(done => { resolve = done; })).mockResolvedValue(list(["favorite"]));
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "recent" } });
    await flushPromises();
    const signal = vi.mocked(personalLibraryApi.list).mock.calls[0]![2]!;
    await wrapper.setProps({ tab: "favorites" }); await flushPromises();
    resolve(list(["stale-recent"])); await flushPromises();
    expect(signal.aborted).toBe(true); expect(wrapper.text()).not.toContain("stale-recent");
    expect(wrapper.text()).toContain("Stránka favorite");
    await wrapper.setProps({ refreshKey: 1 }); await flushPromises();
    expect(personalLibraryApi.list).toHaveBeenCalledTimes(3);
  });

  it("clears displayed data if permission expires while loading more", async () => {
    vi.mocked(personalLibraryApi.list).mockResolvedValueOnce(list(["secret"], 1)).mockRejectedValueOnce(new ApiError(401, "Přihlášení vypršelo."));
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "favorites" } }); await flushPromises();
    await wrapper.get(".library-more").trigger("click"); await flushPromises();
    expect(wrapper.find(".library-items").exists()).toBe(false);
    expect(wrapper.get('[role="alert"]').text()).toContain("Přihlášení vypršelo");
  });

  it("keeps an empty panel in place after loading with useful guidance", async () => {
    let resolve!: (value: LibraryList) => void;
    vi.mocked(personalLibraryApi.list).mockReturnValueOnce(new Promise(done => { resolve = done; }));
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "drafts" } });
    await wrapper.vm.$nextTick();
    expect(wrapper.get('[role="status"]').text()).toContain("Načítání");
    resolve(list([])); await flushPromises();
    expect(wrapper.find(".personal-library").exists()).toBe(true);
    expect(wrapper.text()).toContain("Nemáte žádné dostupné rozpracované koncepty");

    wrapper.unmount();
    vi.mocked(personalLibraryApi.list).mockResolvedValueOnce(list([]));
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "drafts" } }); await flushPromises();
    expect(wrapper.text()).toContain("Nemáte žádné dostupné rozpracované koncepty");
    expect(wrapper.find(".library-item").exists()).toBe(false);
  });

  it("says an empty part with the shared empty state, and gives the #empty slot the whole sentence", async () => {
    vi.mocked(personalLibraryApi.list).mockResolvedValue(list([]));
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "favorites", tabbed: true } }); await flushPromises();
    // The same empty state as Prostory and a space without pages: a tile, a headline without a full stop, the hint beneath.
    expect(wrapper.get(".st-empty strong").text()).toBe("Zatím nemáte žádné oblíbené stránky");
    expect(wrapper.get(".st-empty p").text()).toBe("Otevřete článek a označte ho hvězdičkou.");
    await wrapper.setProps({ tab: "drafts" }); await flushPromises();
    expect(wrapper.get(".st-empty strong").text()).toBe("Nemáte žádné dostupné rozpracované koncepty");
    expect(wrapper.find(".st-empty p").exists()).toBe(false);

    wrapper.unmount();
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "favorites", embedded: true }, slots: { empty: `<template #empty="{ text }"><i class="slot-text">{{ text }}</i></template>` } }); await flushPromises();
    expect(wrapper.get(".slot-text").text()).toBe("Zatím nemáte žádné oblíbené stránky. Otevřete článek a označte ho hvězdičkou.");
    expect(wrapper.find(".st-empty").exists()).toBe(false);
  });

  it("says a failed part with the shared error state and retries from it", async () => {
    vi.mocked(personalLibraryApi.list).mockRejectedValueOnce(new ApiError(500, "Seznam se nepodařilo načíst.")).mockResolvedValueOnce(list(["a"]));
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "favorites", tabbed: true } }); await flushPromises();
    // The error state of Profil and of a space: what failed, the reason, one retry (not a bare notice and a second button below).
    expect(wrapper.get('.error-state[role="alert"] strong').text()).toBe("Oblíbené se nepodařilo načíst");
    expect(wrapper.get(".error-state p").text()).toBe("Seznam se nepodařilo načíst.");
    expect(wrapper.findAll("button").filter(button => button.text().includes("znovu"))).toHaveLength(1);
    await wrapper.get(".error-state button").trigger("click"); await flushPromises();
    expect(wrapper.find(".error-state").exists()).toBe(false);
    expect(wrapper.get(".library-item").text()).toContain("Stránka a");
  });

  // Behaviour of 2.6.0, which the shared error state of a part that failed as a whole must not change: a failed next page
  // leaves the rows where they are, says so at the foot, and trying again asks for the same next page.
  it("keeps the rows when the next page fails, says it at the foot and asks for the same page again", async () => {
    vi.mocked(personalLibraryApi.list)
      .mockResolvedValueOnce(list(["a", "b"], 2))
      .mockRejectedValueOnce(new ApiError(500, "Další stránky se nepodařilo načíst."))
      .mockResolvedValueOnce(list(["c"], null));
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "recent", tabbed: true } }); await flushPromises();
    await wrapper.get(".library-more").trigger("click"); await flushPromises();
    // The rows stay; no second big error state above them; the failure is a line in the footer, the retry beside it.
    expect(wrapper.findAll(".library-item")).toHaveLength(2);
    expect(wrapper.find(".error-state").exists()).toBe(false);
    expect(wrapper.get(".library-footer [role=alert]").text()).toContain("Další stránky se nepodařilo načíst.");
    expect(wrapper.get(".library-footer .help").text()).toBe("Načtené stránky: 2.");
    expect(wrapper.get(".library-more").text()).toContain("Načíst další stránky");
    expect(vi.mocked(personalLibraryApi.list).mock.calls[1]![1]).toBe(2);

    await wrapper.get(".library-more").trigger("click"); await flushPromises();
    // The same next page was asked for, not the first.
    expect(vi.mocked(personalLibraryApi.list).mock.calls[2]![1]).toBe(2);
    expect(wrapper.findAll(".library-item").map(row => row.text())).toEqual([expect.stringContaining("Stránka a"), expect.stringContaining("Stránka b"), expect.stringContaining("Stránka c")]);
    expect(wrapper.find(".library-footer [role=alert]").exists()).toBe(false);
    expect(wrapper.get(".library-footer .help").text()).toBe("Načtené stránky: 3.");
    expect(wrapper.find(".library-more").exists()).toBe(false);
  });

  it("leaves a custom #error slot as it was for a failed next page, and adds no second line", async () => {
    vi.mocked(personalLibraryApi.list).mockResolvedValueOnce(list(["a"], 1)).mockRejectedValueOnce(new ApiError(500, "Selhalo."));
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "recent", embedded: true }, slots: { error: `<template #error="{ message }"><b class="custom-error">{{ message }}</b></template>` } }); await flushPromises();
    await wrapper.get(".library-more").trigger("click"); await flushPromises();
    expect(wrapper.get(".custom-error").text()).toBe("Selhalo.");
    expect(wrapper.find(".library-footer [role=alert]").exists()).toBe(false);
    expect(wrapper.findAll(".library-item")).toHaveLength(1);
    expect(wrapper.find(".library-more").exists()).toBe(true);
  });

  it("keeps current pages visible during refresh but clears them when changing tabs", async () => {
    let resolveRefresh!: (value: LibraryList) => void;
    vi.mocked(personalLibraryApi.list).mockResolvedValueOnce(list(["existing"]))
      .mockReturnValueOnce(new Promise(done => { resolveRefresh = done; }));
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "recent" } });
    await flushPromises();
    await wrapper.get(".library-refresh").trigger("click");
    expect(wrapper.get(".personal-library").attributes("aria-busy")).toBe("true");
    expect(wrapper.get(".library-item").text()).toContain("Stránka existing");
    resolveRefresh(list(["updated"])); await flushPromises();
    expect(wrapper.get(".library-item").text()).toContain("Stránka updated");

    let resolveTab!: (value: LibraryList) => void;
    vi.mocked(personalLibraryApi.list).mockReturnValueOnce(new Promise(done => { resolveTab = done; }));
    await wrapper.setProps({ tab: "favorites" });
    expect(wrapper.find(".library-item").exists()).toBe(false);
    resolveTab(list(["favorite"])); await flushPromises();
    expect(wrapper.get(".library-item").text()).toContain("Stránka favorite");
  });

  it("renders real page links and delegates only plain primary-button navigation", async () => {
    vi.mocked(personalLibraryApi.list).mockResolvedValue(list(["linked"]));
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "recent" } }); await flushPromises();
    const link = wrapper.get(".library-item");
    expect(link.element.tagName).toBe("A");
    expect(link.attributes("href")).toContain("#/pages/linked");

    await link.trigger("click", { metaKey: true });
    await link.trigger("click", { button: 1 });
    expect(wrapper.emitted("select")).toBeUndefined();

    const plainClick = new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 });
    expect(link.element.dispatchEvent(plainClick)).toBe(false);
    expect(wrapper.emitted("select")).toEqual([["linked", "page"]]);
  });

  it("lists a board like a page, with the board icon and its own address, and opens it as a board", async () => {
    const board: LibraryItem = { ...item("plan"), type: "board", title: "Plán vydání", version: 0 };
    vi.mocked(personalLibraryApi.list).mockResolvedValue({ items: [board, item("a")], hasMore: false, nextOffset: null });
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "favorites" } }); await flushPromises();
    const [boardRow, pageRow] = wrapper.findAll(".library-item");
    expect(boardRow!.attributes("href")).toContain("#/boards/plan");
    expect(boardRow!.attributes("aria-label")).toBe("Otevřít tabuli Plán vydání");
    expect(boardRow!.find('[data-icon="layout-dashboard"], [name="layout-dashboard"]').exists() || boardRow!.html().includes("layout-dashboard")).toBe(true);
    // A board has no publication: no status chip beside it.
    expect(boardRow!.find("[data-status]").exists()).toBe(false);
    expect(pageRow!.attributes("href")).toContain("#/pages/a");
    boardRow!.element.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
    expect(wrapper.emitted("select")).toEqual([["plan", "board"]]);
  });

  it("shows the change summary only on the latest publications tab", async () => {
    const text = "Opraveny odkazy v kapitole o zálohách";
    vi.mocked(personalLibraryApi.list).mockResolvedValue({ items: [{ ...item("a"), changeSummary: text }, { ...item("b"), changeSummary: null }], hasMore: false, nextOffset: null });
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "updates" } }); await flushPromises();
    const rows = wrapper.findAll(".library-item");
    expect(rows[0]!.get(".library-change-summary").text()).toBe(text);
    expect(rows[0]!.get(".library-change-summary").attributes("title")).toBe(text);
    expect(rows[1]!.find(".library-change-summary").exists()).toBe(false);
    await wrapper.setProps({ tab: "recent" }); await flushPromises();
    expect(wrapper.find(".library-change-summary").exists()).toBe(false);
  });
  it("uses the shared page-state vocabulary instead of a per-tab wording", async () => {
    vi.mocked(personalLibraryApi.list).mockResolvedValue(list(["a"]));
    wrapper = mount(PersonalLibraryPanel, { props: { tab: "drafts" } }); await flushPromises();
    expect(wrapper.get(".library-item [data-slot=badge]").text()).toBe("Nepublikované změny");
    expect(wrapper.text()).not.toContain("Rozpracováno");
    await wrapper.setProps({ tab: "recent" }); await flushPromises();
    // The usual state is left unmarked; only what needs attention carries a badge.
    expect(wrapper.find(".library-item [data-slot=badge]").exists()).toBe(false);
    expect(wrapper.text()).not.toContain("Má publikovanou verzi");
  });
});
