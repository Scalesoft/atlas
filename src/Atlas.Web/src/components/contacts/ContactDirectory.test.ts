import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { toast } from "vue-sonner";
import { ApiError } from "../../api";
import { createLiveObjects } from "../../objects/liveObjects";
import type { ObjectDto, ObjectList, ObjectSummary, TrashedObject } from "../../objects/objectApi";
import ContactDirectory from "./ContactDirectory.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

const summary = (id: string, name: string, emails: string[] = []): ObjectSummary =>
  ({ id, typeId: "atlas.contact", scope: "wiki", spaceId: null, title: name, version: 1, updatedAt: "2026-10-26T10:00:00Z", data: { name, emails } });
const listOf = (items: ObjectSummary[], canCreate = true): ObjectList => ({ items, total: items.length, page: 1, pageSize: 50, canCreate });

function show(items: ObjectSummary[], canCreate = true, overrides: Record<string, unknown> = {}, uses = 2) {
  const source = {
    list: vi.fn(async () => listOf(items, canCreate)),
    create: vi.fn(async (): Promise<ObjectDto> => { throw new ApiError(409, "Duplicita", "duplicate_contact", { contactId: "c2" }); }),
    trash: vi.fn(async () => {}),
    trashList: vi.fn(async () => ({ items: [] as TrashedObject[], total: 0, page: 1, pageSize: 50 })),
    restore: vi.fn(async () => ({ version: 1, duplicate: false })),
    purge: vi.fn(async () => {}),
    ...overrides,
  };
  const get = vi.fn(async (id: string) => ({ ...summary(id, "Karel", ["karel@example.cz"]), canEdit: true }) as unknown as ObjectDto);
  const store = createLiveObjects({ get, change: vi.fn() });
  wrapper = mount(ContactDirectory, { attachTo: document.body, props: { source, store, usage: { count: vi.fn(async (id: string) => ({ objectId: id, total: uses, visible: uses, hidden: 0 })) } } });
  return { source, get };
}
const names = () => wrapper!.findAll("[data-contact-row]").map(row => row.get('[data-contact-field="name"]').text());

it("lists the directory at once and offers a new contact only to who may add one", async () => {
  const { source } = show([summary("c1", "Jana"), summary("c2", "Karel")]);
  await flushPromises();
  expect(source.list).toHaveBeenCalledWith({ type: "atlas.contact", scope: "wiki" });
  expect(names()).toEqual(["Jana", "Karel"]);
  expect(wrapper!.find("[data-contact-create]").exists()).toBe(true);
  wrapper!.unmount();
  show([summary("c1", "Jana")], false);
  await flushPromises();
  expect(wrapper!.find("[data-contact-create]").exists()).toBe(false);
  expect(wrapper!.find("[data-contact-trash]").exists()).toBe(false);
});

it("brings up the contact the directory has when a new one has its address", async () => {
  show([summary("c1", "Jana")]);
  await flushPromises();
  await wrapper!.get("[data-contact-create]").trigger("click");
  await wrapper!.get("[data-contact-name]").setValue("Karel N.");
  await wrapper!.get("[data-contact-emails]").setValue("karel@example.cz");
  await wrapper!.get("[data-contact-form]").trigger("submit");
  await vi.waitFor(() => expect(wrapper!.find("[data-contact-note]").exists()).toBe(true));
  expect(names()).toEqual(["Karel", "Jana"]);
  expect(wrapper!.get('[data-contact-row="c2"]').classes()).toContain("is-highlighted");
});

// PR2-6 (UX cycle 2): a contact written only in a page (a vizitka) is found by the picker of /kontakt but not here; „Žádný kontakt neodpovídá“ alone read as if there were none.
it("says an empty search leaves out the contacts that are only written in pages, which the picker of a page finds", async () => {
  show([summary("c1", "Jana")], true, { list: vi.fn(async (query: { q?: string }) => listOf(query.q ? [] : [summary("c1", "Jana")])) });
  await flushPromises();
  await wrapper!.get("input[data-contact-search]").setValue("Neznámý");
  await new Promise(done => setTimeout(done, 350));
  await flushPromises();
  expect(wrapper!.text()).toContain("Žádný kontakt neodpovídá");
  expect(wrapper!.text()).toContain("Adresář ukazuje jen uložené kontakty. Kontakt jen z vizitky na stránce najdete přes /kontakt v textu stránky.");
});

