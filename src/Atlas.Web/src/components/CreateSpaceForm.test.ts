import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import CreateSpaceForm from "./CreateSpaceForm.vue";
import { adminApi } from "../administration";
import { post } from "../api";
import { spaceCoverApi } from "../spaceCover";
import { appearanceIcons, quickAppearanceIconIds } from "./settings/appearanceIcons";

vi.mock("../administration", async (original) => ({
  ...await original<typeof import("../administration")>(),
  adminApi: { groups: vi.fn() },
}));
vi.mock("../api", async (original) => ({
  ...await original<typeof import("../api")>(), post: vi.fn(),
}));
vi.mock("../spaceCover", async (original) => {
  const actual = await original<typeof import("../spaceCover")>();
  return { ...actual, spaceCoverApi: { ...actual.spaceCoverApi, match: vi.fn(), choose: vi.fn(), upload: vi.fn(), setTinted: vi.fn() } };
});
const group = { id: "group-1", name: "Dokumentace", version: "1", source: "local" as const };
const created = { id: "space-1", name: "Nový tým", icon: "book-open", color: "blue", role: "Admin" };
const mounted: ReturnType<typeof mount>[] = [];
async function form() {
  const wrapper = mount(CreateSpaceForm, { attachTo: document.body });
  mounted.push(wrapper);
  await flushPromises();
  await wrapper.get('input[name="name"]').setValue("Nový tým");
  return wrapper;
}
beforeEach(() => { vi.clearAllMocks(); vi.mocked(adminApi.groups).mockResolvedValue([group]); vi.mocked(post).mockResolvedValue(created); vi.mocked(spaceCoverApi.match).mockResolvedValue({ catalogKey: null }); });
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers(); });

