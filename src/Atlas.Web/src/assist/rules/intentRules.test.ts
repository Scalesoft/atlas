// @vitest-environment node
import { readdirSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { detectDocumentIntents, detectIntents, type RuleHit } from "./intentRules";
import { isSpanInside, spanText } from "./span";
import fixture from "./intents.v1.fixture.json";

type FixtureCase = { text: string; expected: string; query: string | null };
const cases = fixture.cases as FixtureCase[];
const space = { allowedProjects: ["ATL", "OPS"] };
const primary = (hits: readonly RuleHit[]) => hits[0]?.intent ?? "none";

// What IntentRules.cs decided for each fixture row, in order (the first version of the
// rules, written in C#). The C# rules had no contact or vault-reference rule.
const csharp = [
  "none", "none", "none", "none", "none", "none", "none",
  "youtrack.issue", "youtrack.issue", "youtrack.issue",
  "secret.plaintext", "secret.plaintext", "secret.plaintext",
  "none", "none",
  "callout.wrap", "callout.wrap", "callout.wrap", "callout.wrap",
  "table.fromText", "table.fromText", "tasklist.fromText", "tasklist.fromText",
  "diagram.fromText", "none",
  "none", "none", "none", "none", "none", "none", "none", "none",
];

describe("intent rules", () => {
  it("detects the prototype's rule intents on the fixture", () => {
    expect(csharp).toHaveLength(cases.length);

    const results = cases.map(item => ({ item, hits: detectIntents(item.text, space) }));
    for (const { item, hits } of results)
      for (const hit of hits) expect(isSpanInside(item.text, hit.span), `${hit.intent} in ${item.text}`).toBe(true);

    // Parity: every row the C# rules got right stays right.
    const regressions = results.filter(({ item, hits }, index) => csharp[index] === item.expected && primary(hits) !== item.expected);
    expect(regressions.map(({ item, hits }) => [item.text, primary(hits)])).toEqual([]);

    // The phase 1 additions (contact, contact card, vault reference) leave the rows that need a model: a contact
    // without a handover word or without a surname, and a process told in prose.
    const misses = results.filter(({ item, hits }) => primary(hits) !== item.expected).map(({ item }) => item.text);
    expect(misses).toEqual([
      "S fakturací vám pomůže Petra Králová.",
      "Ozvěte se Martinovi z účtárny, ten má přehled o fakturách.",
      "Nasazení probíhá tak, že se nejdřív sestaví build, pak běží testy, následuje schválení, nasazení do produkce a nakonec monitoring.",
    ]);
    expect(cases.length - misses.length).toBeGreaterThan(csharp.filter((intent, index) => intent === cases[index]!.expected).length);

    const issues = results.filter(({ item }) => item.expected === "youtrack.issue").map(({ item, hits }) => hits[0]!.query);
    expect(issues).toEqual(["ATL-482", "ATL-17", "OPS-2210"]);
    const url = results.find(({ item }) => item.text.includes("https://"))!;
    expect(spanText(url.item.text, url.hits[0]!.span)).toBe("https://youtrack.example.test/issue/ATL-17");
    const secrets = results.filter(({ item }) => item.expected === "secret.plaintext").map(({ item, hits }) => spanText(item.text, hits[0]!.span));
    expect(secrets).toEqual(["Srv#2026!rack", "sk-test-9f8a7b6c5d4e3f2a1b0c", "Winter2026!x"]);
    expect(results.filter(({ item }) => item.expected === "secret.plaintext").every(({ hits }) => hits[0]!.query === undefined)).toBe(true);
    const contact = results.find(({ item }) => item.text.startsWith("Pro přístup"))!;
    expect(contact.hits[0]).toMatchObject({ intent: "contact.inline", query: "Jana Nováka" });
    const vault = results.find(({ item }) => item.text.startsWith("Heslo k administraci"))!;
    expect(vault.hits[0]).toMatchObject({ intent: "secret.reference", query: "administraci tiskáren" });
  });

  it("suggests nothing for the fixture rows without intent", () => {
    const negatives = cases.filter(item => item.expected === "none");
    expect(negatives.map(item => item.text)).toContain("Heslo musí mít alespoň 14 znaků.");
    expect(negatives.map(item => item.text)).toContain("Soubory ukládejte v kódování UTF-8.");
    // With the space's projects (S7a) and without them (O4: candidates for `resolve`), the injection row included.
    for (const options of [space, {}])
      expect(negatives.filter(item => detectIntents(item.text, options).length > 0).map(item => item.text)).toEqual([]);
  });

  it("honours Czech word boundaries in callout openers", () => {
    // `\b` treats "í" and "é" as non-word characters: "Upozornění:" and "Důležité –" would never match.
    expect(detectIntents("Upozornění: restart odpojí všechny uživatele.")[0]).toMatchObject({ intent: "callout.wrap", callout: "warning" });
    expect(detectIntents("Důležité – zálohu ověřte.")[0]).toMatchObject({ intent: "callout.wrap", callout: "important" });
    expect(detectIntents("Pozorně si přečtěte návod.")).toEqual([]);
    expect(detectIntents("Tipy na výlet najdete na nástěnce.")).toEqual([]);
  });

  it("ignores keys of projects outside the space", () => {
    const text = "Souvisí s ATL-12 a OPS-7, norma ISO-9001.";
    expect(detectIntents(text, { allowedProjects: ["ATL"] }).map(hit => hit.query)).toEqual(["ATL-12"]);
    expect(detectIntents(text, { allowedProjects: [] })).toEqual([]);
    // Without the project list every key is a candidate for `resolve` (H2), except standards and keys the
    // resolver already knows to be missing (negative cache).
    const missing = new Set(["OPS-7"]);
    expect(detectIntents(text, { isKnownMissingIssue: key => missing.has(key) }).map(hit => hit.query)).toEqual(["ATL-12"]);
  });

  it("detects the phase 1 rules beyond the prototype: pasted page link, code and table of contents", () => {
    const url = "https://atlas.example.cz/#/pages/0a1b2c3d-1111-2222-3333-444455556666";
    const text = `Postup je na ${url}.`;
    expect(detectIntents(text)).toEqual([]);
    const pasted = detectIntents(text, { pasted: { start: 13, end: 13 + url.length } });
    expect(pasted).toEqual([{ intent: "page.link", span: { start: 13, end: 13 + url.length }, query: "0a1b2c3d-1111-2222-3333-444455556666" }]);

    expect(primary(detectIntents("#!/bin/sh\nset -e\necho hotovo"))).toBe("code.fromText");
    expect(primary(detectIntents("function start() {\n  const port = 8080;\n  return listen(port);\n}"))).toBe("code.fromText");

    const headings = (count: number) => Array.from({ length: count }, () => ({ level: 2 }));
    expect(detectDocumentIntents({ headings: headings(6), hasToc: false })).toEqual([{ intent: "toc.insert" }]);
    expect(detectDocumentIntents({ headings: headings(6), hasToc: true })).toEqual([]);
    expect(detectDocumentIntents({ headings: [...headings(5), { level: 3 }], hasToc: false })).toEqual([]);
  });

  it("stays under 5 ms for a 2 000 character block", () => {
    const sentence = "Pro přístup kontaktujte Jana Nováka z provozu, chyba je v ATL-482 a heslo: Srv#2026!rack, viz https://youtrack.example.test/issue/ATL-17. ";
    const block = sentence.repeat(Math.ceil(2000 / sentence.length)).slice(0, 2000);
    detectIntents(block, space);
    const timings = Array.from({ length: 15 }, () => {
      const started = performance.now();
      detectIntents(block, space);
      return performance.now() - started;
    }).sort((a, b) => a - b);
    expect(timings[7]).toBeLessThan(5);
  });

  it("keeps the rules free of the editor and Vue (plan §0.3)", () => {
    const imports = readdirSync(__dirname).filter(name => name.endsWith(".ts") && !name.endsWith(".test.ts"))
      .flatMap(name => [...readFileSync(resolve(__dirname, name), "utf8").matchAll(/\bfrom\s+["']([^"']+)["']|\bimport\s*\(\s*["']([^"']+)["']/g)]
        .map(match => match[1] ?? match[2]!));
    expect(imports.length).toBeGreaterThan(0);
    expect(imports.filter(specifier => !specifier.startsWith("./"))).toEqual([]);
  });
});
