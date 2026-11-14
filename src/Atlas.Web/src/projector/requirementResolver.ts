import t from "../i18n/projector";
import type { CatalogDefinition, ResolvedRequirement } from "./projectorApi";

// The server's RequirementResolver (Features/Projector/Domain/RequirementResolver.cs) on the catalog the panel already holds, so a
// pick needs no request. The server still checks every write; tests/fixtures/content/requirement-resolver.json
// keeps both answering the same.

/** As the server's RequirementResolver.MaxDepth and MaxItems. */
export const MAX_DEPTH = 64;
export const MAX_ITEMS = 1000;

export type Resolution =
  | { items: ResolvedRequirement[]; warnings: string[] }
  | { refused: { code: "cyclic_dependency"; path: string[] } | { code: "dependency_too_deep" } };

const repeated = (first: CatalogDefinition) => t.resolver.repeated(first.key, first.title);

/**
 * What inserting `wanted` adds: what each needs first, depth first, a shared dependency once. Keys on the page are skipped with what
 * they need (a wanted one is reported), one marked not applicable there is reported to what needs it, an unknown key is a warning, a
 * cycle or a chain past the caps refuses the whole insertion.
 */
export function resolveRequirements(catalog: readonly CatalogDefinition[], wanted: readonly string[], present: readonly string[],
  notApplicable: readonly string[] = [], source: "catalog" | "page" = "catalog"): Resolution {
  const byKey = new Map<string, CatalogDefinition[]>();
  for (const definition of catalog) {
    const same = byKey.get(definition.key);
    if (same) same.push(definition); else byKey.set(definition.key, [definition]);
  }
  const dismissed = new Set(notApplicable), onPage = new Set(present), done = new Set(present), onPath = new Set<string>();
  // The definitions being visited, outermost first, with the next dependency each still has to visit.
  const stack: { definition: CatalogDefinition; dependent: CatalogDefinition | null; next: number }[] = [];
  const items: ResolvedRequirement[] = [], warnings: string[] = [];

  const enter = (key: string, dependent: CatalogDefinition | null): Resolution | null => {
    if (onPath.has(key)) {
      const keys = stack.map(entry => entry.definition.key);
      return { refused: { code: "cyclic_dependency", path: [...keys.slice(keys.indexOf(key)), key] } };
    }
    if (dismissed.delete(key)) warnings.push(t.resolver.dismissed(key, dependent?.title));
    else if (!dependent && onPage.has(key)) warnings.push(t.resolver.onPage(key));
    // A block of another page (key@pageId) is no definition to insert: the copy keeps it as a dependency on that page.
    if (done.has(key) || key.includes("@")) return null;
    done.add(key);
    const definitions = byKey.get(key);
    if (!definitions) { warnings.push(t.resolver.missing(key, source)); return null; }
    if (definitions.length > 1) warnings.push(repeated(definitions[0]!));
    if (stack.length === MAX_DEPTH || items.length + stack.length === MAX_ITEMS) return { refused: { code: "dependency_too_deep" } };
    stack.push({ definition: definitions[0]!, dependent, next: 0 });
    onPath.add(key);
    return null;
  };

  for (const key of wanted) {
    const refused = enter(key.trim(), null);
    if (refused) return refused;
    while (stack.length) {
      const top = stack[stack.length - 1]!;
      if (top.next < top.definition.requires.length) {
        const refusal = enter(top.definition.requires[top.next++]!, top.definition);
        if (refusal) return refusal;
        continue;
      }
      stack.pop();
      onPath.delete(top.definition.key);
      items.push({ definition: top.definition, requiredBy: top.dependent?.key ?? null,
        reason: top.dependent ? t.resolver.neededBy(top.dependent.title) : t.resolver.chosen });
    }
  }
  return { items, warnings };
}
