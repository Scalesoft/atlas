// @vitest-environment node
import { expect, it } from "vitest";
import { boardKeymap, boardToolKeys } from "./boardKeymap";

it("names every tool letter the board answers to, in the help's shape", () => {
  const tools = boardKeymap[0]!.keys.split(" ");
  expect(Object.keys(boardToolKeys).map(key => key.toUpperCase()).sort()).toEqual([...tools].sort());
  expect(boardKeymap.every(entry => entry.group === "Tabule" && entry.keys && entry.label)).toBe(true);
});

it("teaches D1: Enter and F2 edit, Ctrl+Enter opens, the actions menu has its keys", () => {
  const label = (keys: string) => boardKeymap.find(entry => entry.keys === keys)?.label;
  expect(label("Enter F2")).toMatch(/^Upravit/);
  expect(label("Ctrl Enter")).toMatch(/^Otevřít/);
  expect(label("Ctrl Mezerník, Alt Enter, Ctrl ., Shift F10")).toMatch(/^Co s tím jde dělat/);
  expect(label("Delete")).toContain("Odebrat z tabule");
});
