import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises } from "@vue/test-utils";
import { mountPage, testContext } from "../general/testing";
import type { ContentHealth, ContentHealthItem, ContentHealthQuery } from "../../../pageVerification";
import SpaceContentHealthPage from "./SpaceContentHealthPage.vue";

const day = 86_400_000;
const item = (n: number, over: Partial<ContentHealthItem> = {}): ContentHealthItem => ({ pageId: `p${n}`, title: `Stránka ${n}`, path: ["Provoz"], state: "verified", stewardId: "u1", stewardName: "Jana Malá", verifiedUntil: new Date(Date.now() + 90 * day).toISOString(), isPublished: true, views: 10, readers: 4, lastViewedAt: null, lastPublishedAt: "2026-10-01T08:00:00Z", ...over });
const rows = [item(1, { state: "expired", readers: 40 }), item(2, { state: "expiring", readers: 12 }), item(3, { state: "unverified", stewardId: null, stewardName: null, readers: 0 }), item(4)];
const health = (items = rows, over: Partial<ContentHealth> = {}): ContentHealth => ({ summary: { verified: 1, expiring: 1, expired: 1, unverified: 1, withoutSteward: 1 }, items, nextCursor: null, enabled: true, ...over });

let mounted: ReturnType<typeof mountPage> | undefined;
function open(over: Partial<ContentHealth> = {}) {
  const api = { list: vi.fn(async (_space: string, _query: ContentHealthQuery) => health(rows, over)), bulk: vi.fn(async (_space: string, request: { pageIds: string[] }) => ({ changed: request.pageIds.length, skipped: [] })) };
  const people = vi.fn(async () => [{ id: "u9", name: "Eva Nováková" }]);
  const context = testContext();
  mounted = mountPage(SpaceContentHealthPage, { spaceId: "provoz", api, people }, { hash: "#/spaces/provoz/settings/content-health", context });
  return { api, people, context, view: mounted.wrapper, flash: mounted.flash };
}
const lastQuery = (api: ReturnType<typeof open>["api"]) => api.list.mock.calls.at(-1)![1];
afterEach(() => { mounted?.wrapper.unmount(); mounted = undefined; document.body.innerHTML = ""; history.replaceState(null, "", "/"); });

describe("Stav obsahu", () => {
  it("shows the four counts and the pages with their state, the default order is the server's (read and unverified first)", async () => {
    const { api, view } = open();
    await flushPromises();
    expect(api.list).toHaveBeenCalledWith("provoz", { state: "", mine: false, sort: "priority", days: 90, q: "", cursor: null }, expect.anything());
    expect(view.findAll("[data-health-tile]").map((tile) => tile.text())).toEqual(["Ověřeno1", "Brzy vyprší1", "Vypršelo / neověřeno2", "Bez garanta1"]);
    const states = view.findAll("[data-health-row]").map((row) => row.get("[data-health-state]").attributes("data-health-state"));
    expect(states).toEqual(["expired", "expiring", "unverified", "verified"]);
    expect(view.findAll("[data-health-row]")[0]!.text()).toContain("40 čtenářů / 90 dní");
    expect(view.findAll("[data-health-row]")[2]!.text()).toContain("Bez garanta");
    // The counts carry no names of readers.
    expect(view.text()).not.toContain("views");
  });

  it("asks again for a tile's filter, the sort and the period, and shows an empty answer", async () => {
    const { api, view } = open();
    await flushPromises();
    await view.get('[data-health-tile="noSteward"]').trigger("click");
    await flushPromises();
    expect(lastQuery(api).state).toBe("no-steward");
    await view.get('[data-health-tile="expired"]').trigger("click");
    await flushPromises();
    expect(lastQuery(api).state).toBe("due");

    api.list.mockResolvedValueOnce(health([]));
    await view.get("[data-health-search]").setValue("vpn");
    await vi.waitFor(() => expect(lastQuery(api).q).toBe("vpn"), { timeout: 1500 });
    await flushPromises();
    expect(view.find("[data-health-row]").exists()).toBe(false);
    expect(view.text()).toContain("Nic, co by odpovídalo filtru");
  });

  it("selects pages and verifies them in bulk, then reads the list again", async () => {
    const { api, view, flash } = open();
    await flushPromises();
    expect(view.find("[data-health-bulk]").exists()).toBe(false);
    const boxes = view.findAll("[data-health-row] [role=checkbox]");
    await boxes[0]!.trigger("click");
    await boxes[1]!.trigger("click");
    await flushPromises();
    expect(view.get("[data-health-bulk]").text()).toContain("Vybrány 2 stránky");
    await view.get("[data-health-verify]").trigger("click");
    await flushPromises();
    expect(api.bulk).toHaveBeenCalledWith("provoz", { action: "verify", pageIds: ["p1", "p2"] });
    expect(api.list).toHaveBeenCalledTimes(2);
    expect(flash().message.value?.text).toBe("Ověřeny 2 stránky.");
  });

  it("fills owners from the last author for the selection", async () => {
    const { api, view } = open();
    await flushPromises();
    await view.get("[data-health-all]").trigger("click");
    await flushPromises();
    await view.get("[data-health-from-author]").trigger("click");
    await flushPromises();
    expect(api.bulk).toHaveBeenCalledWith("provoz", { action: "stewardFromLastAuthor", pageIds: ["p1", "p2", "p3", "p4"] });
  });

  it("is driven by the keyboard: arrows move, Space selects, V verifies the selection, G picks an owner with Enter", async () => {
    const { api, people, view } = open();
    await flushPromises();
    const list = () => view.findAll<HTMLElement>("[data-health-row]");
    list()[0]!.element.focus();
    await list()[0]!.trigger("keydown", { key: "ArrowDown" });
    expect(document.activeElement).toBe(list()[1]!.element);
    await list()[1]!.trigger("keydown", { key: " " });
    expect(list()[1]!.attributes("data-selected")).toBeDefined();
    await list()[1]!.trigger("keydown", { key: "v" });
    await flushPromises();
    expect(api.bulk).toHaveBeenLastCalledWith("provoz", { action: "verify", pageIds: ["p2"] });

    await list()[1]!.trigger("keydown", { key: "g" });
    await flushPromises();
    await vi.waitFor(() => expect(people).toHaveBeenCalledWith("p2", "", expect.anything()));
    await flushPromises();
    const search = document.querySelector<HTMLInputElement>("dialog[open] input[type=search]")!;
    search.value = "Eva";
    search.dispatchEvent(new Event("input", { bubbles: true }));
    await vi.waitFor(() => expect(people).toHaveBeenCalledTimes(2), { timeout: 1500 });
    await flushPromises();
    search.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    await flushPromises();
    expect(api.bulk).toHaveBeenLastCalledWith("provoz", { action: "steward", pageIds: ["p2"], stewardId: "u9" });
  });

  it("explains that verification is off in this space", async () => {
    const { view } = open({ enabled: false });
    await flushPromises();
    expect(view.text()).toContain("Ověřování obsahu je v tomto prostoru vypnuté");
    expect(view.find("[data-health-tile]").exists()).toBe(false);
  });
});
