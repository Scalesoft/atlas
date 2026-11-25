import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PersonActivity from "./PersonActivity.vue";
import { homeApi, type RecentChange } from "../home/homeApi";

const apiMock = vi.hoisted(() => vi.fn(async () => ({ items: [], next: null })));
vi.mock("../../api", async original => ({ ...(await original<typeof import("../../api")>()), api: apiMock }));

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; apiMock.mockClear(); });

const change = (id: string, extra: Partial<RecentChange> = {}): RecentChange => ({
  pageId: `page-${id}`, title: `Stránka ${id}`, spaceId: "s1", spaceName: "Vývoj", revisionId: `rev-${id}`, version: 2, at: new Date().toISOString(),
  authorId: "acc-1", authorName: "Eva Dvořáková", channel: "web", isAi: false, apiToken: null, furtherChanges: 0, canOpenHistory: true, ...extra,
});
const spaces = [{ id: "s1", name: "Vývoj", icon: "book" }, { id: "s2", name: "Provoz", icon: "book" }];

describe("person activity", () => {
  it("asks Co se změnilo for one author and one space", async () => {
    await homeApi.changes("all", "c1", undefined, 10, { author: "acc-1", space: "s2" });
    expect(apiMock).toHaveBeenCalledWith("/me/changes?filter=all&limit=10&cursor=c1&author=acc-1&space=s2", expect.anything());
    await homeApi.changes("all", null);
    expect(apiMock).toHaveBeenLastCalledWith("/me/changes?filter=all&limit=8", expect.anything());
  });

  it("lists the account's changes newest first, opens a page, loads more and narrows by space", async () => {
    const changes = vi.fn(async (_filter: string, cursor: string | null, _signal?: AbortSignal, _limit?: number, scope?: { author?: string; space?: string }) =>
      cursor ? { items: [change("older")], next: null } : scope?.space === "s2" ? { items: [], next: null } : { items: [change("new")], next: "c1" });
    wrapper = mount(PersonActivity, { props: { accountId: "acc-1", source: { changes, spaces: async () => spaces } }, attachTo: document.body });
    await flushPromises();

    expect(changes).toHaveBeenCalledWith("all", null, expect.any(AbortSignal), 10, { author: "acc-1", space: undefined });
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
    expect(wrapper.find("[data-change-row]").exists()).toBe(false);
    expect(wrapper.text()).toContain("Za posledních 30 dní");
  });
});
