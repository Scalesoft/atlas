import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../../api";
import type { SpaceCover, SpaceCoverChoice, SpaceCoverMatchInput } from "../../spaceCover";
import SpaceCoverField from "./SpaceCoverField.vue";

const space = { id: "space-1", name: "Provoz", icon: "server", color: "teal-2", role: "Admin" as const, pageCount: 3, lastChangedAt: null };
const cover = (value: Partial<SpaceCover>): SpaceCover => ({ source: "Auto", catalogKey: null, version: null, tinted: true, ...value });
const stub = () => ({
  upload: vi.fn<(spaceId: string, file: File) => Promise<SpaceCover>>(),
  setTinted: vi.fn<(spaceId: string, tinted: boolean) => Promise<SpaceCover>>(),
  choose: vi.fn<(spaceId: string, choice: SpaceCoverChoice) => Promise<SpaceCover>>(async (_spaceId, choice) => cover({ source: choice.source, catalogKey: choice.catalogKey ?? null })),
  match: vi.fn<(input: SpaceCoverMatchInput) => Promise<{ catalogKey: string | null }>>(async () => ({ catalogKey: null })),
});
const mounted: VueWrapper[] = [];
function block(stored: SpaceCover | undefined, api = stub(), matchInput?: SpaceCoverMatchInput) {
  const wrapper = mount(SpaceCoverField, { props: { space, cover: stored, api, matchInput }, attachTo: document.body });
  mounted.push(wrapper);
  return wrapper;
}
const line = (wrapper: VueWrapper) => wrapper.get("[data-space-cover-line]").text();
const preview = (wrapper: VueWrapper) => wrapper.get(".space-cover-preview .space-cover");
const option = (wrapper: VueWrapper, value: string) => wrapper.get(`[data-space-cover-options] [data-value="${value}"]`);
async function openGallery(wrapper: VueWrapper) {
  await wrapper.get("[data-space-cover-pick]").trigger("click");
  await flushPromises();
}
afterEach(() => { mounted.splice(0).forEach((wrapper) => wrapper.unmount()); vi.useRealTimers(); });

