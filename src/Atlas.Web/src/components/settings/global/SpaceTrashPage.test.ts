import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises } from "@vue/test-utils";
import type { WikiTrashCleanup, WikiTrashCleanupApi } from "../../../trashCleanupApi";
import { fakeApi, mountPage } from "../general/testing";
import SpaceTrashPage from "./SpaceTrashPage.vue";

let mounted: ReturnType<typeof mountPage> | undefined;
afterEach(() => { mounted?.wrapper.unmount(); mounted = undefined; document.body.innerHTML = ""; vi.unstubAllGlobals(); });

function open(state: Partial<WikiTrashCleanup> = {}) {
  fakeApi((_method, path) => (path === "/space-trash" ? [] : undefined));
  const api = {
    load: vi.fn(async () => ({ scope: "space", automatic: false, days: 60, version: "v1", ...state })),
    save: vi.fn(async (rule: object) => ({ ...rule, version: "v2" })),
  } as unknown as WikiTrashCleanupApi & { save: ReturnType<typeof vi.fn> };
  mounted = mountPage(SpaceTrashPage, { cleanupApi: api }, { hash: "#/settings/spaces/trash" });
  return api;
}
const view = () => mounted!.wrapper;

describe("SpaceTrashPage cleanup rule", () => {
  it("shows the rule fields only for Platí všude and saves the scope with the loaded version", async () => {
    const api = open();
    await flushPromises();
    expect(view().find("[data-trash-cleanup-mode]").exists()).toBe(false);
    await view().get('[data-trash-cleanup-scope] [data-value="wiki"]').trigger("click");
    await flushPromises();
    await view().get('[data-trash-cleanup-mode] [data-value="auto"]').trigger("click");
    await flushPromises();
    await view().get("[data-trash-cleanup-days]").setValue("90");
    await view().get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(api.save).toHaveBeenCalledWith({ scope: "wiki", automatic: true, days: 90 }, "v1");
  });
});
