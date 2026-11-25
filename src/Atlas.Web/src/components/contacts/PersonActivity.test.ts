import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PersonActivity from "./PersonActivity.vue";
import { homeApi, personActivityDays, type RecentChange } from "../home/homeApi";
import { shiftDay } from "../../activity";
import { pragueNow } from "@atlas/markdown/core";

const apiMock = vi.hoisted(() => vi.fn(async () => ({ items: [], next: null })));
vi.mock("../../api", async original => ({ ...(await original<typeof import("../../api")>()), api: apiMock }));

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; apiMock.mockClear(); });

const change = (id: string, extra: Partial<RecentChange> = {}): RecentChange => ({
  pageId: `page-${id}`, title: `Stránka ${id}`, spaceId: "s1", spaceName: "Vývoj", revisionId: `rev-${id}`, version: 2, at: new Date().toISOString(),
  authorId: "acc-1", authorName: "Eva Dvořáková", channel: "web", isAi: false, apiToken: null, furtherChanges: 0, canOpenHistory: true, ...extra,
});
const today = pragueNow().date;
const year = [{ day: shiftDay(today, -1), spaceId: "s1", spaceName: "Vývoj", changes: 2 }, { day: today, spaceId: "s1", spaceName: "Vývoj", changes: 1 },
  { day: today, spaceId: "s2", spaceName: "Provoz", changes: 1 }];

describe("person activity", () => {
  it("asks Co se změnilo for one author and one space", async () => {
    await homeApi.changes("all", "c1", undefined, 10, { author: "acc-1", space: "s2" });
    expect(apiMock).toHaveBeenCalledWith("/me/changes?filter=all&limit=10&cursor=c1&author=acc-1&space=s2", expect.anything());
    await homeApi.changes("all", null);
    expect(apiMock).toHaveBeenLastCalledWith("/me/changes?filter=all&limit=8", expect.anything());
    await personActivityDays("acc-1");
    expect(apiMock).toHaveBeenLastCalledWith("/me/changes/days?author=acc-1", expect.anything());
  });

  it("lists the account's changes newest first, opens a page, loads more and narrows list and calendar by space", async () => {
    const changes = vi.fn(async (_filter: string, cursor: string | null, _signal?: AbortSignal, _limit?: number, scope?: { author?: string; space?: string }) =>
      cursor ? { items: [change("older")], next: null } : scope?.space === "s2" ? { items: [], next: null } : { items: [change("new")], next: "c1" });
    wrapper = mount(PersonActivity, { props: { accountId: "acc-1", source: { changes, days: async () => ({ days: year }) } }, attachTo: document.body });
    await flushPromises();

    expect(changes).toHaveBeenCalledWith("all", null, expect.any(AbortSignal), 10, { author: "acc-1", space: undefined });
    // The year at a glance: how much, when last, where; the calendar sums the spaces of a day.
    expect(wrapper.get("[data-person-activity-summary]").text()).toContain("4 změny za rok");
    expect(wrapper.get("[data-person-activity-summary]").text()).toContain("Vývoj");
    const calendar = wrapper.findComponent({ name: "ActivityHeatmap" });
    expect(calendar.props("days")).toEqual([{ day: shiftDay(today, -1), count: 2 }, { day: today, count: 2 }]);
    // The dock's width: the last 21 weeks, without scrolling; the summary keeps the year.
    expect([calendar.props("from"), calendar.props("to")]).toEqual([shiftDay(today, -146), today]);
    expect(wrapper.findComponent({ name: "FilterMenu" }).props("options").map((option: { label: string }) => option.label)).toEqual(["Všechny prostory", "Vývoj · 3", "Provoz · 1"]);
    const rows = wrapper.findAll("[data-change-row]");
    expect(rows.map(row => row.get(".change-title").text())).toEqual(["Stránka new"]);
    // The dock opens pages, not the page history.
    expect(wrapper.find("[data-change-history]").exists()).toBe(false);
    await rows[0]!.get("a").trigger("click", { button: 0 });
    expect(wrapper.emitted("open")?.[0]).toEqual(["page-new"]);

    await wrapper.get("[data-person-activity-more]").trigger("click");
    await flushPromises();
    expect(wrapper.findAll(".change-title").map(title => title.text())).toEqual(["Stránka new", "Stránka older"]);
    expect(wrapper.find("[data-person-activity-more]").exists()).toBe(false);

    wrapper.findComponent({ name: "FilterMenu" }).vm.$emit("update:modelValue", "s2");
    await flushPromises();
    expect(changes).toHaveBeenLastCalledWith("all", null, expect.any(AbortSignal), 10, { author: "acc-1", space: "s2" });
    expect(wrapper.findComponent({ name: "ActivityHeatmap" }).props("days")).toEqual([{ day: today, count: 1 }]);
    expect(wrapper.find("[data-change-row]").exists()).toBe(false);
    expect(wrapper.text()).toContain("Žádné změny stránek, které smíte číst");
  });
});