describe("new-space optional access", () => {
  it("does not create while another workspace action is pending", async () => {
    const wrapper = await form();
    await wrapper.setProps({ disabled: true });
    await wrapper.get("form").trigger("submit");
    expect(post).not.toHaveBeenCalled();
  });
  it("releases the workspace busy guard before handing over the created space", async () => {
    const events: string[] = [];
    const wrapper = mount(CreateSpaceForm, { props: { onBusy: value => events.push(`busy:${value}`), onCreated: () => events.push("created") } });
    mounted.push(wrapper);
    await wrapper.get('input[name="name"]').setValue("Nový tým");
    await wrapper.get("form").trigger("submit"); await flushPromises();
    expect(events).toEqual(["busy:true", "busy:false", "created"]);
  });
  it("keeps the default private and emits the created space", async () => {
    const wrapper = await form();
    expect(wrapper.text()).toContain("pouze vám a správcům wiki");
    await wrapper.get("form").trigger("submit"); await flushPromises();
    expect(post).toHaveBeenCalledWith("/spaces", { name: "Nový tým", description: "", icon: "book-open", color: "blue", pinPolicy: "None", groups: [] });
    expect(wrapper.emitted("created")).toEqual([[created]]);
  });
  it("submits selected group roles in the same create request and keeps them on failure", async () => {
    vi.mocked(post).mockRejectedValueOnce(new Error("Uložení selhalo"));
    const wrapper = await form();
    await wrapper.get('[data-action="add-group"]').trigger("click");
    await wrapper.get(`[data-group-picker] [data-value="${group.id}"]`).trigger("click"); await flushPromises();
    await wrapper.get('[data-group-role="1"] [data-value="Writer"]').trigger("click"); await flushPromises();
    await wrapper.get("form").trigger("submit"); await flushPromises();
    expect(post).toHaveBeenCalledWith("/spaces", expect.objectContaining({ groups: [{ groupId: group.id, role: "Writer" }] }));
    expect(wrapper.get('[role="alert"]').text()).toContain("Uložení selhalo");
    expect((wrapper.get('input[name="name"]').element as HTMLInputElement).value).toBe("Nový tým");
    expect(wrapper.get('[data-group-role="1"] [data-value="Writer"]').attributes("aria-checked")).toBe("true");
    expect(wrapper.emitted("created")).toBeUndefined();
  });
  it("blocks repeat submit and dismissal while creation is pending", async () => {
    let finish!: (value: typeof created) => void;
    vi.mocked(post).mockReturnValue(new Promise(resolve => { finish = resolve; }));
    const wrapper = await form();
    await wrapper.get("form").trigger("submit");
    await wrapper.get("form").trigger("submit");
    expect(post).toHaveBeenCalledTimes(1);
    expect(wrapper.get("[data-space-cancel]").attributes("disabled")).toBeDefined();
    finish(created); await flushPromises();
    expect(wrapper.emitted("busy")).toEqual([[true], [false]]);
  });
  it("focuses the name and refuses to create without one", async () => {
    const wrapper = mount(CreateSpaceForm, { attachTo: document.body });
    mounted.push(wrapper);
    await flushPromises();
    expect(document.activeElement).toBe(wrapper.get('input[name="name"]').element);
    await wrapper.get("[data-space-create]").trigger("click"); await wrapper.get("form").trigger("submit"); await flushPromises();
    expect(post).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain("Zadejte název prostoru.");
    // The browser's own bubble would show a second message for the same mistake.
    expect(wrapper.get("form").attributes("novalidate")).toBeDefined();
    await wrapper.get('input[name="name"]').setValue("Nový tým"); await flushPromises();
    expect(wrapper.emitted("dirty")?.at(-1)).toEqual([true]);
    await wrapper.get("form").trigger("submit"); await flushPromises();
    expect(post).toHaveBeenCalledWith("/spaces", { name: "Nový tým", description: "", icon: "book-open", color: "blue", pinPolicy: "None", groups: [] });
  });
  it("offers the fields of Obecné and creates the space with them", async () => {
    const wrapper = await form();
    await wrapper.get("[data-space-description]").setValue(" Runbooky ");
    await wrapper.get('[data-space-pin-policy] [data-value="Default"]').trigger("click"); await flushPromises();
    await wrapper.get("form").trigger("submit"); await flushPromises();
    expect(post).toHaveBeenCalledWith("/spaces", expect.objectContaining({ description: "Runbooky", pinPolicy: "Default" }));
  });
  it("keeps the chosen image until the space exists, then saves it, and reports a refused image without undoing the space", async () => {
    const saved = { source: "None" as const, catalogKey: null, version: null, tinted: true };
    vi.mocked(spaceCoverApi.choose).mockResolvedValueOnce(saved);
    const wrapper = await form();
    expect(wrapper.text()).toContain("Uloží se se založením prostoru.");
    await wrapper.get("[data-space-cover-pick]").trigger("click"); await flushPromises();
    await wrapper.get('[data-space-cover] [data-value=":none"]').trigger("click"); await flushPromises();
    expect(spaceCoverApi.choose).not.toHaveBeenCalled();
    await wrapper.get("form").trigger("submit"); await flushPromises();
    expect(spaceCoverApi.choose).toHaveBeenCalledWith(created.id, { source: "None", catalogKey: undefined });
    expect(wrapper.emitted("created")).toEqual([[{ ...created, cover: saved }]]);

    vi.mocked(spaceCoverApi.choose).mockRejectedValueOnce(new Error("Obrázek odmítnut"));
    const again = await form();
    await again.get("[data-space-cover-pick]").trigger("click"); await flushPromises();
    await again.get('[data-space-cover] [data-value=":none"]').trigger("click"); await flushPromises();
    await again.get("form").trigger("submit"); await flushPromises();
    const [space, warning] = again.emitted<[unknown, string]>("created")![0]!;
    expect(space).toEqual(created);
    expect(warning).toContain("Obrázek odmítnut");
  });
  it("cancels without sending anything", async () => {
    const wrapper = await form();
    await wrapper.get("[data-space-cancel]").trigger("click");
    expect(wrapper.emitted("cancel")).toHaveLength(1);
    expect(post).not.toHaveBeenCalled();
  });
  it("offers the whole icon set with search and the colour palette, never a plain select", async () => {
    const wrapper = await form();
    const options = () => wrapper.findAll("[data-appearance-results] [role=option]");
    expect(wrapper.find("select").exists()).toBe(false);
    // Common icons and ten colours first; the whole set and the palette only when asked for.
    expect(wrapper.findAll("[data-appearance-icons] [role=radio]")).toHaveLength(quickAppearanceIconIds.length);
    expect(wrapper.findAll("[data-appearance-quick-colors] [role=radio]")).toHaveLength(10);
    await wrapper.get("[data-appearance-more-icons]").trigger("click"); await vi.dynamicImportSettled(); await flushPromises();
    expect(options()).toHaveLength(appearanceIcons.length);
    await wrapper.get("[data-appearance-more-colors]").trigger("click"); await flushPromises();
    expect(wrapper.findAll("[data-appearance-colors] [role=radio]")).toHaveLength(60);
    await wrapper.get("[data-appearance-search]").setValue("server");
    expect(options()[0]!.attributes("data-value")).toBe("server");
    await options()[0]!.trigger("click");
    await wrapper.get('[data-appearance-colors] [data-value="teal-4"]').trigger("click"); await flushPromises();
    expect(wrapper.get("[data-appearance-preview]").text()).toContain("Server · Tyrkysová, tmavá");
    await wrapper.get("form").trigger("submit"); await flushPromises();
    expect(post).toHaveBeenLastCalledWith("/spaces", expect.objectContaining({ icon: "server", color: "teal-4" }));
    // An icon of the icon pack is found by an English word and sent like any other.
    await wrapper.get("[data-appearance-search]").setValue("rocket");
    await wrapper.get('[data-appearance-results] [data-value="rocket"]').trigger("click");
    await wrapper.get("form").trigger("submit"); await flushPromises();
    expect(post).toHaveBeenLastCalledWith("/spaces", expect.objectContaining({ icon: "rocket", color: "teal-4" }));
  });
  it("previews the card with the drawing picked for the typed name, and creates the space when the pick fails", async () => {
    // Only the debounce is faked; flushPromises keeps its own scheduler.
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    vi.mocked(spaceCoverApi.match).mockResolvedValue({ catalogKey: "it-backup" });
    const wrapper = await form();
    const cover = () => wrapper.get("[data-space-cover] .space-cover");
    expect(cover().attributes("data-cover")).toBe("pattern");
    await vi.advanceTimersByTimeAsync(300); await flushPromises();
    expect(spaceCoverApi.match).toHaveBeenCalledWith({ name: "Nový tým", description: "", icon: "book-open" }, expect.any(AbortSignal));
    expect(cover().get("img").attributes("src")).toContain("it-backup.svg");
    // The preview is a convenience: without an answer the card keeps its pattern and nothing waits for it.
    vi.mocked(spaceCoverApi.match).mockRejectedValue(new Error("Spojení selhalo"));
    await wrapper.get('input[name="name"]').setValue("Zálohy");
    await vi.advanceTimersByTimeAsync(300); await flushPromises();
    expect(cover().attributes("data-cover")).toBe("pattern");
    await wrapper.get("form").trigger("submit"); await flushPromises();
    expect(wrapper.emitted("created")).toEqual([[created]]);
  });
  it("offers retry when the group directory fails without clearing the name", async () => {
    vi.mocked(adminApi.groups).mockRejectedValueOnce(new Error("Skupiny nejsou dostupné"));
    const wrapper = await form();
    expect(wrapper.get('[role="alert"]').text()).toContain("Skupiny nejsou dostupné");
    await wrapper.get("[data-groups-error] button").trigger("click"); await flushPromises();
    expect(adminApi.groups).toHaveBeenCalledTimes(2);
    expect((wrapper.get('input[name="name"]').element as HTMLInputElement).value).toBe("Nový tým");
  });
});
