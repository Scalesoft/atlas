// @vitest-environment node
import { describe, expect, it } from "vitest";
import { foldText } from "./foldText";

describe("foldText", () => {
  it("lower-cases and drops Czech diacritics, composed or already decomposed", () => {
    const composed = "Příliš žluťoučký kůň";
    expect(composed.normalize("NFC")).toBe(composed);
    expect(foldText(composed)).toBe("prilis zlutoucky kun");
    const decomposed = composed.normalize("NFD");
    expect(decomposed).not.toBe(composed);
    expect(decomposed.length).toBeGreaterThan(composed.length);
    expect(foldText(decomposed)).toBe("prilis zlutoucky kun");
    expect(foldText("")).toBe("");
  });

  it("folds the dotted capital I to a plain i: the dot is a mark, dropped before lower-casing", () => {
    const dotted = String.fromCodePoint(0x130);
    expect(foldText(`I${dotted}I`)).toBe("iii");
  });

  it("drops every mark by default, only the diacritics block on request", () => {
    const devanagariKi = String.fromCodePoint(0x915, 0x93f);
    expect(foldText(devanagariKi)).toBe(String.fromCodePoint(0x915));
    expect(foldText(devanagariKi, "block")).toBe(devanagariKi);
    expect(foldText("Žluťoučký", "block")).toBe(foldText("Žluťoučký"));
  });
});
