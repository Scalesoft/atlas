// @vitest-environment node
import { expect, it } from "vitest";
import { cleanSnippet, highlightSearch } from "./searchHighlight";
it("matches unaccented input while preserving the original Czech text", () => {
  const result = highlightSearch("Přihlášení přes firemní účty", "prihlaseni ucty");
  expect(result.filter(x => x.matched).map(x => x.text)).toEqual(["Přihlášení", "účty"]);
  expect(result.map(x => x.text).join("")).toBe("Přihlášení přes firemní účty");
});
it("preserves literal markup and distinguishes technical identifiers", () => {
  const source = "<img onerror=alert(1)> C++ a C#";
  const result = highlightSearch(source, "C++");
  expect(result.filter(x => x.matched).map(x => x.text)).toEqual(["C++"]);
  expect(result.map(x => x.text).join("")).toBe(source);
});
it("strips a thematic break and bold marks from a snippet fragment", () => {
  expect(cleanSnippet("… --- Naposledy **tip** ověřeno")).toBe("… Naposledy tip ověřeno");
});
it("strips heading marks", () => {
  expect(cleanSnippet("# Nadpis sekce popisuje postup")).toBe("Nadpis sekce popisuje postup");
});
it("strips blockquote marks", () => {
  expect(cleanSnippet("> Citace z dokumentace")).toBe("Citace z dokumentace");
});
it("turns a markdown link into its visible text", () => {
  expect(cleanSnippet("Viz [dokumentaci](https://example.com) pro detaily")).toBe("Viz dokumentaci pro detaily");
});
it('keeps a link label when the search excerpt cuts its destination short', () => {
  expect(cleanSnippet('Viz [dokumentaci](https://example.com/very-long')).toBe('Viz dokumentaci');
});
it('decodes common entities in plain text excerpts', () => {
  expect(cleanSnippet('Settings &gt; Git hooks &amp; SSH')).toBe('Settings > Git hooks & SSH');
});
it("strips backticks and __underscore__ bold", () => {
  expect(cleanSnippet("Spusťte `dotnet build` a __počkejte__ na dokončení")).toBe("Spusťte dotnet build a počkejte na dokončení");
});
it("strips a single-asterisk italic emphasis", () => {
  expect(cleanSnippet("Text je *zvýrazněný* kurzívou")).toBe("Text je zvýrazněný kurzívou");
});
it("collapses the whitespace left behind by stripped markdown and trims the ends", () => {
  expect(cleanSnippet("  ***  Dvojitá emphasis na okrajích  ***  ")).toBe("Dvojitá emphasis na okrajích");
});
it("does not mangle an identifier with underscores that is not emphasis (finding 19)", () => {
  expect(cleanSnippet("Proměnná se jmenuje snake_case_name v kódu")).toBe("Proměnná se jmenuje snake_case_name v kódu");
});
it("treats letters with diacritics as word characters at the emphasis boundary (review)", () => {
  // \w is ASCII-only; a Czech letter before the star must count as "inside a word" too.
  expect(cleanSnippet("nekonzistentní*formátování* text")).toBe("nekonzistentní*formátování* text");
  expect(cleanSnippet("Nová_tabulka_dat se načte")).toBe("Nová_tabulka_dat se načte");
  expect(cleanSnippet("Slovo *zvýrazněné* končí")).toBe("Slovo zvýrazněné končí");
});
it("does not mangle asterisks used as a multiplication operator (finding 19)", () => {
  expect(cleanSnippet("Výsledek je a * b * c po výpočtu")).toBe("Výsledek je a * b * c po výpočtu");
});

// Snippets as the search returns them for the seeded and imported wiki (refresh 2.5, clean snippets).
it.each([
  ["> [!CAUTION] > Stránka je udržována týmem platformy. Služba komunikuje", "Stránka je udržována týmem platformy. Služba komunikuje"],
  ["WARNING] > Pozor: platí pouze pro verzi 2025.4 a novější.", "Pozor: platí pouze pro verzi 2025.4 a novější."],
  ["…!NOTE] > Návod předpokládá přístup do interní VPN.", "… Návod předpokládá přístup do interní VPN."],
  ["## Table of versions > [!NOTE] > For all project, that are", "Table of versions For all project, that are"],
  ["> **Poznámka:** Portál používá databázového uživatele", "Portál používá databázového uživatele"],
  ["jinak se vymažou všechny čipy !!! > > {.is-danger} {.is-warning} > Heslo je v Keepass {.is-info} 1. Nejdříve", "jinak se vymažou všechny čipy !!! Heslo je v Keepass 1. Nejdříve"],
  ["| Nazev | URL | Poznámka | | --- | --- | --- | | Liferay portal | [https://portal.example.test](https://portal.example.test/) | dostupný", "Nazev · URL · Poznámka · Liferay portal · https://portal.example.test · dostupný"],
  ["| Albánie | **AL** | | --- | --- | | Alžírsko | **DZ** |", "Albánie · AL · Alžírsko · DZ"],
  [String.raw`\[Potřeba doplnit informace\] Poznámka: Portál`, "[Potřeba doplnit informace] Poznámka: Portál"],
  [String.raw`## Bootcamp 2026 Odkaz na návod\ Postup`, "Bootcamp 2026 Odkaz na návod Postup"],
  ["Environment: ProductionUPMD URL: - <https://", "Environment: ProductionUPMD URL: - https://"],
  ["Vytvoření DB tabulek z dumpu: ``` DROP DATABASE xxx; ``` Vytvoření", "Vytvoření DB tabulek z dumpu: DROP DATABASE xxx; Vytvoření"],
  ["```bash ssh deploy@app01 sudo systemctl restart reporting ``` ## Řešení potíží", "ssh deploy@app01 sudo systemctl restart reporting Řešení potíží"],
  ["Viz ![schéma](/api/attachments/1/download) a <b>tučné</b> <br/> řádky<!-- skryté -->", "Viz schéma a tučné řádky"],
  ["ke stažení: **Testovací identity - výběr a plná", "ke stažení: Testovací identity - výběr a plná"],
  ["Postup (viz [IdentityServer4](https://github.com/IdentityServer/IdentityServer4) a [https://", "Postup (viz IdentityServer4 a https://"],
  ["~~staré~~ ==nové== hodnoty - [x] hotovo", "staré nové hodnoty hotovo"],
])("cleans %j", (raw, clean) => {
  expect(cleanSnippet(raw)).toBe(clean);
});
it("keeps what only looks like syntax: channels, comparisons, code pipes and escaped characters", () => {
  expect(cleanSnippet("dotazy směřujte do kanálu #platforma.")).toBe("dotazy směřujte do kanálu #platforma.");
  expect(cleanSnippet("Podmínka a|b platí, když x < y")).toBe("Podmínka a|b platí, když x < y");
  expect(cleanSnippet(String.raw`Cena je 5 \* 3 a soubor \_draft\_.md`)).toBe("Cena je 5 * 3 a soubor _draft_.md");
});
it("highlights the cleaned text, so a match never points into removed syntax", () => {
  const clean = cleanSnippet("> [!NOTE] > Rotace **certifikátů** na IIS");
  expect(clean).toBe("Rotace certifikátů na IIS");
  const parts = highlightSearch(clean, "certifikatu");
  expect(parts.filter((part) => part.matched).map((part) => part.text)).toEqual(["certifikátů"]);
  expect(parts.map((part) => part.text).join("")).toBe(clean);
});
