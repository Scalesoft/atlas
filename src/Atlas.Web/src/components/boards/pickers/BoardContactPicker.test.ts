import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { DOMWrapper, mount, type VueWrapper } from "@vue/test-utils";
import { nextTick } from "vue";
import { ApiError } from "../../../api";
import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import type { ContactAsk } from "../../../contacts/contactLinking";
import type { ObjectList, ObjectSummary } from "../../../objects/objectApi";
import BoardContactPicker from "./BoardContactPicker.vue";
import type { SearchItemLike } from "../../../contacts/contactHits";

let wrapper: VueWrapper | undefined;
/** The picker's panel is portalled to the body. */
const body = () => new DOMWrapper(document.body);
// Only the timers: the date stays real, so that a listener never looks newer than the event it gets (Vue skips such a listener).
beforeEach(() => vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] }));
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.useRealTimers(); });

const results: SearchItemLike[] = [
  { title: "Adresář", spaceName: "Provoz", widgets: [
    { kind: "contact", title: "Jana Nováková", detail: "Vedoucí provozu · Modelsoft · jana@example.cz · +420 777 123 456" },
    { kind: "contact", title: "Karel Novák", detail: "karel@example.cz" },
    { kind: "youtrack", title: "ATL-1", detail: "x" },
  ] },
];
const search = (items: SearchItemLike[] = results) => vi.fn(async () => items);
const petr: ObjectSummary = { id: "c-petr", typeId: "atlas.contact", scope: "wiki", spaceId: null, title: "Petr Novák", version: 2, updatedAt: "2026-10-26T10:00:00Z",
  data: { name: "**Petr** Novák", title: "Jednatel", emails: ["karel@example.cz"] } };
const listOf = (items: ObjectSummary[]): ObjectList => ({ items, total: items.length, page: 1, pageSize: 50, canCreate: true });
function directory(items: ObjectSummary[] = [petr]) {
  return { list: vi.fn(async () => listOf(items)) };
}
const linking = () => vi.fn(async (contact: MarkdownContact, _ask: ContactAsk): Promise<MarkdownContact | null> => ({ ...contact, objectId: `new:${contact.name}` }));

async function show(fetch = search(), contacts = directory(), link = linking()) {
  wrapper = mount(BoardContactPicker, { attachTo: document.body, props: { open: true, search: fetch, directory: contacts, link } });
  await nextTick();
  return { fetch, contacts, link };
}
async function find(text: string) {
  await body().get("input[type=search]").setValue(text);
  await vi.advanceTimersByTimeAsync(300);
  await nextTick();
}
const rows = () => body().findAll(".pick-item").map(row => row.text());
const box = (name: string) => body().findAll("input[type=checkbox]").find(input => input.element.closest("label")?.textContent?.includes(name))!;
async function confirm() {
  await body().get("[data-board-contact-confirm]").trigger("click");
  await vi.waitFor(() => expect(!!wrapper!.emitted("pick") || body().find("[data-board-contact-note]").exists()).toBe(true));
}

it("lists the directory's contacts first, then those written only in pages that the directory lacks", async () => {
  const { fetch, contacts } = await show();
  await find("novak");
  expect(fetch).toHaveBeenCalledWith("novak", expect.any(AbortSignal));
  expect(contacts.list).toHaveBeenCalledWith({ type: "atlas.contact", scope: "wiki", q: "novak" }, expect.any(AbortSignal));
  // Karel's address is Petr's in the directory: the directory's contact stands for it.
  expect(rows()).toHaveLength(2);
  expect(rows()[0]).toContain("Petr Novák");
  expect(rows()[0]).toContain("Jednatel");
  expect(rows()[1]).toContain("Jana Nováková");
  expect(rows()[1]).toContain("Jen ve stránce Adresář · Provoz · přidá se do adresáře");
});

