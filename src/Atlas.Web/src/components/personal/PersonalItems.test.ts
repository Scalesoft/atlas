import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { ApiError } from "../../api";
import { drafts, hasUnsavedWork } from "../../lib/unsavedWork";
import type { ObjectDto, TrashedObject } from "../../objects/objectApi";
import type { PersonalApi, PersonalItem } from "../../personal/personalApi";
import BoardTextEditor from "../boards/text/BoardTextEditor.vue";
import NoteCard from "./NoteCard.vue";
import PersonalItems from "./PersonalItems.vue";

const notice = vi.hoisted(() => ({ success: vi.fn() }));
vi.mock("../../composables/useToast", () => ({ useToast: () => ({ success: notice.success, error: vi.fn(), warning: vi.fn(), dismissAll: vi.fn() }) }));

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; drafts.clear(); });

const item = (id: string, title: string, extra: Partial<PersonalItem> = {}): PersonalItem => ({
  id, typeId: "atlas.note", title, updatedAt: "2026-10-29T09:00:00Z", version: 1, canEdit: true, shares: 0, ownerName: null, level: null, expiresAt: null,
  excerpt: "", ...extra,
});

const trashedRow = (id: string, title: string): TrashedObject => ({
  id, typeId: "atlas.note", scope: "private", spaceId: null, title, version: 1, data: null, trashedAt: "2026-10-29T08:00:00Z", trashedById: "me", trashedByName: "Já",
});

function show(mode: "mine" | "shared", items: PersonalItem[]) {
  const source = {
    mine: vi.fn(async () => items),
    shared: vi.fn(async () => items),
    note: vi.fn(async (id: string) => ({ version: 3, canEdit: true, data: { title: `Celá ${id}`, text: `Celý text ${id}` } })),
    createNote: vi.fn(async (title: string) => ({ id: "n9", typeId: "atlas.note", title, version: 1, updatedAt: "2026-10-29T10:00:00Z" }) as unknown as ObjectDto),
    changeNote: vi.fn(async () => ({ version: 2, duplicate: false })),
    trash: vi.fn(async () => {}),
    trashList: vi.fn(async () => ({ items: [trashedRow("t1", "Stará poznámka"), trashedRow("t2", "Druhá poznámka")], total: 2, page: 1, pageSize: 50 })),
    restore: vi.fn(async () => ({ version: 2, duplicate: false })),
    purge: vi.fn(async () => {}),
  } satisfies PersonalApi;
  wrapper = mount(PersonalItems, { attachTo: document.body, props: { mode, source } });
  return source;
}
const retryUnsaved = () => wrapper!.findAll("[data-personal-unsaved] button").at(-1)!.trigger("click");
const titles = () => wrapper!.findAll("[data-personal-row]").map(row => row.attributes("data-personal-row"));

it("lists every note as the same row with its excerpt and shares, and reads the whole note only when it is opened", async () => {
  const source = show("mine", [item("n1", "Porada", { shares: 2, excerpt: "rozpočet a termíny" }), item("n2", "Nákup")]);
  await flushPromises();
  expect(titles()).toEqual(["n1", "n2"]);
  expect(wrapper!.findAll("[data-personal-row] .personal-item-icon")).toHaveLength(2);
  expect(wrapper!.get('[data-personal-row="n1"]').text()).toContain("rozpočet a termíny");
  expect(wrapper!.get('[data-personal-row="n1"]').text()).toContain("Sdíleno: 2");
  // No editor in the list: an empty note offers no "Napsat text" until it is opened.
  expect(wrapper!.find("[data-note-card]").exists()).toBe(false);
  expect(source.note).not.toHaveBeenCalled();

  let arrive: () => void = () => {};
  source.note.mockImplementationOnce(async (id: string) => {
    await new Promise<void>(resolve => { arrive = resolve; });
    return { version: 3, canEdit: true, data: { title: `Celá ${id}`, text: `Celý text ${id}` } };
  });
  await wrapper!.get('[data-personal-row="n1"] [data-personal-open]').trigger("click");
  expect(wrapper!.get('[data-personal-row="n1"] [data-personal-open]').attributes("aria-expanded")).toBe("true");
  // A sized skeleton while the whole note loads.
  expect(wrapper!.get('[data-personal-row="n1"]').text()).toContain("Načítání poznámky");
  arrive();
  await flushPromises();
  expect(source.note).toHaveBeenCalledWith("n1", expect.any(AbortSignal));
  expect(wrapper!.find('[data-personal-row="n1"] [data-note-card]').exists()).toBe(true);
  await wrapper!.get('[data-personal-row="n1"] [data-personal-open]').trigger("click");
  expect(wrapper!.get('[data-personal-row="n1"] [data-personal-open]').attributes("aria-expanded")).toBe("false");
});

