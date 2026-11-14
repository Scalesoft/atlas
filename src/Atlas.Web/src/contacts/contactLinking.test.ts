import { describe, expect, it, vi } from "vitest";
import { ApiError } from "../api";
import type { ContactMatch } from "./contactApi";
import { useToast } from "../composables/useToast";
import { askLastUsage, contactPluginOptions, contactRemoved, contactsIn, contactsRestored, decisionOf, isLastUsage, lastUsagePrompt, linkContact, repeatedContact } from "./contactLinking";

vi.mock("../composables/useToast", () => ({ useToast: vi.fn(() => ({ success: vi.fn(), error: vi.fn(), warning: vi.fn(), dismissAll: vi.fn() })) }));

const id = "a1b2c3d4-0000-4000-8000-000000000001";
const jana = { contactId: id, name: "Jana Nováková", usedIn: [{ kind: "page" as const, id: "p1", title: "Provozní příručka" }] };

it("asks about a strong match, its differing fields, a weak match, and nothing without a match", () => {
  const contact = { name: "Jana" };
  expect(decisionOf(contact, { strong: jana, weak: [], conflicts: [] })).toEqual({ kind: "exists", ...jana });
  const conflicts = [{ field: "phones", existing: ["+420777123456"], new: ["+420602000111"] }];
  expect(decisionOf(contact, { strong: { ...jana, canEdit: false }, weak: [], conflicts })).toEqual({ kind: "conflict", ...jana, conflicts, canEdit: false });
  expect(decisionOf(contact, { strong: null, weak: [jana], conflicts: [] })).toEqual({ kind: "weak", name: "Jana", candidates: [jana] });
  expect(decisionOf(contact, { weak: [], conflicts: [] })).toBeNull();
});

function services(match: ContactMatch) {
  return {
    contacts: { match: vi.fn(async () => match), forget: vi.fn(),
      resolve: vi.fn(async (_contact: unknown, contactId?: string) => ({ contactId: contactId ?? "new-id", created: !contactId, contact: { name: "Jana Nováková", phones: ["+420777123456"] }, warnings: [] })) },
    objects: { change: vi.fn(async () => ({ version: 2, duplicate: false })) },
  };
}

it("links a pick to the contact the person chose, writing the fields they chose as new", async () => {
  const conflicts = [{ field: "phones", existing: ["+420777123456"], new: ["+420602000111"] }];
  const linked = services({ strong: jana, weak: [], conflicts });
  const ask = vi.fn(async () => ({ contactId: id, fields: { phones: ["+420602000111"] } }));
  const result = await linkContact({ name: "Jana", role: "PM", phones: ["+420602000111"] }, ask, linked);
  expect(ask).toHaveBeenCalledWith(expect.objectContaining({ kind: "conflict" }));
  expect(linked.contacts.resolve).toHaveBeenCalledWith({ name: "Jana", role: "PM", phones: ["+420602000111"] }, id);
  expect(linked.objects.change).toHaveBeenCalledWith(id, { fields: { phones: ["+420602000111"] } });
  expect(result).toEqual({ name: "Jana Nováková", role: "PM", phones: ["+420602000111"], objectId: id });
});

it("puts nothing in when the person goes back, and a weak match's „Vytvořit nového“ resolves without an id", async () => {
  const back = services({ strong: jana, weak: [], conflicts: [] });
  expect(await linkContact({ name: "Jana" }, async () => null, back)).toBeNull();
  expect(back.contacts.resolve).not.toHaveBeenCalled();
  const fresh = services({ weak: [jana], conflicts: [] });
  expect((await linkContact({ name: "Jana" }, async () => ({}), fresh))?.objectId).toBe("new-id");
  expect(fresh.contacts.resolve).toHaveBeenCalledWith({ name: "Jana" }, undefined);
  expect(fresh.objects.change).not.toHaveBeenCalled();
});

