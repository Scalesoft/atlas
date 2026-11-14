// @vitest-environment node
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { normalizeContact } from "@atlas/markdown/plugins/contacts";
import { ApiError } from "../api";
import { contactData, contactFieldEdit, contactPatch, duplicateContactId, maxContactAddress, maxContactLine, mergedContact, mergeOverCapacity, withoutContactMentions } from "./contactData";

// The card's editor lets through exactly what the server takes (UX3-2): a smaller limit would cut a field silently.
it("limits a contact's line fields and address as the server does", () => {
  const server = readFileSync(resolve(__dirname, "../../../Atlas.Server/Features/Contacts/Domain/ContactFields.cs"), "utf8");
  const of = (name: string) => Number(new RegExp(`const int ${name} = ([\\d_]+);`).exec(server)?.[1]?.replace(/_/g, ""));
  expect([maxContactLine, maxContactAddress]).toEqual([of("MaxLine"), of("MaxAddress")]);
});

describe("contactData", () => {
  it("keeps what the directory stores of an editor's contact: login, display kind and internal identity stay with the page", () => {
    expect(contactData({ name: "Jana", title: "Ředitelka", username: "jana", kind: "internal", emails: ["jana@example.com"], internalUser: { id: "u1" } }))
      .toEqual({ name: "Jana", title: "Ředitelka", emails: ["jana@example.com"] });
  });
});

describe("contactPatch", () => {
  it("names only the fields that differ, a cleared one as null", () => {
    expect(contactPatch({ name: "Jana", title: "Ředitelka", emails: ["a@b.cz"] }, { name: "Jana", emails: ["a@b.cz", "c@d.cz"], phones: ["777"] }))
      .toEqual({ title: null, emails: ["a@b.cz", "c@d.cz"], phones: ["777"] });
    expect(contactPatch({ name: "Jana" }, { name: "Jana" })).toEqual({});
  });
});

describe("contactFieldEdit", () => {
  const jana = { name: "**Jana**", title: "Ředitelka", emails: ["jana@example.com"], website: "https://example.test" };

  it("validates one edited field by the editor's rules and answers the change of just that field", () => {
    expect(contactFieldEdit(jana, "title", "Vedoucí _provozu_", normalizeContact)).toEqual({ patch: { title: "Vedoucí _provozu_" } });
    expect(contactFieldEdit(jana, "emails", "jana@example.com\nj.n@firma.example.test", normalizeContact)).toEqual({ patch: { emails: ["jana@example.com", "j.n@firma.example.test"] } });
    expect(contactFieldEdit(jana, "website", "", normalizeContact)).toEqual({ patch: { website: null } });
    expect(contactFieldEdit(jana, "website", "example.test/kontakt", normalizeContact)).toEqual({ patch: { website: "https://example.test/kontakt" } });
    expect(contactFieldEdit(jana, "title", "Ředitelka", normalizeContact)).toEqual({ patch: {} });
  });

  it("names what is wrong and changes nothing", () => {
    expect(contactFieldEdit(jana, "emails", "not an address", normalizeContact).error).toBeTruthy();
    expect(contactFieldEdit(jana, "name", "  ", normalizeContact).error).toBeTruthy();
  });
});

describe("duplicateContactId", () => {
  it("is the existing contact's id of a duplicate_contact refusal and nothing else", () => {
    expect(duplicateContactId(new ApiError(409, "Kontakt už v adresáři je.", "duplicate_contact", { contactId: "c1" }))).toBe("c1");
    expect(duplicateContactId(new ApiError(409, "Konflikt", "version_conflict", { contactId: "c1" }))).toBeNull();
    expect(duplicateContactId(new Error("x"))).toBeNull();
  });
});

describe("mergedContact", () => {
  it("keeps the target's fields, fills its empty ones from the source and joins e-mails and phones", () => {
    expect(mergedContact({ name: "Karel Novák", department: "Podpora", phones: ["+420603000222"], emails: ["karel@a.cz"] },
      { name: "K. Novák", organization: "Modelsoft", department: "Vývoj", phones: ["+420603000222", "+420602000111"] }))
      .toEqual({ name: "Karel Novák", department: "Podpora", organization: "Modelsoft", phones: ["+420603000222", "+420602000111"], emails: ["karel@a.cz"] });
  });

  it("joins a phone and an address the target has in another spelling once, as the server does", () => {
    expect(mergedContact({ name: "Jana", phones: ["+420 777 111 222"], emails: ["jana@firma.example.test"] }, { name: "Jana", phones: ["777 111 222", "00420 777 111 222"], emails: ["JANA@firma.example.test", "jana.n@home.example.test"] }))
      .toEqual({ name: "Jana", phones: ["+420 777 111 222"], emails: ["jana@firma.example.test", "jana.n@home.example.test"] });
  });

  // The server refuses a union over its 20 entries before any change (MergeContactsHandler.Merged) and says the same.
  it("tells a merge whose list would exceed 20 entries, and none that fits", () => {
    const list = (prefix: string, count: number) => Array.from({ length: count }, (_, i) => `${prefix}${i}`);
    expect(mergeOverCapacity({ name: "Jana", phones: list("+42060200000", 10).concat(list("+42060300000", 10)) }, { name: "Jana", phones: ["+420604000000"] }))
      .toBe("Kontakty nejde sloučit: pole „Telefon“ by mělo 21 hodnot (cílový kontakt 20, ze zdrojového 1 dalších), kontakt jich unese nejvýše 20. Nejdřív u jednoho z nich některé odeberte.");
    expect(mergeOverCapacity({ name: "Jana", aliases: list("J", 19) }, { name: "Jana", aliases: ["J0", "Mára"], emails: list("j@", 20) })).toBeNull();
  });
});

it("keeps the text of a contact mention written into a contact's own field, never the mention", () => {
  expect(withoutContactMentions("Zástupce [Petr \\] Svoboda](a-contact:?name=Petr) a [web](https://x.cz)")).toBe("Zástupce Petr ] Svoboda a [web](https://x.cz)");
  expect(contactFieldEdit({ name: "Jana" }, "title", "Asistentka [Jana Nováková](a-contact:?name=Jana%20Nov%C3%A1kov%C3%A1&objectId=0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b)", normalizeContact))
    .toEqual({ patch: { title: "Asistentka Jana Nováková" } });
});
