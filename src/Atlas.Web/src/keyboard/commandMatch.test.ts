// @vitest-environment node
import { describe, expect, it } from "vitest";
import { matchCommands } from "./commandMatch";

const items = [
  { id: "copy", label: "Kopírovat odkaz na stránku", detail: "Návod" },
  { id: "create", label: "Nová stránka", detail: "Provoz" },
  { id: "space-ops", label: "Přejít do prostoru Provoz", detail: "Prostor", keywords: ["ops"] },
  { id: "access", label: "Kdo má přístup", detail: "Sdílení stránky", keywords: ["sdílet", "oprávnění"] },
  { id: "drafts", label: "Moje koncepty", detail: "Knihovna" },
];

describe("matching commands", () => {
  it("keeps the order for an empty query", () => {
    expect(matchCommands("", items).map(item => item.id)).toEqual(["copy", "create", "space-ops", "access", "drafts"]);
  });

  it("ignores case and diacritics", () => {
    expect(matchCommands("NOVA STR", items).map(item => item.id)).toEqual(["create"]);
    expect(matchCommands("pristup", items).map(item => item.id)).toEqual(["access"]);
  });

  it("puts a label that starts with the query before one that only contains it", () => {
    // „prov“: the detail of Nová stránka, a word of Přejít do prostoru Provoz.
    expect(matchCommands("prov", items).map(item => item.id)).toEqual(["space-ops", "create"]);
  });

  it("finds a command by its keywords", () => {
    expect(matchCommands("sdilet", items)[0]?.id).toBe("access");
  });

  it("accepts the words in any order", () => {
    expect(matchCommands("stranku odkaz", items).map(item => item.id)).toEqual(["copy"]);
  });

  it("returns nothing when a word matches nowhere", () => {
    expect(matchCommands("nova tabule", items)).toEqual([]);
  });
});