it("creates a note from a title revealed under the toolbar and opens it ready for its text", async () => {
  const source = show("mine", []);
  await flushPromises();
  expect(wrapper!.text()).toContain("Můj prostor je zatím prázdný");
  // Nothing to search yet: the search waits for the first note.
  expect(wrapper!.find("input[data-personal-search]").exists()).toBe(false);
  expect(wrapper!.find("[data-personal-new-board]").exists()).toBe(false);
  await wrapper!.get("[data-personal-new-note]").trigger("click");
  await wrapper!.get("input[data-personal-title]").setValue("Nákup");
  await wrapper!.get("form").trigger("submit");
  await flushPromises();
  expect(source.createNote).toHaveBeenCalledWith("Nákup");
  expect(titles()).toEqual(["n9"]);
  expect(wrapper!.get('[data-personal-row="n9"] [data-personal-open]').attributes("aria-expanded")).toBe("true");
  expect(source.note).not.toHaveBeenCalled();
  expect(wrapper!.find('[data-personal-row="n9"] [data-note-card]').exists()).toBe(true);
  expect(wrapper!.find("input[data-personal-search]").exists()).toBe(true);
});

it("shows who shares a note and my level, offers no share or trash, and Enter in the search opens the first match", async () => {
  const source = show("shared", [item("n2", "Plán konference", { ownerName: "Petr Svoboda", level: "read", canEdit: false }),
    item("n1", "Runbook", { ownerName: "Jana", level: "write", expiresAt: "2026-11-30T23:00:00Z", excerpt: "obnova zálohy" })]);
  await flushPromises();
  expect(wrapper!.get('[data-personal-row="n2"]').text()).toContain("Sdílí Petr Svoboda · Čtete");
  expect(wrapper!.get('[data-personal-row="n1"]').text()).toContain("Upravujete");
  expect(wrapper!.find("[data-personal-share]").exists()).toBe(false);
  expect(wrapper!.find("[data-personal-trash]").exists()).toBe(false);
  // The search finds the excerpt too.
  await wrapper!.get("input[data-personal-search]").setValue("záloh");
  expect(titles()).toEqual(["n1"]);
  await wrapper!.get("input[data-personal-search]").trigger("keydown", { key: "Enter" });
  await flushPromises();
  expect(source.note).toHaveBeenCalledWith("n1", expect.any(AbortSignal));
  expect(wrapper!.get('[data-personal-row="n1"] [data-personal-open]').attributes("aria-expanded")).toBe("true");
});

it("Enter in the search opens the first note and never closes one that is open already (P8)", async () => {
  const source = show("shared", [item("n2", "Plán konference", { ownerName: "Petr Svoboda", level: "read", canEdit: false })]);
  source.note.mockImplementation(async (id: string) => ({ version: 3, canEdit: false, data: { title: `Celá ${id}`, text: "Text" } }));
  await flushPromises();
  const search = wrapper!.get("input[data-personal-search]");
  const open = () => wrapper!.get('[data-personal-row="n2"] [data-personal-open]').attributes("aria-expanded");
  await search.setValue("plán");
  await search.trigger("keydown", { key: "Enter" });
  await flushPromises();
  expect(open()).toBe("true");
  await search.setValue("plán k");
  await search.trigger("keydown", { key: "Enter" });
  await flushPromises();
  expect(open()).toBe("true");
});

