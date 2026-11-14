// @vitest-environment node
import { describe, expect, it } from "vitest";
import { isIconName } from "./appearance";
import { appearanceIcons, findAppearanceIcons, type AppearanceIcon } from "./components/settings/appearanceIcons";
import { foldText } from "./lib/foldText";
import catalog from "./assets/icon-pack/catalog.json";

// The 87 plus a few pack entries, listed first so the order of the input proves nothing.
const pack: AppearanceIcon[] = [
  { id: "database-backup", label: "Záloha databáze", group: "Vývoj a data", words: "zálohování backup" },
  { id: "database-zap", label: "Databáze s bleskem", group: "Vývoj a data", words: "napájení zap" },
  { id: "server-cog", label: "Nastavení serveru", group: "Zařízení a sítě", words: "konfigurace cog" },
  { id: "hard-drive", label: "Pevný disk", group: "Zařízení a sítě", words: "databáze úložiště storage" },
  { id: "rocket", label: "Raketa", group: "Místa a doprava", words: "start launch" },
];
const icons = [...pack, ...appearanceIcons];
const ids = (query: string) => findAppearanceIcons(query, icons).map((icon) => icon.id);

describe("findAppearanceIcons", () => {
  // Whole label, start of the label, start of a word of the label, start of a keyword; diacritics are ignored.
  it.each(["databáze", "databaze"])("ranks %s by where it matches, the 87 before pack icons", (query) => {
    expect(ids(query)).toEqual(["database", "database-zap", "database-backup", "hard-drive"]);
  });

  it("finds a word of the label and an English keyword", () => {
    expect(ids("server")).toEqual(["server", "server-cog"]);
    expect(ids("launch")).toEqual(["rocket"]);
  });

  it("puts one of the 87 before a pack icon of the same rank", () => {
    const found = ids("data");
    expect(found.indexOf("table")).toBeGreaterThanOrEqual(0);
    expect(found.indexOf("table")).toBeLessThan(found.indexOf("hard-drive"));
  });

  // The category is a filter of its own (the pills of AppearanceIconBrowser): searched too, "data" would bring every
  // icon of "Vývoj a data". Two entries above are in "Zařízení a sítě" and two in "Vývoj a data".
  it("does not find an icon through the label of its category", () => {
    expect(ids("zařízení")).toEqual([]);
    expect(ids("Zařízení a sítě")).toEqual([]);
    expect(ids("vývoj")).toEqual(["code-xml", "line-chart"]);
  });

  // The sentence shown when nothing matches (AppearanceIconBrowser) suggests these words.
  it.each(["lidé", "data", "zámek"])("finds one of the 87 for the suggested word %s", (query) => {
    expect(findAppearanceIcons(query, appearanceIcons).length).toBeGreaterThan(0);
  });

  it("finds nothing for an unknown word and returns the list itself for an empty query", () => {
    expect(ids("qqq")).toEqual([]);
    expect(findAppearanceIcons("", icons)).toBe(icons);
  });
});

// The real catalog as the icon browser lists it: the 87 and the icons of the pack.
const everything: AppearanceIcon[] = [
  ...appearanceIcons,
  ...catalog.icons.map((icon) => ({ id: icon.id, label: icon.label, group: icon.category, words: icon.words })),
];
const startsAWord = (icon: AppearanceIcon, prefix: string) =>
  foldText(`${icon.label} ${icon.words ?? ""} ${icon.id}`).split(/[^\p{L}\p{N}]+/u).some((word) => word.startsWith(prefix));
const idsOf = (query: string) => findAppearanceIcons(query, everything).map((icon) => icon.id);

// Anywhere in a word, "a" found 1 849 of the 1 864 icons and "ar" 1 165, so the first keystroke drew almost all of them.
describe("findAppearanceIcons with a word of one or two characters", () => {
  it("finds an icon by a one-letter word only where a word of its label, keywords or id starts", () => {
    const found = findAppearanceIcons("a", everything);
    expect(found.length).toBeGreaterThan(0);
    // 839 today, almost all through the search words ("arrow", "add", "alert"): a guard against "anywhere", not a small number.
    expect(found.length).toBeLessThan(everything.length * 2 / 3);
    expect(found.filter((icon) => !startsAWord(icon, "a")).map((icon) => icon.id)).toEqual([]);
  });

  it("finds archive-like ids by the start of a word, not an icon that holds the letters inside a word", () => {
    const found = idsOf("ar");
    expect(found).toEqual(expect.arrayContaining(["archive-restore", "armenian-dram", "folder-archive"]));
    // "star" holds "ar" only inside its one word.
    expect(found).not.toContain("star");
    expect(found.length).toBeLessThan(400);
  });

  it("still finds a word of three characters anywhere in a word", () => {
    expect(idsOf("tar")).toContain("star");
  });
});

describe("isIconName", () => {
  it("accepts the longest Lucide name", () => {
    expect(isIconName("triangles-centerline-dashed-horizontal")).toBe(true);
  });

  it.each(["Folder", "a--b", "-a", "a".repeat(41), ""])("refuses %j", (name) => {
    expect(isIconName(name)).toBe(false);
  });
});
