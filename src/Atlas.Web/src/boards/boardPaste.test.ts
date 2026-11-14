// @vitest-environment node
import { describe, expect, it } from "vitest";
import { maxNoteText, maxPasteItems, parsePaste, scanPaste, type PasteContext, type PasteItem } from "./boardPaste";

const id = "3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b";
const other = "a1b2c3d4-0000-4000-8000-000000000001";
const atlas: PasteContext = { origin: "https://atlas.example", youtrackBaseUrls: ["https://yt.example/"] };
const page = (resourceId: string, anchor?: string): PasteItem => ({ kind: "page", resourceId, ...(anchor && { anchor }) });
const issue = (issueId: string): PasteItem => ({ kind: "youtrack", issueId });
const note = (text: string): PasteItem => ({ kind: "note", text });

describe("parsePaste: pages of this Atlas", () => {
  it("reads the address forms the application writes, absolute and relative", () => {
    expect(parsePaste(`https://atlas.example/#/pages/${id}`, atlas)).toEqual([page(id)]);
    expect(parsePaste(`https://atlas.example/#/pages/${id}?heading=uvod`, atlas)).toEqual([page(id, "uvod")]);
    // The omnisearch overlay keeps its state in the query before the hash; the page is still the one the hash names.
    expect(parsePaste(`https://atlas.example/?search=1&q=plan#/pages/${id}`, atlas)).toEqual([page(id)]);
    expect(parsePaste(`#/pages/${id}`, atlas)).toEqual([page(id)]);
    expect(parsePaste(`/#/pages/${id}`, atlas)).toEqual([page(id)]);
  });

  it("makes a card of one section of the page out of a section link, the heading id decoded", () => {
    expect(parsePaste(`#/pages/${id}?heading=${encodeURIComponent("úvod-1")}`, atlas)).toEqual([page(id, "úvod-1")]);
    expect(parsePaste(`#/pages/${id}?heading=${"x".repeat(201)}`, atlas)).toEqual([page(id)]);
  });

  it("writes the page id in lower case, as the server does", () => {
    expect(parsePaste(`https://atlas.example/#/pages/${id.toUpperCase()}`, atlas)).toEqual([page(id)]);
  });

  it("follows the path the application is served under", () => {
    const nested = { ...atlas, pathname: "/atlas/" };
    expect(parsePaste(`https://atlas.example/atlas/#/pages/${id}`, nested)).toEqual([page(id)]);
    expect(parsePaste(`#/pages/${id}`, nested)).toEqual([page(id)]);
    expect(parsePaste(`https://atlas.example/#/pages/${id}`, nested)).toEqual([note(`https://atlas.example/#/pages/${id}`)]);
  });

  it.each([
    ["another instance", `https://elsewhere.example/#/pages/${id}`],
    ["another scheme", `http://atlas.example/#/pages/${id}`],
    ["a protocol-relative address", `//elsewhere.example/#/pages/${id}`],
    ["an address with credentials", `https://user:secret@atlas.example/#/pages/${id}`],
    ["the access screen of a page", `https://atlas.example/#/pages/${id}/access`],
    ["a space", `https://atlas.example/#/spaces/${id}`],
    ["a board", `https://atlas.example/#/boards/${id}`],
    ["an id that is no page id", "https://atlas.example/#/pages/not-a-uuid"],
    ["no hash at all", "https://atlas.example/"],
  ])("keeps %s as a note", (_, text) => {
    expect(parsePaste(text, atlas)).toEqual([note(text)]);
  });
});

