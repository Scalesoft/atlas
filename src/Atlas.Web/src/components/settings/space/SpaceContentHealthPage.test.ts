import { afterEach, describe, expect, it, vi } from "vitest";
import { toast } from "vue-sonner";
import { ApiError } from "../../../api";
import { flushPromises } from "@vue/test-utils";
import { mountPage, testContext } from "../general/testing";
import type { ContentHealth, ContentHealthItem, ContentHealthQuery } from "../../../verification/pageVerification";
import SpaceContentHealthPage from "./SpaceContentHealthPage.vue";

const day = 86_400_000;
const item = (n: number, over: Partial<ContentHealthItem> = {}): ContentHealthItem => ({ pageId: `p${n}`, title: `Stránka ${n}`, path: ["Provoz"], state: "verified", stewardId: "u1", stewardName: "Jana Malá", verifiedUntil: new Date(Date.now() + 90 * day).toISOString(), canEdit: true, views: 10, readers: 4, lastViewedAt: null, lastPublishedAt: "2026-10-01T08:00:00Z", ...over });
const rows = [item(1, { state: "expired", readers: 40 }), item(2, { state: "expiring", readers: 12 }), item(3, { state: "unverified", stewardId: null, stewardName: null, readers: 0, views: 0 }), item(4)];
const health = (items = rows, over: Partial<ContentHealth> = {}): ContentHealth => ({ summary: { verified: 1, expiring: 1, expired: 1, unverified: 1, withoutSteward: 1 }, items, nextCursor: null, enabled: true, ...over });