it("places directory contacts as they are and links a page's contact to the directory first", async () => {
  const { link } = await show();
  await find("novak");
  await box("Petr").setValue(true);
  await box("Jana").setValue(true);
  expect(body().get("[data-board-contact-confirm]").text()).toBe("Přidat 2 kontakty");
  await confirm();
  expect(link).toHaveBeenCalledOnce();
  expect(link.mock.calls[0]![0]).toMatchObject({ name: "Jana Nováková", emails: ["jana@example.cz"], phones: ["+420 777 123 456"] });
  expect(wrapper!.emitted("pick")).toEqual([[[{ objectId: "c-petr", typeId: "atlas.contact" }, { objectId: "new:Jana Nováková", typeId: "atlas.contact" }]]]);
});

it("places the contact the match links to, and names a contact it refuses", async () => {
  const link = vi.fn(async (contact: MarkdownContact): Promise<MarkdownContact | null> => {
    if (contact.name === "Jana Nováková") return { ...contact, objectId: "c-jana" };
    throw new ApiError(400, "Neplatné pole kontaktu: Telefon.", "invalid_contact");
  });
  // A page the search finds lists all its contacts; only those the typed words match are offered.
  await show(search([{ title: "Adresář", spaceName: "Provoz", widgets: [...results[0]!.widgets!, { kind: "contact", title: "Zlý Novák", detail: "", contact: { name: "Zlý Novák", phones: ["12345"] } },
    { kind: "contact", title: "Eva Malá", detail: "" }] }]), directory([]), link);
  await find("novak");
  expect(box("Eva")).toBeUndefined();
  await box("Jana").setValue(true);
  await confirm();
  expect(wrapper!.emitted("pick")).toEqual([[[{ objectId: "c-jana", typeId: "atlas.contact" }]]]);
  await box("Zlý").setValue(true);
  await body().get("[data-board-contact-confirm]").trigger("click");
  await vi.waitFor(() => expect(body().find("[data-board-contact-note]").exists()).toBe(true));
  expect(wrapper!.emitted("pick")).toHaveLength(1);
  expect(body().get("[data-board-contact-note]").text()).toContain("Zlý Novák: Neplatné pole kontaktu: Telefon.");
});

it("says that the search failed instead of that nothing matches", async () => {
  await show(vi.fn(async () => { throw new Error("500"); }));
  await find("novak");
  expect(body().text()).toContain("Hledání se nepodařilo.");
  expect(body().text()).not.toContain("Žádný kontakt neodpovídá");
});

it("says so when no contact matches, and points at the new contact", async () => {
  await show(search([]), directory([]));
  await find("nikdo");
  expect(body().text()).toContain("Žádný kontakt neodpovídá. Zkuste jiné slovo, nebo vytvořte nový.");
});

it("reveals the form of a new contact under the list; the contact it makes joins the selection and is added to the directory", async () => {
  await show();
  expect(body().find("[data-contact-form]").exists()).toBe(false);
  await body().get("[data-board-contact-new]").trigger("click");
  expect(body().get("[data-board-contact-new]").attributes("aria-expanded")).toBe("true");
  expect(body().find("[data-contact-form]").exists()).toBe(true);
  await body().get("[data-contact-name]").setValue("Marie Nová");
  await body().get("[data-contact-emails]").setValue("marie@example.cz");
  await body().get("[data-contact-form]").trigger("submit");
  await vi.waitFor(() => expect(body().find("[data-pick-count]").exists()).toBe(true));
  expect(body().get("[data-pick-count]").text()).toBe("Vybráno 1 z 100");
  expect(body().get(".pick-tag").text()).toBe("Marie Nová");
  // The form is empty again and folds away.
  expect(body().get("[data-board-contact-new]").attributes("aria-expanded")).toBe("false");
  await body().get("[data-board-contact-confirm]").trigger("click");
  await vi.waitFor(() => expect(wrapper!.emitted("pick")).toBeDefined());
  expect(wrapper!.emitted("pick")).toEqual([[[{ objectId: "new:Marie Nová", typeId: "atlas.contact" }]]]);
});

