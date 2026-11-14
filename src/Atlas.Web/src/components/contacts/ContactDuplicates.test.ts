import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { toast } from "vue-sonner";
import type { AccountContactSuggestion, ContactPair } from "../../contacts/contactApi";
import ContactDuplicates from "./ContactDuplicates.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.restoreAllMocks(); });

const suggestion = (accountId: string, contactId: string, extra: Partial<AccountContactSuggestion> = {}): AccountContactSuggestion => ({
  accountId, accountName: "Eva Malá", accountEmail: null, accountActive: true, reason: "name",
  contact: { contactId, name: "Eva Malá", contact: { name: "Eva Malá", emails: contactId === "c1" ? ["eva@old.example.test"] : [] } }, ...extra,
});

function show(items: AccountContactSuggestion[], pairs: ContactPair[] = []) {
  const api = {
    duplicates: vi.fn(async () => ({ pairs })),
    merge: vi.fn(async () => ({ contactId: "x" })),
    accountSuggestions: vi.fn(async () => ({ items })),
    resolveAccount: vi.fn(async (_account: string, _contact: string, decision: string) => ({ mergedContactId: null, createdContactId: decision === "dismiss" ? "own" : null })),
  };
  wrapper = mount(ContactDuplicates, { attachTo: document.body, props: { api } });
  return api;
}
const rows = () => wrapper!.findAll("[data-account-suggestion]");

it("shows a wiki admin the accounts to link with the evidence, and nothing of them to anyone else", async () => {
  show([suggestion("u1", "c1"), suggestion("u1", "c2", { ownContactId: "own" })]);
  await flushPromises();
  expect(wrapper!.text()).toContain("Propojit účty s kontakty");
  expect(rows().map(row => row.get(".contact-duplicates-reason").text())).toEqual(["Stejné jméno, účet bez e-mailu", "Stejné jméno, účet bez e-mailu · účet už kontakt má, sloučí se"]);
  expect(rows()[0]!.get("[data-account-link]").attributes()).toHaveProperty("autofocus");
  wrapper!.unmount();

  show([]);
  await flushPromises();
  expect(wrapper!.text()).not.toContain("Propojit účty s kontakty");
  expect(wrapper!.text()).toContain("Žádné možné duplicity");
});

it("links an account, its other candidates leave, and a merged own contact is told to the directory", async () => {
  const success = vi.spyOn(toast, "success");
  const api = show([suggestion("u1", "c1", { ownContactId: "own" }), suggestion("u1", "c2"), suggestion("u2", "c3", { accountName: "Petr" }),
    suggestion("u3", "c1", { accountUsername: "eva.mala2" })]);
  await flushPromises();
  // Two accounts of one name without an address differ by their username (P8).
  expect(rows()[3]!.text()).toContain("Účet · eva.mala2");
  await rows()[0]!.get("[data-account-link]").trigger("click");
  await flushPromises();
  expect(api.resolveAccount).toHaveBeenCalledWith("u1", "c1", "link");
  expect(rows().map(row => row.get("strong").text())).toEqual(["Petr"]);
  expect(wrapper!.emitted("merged")).toEqual([["own"]]);
  expect(success).toHaveBeenCalledWith("Účet Eva Malá je propojen s kontaktem Eva Malá.", expect.anything());
  expect(document.activeElement).toBe(rows()[0]!.get("[data-account-link]").element);
});

it("„Není to on“ gives the account its own contact, so all its pairs go, and Zpět trashes that contact and brings them back", async () => {
  const success = vi.spyOn(toast, "success");
  const api = show([suggestion("u1", "c1"), suggestion("u2", "c3", { accountName: "Petr" }), suggestion("u1", "c2")]);
  await flushPromises();
  await rows()[0]!.get("[data-account-dismiss]").trigger("click");
  await flushPromises();
  expect(api.resolveAccount).toHaveBeenCalledWith("u1", "c1", "dismiss");
  expect(rows().map(row => row.get("strong").text())).toEqual(["Petr"]);
  expect(success).toHaveBeenCalledWith(expect.stringContaining("má vlastní kontakt"), expect.objectContaining({ action: expect.objectContaining({ label: "Zpět" }) }));
  (success.mock.calls[0]![1] as { action: { onClick: () => void } }).action.onClick();
  await flushPromises();
  expect(api.resolveAccount).toHaveBeenLastCalledWith("u1", "own", "restore");
  expect(rows().map(row => row.get("[data-account-link]").element.closest("[data-account-suggestion]")!.getAttribute("data-account-suggestion"))).toEqual(["u1:c1", "u2:c3", "u1:c2"]);
  // Zpět left with its toast; the keyboard is on the pair that is back (P7).
  expect(document.activeElement).toBe(rows()[0]!.get("[data-account-link]").element);
});

it("offers both decisions from the row's actions menu (Shift+F10)", async () => {
  const api = show([suggestion("u1", "c1")]);
  await flushPromises();
  await rows()[0]!.get("[data-account-link]").trigger("keydown", { key: "F10", shiftKey: true });
  await flushPromises();
  const items = [...document.querySelectorAll<HTMLElement>("[data-row-action]")];
  expect(items.map(item => item.textContent?.trim())).toEqual(["Propojit", "Není to on"]);
  items[1]!.click();
  await flushPromises();
  expect(api.resolveAccount).toHaveBeenCalledWith("u1", "c1", "dismiss");
});

it("says why a merge whose e-mails would not fit cannot happen, instead of a result the server would refuse", async () => {
  const emails = (prefix: string, count: number) => Array.from({ length: count }, (_, i) => `${prefix}${i}@x.cz`);
  const api = show([], [{ reason: "similar", a: { contactId: "a", name: "Jana", contact: { name: "Jana", emails: emails("a", 20) } }, b: { contactId: "b", name: "Jana N.", contact: { name: "Jana N.", emails: ["b@x.cz"] } } }]);
  await flushPromises();
  await wrapper!.get("[data-contact-merge]").trigger("click");
  await flushPromises();

  const alert = document.querySelector(".confirm-dialog-error")!;
  expect(alert.textContent).toContain("pole „E-mail“ by mělo 21 hodnot (cílový kontakt 20, ze zdrojového 1 dalších)");
  expect(document.querySelector("[data-contact-merge-result]")).toBeNull();
  expect(document.querySelector<HTMLButtonElement>("[data-confirm-action]")!.disabled).toBe(true);
  expect(api.merge).not.toHaveBeenCalled();
});
