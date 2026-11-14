import { afterEach, describe, expect, it, vi } from "vitest";

// English is decided when src/i18n loads (as in src/i18n/i18n.test.ts): set the cookie, reset the modules, import afresh, wait for the texts.
async function load(cookie?: string) {
  document.cookie = cookie ? `atlas-language=${cookie};path=/` : "atlas-language=;path=/;max-age=0";
  vi.resetModules();
  const core = await import("../i18n");
  const notes = await import("./releaseNotes");
  await core.textsLoaded();
  return notes.releaseNotes.find(note => note.version === "4.0.0")!;
}

afterEach(() => {
  document.cookie = "atlas-language=;path=/;max-age=0";
});

describe("bilingual release notes", () => {
  it("keeps the Czech notes for a Czech page", async () => {
    const note = await load();
    expect([note.codename, note.lead]).toEqual(["Quiet Quince", "Stránky se otevřou hned, píše se do nich rovnou a Atlas potichu ukládá."]);
  });

  it("reads <version>.en.md on an English page, with the codename and the mark of the Czech source", async () => {
    const note = await load("en");
    expect([note.codename, note.mark, note.lead]).toEqual(["Quiet Quince", "quince", "Pages open at once, you write straight into them and Atlas saves quietly."]);
    expect(note.highlights.map(item => item.title)).toContain("Czech and English");
  });
});
