import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { describe, expect, it } from "vitest";

// tests/changed.ps1 runs every *.guard.test.ts with each web change, because a scanner reads files it does not import and
// `vitest --changed` never selects it. A test that lists a directory is such a scanner unless it is listed here with its reason.
const NOT_GUARDS: Record<string, string> = {
  "assist/rules/intentRules.test.ts": "reads the rules folder it imports, so a change there selects it",
  "contracts/appInteraction.contract.test.ts": "a scanner of the interaction contracts; the contracts/ suites are aligned elsewhere, rename it when they land",
  "components/dev/AiGalleryPage.test.ts": "mounts every example one by one; too slow for every change, runs with the gallery",
  "components/dev/RefreshGalleryPage.test.ts": "mounts every example one by one; too slow for every change, runs with the gallery",
  "wikiImportGolden.test.ts": "reads fixtures, not sources",
};

describe("test files that scan directories", () => {
  it("are guards or listed as none", () => {
    const root = join(import.meta.dirname);
    const scanners: string[] = [];
    const walk = (dir: string) => {
      for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const path = join(dir, entry.name);
        if (entry.isDirectory()) walk(path);
        else if (/\.test\.ts$/.test(entry.name) && !/\.guard\.test\.ts$/.test(entry.name) && /\breaddirSync\b/.test(readFileSync(path, "utf8")))
          scanners.push(relative(root, path).replaceAll("\\", "/"));
      }
    };
    walk(root);
    expect(scanners.filter(file => !(file in NOT_GUARDS)), "rename to *.guard.test.ts, or list it in NOT_GUARDS with the reason").toEqual([]);
    expect(Object.keys(NOT_GUARDS).filter(file => !scanners.includes(file)), "no longer scans a directory: remove from NOT_GUARDS").toEqual([]);
  });
});