it("leaves a directory contact as it is and keeps a pick unlinked for who may not add to the wiki", async () => {
  const linked = services({ weak: [], conflicts: [] });
  expect(await linkContact({ name: "Jana", objectId: id }, vi.fn(), linked)).toEqual({ name: "Jana", objectId: id });
  expect(linked.contacts.match).not.toHaveBeenCalled();
  linked.contacts.resolve.mockRejectedValueOnce(new ApiError(403, "Nesmíte.", "contact_forbidden"));
  expect(await linkContact({ name: "Jana" }, undefined, linked)).toEqual({ name: "Jana" });
  linked.contacts.resolve.mockRejectedValueOnce(new ApiError(400, "Neplatný e-mail.", "invalid_contact"));
  await expect(linkContact({ name: "Jana" }, undefined, linked)).rejects.toThrow("Neplatný e-mail.");
});

it("never resolves an unresolved name: a press on its chip must not create one more contact of that name", async () => {
  const linked = services({ weak: [], conflicts: [] });
  const unresolved = { name: "Jana Nováková", unresolved: true as const };
  expect(await linkContact(unresolved, undefined, linked)).toEqual(unresolved);
  expect(linked.contacts.match).not.toHaveBeenCalled();
  expect(linked.contacts.resolve).not.toHaveBeenCalled();
});

it("says a contact the page has already when the editor plugin puts one in, never while linking in the background", async () => {
  const warning = vi.fn();
  vi.mocked(useToast).mockReturnValue({ success: vi.fn(), error: vi.fn(), warning, dismissAll: vi.fn(), progress: vi.fn(), dismiss: vi.fn() });
  const signal = new AbortController().signal, insert = { editor: {} as never, anchor: () => new DOMRect() };
  const options = contactPluginOptions({ place: () => ({ kind: "page", id: "p1" }), count: () => 1 });
  expect(await options.link!({ name: "Jana", objectId: id }, { signal, insert })).toEqual({ name: "Jana", objectId: id });
  expect(warning).toHaveBeenCalledWith(repeatedContact());
  warning.mockClear();
  expect(await options.link!({ name: "Jana", objectId: id }, { signal })).toEqual({ name: "Jana", objectId: id });
  expect(warning).not.toHaveBeenCalled();
});

it("counts the references to a contact in a document", () => {
  const node = (name: string, contacts: { objectId?: string }[]) => ({ type: { name }, attrs: { data: { contacts } } });
  const nodes = [node("paragraph", []), node("contactCard", [{ objectId: id.toUpperCase() }, {}, { objectId: "other" }]), node("contactInline", [{ objectId: id }])];
  expect(contactsIn({ descendants: visit => nodes.forEach(entry => visit(entry)) }, id)).toBe(2);
  // A requirement heading's party contact is a reference too (shape §6).
  const party = { type: { name: "heading" }, attrs: { partyContactId: id.toUpperCase() } };
  expect(contactsIn({ descendants: visit => [...nodes, party].forEach(entry => visit(entry)) }, id)).toBe(3);
});

it("is the last usage only when no other place, seen or unseen, uses the contact", () => {
  const here = { id: "B1" };
  const row = (placeId: string) => ({ placeId, placeTypeId: "atlas.board", placeTitle: "Tabule", spaceId: null, spaceName: null, kind: "placement" as const, count: 1, draft: false });
  expect(isLastUsage({ total: 0 }, here, 0)).toBe(true);
  expect(isLastUsage({ total: 1 }, here, 0, { items: [row("b1")], hidden: 0 })).toBe(true);
  // The one use is elsewhere (this page not saved yet), unseen, or not listed: not the last.
  expect(isLastUsage({ total: 1 }, here, 0, { items: [row("p9")], hidden: 0 })).toBe(false);
  expect(isLastUsage({ total: 1 }, here, 0, { items: [], hidden: 1 })).toBe(false);
  expect(isLastUsage({ total: 1 }, here, 0)).toBe(false);
  expect(isLastUsage({ total: 0 }, here, 1)).toBe(false);
  expect(isLastUsage({ total: 2 }, here, 0, { items: [row("b1"), row("p1")], hidden: 0 })).toBe(false);
});

