// @vitest-environment node
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { CZECH_LITERAL_BASELINE, MIGRATED_AREAS } from "./czechLiterals.baseline";

/*
 * Ratchet of the 4.1 localization: in an
 * area whose texts moved into src/i18n, the Czech left in the code only shrinks. It counts, per file, the lines with a Czech
 * letter once comments are left out; a file above its baseline fails (a new text goes into the area's dictionary), and so does
 * one below it (lower the baseline). Czech written in ASCII only („Obsah“) is not seen: the review is still the final check.
 */

const root = process.cwd();
const CZECH = /[áčďéěíňóřšťúůýž]/i;

/** Comments are prose for developers, not texts of the tool: block, HTML and line comments (not `://` in an address). */
const withoutComments = (source: string) => source
  .replace(/\/\*[\s\S]*?\*\//g, "")
  .replace(/<!--[\s\S]*?-->/g, "")
  .replace(/(?<![:"'`\w])\/\/.*$/gm, "");

/** Lines with Czech outside comments, per source file of the migrated areas (tests left out). */
function countCzechLines(): Record<string, number> {
  const counts: Record<string, number> = {};
  const visit = (path: string) => {
    if (statSync(path).isDirectory()) {
      // The Czech source dictionaries and the gallery are Czech by design.
      if (/[\\/](i18n|dev)$/.test(path)) return;
      for (const entry of readdirSync(path)) visit(join(path, entry));
      return;
    }
    if (!/\.(vue|ts)$/.test(path) || /\.test\.ts$/.test(path)) return;
    const lines = withoutComments(readFileSync(path, "utf8")).split("\n").filter((line) => CZECH.test(line)).length;
    if (lines > 0) counts[relative(root, path).replaceAll("\\", "/")] = lines;
  };
  for (const area of MIGRATED_AREAS) visit(resolve(root, area));
  return counts;
}

describe("Czech texts in the migrated areas", () => {
  it("only shrink", () => {
    const counts = countCzechLines();
    const grown = Object.entries(counts).filter(([file, lines]) => lines > (CZECH_LITERAL_BASELINE[file] ?? 0)).map(([file, lines]) => `${file}: ${lines}`);
    const shrunk = Object.entries(CZECH_LITERAL_BASELINE).filter(([file, lines]) => (counts[file] ?? 0) < lines).map(([file]) => `${file}: ${counts[file] ?? 0}`);
    expect(grown, "new Czech text in a migrated area: put it in the area's dictionary (src/i18n/<area>.ts)").toEqual([]);
    expect(shrunk, "fewer Czech lines than the baseline: lower it in czechLiterals.baseline.ts").toEqual([]);
  });
});
