// @vitest-environment node
import { expect, it } from "vitest";
import { rankExactFirst } from "./rankExactFirst";

const read = (title: string) => ({ title, keywords: title === "Objednávky otestovány" ? ["portal.orders.tested"] : [] });
const titles = ["Grafy", "Graf", "Objednávky otestovány", "SSL certifikát (portál + autentizační služba)", "Veřejné domény (portál)"];

it("puts a whole title first, then the editor's order", () => {
  expect(rankExactFirst("graf", titles, read)).toEqual(["Graf", "Grafy"]);
});

it("also finds a title by the beginnings of several words in any order, diacritics aside", () => {
  expect(rankExactFirst("otestov objedn", titles, read)).toEqual(["Objednávky otestovány"]);
  expect(rankExactFirst("portal ssl", titles, read)).toEqual(["SSL certifikát (portál + autentizační služba)"]);
  // One word stays the editor's ranking: titles, then keys.
  expect(rankExactFirst("portal", titles, read)).toEqual(["SSL certifikát (portál + autentizační služba)", "Veřejné domény (portál)", "Objednávky otestovány"]);
  expect(rankExactFirst("tested orders", titles, read)).toEqual(["Objednávky otestovány"]);
});
