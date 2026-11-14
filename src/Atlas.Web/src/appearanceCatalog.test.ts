// @vitest-environment node
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { appearanceColors, isIconName, legacyAppearanceColors } from "./appearance";
import { appearanceIcons } from "./components/settings/appearanceIcons";
import { atlasIconNames } from "./components/atlasIconNames";
import catalog from "./assets/icon-pack/catalog.json";
import markup from "./assets/icon-pack/markup.json";
import meta from "./assets/icon-pack/meta.json";

// The server accepts exactly the catalog it lists, the icon pack it embeds and the legacy names; a value the
// picker offers but the server refuses would fail only on save. Keep the lists, the generated files of the pack
// (tooling/icon-pack) and the CSS palette in step.
const server = readFileSync(resolve(__dirname, "../../Atlas.Server/Platform/Validation/AppearanceCatalog.cs"), "utf8");
const serverList = (name: string) => [...new RegExp(`${name} =\\s*\\[([^\\]]*)\\]`).exec(server)![1]!.matchAll(/"([a-z0-9-]+)"/g)].map((match) => match[1]!);
const serverPack = readFileSync(resolve(__dirname, "../../Atlas.Server/Platform/Validation/icon-pack-names.txt"), "utf8").split(/\r?\n/).filter(Boolean);
const labels: Record<string, { label: string }> = JSON.parse(readFileSync(resolve(__dirname, "../../../tooling/icon-pack/labels.cs.json"), "utf8"));
const css = readFileSync(resolve(__dirname, "styles/appearance.css"), "utf8");

const offered = appearanceIcons.map((icon) => icon.id);
const legacy = serverList("LegacyIcons");
const packNames = Object.keys(markup);
// The generator's allow-list (tooling/icon-pack/lib.mjs): seven shapes, fifteen geometry attributes and fill="currentColor".
const allowedMarkup = /^(?:<(?:path|circle|rect|line|ellipse|polyline|polygon)(?: (?:d|cx|cy|r|rx|ry|x|y|x1|y1|x2|y2|width|height|points)="[0-9a-zA-Z .,+-]+"| fill="currentColor")*\/>)+$/;

describe("appearance catalog", () => {
  // atlasIconNames.guard.test.ts checks that every catalog icon has a drawing.
  it("offers exactly the core icons of the server; the legacy names stay valid but are not offered", () => {
    expect(serverList("CoreIcons").sort()).toEqual([...offered].sort());
    expect(legacy).toEqual(["sparkles", "archive"]);
    // Plan §0a.3, O9: sparkles means AI now, so no picker offers it.
    expect(offered).not.toContain("sparkles");
  });
  it("has 60 colours plus the stored legacy ones, all known to the server and the palette CSS", () => {
    expect(appearanceColors).toHaveLength(60);
    for (const hue of new Set(appearanceColors.map((color) => color.hue))) {
      expect(server, hue).toContain(`"${hue}"`);
      expect(css, hue).toContain(`[data-color^="${hue}-"]`);
    }
    for (const legacy of Object.keys(legacyAppearanceColors)) {
      expect(server, legacy).toContain(`"${legacy}"`);
      expect(css, legacy).toContain(`[data-color="${legacy}"]`);
    }
  });
});

describe("icon pack", () => {
  it("lists the same names in the drawings, the catalog and the server's name file", () => {
    expect(packNames).toHaveLength(meta.count);
    expect(catalog.icons.map((icon) => icon.id)).toEqual(packNames);
    expect(serverPack).toEqual(packNames);
  });
  it("adds only well-formed names that are neither core nor legacy, and none the wiki draws as another icon", () => {
    // A picked icon must be the drawn icon. A wiki-local name that maps to itself (copy, menu, plus) is the same drawing.
    const aliased = Object.keys(atlasIconNames).filter((name) => atlasIconNames[name] !== name);
    const taken = new Set([...offered, ...legacy, ...aliased]);
    expect(packNames.filter((name) => taken.has(name))).toEqual([]);
    expect(packNames.filter((name) => !isIconName(name))).toEqual([]);
  });
  it("gives each of the 87 and every pack icon a declared category", () => {
    const declared = new Set(catalog.categories.map((category) => category.id));
    expect(Object.keys(catalog.core).sort()).toEqual([...offered].sort());
    expect([...Object.values(catalog.core), ...catalog.icons.map((icon) => icon.category)].filter((id) => !declared.has(id))).toEqual([]);
  });
  // AtlasIcon inserts the drawings as HTML, so nothing outside the generator's allow-list may be committed.
  it("holds only allow-listed shapes and attributes in the drawings", () => {
    expect(Object.entries(markup).filter(([, drawing]) => !allowedMarkup.test(drawing)).map(([name]) => name)).toEqual([]);
  });
  it("carries the labels of the label source people correct", () => {
    expect(catalog.icons.filter((icon) => icon.label !== labels[icon.id]?.label).map((icon) => icon.id)).toEqual([]);
  });
});