describe("SpaceCoverField", () => {
  it("says how the image was chosen and offers the controls that belong to it", async () => {
    const automatic = block(cover({ catalogKey: "it-monitoring" }));
    expect(line(automatic)).toContain("Vybráno automaticky: Monitoring a provoz");
    expect(line(automatic)).toContain("Podle názvu, popisu a ikony prostoru.");
    expect(preview(automatic).get("img").attributes("src")).toContain("it-monitoring.svg");
    for (const unmatched of [cover({}), undefined]) {
      expect(line(block(unmatched))).toBe("Automaticky: k názvu, popisu ani ikoně zatím žádný obrázek nesedí. Prostor má motiv ve své barvě.");
    }
    expect(line(block(cover({ source: "Catalog", catalogKey: "it-database" })))).toBe("Vybráno z katalogu: Databáze a data");
    const none = block(cover({ source: "None" }));
    expect(line(none)).toBe("Bez obrázku. Prostor má motiv ve své barvě.");
    expect(preview(none).attributes("data-cover")).toBe("pattern");
    expect(none.find("[data-space-cover-tint]").exists()).toBe(false);

    const uploaded = block(cover({ source: "Upload", version: "a1", tinted: false }));
    expect(line(uploaded)).toBe("Vlastní obrázek.");
    expect(preview(uploaded).attributes("data-cover")).toBe("photo");
    expect(preview(uploaded).get("img").attributes("src")).toBe("/api/spaces/space-1/cover?v=a1");
    expect(uploaded.get("[data-space-cover-tint]").attributes("aria-checked")).toBe("false");
    expect(uploaded.get("[data-space-cover-upload]").text()).toBe("Nahrát vlastní…");

    // The gallery is not there (and asks for no thumbnail) until it is opened.
    const pick = uploaded.get("[data-space-cover-pick]");
    expect(pick.text()).toBe("Vybrat jiný");
    expect(pick.attributes("aria-expanded")).toBe("false");
    expect(uploaded.find("[data-space-cover-gallery]").exists()).toBe(false);
    await openGallery(uploaded);
    expect(pick.text()).toBe("Zavřít výběr");
    expect(pick.attributes("aria-expanded")).toBe("true");
    expect(uploaded.get(`#${pick.attributes("aria-controls")}`).find("[data-space-cover-gallery]").exists()).toBe(true);
  });

  // Offline, 429 or an older server: no answer does not mean that nothing fits.
  it("keeps the pick the server stored until the live match answers", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const api = stub();
    api.match.mockRejectedValueOnce(new Error("offline")).mockResolvedValueOnce({ catalogKey: "it-database" });
    const wrapper = block(cover({ catalogKey: "it-monitoring" }), api, { name: "Provoz", description: "", icon: "server" });
    await vi.advanceTimersByTimeAsync(300); await flushPromises();
    expect(api.match).toHaveBeenCalledTimes(1);
    expect(line(wrapper)).toContain("Vybráno automaticky: Monitoring a provoz");
    expect(preview(wrapper).get("img").attributes("src")).toContain("it-monitoring.svg");

    await wrapper.setProps({ matchInput: { name: "Databáze", description: "", icon: "server" } });
    await vi.advanceTimersByTimeAsync(300); await flushPromises();
    expect(line(wrapper)).toContain("Vybráno automaticky: Databáze a data");
  });

  it("shows a clicked drawing at once and saves it, and returns to the previous image when the server refuses", async () => {
    const api = stub();
    let answer!: (value: SpaceCover) => void;
    api.choose.mockReturnValueOnce(new Promise((resolve) => { answer = resolve; }));
    const wrapper = block(cover({ catalogKey: "it-monitoring" }), api);
    await openGallery(wrapper);
    await option(wrapper, "it-database").trigger("click");
    expect(api.choose.mock.calls).toEqual([["space-1", { source: "Catalog", catalogKey: "it-database" }]]);
    // Before the server answers.
    expect(preview(wrapper).get("img").attributes("src")).toContain("it-database.svg");
    expect(line(wrapper)).toBe("Vybráno z katalogu: Databáze a data");
    expect(option(wrapper, "it-database").attributes("aria-checked")).toBe("true");
    expect(wrapper.emitted("change")).toBeUndefined();
    const saved = cover({ source: "Catalog", catalogKey: "it-database" });
    answer(saved); await flushPromises();
    expect(wrapper.emitted("change")).toEqual([[saved]]);
    await wrapper.setProps({ cover: saved });
    expect(wrapper.get("[role=status]").text()).toBe("Obrázek je uložený.");

    let refuse!: (reason: Error) => void;
    api.choose.mockReturnValueOnce(new Promise((_resolve, reject) => { refuse = reject; }));
    await option(wrapper, "it-backup").trigger("click");
    expect(line(wrapper)).toBe("Vybráno z katalogu: Zálohy a obnova");
    refuse(new Error("Tento obrázek v katalogu není. Vyberte jiný.")); await flushPromises();
    expect(wrapper.get("[data-space-cover-error]").text()).toBe("Tento obrázek v katalogu není. Vyberte jiný.");
    expect(preview(wrapper).get("img").attributes("src")).toContain("it-database.svg");
    expect(line(wrapper)).toBe("Vybráno z katalogu: Databáze a data");
    expect(option(wrapper, "it-database").attributes("aria-checked")).toBe("true");
    expect(wrapper.emitted("change")).toHaveLength(1);
  });

  // A 404 for the tint proves only that the upload is gone, not what the image is now; a 404 for a choice is an older server.
  it("does not guess the image when the upload is gone, and explains a server without images", async () => {
    const api = stub();
    api.setTinted.mockRejectedValueOnce(new ApiError(404, "Not Found"));
    const uploaded = block(cover({ source: "Upload", version: "a1" }), api);
    await uploaded.get("[data-space-cover-tint]").trigger("click"); await flushPromises();
    expect(api.setTinted).toHaveBeenCalledWith("space-1", false);
    expect(uploaded.get("[role=alert]").text()).toBe("Vlastní obrázek mezitím někdo odebral nebo nahradil. Načtěte stránku znovu.");
    expect(uploaded.emitted("change")).toBeUndefined();
    expect(uploaded.get("[data-space-cover-tint]").attributes("disabled")).toBeDefined();

    api.choose.mockRejectedValueOnce(new ApiError(404, "Not Found"));
    const older = block(cover({ catalogKey: "it-monitoring" }), api);
    await openGallery(older);
    await option(older, "it-database").trigger("click"); await flushPromises();
    expect(older.get("[role=alert]").text()).toBe("Tento server obrázky prostorů zatím neumí. Po aktualizaci Atlasu je půjde vybrat.");
    expect(line(older)).toContain("Vybráno automaticky: Monitoring a provoz");
    expect(older.get("[data-space-cover-upload]").attributes("disabled")).toBeDefined();
    expect(option(older, "it-backup").attributes("disabled")).toBeDefined();
  });

  // One request at a time, so the server gets the choices in the order they were made.
  it("sends a choice made while another is on its way after it, also when the first one fails", async () => {
    const api = stub();
    let answer!: (value: SpaceCover) => void;
    api.choose.mockReturnValueOnce(new Promise((resolve) => { answer = resolve; }));
    const wrapper = block(cover({ catalogKey: "it-monitoring" }), api);
    await openGallery(wrapper);
    await option(wrapper, "it-database").trigger("click");
    await option(wrapper, "it-backup").trigger("click");
    expect(api.choose.mock.calls).toEqual([["space-1", { source: "Catalog", catalogKey: "it-database" }]]);
    const first = cover({ source: "Catalog", catalogKey: "it-database" });
    answer(first); await flushPromises();
    expect(api.choose).toHaveBeenLastCalledWith("space-1", { source: "Catalog", catalogKey: "it-backup" });
    expect(wrapper.emitted("change")).toEqual([[first], [cover({ source: "Catalog", catalogKey: "it-backup" })]]);

    // The failure is the older choice's: the newer one is still sent and nothing is reported.
    let refuse!: (reason: Error) => void;
    api.choose.mockReturnValueOnce(new Promise((_resolve, reject) => { refuse = reject; }));
    await option(wrapper, "it-api").trigger("click");
    await option(wrapper, "it-analytics").trigger("click");
    refuse(new Error("Spojení se serverem se nepodařilo.")); await flushPromises();
    expect(api.choose).toHaveBeenCalledTimes(4);
    expect(api.choose).toHaveBeenLastCalledWith("space-1", { source: "Catalog", catalogKey: "it-analytics" });
    expect(wrapper.emitted("change")).toHaveLength(3);
    expect(wrapper.find("[data-space-cover-error]").exists()).toBe(false);
    expect(wrapper.get("[role=status]").text()).toBe("Obrázek je uložený.");
  });

  // Arrow keys select as they move; a row in the image's history per key press would be noise.
  it("saves a walk by keyboard once, where it rests or when focus leaves the gallery", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const api = stub();
    const wrapper = block(cover({}), api);
    await openGallery(wrapper);
    await option(wrapper, ":none").trigger("keydown", { key: "ArrowRight" });
    await option(wrapper, "it-ai-ml").trigger("click");
    await vi.advanceTimersByTimeAsync(200);
    await option(wrapper, "it-analytics").trigger("click");
    expect(line(wrapper)).toBe("Vybráno z katalogu: Analytika a reporty");
    await vi.advanceTimersByTimeAsync(399);
    expect(api.choose).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1); await flushPromises();
    expect(api.choose.mock.calls).toEqual([["space-1", { source: "Catalog", catalogKey: "it-analytics" }]]);

    // Leaving the gallery saves what the keyboard rests on at once, and its timer does not send it again.
    await option(wrapper, "it-api").trigger("click");
    await option(wrapper, "it-api").trigger("focusout", { relatedTarget: wrapper.get("[data-space-cover-upload]").element });
    await flushPromises();
    expect(api.choose).toHaveBeenCalledTimes(2);
    expect(api.choose).toHaveBeenLastCalledWith("space-1", { source: "Catalog", catalogKey: "it-api" });
    await vi.advanceTimersByTimeAsync(400); await flushPromises();
    expect(api.choose).toHaveBeenCalledTimes(2);
  });

  // Any other choice makes the server delete the uploaded file, and an arrow key selects as it moves.
  it("only shows a choice by keyboard while an uploaded image is stored, until Enter or a click confirms it", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const api = stub();
    const wrapper = block(cover({ source: "Upload", version: "a1" }), api);
    // The live region is there before its text: a status that arrives together with its text is not announced by every screen reader.
    const status = wrapper.get("[role=status]");
    const uploadShown = () => {
      expect(line(wrapper)).toBe("Vlastní obrázek.");
      expect(preview(wrapper).get("img").attributes("src")).toBe("/api/spaces/space-1/cover?v=a1");
      expect(status.text()).toBe("");
    };
    uploadShown();
    await openGallery(wrapper);
    await option(wrapper, ":none").trigger("keydown", { key: "ArrowRight" });
    await option(wrapper, "it-ai-ml").trigger("click");
    await option(wrapper, "it-analytics").trigger("click");
    expect(line(wrapper)).toBe("Vybráno z katalogu: Analytika a reporty");
    expect(preview(wrapper).get("img").attributes("src")).toContain("it-analytics.svg");
    expect(option(wrapper, "it-analytics").attributes("aria-checked")).toBe("true");
    expect(status.text()).toBe("Vlastní obrázek zůstává. Jiný obrázek potvrdíte klávesou Enter.");
    await vi.advanceTimersByTimeAsync(400);
    await option(wrapper, "it-analytics").trigger("focusout", { relatedTarget: wrapper.get("[data-space-cover-upload]").element });
    await flushPromises();
    uploadShown();

    await option(wrapper, "it-api").trigger("click");
    expect(line(wrapper)).toBe("Vybráno z katalogu: API a integrace");
    await option(wrapper, "it-api").trigger("keydown", { key: "Escape" });
    uploadShown();
    expect(api.choose).not.toHaveBeenCalled();

    await option(wrapper, "it-api").trigger("click");
    await option(wrapper, "it-api").trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(api.choose.mock.calls).toEqual([["space-1", { source: "Catalog", catalogKey: "it-api" }]]);

    // A click is a deliberate act, also on the option the keyboard has just chosen.
    await option(wrapper, "it-api").trigger("keydown", { key: "ArrowRight" });
    await option(wrapper, "it-backup").trigger("click");
    await option(wrapper, "it-backup").trigger("pointerdown");
    await option(wrapper, "it-backup").trigger("click");
    await flushPromises();
    expect(api.choose).toHaveBeenCalledTimes(2);
    expect(api.choose).toHaveBeenLastCalledWith("space-1", { source: "Catalog", catalogKey: "it-backup" });
  });

  // Tab leaves the search on the way to the group pills: the drawing it takes is a keyboard choice, not a deliberate replacement.
  it("holds the first drawing of the search that Tab takes over an uploaded image until Enter, where Enter in the search replaces it", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const api = stub();
    const wrapper = block(cover({ source: "Upload", version: "a1" }), api);
    await openGallery(wrapper);
    const search = wrapper.get("[data-space-cover-search]");
    await search.setValue("zalohy");
    await search.trigger("keydown", { key: "Tab" });
    await vi.advanceTimersByTimeAsync(1000);
    expect(api.choose).not.toHaveBeenCalled();
    expect(wrapper.get("[role=status]").text()).toBe("Vlastní obrázek zůstává. Jiný obrázek potvrdíte klávesou Enter.");

    await search.trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(api.choose.mock.calls).toEqual([["space-1", { source: "Catalog", catalogKey: "it-backup" }]]);
  });

  // Focus moves without selecting (Home, End, often an arrow), so the key can be pressed on an option that is not the
  // held one. Confirming the held one there would delete the upload for an image the person is not looking at.
  it("confirms a held choice only by a key on that option: on another option the key holds that one instead", async () => {
    const api = stub();
    const wrapper = block(cover({ source: "Upload", version: "a1" }), api);
    await openGallery(wrapper);
    // Space as a browser sends it: the key, then the click of the option it is on. Enter clicks nothing.
    const space = async (value: string) => { await option(wrapper, value).trigger("keydown", { key: " " }); await option(wrapper, value).trigger("click"); await flushPromises(); };
    const heldIs = (value: string, name: string) => {
      expect(api.choose).not.toHaveBeenCalled();
      expect(line(wrapper)).toBe(`Vybráno z katalogu: ${name}`);
      expect(preview(wrapper).get("img").attributes("src")).toContain(`${value}.svg`);
      expect(option(wrapper, value).attributes("aria-checked")).toBe("true");
      expect(wrapper.get("[role=status]").text()).toBe("Vlastní obrázek zůstává. Jiný obrázek potvrdíte klávesou Enter.");
    };
    await space("it-api");
    heldIs("it-api", "API a integrace");
    await space("it-backup");
    heldIs("it-backup", "Zálohy a obnova");
    await option(wrapper, "it-database").trigger("keydown", { key: "Enter" });
    heldIs("it-database", "Databáze a data");

    await option(wrapper, "it-database").trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(api.choose.mock.calls).toEqual([["space-1", { source: "Catalog", catalogKey: "it-database" }]]);
  });

  it("deferred without an api keeps the choice itself and reports it, the file of an upload with it", async () => {
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL: () => "blob:draft", revokeObjectURL: () => {} }));
    const wrapper = mount(SpaceCoverField, { props: { space: { ...space, id: "" }, deferred: true }, attachTo: document.body });
    mounted.push(wrapper);
    await openGallery(wrapper);
    await option(wrapper, "it-database").trigger("click"); await flushPromises();
    expect(wrapper.emitted("change")).toEqual([[cover({ source: "Catalog", catalogKey: "it-database" }), undefined]]);
    expect(line(wrapper)).toBe("Vybráno z katalogu: Databáze a data");
    const file = new File(["x"], "cover.png", { type: "image/png" });
    Object.defineProperty(wrapper.get("[data-space-cover-input]").element, "files", { value: [file] });
    await wrapper.get("[data-space-cover-input]").trigger("change"); await flushPromises();
    expect(wrapper.emitted("change")!.at(-1)).toEqual([cover({ source: "Upload", version: "draft" }), file]);
    expect(wrapper.text()).toContain("Uloží se se založením prostoru.");
  });
});
