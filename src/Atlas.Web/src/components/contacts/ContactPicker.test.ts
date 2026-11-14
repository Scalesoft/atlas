import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { nextTick } from "vue";
import { normalizeContact, type MarkdownContact } from "@atlas/markdown/plugins/contacts";
import type { ContactAsk } from "../../contacts/contactLinking";
import type { PersonHit } from "../../contacts/contactSearch";
import ContactPicker from "./ContactPicker.vue";

let wrapper: VueWrapper | undefined;
beforeEach(() => vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] }));
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.useRealTimers(); });

const hit = (key: string, name: string, more: Partial<PersonHit["contact"]> = {}, origin = "Adresář"): PersonHit => ({ key, contact: { name, ...more }, source: "directory", origin });
const people = [hit("obj:1", "Honza Kovařík", { organization: "AB Klinika" }), hit("obj:2", "Jan Novák", { emails: ["jan@a.cz"] }), hit("obj:3", "Jan Novák", { emails: ["jan@b.cz"] }, "Stránka Kontakty")];

function show(search = vi.fn(async () => people), link?: (contact: MarkdownContact, ask: ContactAsk) => Promise<MarkdownContact | null>) {
  const pick = vi.fn(), close = vi.fn();
  wrapper = mount(ContactPicker, { attachTo: document.body, props: { search, ...(link && { link }), pick, close, normalize: normalizeContact } });
  return { search, link, pick, close };
}
const input = () => wrapper!.get<HTMLInputElement>("[data-contact-picker-query]");
const rows = () => [...document.querySelectorAll("[data-testid=contact-picker-list] [role=option]")].map(row => row.textContent);
const active = () => document.querySelector("[data-testid=contact-picker-list] [aria-selected=true]")?.textContent;
const key = (name: string) => input().trigger("keydown", { key: name });
async function type(text: string) { await input().setValue(text); await vi.advanceTimersByTimeAsync(300); await nextTick(); }

it("finds as it is typed, the first person preselected, and Enter puts that person in", async () => {
  const { search, pick } = show();
  await type("jan kovarik");
  expect(search).toHaveBeenCalledWith("jan kovarik", expect.any(AbortSignal));
  expect(rows()[0]).toContain("Honza Kovařík");
  expect(active()).toContain("Honza Kovařík");
  // The last row makes a new contact of what was typed.
  expect(rows().at(-1)).toContain("Nový kontakt „jan kovarik“");
  await key("Enter");
  expect(pick).toHaveBeenCalledWith({ name: "Honza Kovařík", organization: "AB Klinika" });
});

it("takes Enter pressed before the answer came as the first person of the answer", async () => {
  const { pick } = show();
  await input().setValue("novak");
  await key("Enter");
  expect(pick).not.toHaveBeenCalled();
  await vi.advanceTimersByTimeAsync(300);
  await nextTick();
  expect(pick).toHaveBeenCalledWith({ name: "Jan Novák", emails: ["jan@a.cz"] });
});

it("moves with the arrows and tells two people of one name apart", async () => {
  const { pick } = show();
  await type("novak");
  expect(rows()[0]).toContain("jan@a.cz · Adresář");
  expect(rows()[1]).toContain("jan@b.cz · Stránka Kontakty");
  await key("ArrowDown");
  await key("Enter");
  expect(pick).toHaveBeenCalledWith({ name: "Jan Novák", emails: ["jan@b.cz"] });
});

it("highlights the lone „Nový kontakt“ row only once an arrow moved to it; Enter then opens the form, and in the untouched field it opens nothing", async () => {
  show();
  await nextTick();
  const listShown = () => wrapper!.get<HTMLElement>(".contact-picker-list").element.style.display !== "none";
  expect(input().attributes("aria-activedescendant")).toBeUndefined();
  expect(wrapper!.get("[data-contact-picker]").attributes("data-untouched")).toBeDefined();
  await key("Enter");
  await nextTick();
  expect(listShown()).toBe(true);
  await key("ArrowDown");
  expect(input().attributes("aria-activedescendant")).toBe(document.querySelector("[data-testid=contact-picker-list] [role=option]")!.id);
  expect(wrapper!.get("[data-contact-picker]").attributes("data-untouched")).toBeUndefined();
  await key("Enter");
  await nextTick();
  expect(listShown()).toBe(false);
});

