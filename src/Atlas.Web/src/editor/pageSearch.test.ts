// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { findMatches, foldForSearch, indexPage, placeOf, rangeOf, snippetAround, stepResult } from "./pageSearch";

const find = (text: string, query: string, limit?: number) => {
  const folded = foldForSearch(text);
  return findMatches(folded, query, limit).matches.map(match => text.slice(match.start, match.end));
};

describe("page search", () => {
  it("ignores case and diacritics, as foldText does everywhere in Atlas", () => {
    expect(find("Příliš žluťoučký KŮŇ", "zlutoucky kun")).toEqual(["žluťoučký KŮŇ"]);
    expect(find("Smlouva o dílo", "DÍLO")).toEqual(["dílo"]);
  });

  it("maps matches back over decomposed text and astral characters", () => {
    const text = "Café 🙂 zakázka";
    expect(find(text, "cafe")).toEqual(["Cafe"]);
    expect(find(text, "zakazka")).toEqual(["zakázka"]);
  });

  it("treats any white space as one space but never matches across blocks", () => {
    expect(find("jedna dvě\ttři", "jedna dve tri")).toEqual(["jedna dvě\ttři"]);
    expect(find("konec\nzačátek", "konec zacatek")).toEqual([]);
  });

  it("returns matches in document order without overlaps and says when the limit cut them", () => {
    const folded = foldForSearch("aaaa ab aa");
    expect(findMatches(folded, "aa").matches).toEqual([{ start: 0, end: 2 }, { start: 2, end: 4 }, { start: 8, end: 10 }]);
    expect(findMatches(folded, "a", 3)).toEqual({ matches: [{ start: 0, end: 1 }, { start: 1, end: 2 }, { start: 2, end: 3 }], more: true });
    expect(findMatches(folded, "   ").matches).toEqual([]);
  });

  it("previews the words around a match within its block", () => {
    const text = "Nadpis\nPrvní věta odstavce, ve které je hledané slovo uprostřed a pak pokračuje dlouho dál až do konce bloku.\nDalší blok";
    const start = text.indexOf("hledané");
    expect(snippetAround(text, { start, end: start + 7 }, 20, 20)).toBe("… ve které je hledané slovo uprostřed a …");
    expect(snippetAround(text, { start: 0, end: 6 })).toBe("Nadpis");
  });

  it("steps to the next and previous result with wrap-around", () => {
    expect(stepResult(0, 3, 1)).toBe(1);
    expect(stepResult(2, 3, 1)).toBe(0);
    expect(stepResult(0, 3, -1)).toBe(2);
    expect(stepResult(0, 0, 1)).toBe(-1);
  });

  it("reads the rendered page, hidden parts included, and turns matches into ranges with their place", () => {
    document.body.innerHTML = `<section><p>AI shrnutí mimo dokument: smlouva</p><div class="ProseMirror">
      <h2>Smlouvy<button>smlouva</button></h2><p>Rámcová <strong>smlou</strong>va platí.</p>
      <div class="atlas-heading-folded"><p>Skrytá smlouva</p></div>
      <div data-requirement-view data-requirement-title="Podpis"><div class="requirement-content"><p>Podpis</p><p style="display:none">Poznámka ke smlouvě</p></div></div>
    </div></section>`;
    const index = indexPage(document.querySelector("section")!);
    const { matches } = findMatches(index.text, "smlouv");
    // The heading itself, the paragraph across its bold part, the folded section, the requirement's hidden note; not the button nor the summary.
    expect(matches.map(match => rangeOf(index, match)!.toString())).toEqual(["Smlouv", "smlouv", "smlouv", "smlouv"]);
    expect(rangeOf(index, matches[1]!)!.startContainer.textContent).toBe("smlou");
    expect(placeOf(index, matches[1]!)).toBe("Smlouvy");
    expect(placeOf(index, matches[3]!)).toBe("Smlouvy › Podpis");
  });
});
