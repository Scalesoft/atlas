// @vitest-environment node
import { describe, expect, it } from "vitest";
import { editorKeymap } from "./editorKeymap";

describe("editor keymap in the help", () => {
  // 3.6: a page has no formatting row; everything it offered must stay listed (owner rule: no loss of function).
  it("lists every function the former formatting row offered", () => {
    const labels = editorKeymap.map(entry => entry.label).join("\n");
    for (const label of ["Tučně", "Kurzíva", "Přeškrtnuté", "Inline kód", "Odkaz", "Běžný text", "Nadpis 1", "Nadpis 4", "Blok kódu",
      "Odrážkový seznam", "Číslovaný seznam", "Checklist", "Časová odrážka", "Citace", "Informační box", "Tabulka"])
      expect(labels).toContain(label);
  });
});