it("warns with the number of uses before a used contact goes to the trash", async () => {
  const { source } = show([summary("c1", "Jana"), summary("c2", "Karel")]);
  await flushPromises();
  await wrapper!.findAll("[data-contact-trash]")[0]!.trigger("click");
  await flushPromises();
  expect(document.body.textContent).toContain("Přesunout kontakt do koše?");
  expect(document.body.textContent).toContain("se používá na 2 místech");
  expect(source.trash).not.toHaveBeenCalled();
  (document.querySelector("[data-confirm-action]") as HTMLButtonElement).click();
  await flushPromises();
  expect(source.trash).toHaveBeenCalledWith("c1");
  expect(names()).toEqual(["Karel"]);
});

it("moves an unused contact to the trash without a dialog", async () => {
  const { source } = show([summary("c1", "Jana"), summary("c2", "Karel")], true, {}, 0);
  await flushPromises();
  await wrapper!.findAll("[data-contact-trash]")[0]!.trigger("click");
  await flushPromises();
  expect(document.querySelector("dialog[open]")).toBeNull();
  expect(source.trash).toHaveBeenCalledWith("c1");
  expect(names()).toEqual(["Karel"]);
});

// P7: the button that had the keyboard left with its row; the keyboard goes to the next contact, else the search, never <body>.
it("keeps the keyboard in the list when a contact goes to the trash and when Zpět brings it back", async () => {
  const success = vi.spyOn(toast, "success");
  show([summary("c1", "Jana"), summary("c2", "Karel")], true, {}, 0);
  await flushPromises();
  (wrapper!.findAll("[data-contact-trash]")[0]!.element as HTMLElement).focus();
  await wrapper!.findAll("[data-contact-trash]")[0]!.trigger("click");
  await flushPromises();
  expect(document.activeElement?.closest("[data-contact-row]")?.getAttribute("data-contact-row")).toBe("c2");
  await wrapper!.get("[data-contact-trash]").trigger("click");
  await flushPromises();
  expect(names()).toEqual([]);
  expect(document.activeElement?.hasAttribute("data-contact-search")).toBe(true);
  (success.mock.calls.at(-1)![1] as { action: { onClick: () => void } }).action.onClick();
  await flushPromises();
  expect(document.activeElement?.closest("[data-contact-row]")?.getAttribute("data-contact-row")).toBe("c2");
  success.mockRestore();
});

it("says a trashed contact is in the trash, and Zpět of the toast brings it back first", async () => {
  const success = vi.spyOn(toast, "success");
  const { source, get } = show([summary("c1", "Jana"), summary("c2", "Karel")], true, {}, 0);
  await flushPromises();
  await wrapper!.findAll("[data-contact-trash]")[0]!.trigger("click");
  await flushPromises();
  expect(success).toHaveBeenCalledWith("Kontakt Jana je v koši.", expect.objectContaining({ action: expect.objectContaining({ label: "Zpět" }) }));
  expect(names()).toEqual(["Karel"]);
  (success.mock.calls[0]![1] as { action: { onClick: () => void } }).action.onClick();
  await flushPromises();
  expect(source.restore).toHaveBeenCalledWith("c1");
  expect(get).toHaveBeenCalledWith("c1");
  expect(wrapper!.findAll("[data-contact-row]").map(row => row.attributes("data-contact-row"))).toEqual(["c1", "c2"]);
  expect(wrapper!.get('[data-contact-row="c1"]').classes()).toContain("is-highlighted");
  success.mockRestore();
});

it("goes from the search to the first contact with ↓, and between contacts with ↑ ↓ (keyboard pass)", async () => {
  show([summary("c1", "Jana"), summary("c2", "Karel")]);
  await flushPromises();
  const search = wrapper!.get("[data-contact-search]");
  (search.element as HTMLInputElement).focus();
  // Enter in the empty search takes nothing (P8); ↓ goes to the list.
  await search.trigger("keydown", { key: "Enter" });
  expect(document.activeElement).toBe(search.element);
  await search.trigger("keydown", { key: "ArrowDown" });
  expect(document.activeElement?.closest("[data-contact-row]")?.getAttribute("data-contact-row")).toBe("c1");
  document.activeElement!.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true, cancelable: true }));
  expect(document.activeElement?.closest("[data-contact-row]")?.getAttribute("data-contact-row")).toBe("c2");
  (search.element as HTMLInputElement).focus();
  await search.trigger("keydown", { key: "ArrowDown" });
  expect(document.activeElement?.closest("[data-contact-row]")?.getAttribute("data-contact-row")).toBe("c1");
});