it("hides the search on an empty Sdíleno se mnou", async () => {
  show("shared", []);
  await flushPromises();
  expect(wrapper!.text()).toContain("Nikdo s vámi zatím nic nesdílel");
  expect(wrapper!.find("input[data-personal-search]").exists()).toBe(false);
});

it("opens the trash of Můj prostor, restores a note with its shares and reloads the list on the way back", async () => {
  const source = show("mine", [item("n1", "Porada")]);
  await flushPromises();
  await wrapper!.get("[data-personal-trash-open]").trigger("click");
  await flushPromises();
  expect(wrapper!.get('[data-trash-row="t1"]').text()).toContain("do koše");
  expect(document.activeElement?.getAttribute("aria-label")).toBe("Obnovit: Stará poznámka");
  await wrapper!.get('[data-trash-row="t1"] [data-trash-restore]').trigger("click");
  await flushPromises();
  // By id on the one object trash; the focus goes on to the next row.
  expect(source.restore).toHaveBeenCalledWith("t1");
  expect(wrapper!.find('[data-trash-row="t1"]').exists()).toBe(false);
  expect(document.activeElement?.getAttribute("aria-label")).toBe("Obnovit: Druhá poznámka");
  await wrapper!.get("[data-personal-trash-back]").trigger("click");
  await flushPromises();
  expect(source.mine).toHaveBeenCalledTimes(2);
  expect(titles()).toEqual(["n1"]);
});

it("deletes a note permanently from the trash only after the confirmation", async () => {
  const source = show("mine", []);
  await flushPromises();
  await wrapper!.get("[data-personal-trash-open]").trigger("click");
  await flushPromises();
  await wrapper!.get('[data-trash-row="t2"] [data-trash-purge]').trigger("click");
  await flushPromises();
  expect(source.purge).not.toHaveBeenCalled();
  expect(document.body.textContent).toContain("přestane být sdílená");
  (document.querySelector("[data-confirm-action]") as HTMLButtonElement).click();
  await flushPromises();
  expect(source.purge).toHaveBeenCalledWith("t2");
  expect(wrapper!.find('[data-trash-row="t2"]').exists()).toBe(false);
  expect(source.restore).not.toHaveBeenCalled();
});

it("keeps a refused change in the note with the reason and a retry that saves it all (PER2-2)", async () => {
  const source = show("mine", [item("n1", "Porada")]);
  await flushPromises();
  const open = () => wrapper!.get('[data-personal-row="n1"] [data-personal-open]');
  await open().trigger("click");
  await flushPromises();
  source.changeNote.mockRejectedValueOnce(new Error("Text poznámky smí mít nejvýše 100000 znaků a žádné řídicí znaky."));
  const card = wrapper!.getComponent(NoteCard);
  card.vm.$emit("change", { text: "řeřicha".repeat(3) });
  await flushPromises();
  expect(card.props("note")).toEqual({ title: "Celá n1", text: "řeřicha".repeat(3) });
  const unsaved = wrapper!.get("[data-personal-unsaved]");
  expect(unsaved.text()).toContain("Text poznámky smí mít nejvýše 100000 znaků");
  expect(unsaved.text()).toContain("Text zůstává v poznámce");

  // A title changed meanwhile goes with the refused text.
  card.vm.$emit("change", { title: "Porada 2" });
  await flushPromises();
  expect(source.changeNote).toHaveBeenLastCalledWith("n1", { text: "řeřicha".repeat(3), title: "Porada 2" }, 3);
  expect(wrapper!.find("[data-personal-unsaved]").exists()).toBe(false);

  source.changeNote.mockRejectedValueOnce(new Error("Spojení selhalo."));
  card.vm.$emit("change", { text: "koncept" });
  await flushPromises();
  // Closed and opened again, the note still shows the draft; Uložit znovu sends it.
  await open().trigger("click");
  await open().trigger("click");
  await flushPromises();
  expect(wrapper!.getComponent(NoteCard).props("note").text).toBe("koncept");
  await retryUnsaved();
  await flushPromises();
  // With the version it was written on (the title's save answered 2), not the one read when the note opened again.
  expect(source.changeNote).toHaveBeenLastCalledWith("n1", { text: "koncept" }, 2);
  expect(wrapper!.find("[data-personal-unsaved]").exists()).toBe(false);
});

