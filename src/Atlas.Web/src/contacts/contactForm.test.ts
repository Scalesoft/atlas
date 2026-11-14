// @vitest-environment node
import { describe, expect, it } from "vitest";
import { normalizeContact } from "@atlas/markdown/plugins/contacts";
import { emptyContactForm, validateContactForm, type ContactFormValues } from "./contactForm";

const form = (values: Partial<ContactFormValues>): ContactFormValues => ({ ...emptyContactForm(), ...values });
const validate = (values: Partial<ContactFormValues>) => validateContactForm(form(values), normalizeContact);

describe("validateContactForm: the rules of the editor's contact", () => {
  it("makes the contact of the board's contract from the filled fields, one e-mail and one phone per line", () => {
    const result = validate({
      name: "  Jana Nováková ", title: "Vedoucí provozu", organization: "Modelsoft", department: "Provoz",
      emails: "jana@example.cz\njana.n@example.cz\n\n", phones: "+420 777 123 456\n(02) 555-12.34", website: "https://example.test", address: "Hlavní 1\nPraha",
    });
    expect(result.errors).toEqual({});
    expect(result.contact).toEqual({
      name: "Jana Nováková", title: "Vedoucí provozu", organization: "Modelsoft", department: "Provoz",
      emails: ["jana@example.cz", "jana.n@example.cz"], phones: ["+420 777 123 456", "(02) 555-12.34"], website: "https://example.test", address: "Hlavní 1\nPraha",
    });
  });

  it("needs only a name, which is the editor's one required field", () => {
    expect(validate({ name: "Karel" })).toEqual({ errors: {}, contact: { name: "Karel" } });
    const empty = validate({ name: "   ", title: "Vedoucí" });
    expect(empty.contact).toBeNull();
    expect(empty.errors.name).toBe("Vyplňte pole: jméno.");
  });

  it("names the field that is wrong, with the editor's own wording, and every wrong field at once", () => {
    const result = validate({ name: "Karel", emails: "karel@", phones: "abc" });
    expect(result.contact).toBeNull();
    expect(result.errors).toEqual({ emails: "Zkontrolujte e-mailové adresy.", phones: "Zkontrolujte telefonní čísla." });
  });

  it("allows at most 20 phones and 20 e-mails", () => {
    const twenty = Array.from({ length: 20 }, (_, index) => `a${index}@example.cz`).join("\n");
    expect(validate({ name: "A", emails: twenty }).errors).toEqual({});
    expect(validate({ name: "A", emails: `${twenty}\nz@example.cz` }).errors.emails).toBe("Kontakt může mít nejvýše 20 telefonů a e-mailů.");
    const phones = Array.from({ length: 21 }, (_, index) => `+420 777 000 ${String(index).padStart(3, "0")}`).join("\n");
    expect(validate({ name: "A", phones }).errors.phones).toBe("Kontakt může mít nejvýše 20 telefonů a e-mailů.");
  });

  it("accepts the separators people type between several addresses on one line", () => {
    expect(validate({ name: "A", emails: "a@example.cz, b@example.cz; c@example.cz" }).contact?.emails).toEqual(["a@example.cz", "b@example.cz", "c@example.cz"]);
  });

  it("writes a website without a scheme as https, and refuses one that is no web address", () => {
    expect(validate({ name: "A", website: "example.test" }).contact?.website).toBe("https://example.test");
    expect(validate({ name: "A", website: "javascript:alert(1)" }).errors.website).toBeTruthy();
  });

  it("refuses a field that is too long", () => {
    expect(validate({ name: "A", title: "x".repeat(4097) }).errors.title).toBe("Neplatné pole: Funkce.");
  });

  it("lets no refused character through to the board: a tab is a space, control and bidirectional characters go", () => {
    const bidi = String.fromCharCode(0x202e, 0x2066);
    const result = validate({ name: `Jana${bidi}\tNováková`, title: "Ve\tdoucí\u0085", emails: `jana@example.cz${bidi}`, phones: "+420\t777 123", address: `Hlavní 1\u0085\n${bidi}Praha` });
    expect(result.errors).toEqual({});
    expect(result.contact).toEqual({ name: "Jana Nováková", title: "Ve doucí", emails: ["jana@example.cz"], phones: ["+420 777 123"], address: "Hlavní 1\nPraha" });
  });
});
