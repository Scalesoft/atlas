// @vitest-environment node
import { expect, it } from "vitest";
import { tellTwins } from "./spaceTwins";

const of = (blurbs: Array<string | undefined>) => tellTwins(blurbs, () => "x", blurb => blurb);

it("uses the description only when it is unique among the twins, else the order", () => {
  expect(of(["Prvni popis", undefined, "Prvni popis"])).toEqual(["1. z 3", "2. z 3", "3. z 3"]);
  expect(of(["A", "B"])).toEqual(["A", "B"]);
  expect(of(["A", undefined])).toEqual(["A", "2. z 2"]);
});

it("leaves a space without a twin alone", () => {
  expect(tellTwins(["a", "b", "b"], name => name, () => undefined)).toEqual([undefined, "1. z 2", "2. z 2"]);
});