it("saves with the version it read, and keeps the text when someone else changed the note meanwhile (PER-A-1)", async () => {
  const source = show("shared", [item("n1", "Runbook", { ownerName: "Jana", level: "write" })]);
  await flushPromises();
  await wrapper!.get('[data-personal-row="n1"] [data-personal-open]').trigger("click");
  await flushPromises();
  const card = wrapper!.getComponent(NoteCard);
  card.vm.$emit("change", { text: "první" });
  await flushPromises();
  // The version of the whole note read on opening, then the one the save answered.
  expect(source.changeNote).toHaveBeenLastCalledWith("n1", { text: "první" }, 3);

  source.changeNote.mockRejectedValueOnce(new ApiError(409, "Objekt mezitím změnil někdo jiný; načtěte ho znovu.", "version_conflict"));
  source.note.mockResolvedValueOnce({ version: 7, canEdit: true, data: { title: "Runbook", text: "Janin text" } });
  card.vm.$emit("change", { text: "můj text" });
  await flushPromises();
  expect(source.changeNote).toHaveBeenLastCalledWith("n1", { text: "můj text" }, 2);
  expect(card.props("note").text).toBe("můj text");
  expect(wrapper!.get("[data-personal-unsaved]").text()).toContain("někdo jiný");
  // Saved again on purpose: over the version the other change made.
  await retryUnsaved();
  await flushPromises();
  expect(source.changeNote).toHaveBeenLastCalledWith("n1", { text: "můj text" }, 7);
  expect(wrapper!.find("[data-personal-unsaved]").exists()).toBe(false);
});

it("sends nothing over another's change after a conflict until the reader chooses, and can take the current note instead", async () => {
  const source = show("shared", [item("n1", "Runbook", { ownerName: "Jana", level: "write" })]);
  await flushPromises();
  await wrapper!.get('[data-personal-row="n1"] [data-personal-open]').trigger("click");
  await flushPromises();
  const card = wrapper!.getComponent(NoteCard);
  let refuse!: () => void;
  source.changeNote.mockImplementationOnce(() => new Promise((_, reject) => { refuse = () => reject(new ApiError(409, "Konflikt", "version_conflict")); }));
  source.note.mockResolvedValueOnce({ version: 7, canEdit: true, data: { title: "Runbook", text: "Janin text" } });
  card.vm.$emit("change", { text: "můj text" });
  // A change waiting behind the one refused, and one typed after the conflict shows: both join the draft, neither is sent.
  card.vm.$emit("change", { title: "Můj titulek" });
  await flushPromises();
  refuse();
  await flushPromises();
  card.vm.$emit("change", { text: "můj text 2" });
  await flushPromises();
  expect(source.changeNote).toHaveBeenCalledTimes(1);
  expect(wrapper!.get("[data-personal-unsaved]").text()).toContain("někdo jiný");
  expect(drafts.get("personal-note:n1")).toMatchObject({ fields: { text: "můj text 2", title: "Můj titulek" }, version: 3, theirs: 7 });

  source.note.mockResolvedValueOnce({ version: 8, canEdit: true, data: { title: "Runbook", text: "Janin text" } });
  await wrapper!.get("[data-personal-take-current]").trigger("click");
  await flushPromises();
  expect(source.changeNote).toHaveBeenCalledTimes(1);
  expect(wrapper!.getComponent(NoteCard).props("note")).toEqual({ title: "Runbook", text: "Janin text" });
  expect(wrapper!.find("[data-personal-unsaved]").exists()).toBe(false);
  expect(hasUnsavedWork()).toBe(false);
  wrapper!.getComponent(NoteCard).vm.$emit("change", { text: "nad Janiným" });
  await flushPromises();
  expect(source.changeNote).toHaveBeenLastCalledWith("n1", { text: "nad Janiným" }, 8);
});

