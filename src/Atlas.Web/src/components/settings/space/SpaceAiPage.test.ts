import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises } from "@vue/test-utils";
import { ApiError } from "../../../api";
import type { SpaceAiApi } from "../../../aiPreferences";
import { mountPage } from "../general/testing";
import SpaceAiPage from "./SpaceAiPage.vue";

let mounted: ReturnType<typeof mountPage> | undefined;
afterEach(() => { mounted?.wrapper.unmount(); mounted = undefined; document.body.innerHTML = ""; });

function open(overrides: Partial<SpaceAiApi> = {}) {
  const api = {
    load: vi.fn(async () => ({ included: true, version: "v1" })),
    save: vi.fn(async (_space: string, included: boolean) => ({ included, version: "v2" })),
    ...overrides,
  } as SpaceAiApi & { load: ReturnType<typeof vi.fn>; save: ReturnType<typeof vi.fn> };
  mounted = mountPage(SpaceAiPage, { spaceId: "provoz", api }, { hash: "#/spaces/provoz/settings/ai" });
  return api;
}
const view = () => mounted!.wrapper;
const toggle = () => view().get("[data-space-ai-included]");

describe("SpaceAiPage", () => {
  it("previews the impact before excluding the space and handles a version conflict", async () => {
    const api = open({ save: vi.fn(async () => { throw new ApiError(409, "Conflict"); }) });
    await flushPromises();
    expect(toggle().attributes("aria-checked")).toBe("true");
    expect(view().find("[data-space-ai-impact]").exists()).toBe(false);
    await toggle().trigger("click");
    await flushPromises();
    // The preview names what changes and that ordinary search stays, before anything is saved.
    const impact = view().get("[data-space-ai-impact]");
    expect(impact.text()).toContain("související stránky");
    expect(impact.text()).toContain("v odpovědích asistenta");
    expect(impact.text()).toContain("běžné hledání");
    expect(api.save).not.toHaveBeenCalled();
    await view().get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(api.save).toHaveBeenCalledWith("provoz", false, "v1");
    // The draft stays; Načíst znovu replaces it with the current state.
    expect(view().find("[data-space-ai-conflict]").exists()).toBe(true);
    expect(toggle().attributes("aria-checked")).toBe("false");
    // The note speaks alone: the save bar has no text about the conflict ("Conflict" would be the server's message).
    expect(view().get(".save-bar").text()).not.toContain("Conflict");
    // A second save hides the note for as long as it runs.
    let fail = () => {};
    api.save.mockImplementationOnce(() => new Promise((_, reject) => { fail = () => reject(new ApiError(409, "Conflict")); }));
    await view().get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(view().find("[data-space-ai-conflict]").exists()).toBe(false);
    expect(view().get(".save-bar").text()).not.toContain("Conflict");
    fail();
    await flushPromises();
    expect(view().find("[data-space-ai-conflict]").exists()).toBe(true);
    api.load.mockResolvedValueOnce({ included: true, version: "v3" });
    await view().get("[data-space-ai-conflict] button").trigger("click");
    await flushPromises();
    expect(toggle().attributes("aria-checked")).toBe("true");
    expect(view().find("[data-space-ai-conflict]").exists()).toBe(false);
  });

  it("talks only about what is on: MCP without AI, and both", async () => {
    mounted = mountPage(SpaceAiPage, { spaceId: "provoz", ai: false, mcp: true, api: { load: vi.fn(async () => ({ included: true, version: "v1" })), save: vi.fn() } }, { hash: "#/spaces/provoz/settings/ai" });
    await flushPromises();
    expect(view().text()).toContain("Zahrnout prostor do nástrojů MCP");
    await toggle().trigger("click");
    const impact = view().get("[data-space-ai-impact]").text();
    expect(impact).toContain("Nástroje MCP");
    expect(impact).not.toContain("významové");
    mounted.wrapper.unmount();
    mounted = mountPage(SpaceAiPage, { spaceId: "provoz", ai: true, mcp: true, api: { load: vi.fn(async () => ({ included: true, version: "v1" })), save: vi.fn() } }, { hash: "#/spaces/provoz/settings/ai" });
    await flushPromises();
    expect(view().text()).toContain("Zahrnout prostor do AI a nástrojů MCP");
    await toggle().trigger("click");
    const both = view().get("[data-space-ai-impact]").text();
    expect(both).toContain("související stránky");
    expect(both).toContain("Nástroje MCP");
  });

  it("saves the exclusion with the version and adopts the answer", async () => {
    const api = open();
    await flushPromises();
    await toggle().trigger("click");
    await view().get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(api.save).toHaveBeenCalledWith("provoz", false, "v1");
    expect(mounted!.guard.dirty.value).toBe(false);
    expect(toggle().attributes("aria-checked")).toBe("false");
  });

  it("registers its draft with the leave guard and discards it", async () => {
    open();
    await flushPromises();
    await toggle().trigger("click");
    expect(mounted!.guard.dirty.value).toBe(true);
    mounted!.guard.discard();
    await flushPromises();
    expect(toggle().attributes("aria-checked")).toBe("true");
  });

  it("shows no section to someone the server answers 404", async () => {
    open({ load: vi.fn(async () => { throw new ApiError(404, "Not found"); }) });
    await flushPromises();
    expect(view().text()).toContain("Tato sekce neexistuje");
    expect(view().find("[data-space-ai-included]").exists()).toBe(false);
  });
});
