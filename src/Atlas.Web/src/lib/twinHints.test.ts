// @vitest-environment node
import { expect, it } from "vitest";
import { twinHints } from "./twinHints";

it("gives options of one name that nothing else tells apart their order, and leaves the rest alone", () => {
  const options = [
    { label: "Jan Novák" }, { label: "Eva" }, { label: "jan novák" },
    { label: "Petr", description: "Provoz" }, { label: "Petr", description: "Vývoj" },
    { label: "Ota", description: "Provoz" }, { label: "Ota", description: "Provoz" },
  ];
  // Two Jan Nováks with nothing else: 1. z 2, 2. z 2 (names compare without case and diacritics); two Petrs with their own words need nothing.
  expect(twinHints(options)).toEqual(["1. z 2", undefined, "2. z 2", undefined, undefined, "1. z 2", "2. z 2"]);
});

it("gives a picker that shows no description the unique description of a twin, and the order where that is no help", () => {
  const options = [
    { label: "Jan Novák", description: "jan@a.cz" }, { label: "Jan Novák", description: "jan@b.cz" },
    { label: "Ota", description: "Provoz" }, { label: "Ota", description: "Provoz" }, { label: "Eva", description: "eva@a.cz" },
  ];
  expect(twinHints(options, false)).toEqual(["jan@a.cz", "jan@b.cz", "1. z 2", "2. z 2", undefined]);
  expect(twinHints(options)).toEqual([undefined, undefined, "1. z 2", "2. z 2", undefined]);
});

it("is silent for a list of different names", () => {
  expect(twinHints([{ label: "Jana" }, { label: "Petr" }])).toEqual([undefined, undefined]);
  expect(twinHints([])).toEqual([]);
});
