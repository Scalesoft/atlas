import { afterEach, describe, expect, it, vi } from "vitest";
import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";

const live = vi.hoisted(() => vi.fn());
vi.mock("./contactApi", () => ({ contactApi: { live } }));
const { contactEmails, downloadContacts, pageContacts } = await import("./pageContacts");
await import("./contactOverview");
const { boardDocument, overviewKind } = await import("../overview/overviewKinds");

const card = (...contacts: MarkdownContact[]) => ({ type: "contactCard", attrs: { data: { version: 1, contacts } } });
const mention = (contact: MarkdownContact) => ({ type: "paragraph", content: [{ type: "text", text: "Volejte " }, { type: "contactInline", attrs: { data: { version: 1, contacts: [contact] } } }] });
const jana: MarkdownContact = { name: "Jana Nováková", organization: "Nemocnice", emails: ["jana@hospital.example.test"], objectId: "AAA" };
const petr: MarkdownContact = { name: "Petr Malý", organization: "AB Klinika", emails: ["petr@partner.example.test", "jana@hospital.example.test"], objectId: "bbb" };
const eva: MarkdownContact = { name: "Eva", emails: ["eva@example.cz"] };

afterEach(() => { vi.restoreAllMocks(); live.mockReset(); });

describe("the contacts overview", () => {
  it("collects the page's cards and mentions once each, by organization, without a template's empty place or an unresolved name", () => {
    const document = { type: "doc", content: [card(eva, jana), mention({ ...jana, objectId: "aaa", name: "Jana" }), mention({ name: "PM", role: "PM", unresolved: true }), card(petr), mention(eva)] };
    expect(pageContacts(document).map(contact => contact.name)).toEqual(["Petr Malý", "Jana Nováková", "Eva"]);
    expect(contactEmails(pageContacts(document))).toBe("petr@partner.example.test, jana@hospital.example.test, eva@example.cz");
  });

  it("downloads all of them as one vCard file", async () => {
    let file: Blob | undefined;
    vi.spyOn(URL, "createObjectURL").mockImplementation(blob => { file = blob as Blob; return "blob:x"; });
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) { expect(this.download).toBe("kontakty.vcf"); });
    downloadContacts([jana, petr, eva]);
    const text = await file!.text();
    expect(text.match(/BEGIN:VCARD/g)).toHaveLength(3);
    expect(text).toContain("FN:Petr Malý");
    expect(text).toContain("EMAIL:eva@example.cz");
  });

  it("collects a board's contact cards (live), contact blocks and mentions in its texts with the page's collect (boardDocument)", async () => {
    live.mockImplementation(async (id: string) => (id === "ccc" ? { state: "ok", contact: { name: "Karel", objectId: "ccc" } } : { state: "missing" }));
    const people = pageContacts(await boardDocument(overviewKind("contacts")!, [
      { kind: "object", props: { typeId: "atlas.contact", objectId: "CCC" } },
      { kind: "object", props: { typeId: "atlas.page", objectId: "ddd" } },
      { kind: "block", props: { block: card(jana) } },
      { kind: "note", text: "Ptát se [Jana](a-contact:?name=Jana&objectId=aaa) a [Nikdo](a-contact:?name=Nikdo&objectId=eee)" },
      { kind: "object", props: { typeId: "atlas.contact", objectId: "fff" }, deleted: true },
    ]));
    expect(people.map(contact => contact.name)).toEqual(["Jana Nováková", "Karel"]);
    expect(live.mock.calls.map(([id]) => id)).toEqual(["ccc", "eee"]);
  });
});
