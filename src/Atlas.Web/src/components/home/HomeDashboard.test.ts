import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import HomeDashboard from "./HomeDashboard.vue";
import { dashboardCards } from "./dashboardCards";
import type { HomeApi, RecentChange } from "./homeApi";
import type { LibraryItem } from "../../personalLibrary";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

const change = (id: string, extra: Partial<RecentChange> = {}): RecentChange => ({
  pageId: `page-${id}`, title: `Stránka ${id}`, spaceId: "s", spaceName: "Vývoj", revisionId: `rev-${id}`, version: 3, at: new Date().toISOString(),
  authorId: "a", authorName: "Jana Nováková", channel: "web", isAi: false, apiToken: null, furtherChanges: 0, canOpenHistory: false, ...extra,
});
const visit = (id: string): LibraryItem => ({ id, spaceId: "s", spaceName: "Vývoj", title: `Navštíveno ${id}`, parentId: null, version: 1, isPublished: true,
  isFavorite: false, visitedAt: new Date().toISOString(), publishedAt: null });
function fakeApi(changes: RecentChange[], next: string | null = null) {
  return {
    changes: vi.fn(async (filter: string, cursor: string | null) => cursor ? { items: [change("later")], next: null }
      : { items: filter === "ai" ? changes.filter(x => x.isAi) : changes, next }),
    recent: vi.fn(async () => ({ items: [visit("v1")], hasMore: false, nextOffset: null })),
    drafts: vi.fn(async () => ({ items: [], hasMore: false, nextOffset: null })),
  } satisfies HomeApi;
}

describe("home dashboard", () => {
  it("shows the registry's cards, the author kind of each change, and narrows to changes with AI", async () => {
    const api = fakeApi([
      change("agent", { channel: "mcp", isAi: true, apiToken: { id: "t", name: "Claude Code", revoked: false }, furtherChanges: 2, canOpenHistory: true }),
      change("person"),
    ], "cursor-1");
    wrapper = mount(HomeDashboard, { props: { api }, attachTo: document.body });
    await flushPromises();

    expect(wrapper.findAll("[data-home-card]").map(card => card.attributes("data-home-card"))).toEqual(dashboardCards.map(card => card.id));
    const [agent, person] = wrapper.findAll("[data-change-row]");
    expect(agent!.find("[data-change-ai]").exists()).toBe(true);
    expect(agent!.get("[data-channel-badge]").text()).toContain("Claude Code");
    expect(agent!.text()).toContain("+2 další změny");
    expect(person!.find("[data-change-ai]").exists()).toBe(false);
    expect(person!.find("[data-channel-badge]").exists()).toBe(false);
    // Only who may edit the page gets its history; the row opens the page.
    expect(person!.find("[data-change-history]").exists()).toBe(false);
    await agent!.get("[data-change-history]").trigger("click");
    expect(wrapper.emitted("history")?.[0]?.[0]).toMatchObject({ pageId: "page-agent", revisionId: "rev-agent" });
    await person!.get("a").trigger("click", { button: 0 });
    expect(wrapper.emitted("select")?.[0]).toEqual(["page-person", "page"]);

    await wrapper.get('[data-home-card="changes"] button.option-pill[data-value="ai"]').trigger("click");
    await flushPromises();
    expect(api.changes).toHaveBeenLastCalledWith("ai", null, expect.anything());
    expect(wrapper.findAll("[data-change-row]")).toHaveLength(1);
  });

  it("follows the cursor, links Zobrazit vše to Knihovna and says when there is nothing", async () => {
    const api = fakeApi([change("first")], "cursor-1");
    wrapper = mount(HomeDashboard, { props: { api }, attachTo: document.body });
    await flushPromises();
    await wrapper.get('[data-home-card="changes"] .changes-more button').trigger("click");
    await flushPromises();
    expect(api.changes).toHaveBeenLastCalledWith("all", "cursor-1", expect.anything());
    expect(wrapper.findAll("[data-change-row]")).toHaveLength(2);

    await wrapper.get('[data-home-card="recent"] .dashboard-show-all').trigger("click", { button: 0 });
    expect(wrapper.emitted("navigate")?.[0]).toEqual(["recent"]);
    expect(wrapper.get('[data-home-card="waiting"]').text()).toContain("Žádné rozpracované koncepty");

    wrapper.unmount();
    wrapper = mount(HomeDashboard, { props: { api: fakeApi([]) }, attachTo: document.body });
    await flushPromises();
    expect(wrapper.get('[data-home-card="changes"]').text()).toContain("Za posledních 30 dní se nic nezměnilo");
  });

  it("leaves out the card of a module that is off", async () => {
    wrapper = mount(HomeDashboard, { props: { api: fakeApi([]), cards: [...dashboardCards, { ...dashboardCards[1]!, id: "boards-only", module: "boards" }] } });
    await flushPromises();
    expect(wrapper.find('[data-home-card="boards-only"]').exists()).toBe(false);
  });
});