it("trashes the contact after its last usage only when the person says so", async () => {
  const run = (total: number, trash: boolean, placeId = "p1") => {
    const used = {
      usage: { count: vi.fn(async () => ({ objectId: id, total, visible: total, hidden: 0 })), usage: vi.fn(async () => ({ objectId: id, items: [{ placeId, placeTypeId: "atlas.page", placeTitle: "Stránka", spaceId: null, spaceName: null, kind: "mention" as const, count: 1, draft: true }], total, hidden: 0, nextCursor: null })) },
      contacts: { forget: vi.fn() }, objects: { trash: vi.fn(async () => {}) }, ask: vi.fn(async () => trash),
    };
    return { used, done: contactRemoved(id, "Jana", { kind: "page", id: "p1" }, 0, used) };
  };
  const yes = run(0, true);
  expect(await yes.done).toBe("trashed");
  expect(yes.used.objects.trash).toHaveBeenCalledWith(id);
  expect(yes.used.contacts.forget).toHaveBeenCalledWith(id);
  expect(yes.used.usage.usage).not.toHaveBeenCalled();
  const here = run(1, true);
  expect(await here.done).toBe("trashed");
  expect(here.used.usage.usage).toHaveBeenCalledWith(id);
  const no = run(0, false);
  expect(await no.done).toBe("kept");
  expect(no.used.objects.trash).not.toHaveBeenCalled();
  const other = run(1, true, "p2");
  expect(await other.done).toBe("kept");
  expect(other.used.ask).not.toHaveBeenCalled();
  const elsewhere = run(3, true);
  expect(await elsewhere.done).toBe("kept");
  expect(elsewhere.used.ask).not.toHaveBeenCalled();
});

it("asks one last-usage question at a time", async () => {
  const first = askLastUsage("Jana"), second = askLastUsage("Karel");
  await vi.waitFor(() => expect(lastUsagePrompt.value?.name).toBe("Jana"));
  lastUsagePrompt.value!.answer(true);
  await vi.waitFor(() => expect(lastUsagePrompt.value?.name).toBe("Karel"));
  lastUsagePrompt.value!.answer(false);
  expect([await first, await second]).toEqual([true, false]);
  expect(lastUsagePrompt.value).toBeNull();
});

it("brings a trashed contact back when the card deletion is undone", async () => {
  const objects = { restore: vi.fn(async () => ({ version: 3, duplicate: false })) }, contacts = { forget: vi.fn() };
  await contactsRestored([id], { contacts, objects });
  expect(objects.restore).toHaveBeenCalledWith(id);
  expect(contacts.forget).toHaveBeenCalledWith(id);
});

describe("contact usage for the read-only card", () => {
  it("lists each place once, pages and boards by their route, and counts the rest and the hidden ones", async () => {
    const row = (placeId: string, placeTypeId: string, placeTitle: string) => ({ placeId, placeTypeId, placeTitle, spaceId: "s", spaceName: "Wiki", kind: "mention", count: 1, draft: false });
    const items = [row("p1", "atlas.page", "Zakázka"), row("p1", "atlas.page", "Zakázka"), row("b1", "atlas.board", "Tabule"), ...[2, 3, 4, 5, 6].map(n => row(`p${n}`, "atlas.page", `Strana ${n}`))];
    const answer = await (await import("./contactLinking")).usedOn("c1", new AbortController().signal, { usage: async () => ({ objectId: "c1", items, total: 7, hidden: 2, nextCursor: null }) as never });
    expect(answer!.places).toHaveLength(5);
    expect(answer!.places[0]).toEqual({ title: "Zakázka", detail: "Wiki", url: "#/pages/p1" });
    expect(answer!.places[1]!.url).toContain("b1");
    expect(answer!.more).toBe(2 + 2);
  });
  it("is null for an unreadable contact", async () => {
    expect(await (await import("./contactLinking")).usedOn("c1", new AbortController().signal, { usage: async () => null })).toBeNull();
  });
});
