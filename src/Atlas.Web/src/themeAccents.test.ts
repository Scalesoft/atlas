// @vitest-environment node
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { expect, it } from "vitest";
import { defaultThemeAccent, themeAccents } from "./profile";

// The profile offers exactly the accents the server accepts, each with a light palette and a hue for the tinted
// dark theme; an accent the picker offers but the server refuses would fail only on save.
const rules = readFileSync(resolve(__dirname, "../../Atlas.Server/Platform/Validation/Rules.cs"), "utf8");
const serverAccents = [...(rules.match(/AppearanceAccents = \[([^\]]*)\]/)?.[1] ?? "").matchAll(/"([a-z]+)"/g)].map((match) => match[1]);
const palettes = readFileSync(resolve(__dirname, "styles/palettes.css"), "utf8");

it("keeps the accent catalog, the server whitelist and the palettes in step", () => {
  expect(new Set(themeAccents.map((accent) => accent.id))).toEqual(new Set(serverAccents));
  expect(themeAccents[0]!.id).toBe(defaultThemeAccent);
  for (const accent of themeAccents) {
    expect(palettes, accent.id).toContain(`:root[data-accent="${accent.id}"][data-mode="light"]`);
    expect(palettes, accent.id).toContain(`:root[data-accent="${accent.id}"], .atlas[data-accent="${accent.id}"] { --atlas-hue: ${accent.hue}; }`);
  }
});
