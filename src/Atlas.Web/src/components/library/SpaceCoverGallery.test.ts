import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, describe, expect, it } from "vitest";
import { spaceCoverEntries } from "../../spaceCoverCatalog";
import SpaceCoverGallery from "./SpaceCoverGallery.vue";

const mounted: VueWrapper[] = [];
function gallery() {
  const wrapper = mount(SpaceCoverGallery, {
    props: { space: { id: "space-1", icon: "server", color: "teal-2" }, cover: { source: "Upload" as const, catalogKey: null }, autoKey: "it-monitoring" },
    attachTo: document.body,
  });
  mounted.push(wrapper);
  return wrapper;
}
const names = (wrapper: VueWrapper) => wrapper.findAll("[data-space-cover-options] [role=radio]").map((option) => option.text());
afterEach(() => { mounted.splice(0).forEach((wrapper) => wrapper.unmount()); });

describe("SpaceCoverGallery", () => {
  it("starts with Automaticky and Bez obrázku, lists every drawing with a lazy thumbnail and narrows by word and by group", async () => {
    const wrapper = gallery();
    expect(names(wrapper)).toEqual(["Automaticky", "Bez obrázku", ...spaceCoverEntries.map((entry) => entry.name)]);
    // One per drawing and one for the automatic pick; Bez obrázku is the pattern. None is asked for before it is near.
    const thumbnails = wrapper.findAll("[data-space-cover-options] img");
    expect(thumbnails).toHaveLength(spaceCoverEntries.length + 1);
    expect(thumbnails.filter((image) => image.attributes("loading") !== "lazy")).toEqual([]);
    expect(wrapper.get('[data-value="it-backup"] img').attributes("src")).toContain("it-backup.svg");

    // Without diacritics, over names and tags; the two leading choices are no search results.
    await wrapper.get("[data-space-cover-search]").setValue("zalohy");
    expect(names(wrapper)).toEqual(["Zálohy a obnova"]);
    await wrapper.get("[data-space-cover-search]").setValue("");
    await wrapper.get('[data-space-cover-groups] [data-value="industry"]').trigger("click"); await flushPromises();
    expect(names(wrapper)).toEqual(["Zdravotnictví"]);
  });

  it("chooses the first drawing a search found on Enter in the field, as a click would, and enters the grid with Down", async () => {
    const wrapper = gallery();
    const field = wrapper.get("[data-space-cover-search]");
    await field.trigger("keydown", { key: "Enter" });
    await field.setValue("zalohy");
    expect(wrapper.findAll("[data-first]").map((option) => option.attributes("data-value"))).toEqual(["it-backup"]);
    await field.trigger("keydown", { key: "Enter", isComposing: true });
    await field.trigger("keydown", { key: "Enter", ctrlKey: true });
    expect(wrapper.emitted("choose")).toBeUndefined();
    await field.trigger("keydown", { key: "Enter" });
    expect(wrapper.emitted("choose")).toEqual([[{ source: "Catalog", catalogKey: "it-backup" }, false]]);
    await field.trigger("keydown", { key: "ArrowDown" });
    expect(document.activeElement).toBe(wrapper.get('[data-value="it-backup"]').element);
  });

  // A name and its tags hold one form of a word; the stems the server matches a space's name with hold its beginning.
  it("finds a drawing by another form of a word and by a word only its stems hold", async () => {
    const wrapper = gallery();
    const search = async (word: string) => { await wrapper.get("[data-space-cover-search]").setValue(word); return names(wrapper); };
    expect(await search("zaloha")).toEqual(["Zálohy a obnova"]);
    expect(await search("záloha")).toEqual(["Zálohy a obnova"]);
    expect(await search("backup")).toEqual(["Zálohy a obnova"]);
    // A stem of at most three characters is a whole word, as for the server: "pr" (Marketing) does not begin "provoz".
    const provoz = await search("provoz");
    expect(provoz).toContain("Monitoring a provoz");
    expect(provoz).not.toContain("Marketing");
    expect(await search("xyzqq")).toEqual([]);
  });

  // The block saves a choice at once, so it must know a walk by arrow keys from a click: each arrow selects.
  it("reports each choice with how it was made, and when the keyboard leaves the grid", async () => {
    const wrapper = gallery();
    const option = (value: string) => wrapper.get(`[data-space-cover-options] [data-value="${value}"]`);
    await option("it-database").trigger("click");
    await option(":auto").trigger("click");
    await option(":auto").trigger("keydown", { key: "ArrowRight" });
    await option(":none").trigger("click");
    expect(wrapper.emitted("choose")).toEqual([
      [{ source: "Catalog", catalogKey: "it-database" }, false],
      [{ source: "Auto" }, false],
      [{ source: "None" }, true],
    ]);
    await option(":none").trigger("focusout", { relatedTarget: option("it-backup").element });
    expect(wrapper.emitted("settled")).toBeUndefined();
    await option("it-backup").trigger("focusout", { relatedTarget: wrapper.get("[data-space-cover-search]").element });
    expect(wrapper.emitted("settled")).toHaveLength(1);
  });

  // With an uploaded image stored the block saves a choice by keyboard only after one of these two keys, pressed on
  // that choice: Home, End and often an arrow move focus to an option without checking it.
  it("reports Enter and Space with the option they were pressed on, and does not choose the checked option again when Space clicks it", async () => {
    const wrapper = gallery();
    await wrapper.setProps({ cover: { source: "Catalog", catalogKey: "it-backup" } });
    const option = wrapper.get('[data-space-cover-options] [data-value="it-backup"]');
    await option.trigger("keydown", { key: "ArrowRight" });
    expect(wrapper.emitted("confirm")).toBeUndefined();
    await option.trigger("keydown", { key: "Enter" });
    await option.trigger("keydown", { key: " " });
    await option.trigger("click");
    await wrapper.get('[data-space-cover-options] [data-value=":none"]').trigger("keydown", { key: "Enter" });
    // A key that is held down repeats: that is no second press.
    await option.trigger("keydown", { key: "Enter", repeat: true });
    expect(wrapper.emitted("confirm")).toEqual([[{ source: "Catalog", catalogKey: "it-backup" }], [{ source: "Catalog", catalogKey: "it-backup" }], [{ source: "None" }]]);
    expect(wrapper.emitted("choose")).toBeUndefined();
  });
});
