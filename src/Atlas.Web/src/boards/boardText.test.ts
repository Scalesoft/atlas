// @vitest-environment node
import { describe, expect, it } from "vitest";
import { cleanBoardText, mergeBoardText, noteFontSize } from "./boardText";

describe("a board text edited by two people at once (COL2-9)", () => {
  const base = "Plán: koupit mléko. Zavolat Janě.";
  it("takes whichever side changed when only one did", () => {
    expect(mergeBoardText(base, base, base)).toEqual({ text: base, conflict: false });
    expect(mergeBoardText(base, base + " Ahoj", base)).toEqual({ text: base + " Ahoj", conflict: false });
    expect(mergeBoardText(base, base, "Úkoly: " + base)).toEqual({ text: "Úkoly: " + base, conflict: false });
    expect(mergeBoardText(base, "Stejně", "Stejně")).toEqual({ text: "Stejně", conflict: false });
  });
  it("applies edits in separate places of the text, both of them", () => {
    expect(mergeBoardText(base, "Úkoly: " + base, base + " Hotovo.")).toEqual({ text: "Úkoly: " + base + " Hotovo.", conflict: false });
    expect(mergeBoardText(base, base.replace("mléko", "chléb"), base.replace("Janě", "Petrovi"))).toEqual({ text: "Plán: koupit chléb. Zavolat Petrovi.", conflict: false });
    expect(mergeBoardText(base, base.replace("Janě", "Petrovi"), base.replace("mléko", "chléb"))).toEqual({ text: "Plán: koupit chléb. Zavolat Petrovi.", conflict: false });
    // A deletion beside an insertion.
    expect(mergeBoardText("abc", "ac", "abXc")).toEqual({ text: "aXc", conflict: false });
  });
  it("keeps both versions of one place, the colleague's first, and says so", () => {
    expect(mergeBoardText(base, base.replace("mléko", "sýr"), base.replace("mléko", "máslo"))).toEqual({ text: "Plán: koupit máslo\n\nsýr. Zavolat Janě.", conflict: true });
    expect(mergeBoardText("x", "", "y")).toEqual({ text: "y\n\n", conflict: true });
  });
  it("keeps both texts two people appended at one place", () => {
    expect(mergeBoardText("", "Moje", "Tvoje")).toEqual({ text: "Tvoje\n\nMoje", conflict: true });
    expect(mergeBoardText("Úkoly:", "Úkoly: mléko", "Úkoly: chléb")).toEqual({ text: "Úkoly: chléb\n\nmléko", conflict: true });
  });
});

describe("board text the server accepts (Rules.IsMultiLineText, spec §4)", () => {
  it("keeps line breaks, turns a tab into spaces and drops NUL, other control and bidi characters", () => {
    expect(cleanBoardText("a\tb\u0000c\u0007d\u202Ee\u2066f\u200Fg\u061Ch\u0085i\r\nj\rk")).toBe("a    bcdefghi\nj\nk");
  });

  it("cuts a note at 4000 characters", () => {
    expect(cleanBoardText("x".repeat(4100))).toHaveLength(4000);
  });

  it("makes a connector label one line of at most 200 characters", () => {
    expect(cleanBoardText("vede\nk\tcíli", { line: true })).toBe("vede k    cíli");
    expect(cleanBoardText("y".repeat(300), { line: true })).toHaveLength(200);
  });
});

describe("note text that shrinks to fit (spec §3.2)", () => {
  it("writes a short note large", () => {
    expect(noteFontSize("Ahoj", 200, 200)).toBe(32);
  });

  it("shrinks as the text grows, and never below the minimum: the rest is clipped", () => {
    const sizes = [10, 40, 120, 300, 800].map(length => noteFontSize("slovo ".repeat(length / 6 + 1), 200, 200));
    expect(sizes).toEqual([...sizes].sort((a, b) => b - a));
    expect(sizes[0]).toBeGreaterThan(sizes.at(-1)!);
    expect(noteFontSize("x ".repeat(2000), 200, 200)).toBe(12);
  });

  it("counts the lines the author broke", () => {
    expect(noteFontSize("a\nb\nc\nd\ne\nf", 200, 200)).toBeLessThan(noteFontSize("abcdef", 200, 200));
  });

  // BD2-4: a long word on a small note was cut in the middle ("Zkontrolova|t") at 32 px.
  it("keeps the longest word whole: it sets a size at which it fits the width", () => {
    const font = noteFontSize("Zkontrolovat aktuálnost", 200, 200);
    expect(font).toBeLessThanOrEqual(26);
    expect(noteFontSize("Ahoj světe", 200, 200)).toBe(32);
  });

  // A note with a contact chip fell to 12 px: its stored target (`a-contact:?name=…&objectId=…`) counted as one long word.
  it("measures a chip or link by its label, not its target", () => {
    const chip = "[Jana Nováková](a-contact:?name=Jana%20Nov%C3%A1kov%C3%A1&organization=Modelsoft&objectId=097b5eae-6460-4c7c-acf9-e78941db72c3)";
    expect(noteFontSize(`Zavolat ${chip}`, 240, 160)).toBe(noteFontSize("Zavolat [Jana Nováková]", 240, 160));
    expect(noteFontSize(`Zavolat ${chip}`, 240, 160)).toBeGreaterThan(12);
  });

  it("keeps a size the author chose", () => {
    expect(noteFontSize("x ".repeat(2000), 200, 200, "l")).toBe(24);
  });
});