it("retries a change refused by the network over the version it was written on, so another's change meanwhile is a conflict", async () => {
  const source = show("shared", [item("n1", "Runbook", { ownerName: "Jana", level: "write" })]);
  await flushPromises();
  const open = () => wrapper!.get('[data-personal-row="n1"] [data-personal-open]');
  await open().trigger("click");
  await flushPromises();
  source.changeNote.mockRejectedValueOnce(new Error("Spojení selhalo."));
  wrapper!.getComponent(NoteCard).vm.$emit("change", { text: "offline" });
  await flushPromises();
  // Jana saved meanwhile: the note opens again at version 5.
  source.note.mockResolvedValueOnce({ version: 5, canEdit: true, data: { title: "Runbook", text: "Janin text" } });
  await open().trigger("click");
  await open().trigger("click");
  await flushPromises();
  // The server refuses version 3, which Jana's save replaced.
  source.changeNote.mockRejectedValueOnce(new ApiError(409, "Konflikt", "version_conflict"));
  await retryUnsaved();
  await flushPromises();
  expect(source.changeNote).toHaveBeenLastCalledWith("n1", { text: "offline" }, 3);
  expect(wrapper!.get("[data-personal-unsaved]").text()).toContain("někdo jiný");
});

it("never takes an older version from a read or a reload that overlapped the reader's own save", async () => {
  const source = show("mine", [item("n1", "Porada")]);
  await flushPromises();
  const open = () => wrapper!.get('[data-personal-row="n1"] [data-personal-open]');
  await open().trigger("click");
  await flushPromises();
  let accept!: () => void;
  source.changeNote.mockImplementationOnce(() => new Promise(resolve => { accept = () => resolve({ version: 4, duplicate: false }); }));
  wrapper!.getComponent(NoteCard).vm.$emit("change", { text: "můj" });
  await flushPromises();
  // Opened again and the list reloaded while the save is in the air; both answer the note as it was before it (version 3, 1).
  let arrive!: () => void;
  source.note.mockImplementationOnce(async () => {
    await new Promise<void>(resolve => { arrive = resolve; });
    return { version: 3, canEdit: true, data: { title: "Porada", text: "starý" } };
  });
  source.mine.mockResolvedValueOnce([item("n1", "Porada")]);
  await open().trigger("click");
  await open().trigger("click");
  await wrapper!.setProps({ refreshKey: 1 });
  await flushPromises();
  // The read waits for the save, so it answers what was saved.
  expect(source.note).toHaveBeenCalledTimes(1);
  accept();
  await flushPromises();
  expect(source.note).toHaveBeenCalledTimes(2);
  arrive();
  await flushPromises();
  wrapper!.getComponent(NoteCard).vm.$emit("change", { text: "další" });
  await flushPromises();
  expect(source.changeNote).toHaveBeenLastCalledWith("n1", { text: "další" }, 4);
});

it("keeps a change refused after the list went away, and drops the kept draft once a later save goes through", async () => {
  let source = show("mine", [item("n1", "Porada")]);
  await flushPromises();
  await wrapper!.get('[data-personal-row="n1"] [data-personal-open]').trigger("click");
  await flushPromises();
  let refuse!: () => void;
  source.changeNote.mockImplementationOnce(() => new Promise((_, reject) => { refuse = () => reject(new Error("Spojení selhalo.")); }));
  wrapper!.getComponent(NoteCard).vm.$emit("change", { text: "na cestě" });
  await flushPromises();
  wrapper!.unmount();
  refuse();
  await flushPromises();
  expect(drafts.get("personal-note:n1")).toMatchObject({ fields: { text: "na cestě" } });

  source = show("mine", [item("n1", "Porada")]);
  await flushPromises();
  await wrapper!.get('[data-personal-row="n1"] [data-personal-open]').trigger("click");
  await flushPromises();
  expect(wrapper!.getComponent(NoteCard).props("note").text).toBe("na cestě");
  let accept!: () => void;
  source.changeNote.mockImplementationOnce(() => new Promise(resolve => { accept = () => resolve({ version: 4, duplicate: false }); }));
  wrapper!.getComponent(NoteCard).vm.$emit("change", { title: "Porada 2" });
  await flushPromises();
  wrapper!.unmount();
  accept();
  await flushPromises();
  expect(source.changeNote).toHaveBeenLastCalledWith("n1", { text: "na cestě", title: "Porada 2" }, 3);
  expect(drafts.size).toBe(0);
  expect(hasUnsavedWork()).toBe(false);
});