it("preselects the first contact of a search and Enter edits it where it is; the first Esc empties the search", async () => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  try {
    show([summary("c1", "Jana"), summary("c2", "Karel")]);
    await flushPromises();
    const search = wrapper!.get<HTMLInputElement>("[data-contact-search]");
    search.element.focus();
    await search.setValue("a");
    await vi.advanceTimersByTimeAsync(300);
    await flushPromises();
    expect(wrapper!.findAll("[data-contact-row][data-first]").map(row => row.attributes("data-contact-row"))).toEqual(["c1"]);
    await search.trigger("keydown", { key: "Enter" });
    expect(wrapper!.get('[data-contact-row="c1"] [data-contact-field="name"]').classes()).toContain("is-editing");
    expect(wrapper!.find('[data-contact-row="c2"] .is-editing').exists()).toBe(false);
    await search.trigger("keydown", { key: "Escape" });
    expect(search.element.value).toBe("");
  } finally { vi.useRealTimers(); }
});

const trashedOf = (id: string, name: string, emails: string[] = []): TrashedObject =>
  ({ id, typeId: "atlas.contact", scope: "wiki", spaceId: null, title: name, version: 2, trashedAt: "2026-10-28T10:00:00Z", trashedById: "u1", trashedByName: "Jana", data: { name, emails } });

it("opens the trash, restores a contact into the directory and purges one only after asking, focus staying in the list", async () => {
  const bin = [trashedOf("t1", "Petr", ["petr@a.cz"]), trashedOf("t2", "Petr", ["petr@b.cz"]), trashedOf("t3", "Olga")];
  const { source } = show([summary("c1", "Jana")], true, {
    trashList: vi.fn(async () => ({ items: bin, total: bin.length, page: 1, pageSize: 50 })),
    restore: vi.fn(async () => ({ version: 3, duplicate: false })),
    purge: vi.fn(async () => {}),
  });
  await flushPromises();
  await wrapper!.get("[data-contact-trash-open]").trigger("click");
  await flushPromises();
  expect(source.trashList).toHaveBeenCalledWith({ type: "atlas.contact", scope: "wiki" }, expect.any(AbortSignal));
  const rows = () => wrapper!.findAll("[data-trash-row]");
  // Two Petrs are told apart by their address; who trashed and when is on every row.
  expect(rows()[0]!.text()).toContain("petr@a.cz");
  expect(rows()[0]!.text()).toContain("Jana");
  expect(document.activeElement?.closest("[data-trash-row]")?.getAttribute("data-trash-row")).toBe("t1");

  await rows()[0]!.get("[data-trash-restore]").trigger("click");
  await flushPromises();
  expect(source.restore).toHaveBeenCalledWith("t1");
  expect(document.activeElement?.closest("[data-trash-row]")?.getAttribute("data-trash-row")).toBe("t2");

  await wrapper!.get('[data-trash-row="t3"] [data-trash-purge]').trigger("click");
  expect(document.body.textContent).toContain("Smazat kontakt trvale?");
  expect(source.purge).not.toHaveBeenCalled();
  (document.querySelector("[data-confirm-action]") as HTMLButtonElement).click();
  await flushPromises();
  expect(source.purge).toHaveBeenCalledWith("t3");
  await vi.waitFor(() => expect(rows().map(row => row.attributes("data-trash-row"))).toEqual(["t2"]));

  // Back to the directory: it reloads with the restored contact and the focus returns to Koš.
  source.list.mockClear();
  await wrapper!.get("[data-contact-trash-back]").trigger("click");
  await vi.waitFor(() => expect(source.list).toHaveBeenCalled());
  await flushPromises();
  expect(document.activeElement?.hasAttribute("data-contact-trash-open")).toBe(true);
});