it("validates a new contact by the editor's rules and shows each wrong field with the editor's words", async () => {
  await show();
  await body().get("[data-board-contact-new]").trigger("click");
  await body().get("[data-contact-emails]").setValue("marie@");
  await body().get("[data-contact-phones]").setValue("abc");
  await body().get("[data-contact-form]").trigger("submit");
  await vi.waitFor(() => expect(body().find(".contact-form-error").exists()).toBe(true));
  const errors = body().findAll(".contact-form-error").map(error => error.text());
  expect(errors).toEqual(["Vyplňte pole: jméno.", "Zkontrolujte e-mailové adresy.", "Zkontrolujte telefonní čísla."]);
  expect(body().find("[data-pick-count]").exists()).toBe(false);
});

it("lets a contact carry no character the editor or the server would refuse", async () => {
  await show();
  await body().get("[data-board-contact-new]").trigger("click");
  const bidi = String.fromCharCode(0x202e);
  await body().get("[data-contact-name]").setValue(`Marie${bidi}\tNová`);
  await body().get("[data-contact-form]").trigger("submit");
  await vi.waitFor(() => expect(body().find(".pick-tag").exists()).toBe(true));
  expect(body().get(".pick-tag").text()).toBe("Marie Nová");
});

it("starts afresh when it is opened again", async () => {
  await show();
  await find("novak");
  await box("Petr").setValue(true);
  await wrapper!.setProps({ open: false });
  await wrapper!.setProps({ open: true });
  expect((body().get("input[type=search]").element as HTMLInputElement).value).toBe("");
  // What was added before may be listed (the recent rows), but nothing of this session stays ticked.
  expect(body().findAll(".pick-item input:checked")).toHaveLength(0);
  expect(body().find("[data-pick-count]").exists()).toBe(false);
});

// Spec 2026-10-30 §7: a similar contact asks „Použít existujícího / Vytvořit nového“ in the panel before anything is placed.
it("asks in the panel what a weak match needs and places the contact chosen", async () => {
  const link = vi.fn(async (contact: MarkdownContact, ask: ContactAsk) => {
    const answer = await ask({ kind: "weak", name: contact.name, candidates: [{ contactId: "c-jana2", name: "Jana Nováková", contact: { name: "Jana Nováková", organization: "Modelsoft" } }] });
    return answer ? { ...contact, objectId: answer.contactId ?? "new" } : null;
  });
  await show(search(), directory([]), link);
  await find("novak");
  await box("Jana").setValue(true);
  await body().get("[data-board-contact-confirm]").trigger("click");
  await vi.waitFor(() => expect(body().find("[data-contact-match=weak]").exists()).toBe(true));
  expect(body().get("[data-contact-match]").text()).toContain("Vytvořit nového");
  await body().get("[data-contact-match-use]").trigger("click");
  await vi.waitFor(() => expect(wrapper!.emitted("pick")).toEqual([[[{ objectId: "c-jana2", typeId: "atlas.contact" }]]]));
});

it("opens with the contacts last added, a page's contact as the directory contact it was linked to (P17)", async () => {
  const { link } = await show();
  await find("novak");
  await box("Jana").setValue(true);
  await confirm();
  await wrapper!.setProps({ open: false });
  await wrapper!.setProps({ open: true });
  expect(rows()[0]).toContain("Jana Nováková");
  expect(rows()[0]).toContain("V adresáři");
  await box("Jana").setValue(true);
  await body().get("[data-board-contact-confirm]").trigger("click");
  await vi.waitFor(() => expect(wrapper!.emitted("pick")).toHaveLength(2));
  expect(link).toHaveBeenCalledOnce();
  expect(wrapper!.emitted("pick")![1]).toEqual([[{ objectId: "new:Jana Nováková", typeId: "atlas.contact" }]]);
});

it("2B: teaches a directory contact what was typed for it on confirmation, as ContactPicker does; a page's contact teaches nothing", async () => {
  const learn = vi.fn();
  wrapper = mount(BoardContactPicker, { attachTo: document.body, props: { open: true, search: search(), directory: directory(), link: linking(), learn } });
  await nextTick();
  await find("novak");
  await box("Petr").setValue(true);
  await box("Jana").setValue(true);
  expect(learn).not.toHaveBeenCalled();
  await confirm();
  expect(learn).toHaveBeenCalledOnce();
  expect(learn).toHaveBeenCalledWith({ contact: expect.objectContaining({ name: "Petr Novák" }), objectId: "c-petr" }, "novak");
});