it("keeps a refused change when the list goes away and shows it again when the note is opened (UX-P-6)", async () => {
  let source = show("mine", [item("n1", "Porada")]);
  await flushPromises();
  await wrapper!.get('[data-personal-row="n1"] [data-personal-open]').trigger("click");
  await flushPromises();
  source.changeNote.mockRejectedValueOnce(new Error("Spojení selhalo."));
  wrapper!.getComponent(NoteCard).vm.$emit("change", { text: "rozepsáno" });
  await flushPromises();
  expect(hasUnsavedWork()).toBe(true);
  wrapper!.unmount();

  source = show("mine", [item("n1", "Porada")]);
  await flushPromises();
  await wrapper!.get('[data-personal-row="n1"] [data-personal-open]').trigger("click");
  await flushPromises();
  expect(wrapper!.getComponent(NoteCard).props("note").text).toBe("rozepsáno");
  await retryUnsaved();
  await flushPromises();
  expect(source.changeNote).toHaveBeenLastCalledWith("n1", { text: "rozepsáno" }, 3);
  expect(hasUnsavedWork()).toBe(false);
});

it("puts the keyboard in an opened note, on the row of a read-only one, and back on the row when it closes (UX2-2)", async () => {
  const source = show("shared", [item("n1", "Runbook", { ownerName: "Jana", level: "write" }), item("n2", "Plán", { ownerName: "Petr", level: "read", canEdit: false })]);
  await flushPromises();
  const open = (id: string) => wrapper!.get(`[data-personal-row="${id}"] [data-personal-open]`);
  await open("n1").trigger("click");
  await flushPromises();
  expect(wrapper!.getComponent(NoteCard).props("startEditing")).toBe(true);
  // The note's own limit, not a board item's 4000 (UX3-2).
  expect(wrapper!.getComponent(BoardTextEditor).props("max")).toBe(100_000);
  await open("n1").trigger("click");
  expect(document.activeElement).toBe(open("n1").element);

  source.note.mockResolvedValueOnce({ version: 1, canEdit: false, data: { title: "Plán", text: "jen ke čtení" } });
  (document.activeElement as HTMLElement).blur();
  await open("n2").trigger("click");
  await flushPromises();
  expect(wrapper!.getComponent(NoteCard).props("startEditing")).toBe(false);
  expect(document.activeElement).toBe(open("n2").element);
});

it("moves a note to the trash at once with a „Zpět“ toast and puts the keyboard on the next row (P6, P7)", async () => {
  const source = show("mine", [item("n1", "Porada"), item("n2", "Nákup")]);
  await flushPromises();
  await wrapper!.get('[data-personal-row="n1"] [data-personal-trash]').trigger("click");
  await flushPromises();
  expect(document.querySelector("[data-confirm-action]")).toBeNull();
  expect(source.trash).toHaveBeenCalledWith(expect.objectContaining({ id: "n1" }));
  expect(titles()).toEqual(["n2"]);
  expect(notice.success).toHaveBeenCalledWith("Poznámka Porada je v koši.", expect.objectContaining({ undo: expect.any(Function) }));
  expect(document.activeElement).toBe(wrapper!.get('[data-personal-row="n2"] [data-personal-open]').element);
  (notice.success.mock.lastCall![1] as { undo(): void }).undo();
  await flushPromises();
  expect(source.restore).toHaveBeenCalledWith("n1");
  expect(source.mine).toHaveBeenCalledTimes(2);
});
