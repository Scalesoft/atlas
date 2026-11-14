// @vitest-environment node
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import type { CatalogDefinition } from "./projectorApi";
import { resolveRequirements } from "./requirementResolver";

// The same cases as the server's RequirementResolverParityTests: the panel resolves on its own, the server checks the same way.
interface Case {
  name: string; catalog: Pick<CatalogDefinition, "key" | "title" | "requires">[]; wanted: string[]; present: string[]; notApplicable: string[];
  items?: { key: string; requiredBy: string | null; reason: string }[]; warnings?: string[]; error?: { code: string; path?: string[] };
}
const cases = JSON.parse(readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), "../../../../tests/fixtures/content/requirement-resolver.json"), "utf8")) as Case[];

describe("the catalog's resolver, as the server's", () => {
  it.each(cases.map(entry => [entry.name, entry] as const))("%s", (_name, entry) => {
    const resolution = resolveRequirements(entry.catalog as CatalogDefinition[], entry.wanted, entry.present, entry.notApplicable);
    if (entry.error) {
      expect("refused" in resolution && resolution.refused).toEqual(entry.error);
      return;
    }
    if ("refused" in resolution) throw new Error(`refused: ${resolution.refused.code}`);
    expect(resolution.items.map(item => ({ key: item.definition.key, requiredBy: item.requiredBy, reason: item.reason }))).toEqual(entry.items);
    expect(resolution.warnings).toEqual(entry.warnings);
  });
});
