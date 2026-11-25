// @vitest-environment node
import { describe, expect, it } from "vitest";
import { quoteMarkdown } from "./commentFocus";

describe("quoteMarkdown", () => {
  it("makes one Markdown quote of a selection and leaves the caret a line below it", () => {
    expect(quoteMarkdown("  Záloha\n běží   každou noc ")).toBe("> Záloha běží každou noc\n\n");
    expect(quoteMarkdown("  \n ")).toBe("");
  });

  it("cuts a long selection at a word: the quote points at the place, it is not a copy", () => {
    const quote = quoteMarkdown("slovo ".repeat(100), 20);
    expect(quote).toBe("> slovo slovo slovo…\n\n");
  });
});
