import { test } from "node:test";
import assert from "node:assert/strict";
import {
  CATEGORIES, FOLD, foldCategory, geometryKey, innerMarkup, labelProblems, retainPrevious, searchWords, selectPack,
} from "./lib.mjs";

// The shape of a Lucide source file: attributes on their own lines, CRLF when git checks it out on Windows.
const svg = (inner) => `<svg\r\n  xmlns="http://www.w3.org/2000/svg"\r\n  width="24"\r\n  height="24"\r\n  viewBox="0 0 24 24"\r\n  fill="none"\r\n  stroke="currentColor"\r\n>\r\n  ${inner}\r\n</svg>\r\n`;

test("inner markup is rebuilt from the allow-list and anything else stops the generator", () => {
  assert.equal(
    innerMarkup(svg('<path d="M12 5v14" />\r\n  <circle cx="12" cy="12" r="1" fill="currentColor" />'), "sample"),
    '<path d="M12 5v14"/><circle cx="12" cy="12" r="1" fill="currentColor"/>',
  );
  for (const [inner, message] of [
    ['<path d="M12 5v14" /><script>alert(1)</script>', /^sample: unsupported markup: <script>alert\(1\)<\/script>$/],
    ['<path d="M12 5v14" onclick="alert(1)" />', /^sample: attribute onclick="alert\(1\)" is not allowed$/],
    ['<circle cx="12" cy="12" r="1" fill="red" />', /^sample: attribute fill="red" is not allowed$/],
    ['<a href="https://example.invalid/"><path d="M12 5v14" /></a>', /^sample: unsupported markup: <a href="https:\/\/example\.invalid\/"><\/a>$/],
    ['<path d="M12 5v14&#x3c;script&#x3e;" />', /^sample: attribute d="M12 5v14&#x3c;script&#x3e;" is not allowed$/],
  ]) {
    assert.throws(() => innerMarkup(svg(inner), "sample"), { message });
  }
});

test("geometry has one key whatever the order of its attributes and elements", () => {
  assert.equal(
    geometryKey('<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 9h18"/>'),
    geometryKey('<path d="M3 9h18"/><rect width="18" height="18" rx="2" x="3" y="3"/>'),
  );
  assert.notEqual(geometryKey('<path d="M3 9h18"/>'), geometryKey('<path d="M3 15h18"/>'));
});

test("the pack leaves out what the core already draws and what the wiki already names", () => {
  const path = (d) => `<path d="${d}"/>`;
  const lucide = new Map([
    ["bold", { markup: path("M1 1"), aliases: [] }], // a core glyph outside the 87 under the same name
    ["rocket", { markup: path("M2 2"), aliases: [] }],
    ["house", { markup: path("M3 3"), aliases: ["home"] }], // Lucide's former name is the core key
    ["list-tree", { markup: `${path("M4 4")}<circle cx="5" cy="6" r="1"/>`, aliases: [] }], // the core's outline
    ["folder", { markup: path("M5 5"), aliases: [] }],
    ["sparkles", { markup: path("M6 6"), aliases: [] }],
    ["layers", { markup: path("M7 7"), aliases: [] }], // a wiki-local name of another drawing
  ]);
  const core = {
    bold: path("M1 1"),
    home: path("M3 3h1"),
    outline: `<circle r="1" cy="6" cx="5"/>${path("M4 4")}`,
    folder: path("M5 5"),
    sparkles: path("M6 6"),
    "layout-template": path("M8 8"),
  };
  assert.deepEqual(
    selectPack({
      lucide,
      core,
      catalog: ["home", "folder", "layout-template"],
      aliases: { bold: "bold", home: "home", layers: "layout-template" },
      legacy: ["sparkles", "archive"],
    }),
    {
      pack: ["bold", "rocket"],
      excluded: {
        house: "core:home",
        "list-tree": "core:outline",
        folder: "catalog",
        sparkles: "legacy",
        layers: "alias:layout-template",
      },
    },
  );
});