let mounted: ReturnType<typeof mountPage> | undefined;
function open(over: Partial<ContentHealth> = {}, items = rows, contextOver: Parameters<typeof testContext>[0] = {}) {
  const undo = vi.fn(async () => undefined);
  const api = {
    list: vi.fn(async (_space: string, _query: ContentHealthQuery) => health(items, over)),
    bulk: vi.fn(async (_space: string, request: { pageIds?: string[] }): Promise<{ changed: number; skipped: { pageId: string; reason: string }[]; more?: boolean }> => ({ changed: request.pageIds?.length ?? 0, skipped: [] })),
    trash: vi.fn(async () => ({ undo })),
  };
  const people = vi.fn(async () => [{ id: "u9", name: "Eva Nováková" }]);
  const context = testContext(contextOver);
  mounted = mountPage(SpaceContentHealthPage, { spaceId: "provoz", api, people }, { hash: "#/spaces/provoz/settings/content-health", context });
  return { api, people, context, undo, view: mounted.wrapper, flash: mounted.flash };
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
    expect(lastQuery(api).state).toBe("lapsed");

    api.list.mockResolvedValueOnce(health([]));
    await view.get("[data-health-search]").setValue("vpn");
    await vi.waitFor(() => expect(lastQuery(api).q).toBe("vpn"), { timeout: 1500 });
    await flushPromises();
    expect(view.find("[data-health-row]").exists()).toBe(false);
    expect(view.text()).toContain("Nic, co by odpovídalo filtru");
  });

  it("gives each tile its own filter, marks the lit one and shows everything again on a second click", async () => {
    const { api, view } = open();
    await flushPromises();
    for (const [key, state] of [["verified", "verified"], ["expiring", "expiring"], ["expired", "lapsed"], ["noSteward", "no-steward"]] as const) {
      await view.get(`[data-health-tile="${key}"]`).trigger("click");
      await flushPromises();
      expect(lastQuery(api).state).toBe(state);
      expect(view.get(`[data-health-tile="${key}"]`).attributes("aria-pressed")).toBe("true");
      expect(view.findAll('[data-health-tile][aria-pressed="true"]')).toHaveLength(1);
    }
    await view.get('[data-health-tile="noSteward"]').trigger("click");
    await flushPromises();
    expect(lastQuery(api).state).toBe("");
    expect(view.findAll('[data-health-tile][aria-pressed="true"]')).toHaveLength(0);
  });

  it("keeps the keyboard on its row after V reads the list again, so V, down, V goes on row by row", async () => {
    const { api, view } = open();
    await flushPromises();
    const list = () => view.findAll<HTMLElement>("[data-health-row]");
    list()[0]!.element.focus();
    // The list comes back re-sorted: the verified row moved away, another stands at the index.
    api.list.mockImplementationOnce(async () => health([rows[1]!, rows[2]!, rows[3]!]));
    await list()[0]!.trigger("keydown", { key: "v" });
    await flushPromises();
    expect(document.activeElement).toBe(list()[0]!.element);
    expect(list()[0]!.attributes("data-page")).toBe("p2");
    await list()[0]!.trigger("keydown", { key: "ArrowDown" });
    await list()[1]!.trigger("keydown", { key: "v" });
    await flushPromises();
    expect(api.bulk).toHaveBeenLastCalledWith("provoz", { action: "verify", pageIds: ["p3"] });
  });

  it("offers a row the actions of the bulk buttons in its menu, opened from the visible ⋯", async () => {
    const { api, view } = open();
    await flushPromises();
    await view.get("[data-health-row] [data-row-more]").trigger("click");
    await vi.waitFor(() => expect(document.querySelector("[data-testid=intentions]")).not.toBeNull());
    const labels = [...document.querySelectorAll("[data-testid=intentions] [role=option]")].map((entry) => entry.textContent);
    expect(labels[0]).toContain("Ověřit");
    expect(labels[1]).toContain("garanta");
    (document.querySelector("[data-testid=intentions] [role=option]") as HTMLElement).click();
    await flushPromises();
    expect(api.bulk).toHaveBeenLastCalledWith("provoz", { action: "verify", pageIds: [rows[0]!.pageId] });
  });

  it("asks before verifying many pages at once", async () => {
    const many = Array.from({ length: 12 }, (_, n) => item(n + 1));
    const { api, view } = open();
    await flushPromises();
    api.list.mockImplementation(async () => health(many));
    await view.get("[data-health-search]").setValue("x");
    await vi.waitFor(() => expect(api.list).toHaveBeenCalledTimes(2), { timeout: 1500 });
    await flushPromises();
    await view.get("[data-bulk-all]").trigger("click");
    await flushPromises();
    await view.get("[data-health-verify]").trigger("click");
    await flushPromises();
    expect(api.bulk).not.toHaveBeenCalled();
    expect(document.querySelector("dialog[open]")!.textContent).toContain("Ověřit 12 stránek?");
    [...document.querySelectorAll<HTMLElement>("dialog[open] button")].find((b) => b.textContent?.trim() === "Ověřit")!.click();
    await flushPromises();
    expect(api.bulk).toHaveBeenCalledTimes(1);
  });

  it("selects pages and verifies them in bulk, then reads the list again", async () => {
    const { api, view, flash } = open();
    await flushPromises();
    expect(view.find("[data-bulk-bar]").exists()).toBe(false);
    const boxes = view.findAll("[data-health-row] [role=checkbox]");
    await boxes[0]!.trigger("click");
    await boxes[1]!.trigger("click");
    await flushPromises();
    expect(view.get("[data-bulk-bar]").text()).toContain("Vybráno: 2");
    await view.get("[data-health-verify]").trigger("click");
    await flushPromises();
    expect(api.bulk).toHaveBeenCalledWith("provoz", { action: "verify", pageIds: ["p1", "p2"] });
    expect(api.list).toHaveBeenCalledTimes(2);
    expect(flash().message.value?.text).toBe("Ověřeny 2 stránky.");
  });

  it("fills owners from the last author for the selection", async () => {
    const { api, view } = open();
    await flushPromises();
    await view.get("[data-bulk-all]").trigger("click");
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

  it("explains that verification is off in this space, and only its manager is sent to switch it on", async () => {
    const { view } = open({ enabled: false });
    await flushPromises();
    expect(view.text()).toContain("Zapnete ho v Obecném nastavení prostoru");
    expect(view.find("a[href$='/general']").exists()).toBe(true);
    expect(view.find("[data-health-tile]").exists()).toBe(false);
    view.unmount();
    const writer = open({ enabled: false }, rows, { isWikiAdmin: false, spaces: [{ id: "provoz", role: "Writer" } as never] });
    await flushPromises();
    expect(writer.view.text()).toContain("Zapíná ho správce prostoru");
    expect(writer.view.find("a[href$='/general']").exists()).toBe(false);
  });

  it("offers Ověřit, the checkbox and V only where the reader may verify, and labels a page that does not verify", async () => {
    const own = [item(1, { state: "off", stewardId: null, stewardName: null }), item(2, { state: "expired", canEdit: false, views: 0 }), item(3, { state: "expired" })];
    const { api, view } = open({}, own);
    await flushPromises();
    const list = () => view.findAll<HTMLElement>("[data-health-row]");
    expect(list().map((row) => row.find("[data-health-row-verify]").exists())).toEqual([false, false, true]);
    expect(list().map((row) => row.get("[role=checkbox]").classes("invisible"))).toEqual([true, true, false]);
    expect(list()[0]!.get("[data-health-state]").text()).toBe("Neověřuje se");
    // An unread page offers the trash only to who may edit it.
    expect(list()[1]!.find("[data-health-row-trash]").exists()).toBe(false);
    list()[1]!.element.focus();
    await list()[1]!.trigger("keydown", { key: " " });
    await list()[1]!.trigger("keydown", { key: "v" });
    await flushPromises();
    expect(list()[1]!.attributes("data-selected")).toBeUndefined();
    expect(api.bulk).not.toHaveBeenCalled();
  });

  it("fills owners for the whole space from „Bez garanta“, batch by batch, and counts pages that already have one apart", async () => {
    const { api, view, flash } = open();
    await flushPromises();
    expect(view.find("[data-health-from-author-space]").exists()).toBe(false);
    await view.get('[data-health-tile="noSteward"]').trigger("click");
    await flushPromises();
    api.bulk.mockResolvedValueOnce({ changed: 500, skipped: [], more: true }).mockResolvedValueOnce({ changed: 3, skipped: [{ pageId: "p9", reason: "400" }] });
    await view.get("[data-health-from-author-space]").trigger("click");
    await flushPromises();
    expect(api.bulk.mock.calls.map((call) => call[1])).toEqual([{ action: "stewardFromLastAuthor" }, { action: "stewardFromLastAuthor" }]);
    expect(flash().message.value?.text).toBe("Garant nastaven u 503 stránek. 1 stránku se nepodařilo změnit (nepublikovaná nebo bez práv).");

    api.bulk.mockResolvedValueOnce({ changed: 1, skipped: [{ pageId: "p2", reason: "has_steward" }] });
    await view.findAll("[data-health-row] [role=checkbox]")[0]!.trigger("click");
    await view.findAll("[data-health-row] [role=checkbox]")[1]!.trigger("click");
    await flushPromises();
    await view.get("[data-health-from-author]").trigger("click");
    await flushPromises();
    expect(flash().message.value?.text).toBe("Garant nastaven u 1 stránky. 1 stránka už garanta má.");
  });

  it("moves an unread page to the trash with Zpět in the toast, and „Zkusit znovu“ repeats a failed action", async () => {
    const success = vi.spyOn(toast, "success");
    const { api, undo, view } = open();
    await flushPromises();
    api.trash.mockRejectedValueOnce(new ApiError(500, "Server neodpovídá."));
    await view.findAll("[data-health-row]")[2]!.get("[data-health-row-trash]").trigger("click");
    await flushPromises();
    expect(view.text()).toContain("Server neodpovídá.");
    await view.get(".error-state button").trigger("click");
    await flushPromises();
    expect(api.trash).toHaveBeenCalledTimes(2);
    expect(view.find(".error-state").exists()).toBe(false);
    const [message, options] = success.mock.calls.at(-1)!;
    expect(message).toBe("Stránka „Stránka 3“ je v koši.");
    (options!.action as { onClick: () => void }).onClick();
    await flushPromises();
    expect(undo).toHaveBeenCalled();
    success.mockRestore();
  });
});