it("makes a new contact of the typed name, warns of the people who already have it and links it to the directory", async () => {
  const { pick, link } = show(undefined, vi.fn(async (contact: MarkdownContact) => contact));
  await type("jan novak");
  await key("End");
  await key("Enter");
  await nextTick();
  const name = wrapper!.get<HTMLInputElement>("[data-contact-name]");
  expect(name.element.value).toBe("jan novak");
  await name.setValue("Jan Novák");
  expect(wrapper!.get("[data-contact-picker-same]").text()).toContain("Kontakt se jménem Jan Novák už existuje 2×");
  // The list steps aside while the form is open: the people of the name are offered above the form instead (PR-11).
  expect(wrapper!.get<HTMLElement>(".contact-picker-list").element.style.display).toBe("none");
  await wrapper!.get("[data-contact-form]").trigger("submit");
  await vi.waitFor(() => expect(pick).toHaveBeenCalled());
  expect(link).toHaveBeenCalledWith({ name: "Jan Novák" }, expect.any(Function));
  expect(pick).toHaveBeenCalledWith({ name: "Jan Novák" });
});

it("searches for what was typed while it opened and picks the first person for the Enter after it", async () => {
  const pick = vi.fn();
  wrapper = mount(ContactPicker, { attachTo: document.body, props: { search: vi.fn(async () => people), pick, close: vi.fn(), normalize: normalizeContact,
    typeAhead: { take: () => ({ text: "jan kovarik", enter: true }) } } });
  await nextTick();
  expect(input().element.value).toBe("jan kovarik");
  await vi.advanceTimersByTimeAsync(300);
  await nextTick();
  expect(pick).toHaveBeenCalledWith({ name: "Honza Kovařík", organization: "AB Klinika" });
});

it("puts the directory object id on the picked contact and on the one just written to the directory", async () => {
  const id = "0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b";
  const first = show(vi.fn(async () => [{ ...people[0]!, objectId: id }]));
  await type("honza");
  await key("Enter");
  expect(first.pick).toHaveBeenCalledWith({ name: "Honza Kovařík", organization: "AB Klinika", objectId: id });
  wrapper!.unmount();
  const created = show(vi.fn(async () => people), vi.fn(async (contact: MarkdownContact) => ({ ...contact, objectId: id })));
  await type("jan novak");
  await key("End");
  await key("Enter");
  await wrapper!.get("[data-contact-name]").setValue("Nový Člověk");
  await wrapper!.get("[data-contact-form]").trigger("submit");
  await vi.waitFor(() => expect(created.pick).toHaveBeenCalledWith({ name: "Nový Člověk", objectId: id }));
});

it("puts an existing person in from the warning instead", async () => {
  const { pick } = show();
  await type("jan novak");
  await key("End");
  await key("Enter");
  await wrapper!.get("[data-contact-name]").setValue("Jan Novák");
  await wrapper!.findAll("[data-contact-picker-same] button")[1]!.trigger("click");
  expect(pick).toHaveBeenCalledWith({ name: "Jan Novák", emails: ["jan@b.cz"] });
});

// Spec 2026-10-30 §7: what the match asks shows in place of the list; Pokračovat links to the contact the directory has.
it("asks what the match needs before the person goes in, and Esc goes back to the search", async () => {
  const id = "0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b";
  const link = vi.fn(async (contact: MarkdownContact, ask: ContactAsk) => {
    const answer = await ask({ kind: "exists", name: contact.name, contactId: id, usedIn: [{ kind: "page", id: "p1", title: "Provozní příručka" }] });
    return answer ? { ...contact, objectId: answer.contactId } : null;
  });
  const { pick } = show(undefined, link);
  await type("honza");
  await key("Enter");
  await vi.waitFor(() => expect(wrapper!.find("[data-contact-match=exists]").exists()).toBe(true));
  expect(wrapper!.get("[data-contact-match]").text()).toContain("Honza Kovařík už je v adresáři a používá se na: Provozní příručka. Vložím odkaz na existující kontakt.");
  await wrapper!.get("[data-contact-match]").trigger("keydown", { key: "Escape" });
  await vi.waitFor(() => expect(wrapper!.find("[data-contact-match]").exists()).toBe(false));
  expect(pick).not.toHaveBeenCalled();
  await key("Enter");
  await vi.waitFor(() => expect(wrapper!.find("[data-contact-match-primary]").exists()).toBe(true));
  await wrapper!.get("[data-contact-match-primary]").trigger("click");
  await vi.waitFor(() => expect(pick).toHaveBeenCalledWith({ name: "Honza Kovařík", organization: "AB Klinika", objectId: id }));
});

it("hands a person picked from the search, with what was searched, to learn: the directory then knows „MMu“ as theirs", async () => {
  const martin: PersonHit = { key: "obj:m", contact: { name: "Marek Musil" }, source: "directory", objectId: "c-m", origin: "Adresář" };
  const learn = vi.fn(), pick = vi.fn();
  wrapper = mount(ContactPicker, { attachTo: document.body, props: { search: async () => [martin], learn, pick, close: vi.fn(), normalize: normalizeContact } });
  await type("MMu");
  await key("Enter");
  expect(pick).toHaveBeenCalledWith({ name: "Marek Musil", objectId: "c-m" });
  expect(learn).toHaveBeenCalledWith({ contact: { name: "Marek Musil" }, objectId: "c-m" }, "MMu");
});