test("fourteen categories fold the forty-two of Lucide and the first matching row wins", () => {
  const ids = CATEGORIES.map(({ id }) => id);
  assert.equal(new Set(ids).size, 14);
  assert.equal(FOLD.length, 42);
  assert.equal(new Set(FOLD.map(([source]) => source)).size, 42);
  assert.deepEqual([...new Set(FOLD.map(([, target]) => target))].sort(), [...ids].sort());
  assert.equal(foldCategory(["buildings", "home", "navigation"], "house"), "places");
  assert.equal(foldCategory(["arrows", "gaming"], "move-3d"), "arrows");
  assert.equal(foldCategory(["gaming", "arrows"], "move-3d"), "arrows");
  assert.throws(() => foldCategory(["arrows", "blockchain"], "coins"), { message: "coins: unknown Lucide category blockchain" });
});

test("search words keep the Czech ones first and every word once", () => {
  assert.equal(
    searchWords(["raketa", "start projektu"], ["launch", "start", "space ship"], ["rocket", "space-ship"]),
    "raketa start projektu launch space ship rocket space-ship",
  );
});

test("the label validator names every problem and passes a clean entry", () => {
  const taken = { home: "Domov" };
  assert.deepEqual(labelProblems({ rocket: { label: "Raketa", words: ["start", "vesmír"] } }, ["rocket"], taken), []);
  assert.deepEqual(
    labelProblems(
      {
        anchor: { label: "kotva", words: ["loď", "přístav"] },
        bell: { label: "Zvonek", words: ["upozornění"] },
        door: { label: "Dveře", words: ["vchod", "východ"] },
        "door-open": { label: "DVERE", words: ["vstup", "průchod"] },
        gone: { label: "Zmizelá", words: ["stará", "pryč"] },
        rocket: { label: "Raketa.", words: ["start", "vesmír"] },
        tent: { label: "domov", words: ["stan", "kemp"] },
      },
      ["anchor", "bell", "door", "door-open", "rocket", "tent", "zap"],
      taken,
    ),
    [
      "anchor: the label does not start with a capital letter or a digit",
      "bell: 1 words instead of 2 to 5",
      'door-open: the label "DVERE" is also the label of door',
      "gone: stale entry, the pack has no such icon",
      "rocket: the label ends with punctuation",
      "tent: the label does not start with a capital letter or a digit",
      'tent: the label "domov" is also the label of home',
      "zap: no label",
    ],
  );
  assert.deepEqual(
    labelProblems(
      {
        anchor: { label: "Kotva  u mola", words: ["loď", "Přístav", "loď"] },
        bell: { label: "Zvonek s velmi dlouhým popisem navíc k tomu", words: ["a", "b", "c", "d", "e", "f"] },
        rocket: { label: "Raketa", words: "start" },
      },
      ["anchor", "bell", "rocket"],
      taken,
    ),
    [
      "anchor: the label is empty or is not trimmed to single spaces",
      'anchor: the word "Přístav" is empty, not trimmed, not lower-case or longer than 24 characters',
      "anchor: a word repeats",
      "bell: the label is longer than 36 characters",
      "bell: 6 words instead of 2 to 5",
      'rocket: the entry is not {"label": "…", "words": ["…", "…"]}',
    ],
  );
});

test("a name of the previous pack is kept unless it is valid elsewhere now", () => {
  const icon = (id) => ({ id, label: id, category: "text", words: id });
  const previous = {
    markup: { anchor: '<path d="M1 1"/>', rocket: '<path d="M2 2"/>', "swiss-franc": '<path d="M3 3"/>' },
    icons: [icon("anchor"), icon("rocket"), icon("swiss-franc")],
  };
  const next = { markup: { zap: '<path d="M4 4"/>', anchor: '<path d="M1 1h1"/>' }, icons: [icon("zap"), icon("anchor")] };
  const result = retainPrevious(previous, next, ["rocket", "sparkles"]);
  assert.deepEqual(result.kept, ["swiss-franc"]);
  assert.deepEqual(Object.entries(result.markup), [
    ["anchor", '<path d="M1 1h1"/>'],
    ["swiss-franc", '<path d="M3 3"/>'],
    ["zap", '<path d="M4 4"/>'],
  ]);
  assert.deepEqual(result.icons, [icon("anchor"), icon("swiss-franc"), icon("zap")]);
});
