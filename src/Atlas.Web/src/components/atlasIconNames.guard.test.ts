import { describe, expect, it } from "vitest";
import { globSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { iconMarkup } from "@atlas/markdown/icons";
import { atlasIconNames } from "./atlasIconNames";
import { isPageIcon } from '../pageDecorations';
import { appearanceIcons } from './settings/appearanceIcons';
import packMarkup from "../assets/icon-pack/markup.json";

const packNames = Object.keys(packMarkup);

describe("atlasIconNames", () => {
  it('resolves every selectable page icon through the shared registry', () => {
    for (const icon of appearanceIcons) expect(iconMarkup[atlasIconNames[icon.id] ?? icon.id], icon.id).toBeTruthy();
  });
  it("resolves the icon the server gives an imported space", () => {
    // CommitImportHandler creates the target space with Icon = "archive".
    expect(iconMarkup[atlasIconNames.archive!]).toBeTruthy();
  });
  it("maps every wiki-local name to a name that exists in the shared registry", () => {
    for (const [source, target] of Object.entries(atlasIconNames)) {
      expect(iconMarkup[target], `${source} -> ${target}`).toBeTruthy();
    }
  });
  it("leaves the icon pack its own names: none is a wiki-local name drawn as another icon", () => {
    expect(packNames.length).toBeGreaterThan(0);
    // A name that maps to itself (copy, menu, plus) is drawn as itself and may be in the pack.
    expect(packNames.filter(name => name in atlasIconNames && atlasIconNames[name] !== name)).toEqual([]);
  });
  it("lets a page keep a pack icon and the retired sparkles, but not a malformed name", () => {
    expect(isPageIcon("sparkles")).toBe(true);
    expect(isPageIcon(packNames[0])).toBe(true);
    expect(isPageIcon("Folder")).toBe(false);
  });

  it("covers every literal AtlasIcon name found in the wiki source", () => {
    const root = resolve(import.meta.dirname, "..");
    const files = globSync("**/*.vue", { cwd: root }).map(file => resolve(root, file));
    const literalNamePattern = /<AtlasIcon\b[^>]*?\sname="([a-z0-9-]+)"/g;
    const found = new Set<string>();
    for (const file of files) {
      const source = readFileSync(file, "utf8");
      for (const match of source.matchAll(literalNamePattern)) found.add(match[1]!);
    }
    expect(found.size).toBeGreaterThan(0);
    const unresolved = [...found].filter(name => !(name in atlasIconNames) && !(name in iconMarkup));
    expect(unresolved).toEqual([]);
  });

  it("keeps every icon name the interface passes as data in the core, so no view loads the icon pack for its own chrome", () => {
    // `icon: "cloud"` in a list of impact items drew a question mark before the pack and fetched the whole pack after it.
    const root = resolve(import.meta.dirname, "..");
    const files = globSync("**/*.{vue,ts}", { cwd: root, exclude: ["**/*.test.ts", "assets/**", "components/dev/**"] }).map(file => resolve(root, file));
    // Drawn by CommandIcon (the AI glyph) and by AtlasIcon's provider marks, not by the registry.
    const drawnElsewhere = new Set(["ai", "brand-openai", "brand-anthropic"]);
    const found = new Set<string>();
    for (const file of files) {
      for (const match of readFileSync(file, "utf8").matchAll(/\bicon:\s*"([a-z0-9-]+)"/g)) found.add(match[1]!);
    }
    expect(found.size).toBeGreaterThan(0);
    expect([...found].filter(name => !Object.hasOwn(atlasIconNames, name) && !Object.hasOwn(iconMarkup, name) && !drawnElsewhere.has(name))).toEqual([]);
  });
});
