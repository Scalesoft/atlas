// @vitest-environment node
import { describe, expect, it } from "vitest";
import { allowedBoardLink, escapeInlineMarkdown, flattenInlineMarkdown, isPlainBoardText } from "./boardMarkdownText";

describe("allowedBoardLink", () => {
  const origin = "https://atlas.example";
  it("allows http(s), mailto and internal links", () => {
    for (const href of ["https://example.cz/a", "http://intranet/x", "mailto:jana@example.cz", "#/pages/3f2b?heading=postup", "/api/files/1", "https://atlas.example/#/boards/1"])
      expect(allowedBoardLink(href, origin), href).toBe(true);
  });
  it("refuses every other scheme and what is no link", () => {
    for (const href of ["javascript:alert(1)", "JaVaScRiPt:alert(1)", "data:text/html,x", "tel:+420777", "ftp://x", "vbscript:x", "", " https://x"])
      expect(allowedBoardLink(href, origin), href).toBe(false);
  });
});

describe("escapeInlineMarkdown", () => {
  it("keeps a line that starts like a block as text", () => {
    expect(escapeInlineMarkdown("# Nadpis")).toBe("\\# Nadpis");
    expect(escapeInlineMarkdown("- položka")).toBe("\\- položka");
    expect(escapeInlineMarkdown("> citace")).toBe("\\> citace");
    expect(escapeInlineMarkdown("1. krok")).toBe("1\\. krok");
    expect(escapeInlineMarkdown("---")).toBe("\\---");
    expect(escapeInlineMarkdown("```js")).toBe("\\```js");
  });
  it("leaves inline Markdown alone and puts the text on one line", () => {
    expect(escapeInlineMarkdown("**tučně** a _kurzíva_ #1")).toBe("**tučně** a _kurzíva_ #1");
    expect(escapeInlineMarkdown("a\n# b")).toBe("a # b");
  });
});

describe("flattenInlineMarkdown", () => {
  it("drops block markup and joins the lines, keeping inline marks", () => {
    expect(flattenInlineMarkdown("# Nadpis")).toBe("Nadpis");
    expect(flattenInlineMarkdown("- [ ] úkol\n- [x] hotovo")).toBe("úkol hotovo");
    expect(flattenInlineMarkdown("> citace")).toBe("citace");
    expect(flattenInlineMarkdown("1. krok\n\n2) další")).toBe("krok další");
    expect(flattenInlineMarkdown("**a** _b_")).toBe("**a** _b_");
  });
});

describe("isPlainBoardText (BD2-1)", () => {
  it("takes the plain words of a note, a label, a title", () => {
    for (const text of ["Poznámka", "Krok 12", "Vlastník: Provoz", "Zkontrolovat aktuálnost", "Verze 1.2 hotova.", "Co dál? (nejspíš ano)", "Cena 5 €", "Uvozovky „české“ a \"rovné\"", "Text 1-2", "Praha-Západ", "A/B test", "Tom & Jerry", "Dnes — zítra…"])
      expect(isPlainBoardText(text), text).toBe(true);
  });
  it("leaves everything the Markdown could read as more than one paragraph of the text to the renderer", () => {
    for (const text of ["", " mezera", "1. bod", "# Nadpis", "- odrážka", "**tučně**", "_kurzíva_", "a\nb", "a  b", "konec ", "www.example.cz", "a.cz", "https://example.cz", "[odkaz](#/x)", "x_y", "a &amp; b", "- a-b", "<b>", "a|b", "1) pět", "==důležité==", ":smile:", "@jana", "a&b", "x".repeat(601)])
      expect(isPlainBoardText(text), JSON.stringify(text)).toBe(false);
  });
});
