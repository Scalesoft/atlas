import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises } from "@vue/test-utils";
import { ApiError } from "../../../api";
import type { SpaceTrashCleanup, SpaceTrashCleanupApi } from "../../../trashCleanupApi";
import { mountPage, testContext } from "../general/testing";
import SpaceTrashCleanupPage from "./SpaceTrashCleanupPage.vue";

let mounted: ReturnType<typeof mountPage> | undefined;
afterEach(() => { mounted?.wrapper.unmount(); mounted = undefined; document.body.innerHTML = ""; });

const base: SpaceTrashCleanup = { automatic: false, days: 60, version: "v1", decidedByWiki: false, effectiveAutomatic: false, effectiveDays: 60, counts: { trashed: 5, trashedDue: 2, unusedDue: 3 } };
function open(state: Partial<SpaceTrashCleanup> = {}, overrides: Partial<SpaceTrashCleanupApi> = {}, openTrash?: (spaceId: string) => void) {
  const api = {
    load: vi.fn(async () => ({ ...base, ...state })),
    save: vi.fn(async (_space: string, rule: { automatic: boolean; days: number }) => ({ ...base, ...rule, version: "v2" })),
    run: vi.fn(async () => ({ purged: 2, trashed: 3, skipped: 1 })),
    ...overrides,
  } as SpaceTrashCleanupApi & { load: ReturnType<typeof vi.fn>; save: ReturnType<typeof vi.fn>; run: ReturnType<typeof vi.fn> };
  mounted = mountPage(SpaceTrashCleanupPage, { spaceId: "provoz", api }, { hash: "#/spaces/provoz/settings/trash", context: testContext({ openTrash }) });
  return api;
}
const view = () => mounted!.wrapper;
const body = () => document.body;

describe("SpaceTrashCleanupPage", () => {
  it("links to the space's Koš stránek when something lies in the trash", async () => {
    const openTrash = vi.fn();
    open({}, {}, openTrash);
    await flushPromises();
    await view().get("[data-trash-open]").trigger("click");
    expect(openTrash).toHaveBeenCalledWith("provoz");
    mounted?.wrapper.unmount();
    open({ counts: { trashed: 0, trashedDue: 0, unusedDue: 0 } }, {}, openTrash);
    await flushPromises();
    expect(view().find("[data-trash-open]").exists()).toBe(false);
  });

  it("shows the counts, saves the rule with the loaded version and reports a conflict", async () => {
    const api = open();
    await flushPromises();
    expect(view().get("[data-trash-cleanup-counts]").text()).toContain("5");
    expect(view().find("[data-trash-cleanup-days]").exists()).toBe(false);
    await view().get('[data-trash-cleanup-mode] [data-value="auto"]').trigger("click");
    await flushPromises();
    await view().get("[data-trash-cleanup-days]").setValue("30");
    await view().get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(api.save).toHaveBeenCalledWith("provoz", { automatic: true, days: 30 }, "v1");
    expect(mounted!.flash().message.value?.text).toContain("uložené");
    api.save.mockRejectedValueOnce(new ApiError(409, "Conflict"));
    await view().get("[data-trash-cleanup-days]").setValue("45");
    await view().get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(view().find("[data-trash-cleanup-conflict]").exists()).toBe(true);
  });

  it("refuses days outside 7-3650 before anything is sent", async () => {
    const api = open({ automatic: true });
    await flushPromises();
    await view().get("[data-trash-cleanup-days]").setValue("3");
    await view().get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(api.save).not.toHaveBeenCalled();
    expect(view().text()).toContain("od 7 do 3650");
  });

  it("shows the rule of the wiki read-only", async () => {
    open({ decidedByWiki: true, effectiveAutomatic: true, effectiveDays: 90 });
    await flushPromises();
    expect(view().get("[data-trash-cleanup-wiki]").text()).toContain("90 dní");
    expect(view().find("[data-trash-cleanup-mode]").exists()).toBe(false);
    expect(view().find("[data-trash-cleanup-days]").exists()).toBe(false);
    expect(view().find("[data-save-bar-save]").exists()).toBe(false);
  });

  it("names what Vyčistit teď does, runs it and reloads the counts", async () => {
    const api = open();
    await flushPromises();
    await view().get("[data-trash-cleanup-run]").trigger("click");
    await flushPromises();
    const text = body().querySelector("[data-trash-cleanup-confirm]")!.textContent!;
    expect(text).toContain("2 položky");
    expect(text).toContain("3 nepoužité objekty");
    expect(api.run).not.toHaveBeenCalled();
    const confirm = [...body().querySelectorAll("button")].find((b) => b.textContent?.trim() === "Vyčistit")!;
    confirm.click();
    await flushPromises();
    expect(api.run).toHaveBeenCalledWith("provoz");
    expect(api.load).toHaveBeenCalledTimes(2);
    expect(mounted!.flash().message.value?.text).toContain("Přeskočeno 1");
  });

  it("says the section does not exist on 404", async () => {
    open({}, { load: vi.fn(async () => { throw new ApiError(404, "Not found"); }) });
    await flushPromises();
    expect(view().text()).toContain("Tato sekce neexistuje");
  });
});
