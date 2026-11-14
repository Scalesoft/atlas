import { afterEach, describe, expect, it, vi } from "vitest";

// English for the boards, contacts and objects areas: the modules that read their texts lazily (a getter, a function) show
// English once the texts are in, and Czech stays as it was.
async function load(cookie?: string) {
  document.cookie = cookie ? `atlas-language=${cookie};path=/` : "atlas-language=;path=/;max-age=0";
  vi.resetModules();
  const core = await import("./index");
  const keymap = (await import("../boards/boardKeymap")).boardKeymap;
  const flow = await import("../boards/boardFlow");
  const relations = await import("../objects/relationsApi");
  const palette = await import("../components/boards/canvas/boardPalette");
  const sources = (await import("./contacts")).default;
  await core.textsLoaded();
  return { keymap, flow, relations, palette, sources };
}

afterEach(() => {
  document.cookie = "atlas-language=;path=/;max-age=0";
});

describe("boards, contacts and objects texts", () => {
  it("stay Czech by default", async () => {
    const { keymap, flow, relations, palette } = await load();
    expect([keymap[0]!.group, flow.kindNames.note, relations.placesText(3), palette.colorLabel("yellow")]).toEqual(["Tabule", "Poznámka", "3 místech", "Žlutá"]);
  });

  it("are English from the cookie, also for what the modules read at load", async () => {
    const { keymap, flow, relations, palette, sources } = await load("en");
    expect([keymap[0]!.group, flow.kindNames.note, relations.placesText(3), relations.typeInfo("atlas.board").plural, palette.colorLabel("yellow")])
      .toEqual(["Board", "Note", "3 places", "Boards", "Yellow"]);
    expect(sources.sources.pageOf("Home")).toBe("Page Home");
  });
});