describe("parsePaste: YouTrack issues", () => {
  it("reads an issue id the way the integration accepts it", () => {
    expect(parsePaste("ATL-123", atlas)).toEqual([issue("ATL-123")]);
    expect(parsePaste("  ATL-123\t", atlas)).toEqual([issue("ATL-123")]);
    expect(parsePaste("web_2-7", atlas)).toEqual([issue("web_2-7")]);
    expect(parsePaste("3D-12", atlas)).toEqual([issue("3D-12")]);
  });

  it.each(["ATL-", "-123", "ATL123", "ATL-12a", "ATL--1", "_ATL-1", "ATL 123", "ATL-1-2"])("keeps %s as a note", (text) => {
    expect(parsePaste(text, atlas)).toEqual([note(text)]);
  });

  it("reads an issue URL of a configured YouTrack, with the title, a query or a fragment after the id", () => {
    expect(parsePaste("https://yt.example/issue/ATL-123", atlas)).toEqual([issue("ATL-123")]);
    expect(parsePaste("https://yt.example/issue/ATL-123/Oprava-prihlaseni", atlas)).toEqual([issue("ATL-123")]);
    expect(parsePaste("https://yt.example/issue/ATL-123#focus=Comments-4-5.0-0", atlas)).toEqual([issue("ATL-123")]);
    expect(parsePaste("https://yt.example/issue/ATL-123?foo=bar", atlas)).toEqual([issue("ATL-123")]);
  });

  it("follows the path of the configured base URL and any of several", () => {
    const context = { ...atlas, youtrackBaseUrls: ["https://yt.example/youtrack", "https://cloud.example/"] };
    expect(parsePaste("https://yt.example/youtrack/issue/ATL-5", context)).toEqual([issue("ATL-5")]);
    expect(parsePaste("https://cloud.example/issue/OPS-9", context)).toEqual([issue("OPS-9")]);
    expect(parsePaste("https://yt.example/issue/ATL-5", context)).toEqual([note("https://yt.example/issue/ATL-5")]);
  });

  it.each([
    ["another host", "https://other.example/issue/ATL-123"],
    ["another scheme", "http://yt.example/issue/ATL-123"],
    ["credentials", "https://user:secret@yt.example/issue/ATL-123"],
    ["a page that is no issue", "https://yt.example/dashboard"],
    ["an id that is no issue id", "https://yt.example/issue/nonsense"],
    ["a host that only starts like it", "https://yt.example.evil.test/issue/ATL-123"],
  ])("keeps a YouTrack URL with %s as a note", (_, text) => {
    expect(parsePaste(text, atlas)).toEqual([note(text)]);
  });

  it("does not know an issue URL without a configured base URL, but still reads the id", () => {
    const bare = { origin: "https://atlas.example" };
    expect(parsePaste("https://yt.example/issue/ATL-123", bare)).toEqual([note("https://yt.example/issue/ATL-123")]);
    expect(parsePaste("ATL-123", bare)).toEqual([issue("ATL-123")]);
  });

  it("makes ids and URLs plain text when YouTrack is off", () => {
    const off = { ...atlas, youtrack: false };
    expect(parsePaste("ATL-123", off)).toEqual([note("ATL-123")]);
    expect(parsePaste("https://yt.example/issue/ATL-123", off)).toEqual([note("https://yt.example/issue/ATL-123")]);
    expect(parsePaste(`#/pages/${id}`, off)).toEqual([page(id)]);
  });
});

