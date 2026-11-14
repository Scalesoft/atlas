// @vitest-environment node
import { describe, expect, it } from "vitest";
import { contactFromWidget, contactHitsOf } from "./contactHits";

describe("contactFromWidget", () => {
  it("is a name alone when the hit says no more", () => {
    expect(contactFromWidget({ kind: "contact", title: "Karel Novák", detail: "" })).toEqual({ name: "Karel Novák" });
  });

  it("recognizes e-mails, phones and a website in the flattened detail by their shape", () => {
    const widget = { kind: "contact", title: "Jana Nováková", detail: "jana@example.cz · +420 777 123 456 · https://example.test/jana" };
    expect(contactFromWidget(widget)).toEqual({ name: "Jana Nováková", emails: ["jana@example.cz"], phones: ["+420 777 123 456"], website: "https://example.test/jana" });
  });

  it("keeps the rest of the detail, in its order, as the line under the name, never guessing which of function, organization or login it was", () => {
    const widget = { kind: "contact", title: "Jana Nováková", detail: "Vedoucí provozu · Modelsoft · Provoz · jnovakova · jana@example.cz · Hlavní 1, Praha" };
    expect(contactFromWidget(widget)).toEqual({ name: "Jana Nováková", title: "Vedoucí provozu · Modelsoft · Provoz · jnovakova · Hlavní 1, Praha", emails: ["jana@example.cz"] });
  });

  it("does not take a number that is no phone (a year, a house number) for one", () => {
    expect(contactFromWidget({ kind: "contact", title: "A", detail: "Modelsoft 2024 · 12 · +420 777 123 456" })).toEqual({ name: "A", title: "Modelsoft 2024 · 12", phones: ["+420 777 123 456"] });
  });

  it("keeps an address that is no valid e-mail in the text", () => {
    expect(contactFromWidget({ kind: "contact", title: "A", detail: "jana@@example.cz" })).toEqual({ name: "A", title: "jana@@example.cz" });
  });

  it("prefers the structured contact when the search sends one", () => {
    const widget = { kind: "contact", title: "Jana", detail: "Vedoucí · Modelsoft", contact: { name: "Jana Nováková", title: "Vedoucí", organization: "Modelsoft", username: "jana", emails: ["jana@example.cz"], directoryId: "d1" } };
    expect(contactFromWidget(widget)).toEqual({ name: "Jana Nováková", title: "Vedoucí", organization: "Modelsoft", username: "jana", emails: ["jana@example.cz"], directoryId: "d1" });
  });

  it("is nothing without a name, and carries no character the server refuses", () => {
    expect(contactFromWidget({ kind: "contact", title: "  ", detail: "x" })).toBeNull();
    expect(contactFromWidget({ kind: "contact", title: `Jana\tN${String.fromCharCode(0x202e)}ováková`, detail: "Ve\tdoucí\u0007" })).toEqual({ name: "Jana Nováková", title: "Ve doucí" });
  });
});

describe("contactHitsOf", () => {
  const hit = (title: string, detail: string, page = "Adresář") => ({ title: page, spaceName: "Provoz", widgets: [{ kind: "contact", title, detail }, { kind: "youtrack", title: "ATL-1", detail: "x" }] });

  it("takes the contact widgets of the results, with the page they were found on, and leaves the rest", () => {
    const hits = contactHitsOf([hit("Jana Nováková", "Vedoucí · jana@example.cz")]);
    expect(hits).toEqual([{
      key: "Jana Nováková\u0000Vedoucí · jana@example.cz", contact: { name: "Jana Nováková", title: "Vedoucí", emails: ["jana@example.cz"] },
      detail: "Vedoucí · jana@example.cz", source: "Adresář · Provoz",
    }]);
  });

  it("shows one hit for the same contact written on several pages, and two for two people of the same name", () => {
    const hits = contactHitsOf([hit("Jana", "jana@example.cz", "A"), hit("Jana", "jana@example.cz", "B"), hit("Jana", "jana@another.example.test", "C")]);
    expect(hits.map(item => item.detail)).toEqual(["jana@example.cz", "jana@another.example.test"]);
    expect(hits[0]!.source).toBe("A · Provoz");
  });

  it("skips a widget without a name and results without widgets", () => {
    expect(contactHitsOf([{ title: "P", spaceName: "S" }, { title: "P", spaceName: "S", widgets: [{ kind: "contact", title: " ", detail: "x" }] }])).toEqual([]);
  });

  it("stops at 50", () => {
    const many = Array.from({ length: 80 }, (_, index) => hit(`Kontakt ${index}`, "x"));
    expect(contactHitsOf(many)).toHaveLength(50);
  });
});
