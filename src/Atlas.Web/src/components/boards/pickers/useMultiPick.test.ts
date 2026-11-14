// @vitest-environment node
import { expect, it } from "vitest";
import { maxPicked, useMultiPick } from "./useMultiPick";

const option = (value: string) => ({ value, label: value });

it("keeps what was picked in the order it was picked, and picking again removes it", () => {
  const pick = useMultiPick();
  pick.toggle(option("b"));
  pick.toggle(option("a"));
  pick.toggle(option("c"));
  expect(pick.picked.value.map(item => item.value)).toEqual(["b", "a", "c"]);
  pick.toggle(option("a"));
  expect(pick.picked.value.map(item => item.value)).toEqual(["b", "c"]);
  expect(pick.has("b")).toBe(true);
  expect(pick.has("a")).toBe(false);
});

it("takes at most 100 by default, and still lets one go once it is full", () => {
  expect(maxPicked).toBe(100);
  const pick = useMultiPick();
  for (let index = 0; index < 120; index++) pick.toggle(option(`o${index}`));
  expect(pick.picked.value).toHaveLength(100);
  expect(pick.full.value).toBe(true);
  pick.toggle(option("o0"));
  expect(pick.picked.value).toHaveLength(99);
  expect(pick.full.value).toBe(false);
});

it("clears", () => {
  const pick = useMultiPick(2);
  pick.toggle(option("a"));
  pick.clear();
  expect(pick.picked.value).toEqual([]);
});