describe("parsePaste: lines", () => {
  it("makes one note per non-empty line and keeps Czech text as it is", () => {
    expect(parsePaste("Příliš žluťoučký kůň\n\n   \nÚčastníci: Žaneta, Čeněk", atlas)).toEqual([note("Příliš žluťoučký kůň"), note("Účastníci: Žaneta, Čeněk")]);
  });

  it("reads every kind of line break and trims each line", () => {
    expect(parsePaste("  první  \r\ndruhá\rtřetí\u2028čtvrtá\u2029pátá\n", atlas).map(item => (item as { text: string }).text)).toEqual(["první", "druhá", "třetí", "čtvrtá", "pátá"]);
  });

  it("returns nothing for nothing", () => {
    expect(parsePaste("", atlas)).toEqual([]);
    expect(parsePaste(" \n\t\n ", atlas)).toEqual([]);
  });

  it("keeps the order of the lines across kinds", () => {
    const text = `Dnes řešíme\nATL-7\nhttps://atlas.example/#/pages/${id}\nhttps://yt.example/issue/ATL-8\nKonec`;
    expect(parsePaste(text, atlas)).toEqual([note("Dnes řešíme"), issue("ATL-7"), page(id), issue("ATL-8"), note("Konec")]);
  });

  it("makes a card for each reference on a line made only of references", () => {
    expect(parsePaste(`ATL-1 ATL-2, ATL-3;https://atlas.example/#/pages/${id}`, atlas)).toEqual([issue("ATL-1"), issue("ATL-2"), issue("ATL-3"), page(id)]);
  });

  it("keeps a line that mixes references and words whole, as a note", () => {
    expect(parsePaste("Podívej se na ATL-123 prosím", atlas)).toEqual([note("Podívej se na ATL-123 prosím")]);
    expect(parsePaste(`ATL-1 a ATL-2`, atlas)).toEqual([note("ATL-1 a ATL-2")]);
    expect(parsePaste(`Viz https://atlas.example/#/pages/${id}`, atlas)).toEqual([note(`Viz https://atlas.example/#/pages/${id}`)]);
  });

  it("removes what the server refuses in a note: control characters and bidirectional controls", () => {
    expect(parsePaste("a\u0000b\u0007c\u202ed\u2066e\u200ff\u061cg", atlas)).toEqual([note("abcdefg")]);
    expect(parsePaste("sloupec A\tsloupec B", atlas)).toEqual([note("sloupec A sloupec B")]);
    // A line that is nothing but such characters is no note.
    expect(parsePaste("\u0000\u202e", atlas)).toEqual([]);
  });

  it("reads a row pasted from a spreadsheet as words: a tab, with the spaces around it, is one space, and empty cells add nothing", () => {
    expect(parsePaste("Jméno\tPříjmení\tObor", atlas)).toEqual([note("Jméno Příjmení Obor")]);
    expect(parsePaste("a\t\t\tb", atlas)).toEqual([note("a b")]);
    expect(parsePaste("  a \t b\t", atlas)).toEqual([note("a b")]);
    expect(parsePaste("\tvedoucí\t\n\tčlen\t", atlas)).toEqual([note("vedoucí"), note("člen")]);
    // Spaces the person typed stay.
    expect(parsePaste("a  b", atlas)).toEqual([note("a  b")]);
  });

  it("still finds the references in a row of cells", () => {
    expect(parsePaste(`ATL-1\tATL-2\t\thttps://atlas.example/#/pages/${id}`, atlas)).toEqual([issue("ATL-1"), issue("ATL-2"), page(id)]);
  });

  it("leaves no character in a note that the server refuses, whichever of its ranges it comes from", () => {
    const refused = [[0x00, 0x08], [0x0b, 0x0c], [0x0e, 0x1f], [0x7f, 0x9f], [0x61c, 0x61c], [0x200e, 0x200f], [0x202a, 0x202e], [0x2066, 0x2069]];
    for (const [from, to] of refused as [number, number][]) {
      for (let code = from; code <= to; code++) {
        expect(parsePaste(`a${String.fromCharCode(code)}b`, atlas), `U+${code.toString(16)}`).toEqual([note("ab")]);
      }
    }
  });

  it("cuts a line the server would refuse as too long into notes of the allowed size, at a space", () => {
    const word = "slovo";
    const long = Array.from({ length: 1700 }, () => word).join(" ");
    const items = parsePaste(long, atlas) as { kind: "note"; text: string }[];
    expect(items.length).toBeGreaterThan(1);
    for (const item of items) {
      expect(item.kind).toBe("note");
      expect(item.text.length).toBeLessThanOrEqual(maxNoteText);
      expect(item.text).toBe(item.text.trim());
    }
    expect(items.map(item => item.text).join(" ")).toBe(long);
  });

  it("never cuts a surrogate pair in half", () => {
    const emoji = "😀".repeat(maxNoteText);
    const items = parsePaste(emoji, atlas) as { text: string }[];
    expect(items.length).toBe(2);
    for (const item of items) {
      expect(item.text.length).toBeLessThanOrEqual(maxNoteText);
      expect(item.text).toMatch(/^(?:😀)+$/u);
    }
    expect(items.map(item => item.text).join("")).toBe(emoji);
  });
});

describe("parsePaste: duplicates and the limit", () => {
  it("makes one card for a page or an issue pasted several times, in any form", () => {
    const text = [`#/pages/${id}`, `https://atlas.example/#/pages/${id.toUpperCase()}`, "ATL-1", "atl-1", "https://yt.example/issue/ATL-1", `#/pages/${other}`].join("\n");
    expect(parsePaste(text, atlas)).toEqual([page(id), issue("ATL-1"), page(other)]);
    // A section is another card than the whole page, and one section is one card.
    const sections = [`#/pages/${id}`, `#/pages/${id}?heading=a`, `#/pages/${id}?heading=a`, `#/pages/${id}?heading=b`].join("\n");
    expect(parsePaste(sections, atlas)).toEqual([page(id), page(id, "a"), page(id, "b")]);
  });

  it("keeps every note: two equal lines are two decisions of the person pasting", () => {
    expect(parsePaste("Úkol\nÚkol", atlas)).toEqual([note("Úkol"), note("Úkol")]);
  });

  it("stops at 100 items and says how many it left out", () => {
    const text = Array.from({ length: 130 }, (_, index) => `Poznámka ${index + 1}`).join("\n");
    const items = parsePaste(text, atlas);
    expect(items).toHaveLength(maxPasteItems);
    expect(items[0]).toEqual(note("Poznámka 1"));
    expect(items[99]).toEqual(note("Poznámka 100"));
    const scanned = scanPaste(text, atlas);
    expect(scanned.items).toEqual(items);
    expect(scanned.omitted).toBe(30);
  });

  it("counts a duplicate once before the limit applies", () => {
    const text = Array.from({ length: 150 }, () => "ATL-1").join("\n");
    expect(scanPaste(text, atlas)).toEqual({ items: [issue("ATL-1")], omitted: 0 });
  });

  it("counts the references of a single line against the limit too", () => {
    const text = Array.from({ length: 120 }, (_, index) => `ATL-${index + 1}`).join(" ");
    const scanned = scanPaste(text, atlas);
    expect(scanned.items).toHaveLength(100);
    expect(scanned.omitted).toBe(20);
  });
});
