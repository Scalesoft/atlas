// @vitest-environment node
import { describe, expect, it } from "vitest";
import type { PersonHit } from "../contacts/contactSearch";
import { partySuggestions } from "./partySuggestions";

const person = (key: string, name: string, more: Partial<PersonHit["contact"]> = {}): PersonHit => ({ key, contact: { name, ...more }, source: "directory", origin: "Adresář" });

describe("partySuggestions", () => {
  it("offers the names already used on the page first, then in the space, then people, each name once and only what matches", () => {
    const rows = partySuggestions("i", { page: ["IT AB Klinika", "Jana"], space: ["it ab klinika", "IT KBN"], people: [person("p1", "Iva Malá"), person("p2", "Eva")] });
    expect(rows.map(row => [row.value, row.description])).toEqual([
      ["IT AB Klinika", "Na této stránce"], ["IT KBN", "V prostoru"], ["Iva Malá", "Kontakt"],
    ]);
  });

  it("with nothing typed offers the page's names only", () => {
    expect(partySuggestions("", { page: ["IT AB Klinika"], space: ["IT KBN"], people: [person("p1", "Iva")] }).map(row => row.value)).toEqual(["IT AB Klinika"]);
  });

  it("names two people of one name apart by what fills the field and says why", () => {
    const rows = partySuggestions("novak", { page: [], space: [], people: [person("a", "Jan Novák", { organization: "Modelsoft" }), person("b", "Jan Novák", { emails: ["jan@office.example.test"] })] });
    expect(rows.map(row => row.value)).toEqual(["Jan Novák (Modelsoft)", "Jan Novák (jan@office.example.test)"]);
    expect(rows[0]!.description).toContain("Dva lidé tohoto jména");
  });

  it("keeps a used name that a contact also has as the contact, once", () => {
    const rows = partySuggestions("jana", { page: ["Jana Nováková"], space: [], people: [person("a", "Jana Nováková", { organization: "AB" })] });
    expect(rows.map(row => [row.value, row.description])).toEqual([["Jana Nováková", "Na této stránce · AB"]]);
  });

  it("links a person of the directory as the contact the party is (plan 3.1 §2.3), a used name too; anyone else stays text", () => {
    const rows = partySuggestions("jan", { page: ["Jana Nováková"], space: [], people: [
      { ...person("obj:c1", "Jana Nováková"), objectId: "c1" }, person("user:u1", "Jan Malý")] });
    expect(rows.map(row => [row.value, row.contactId])).toEqual([["Jana Nováková", "c1"], ["Jan Malý", undefined]]);
  });

  it("never lets a stored text party of the space beat the linked contact", () => {
    const rows = partySuggestions("jakub", { page: [], space: ["Jakub Bureš (Jiná nemocnice)"], people: [{ ...person("obj:c1", "Jakub Bureš"), objectId: "c1" }] });
    expect(rows.map(row => [row.value, row.contactId])).toEqual([["Jakub Bureš", "c1"], ["Jakub Bureš (Jiná nemocnice)", undefined]]);
  });
});
