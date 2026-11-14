import type { Editor } from "@tiptap/core";
import type { Node as PMNode } from "@tiptap/pm/model";
import { errorMessage } from "../api";
import { useToast } from "../composables/useToast";
import t from "../i18n/projector";
import { refusalText } from "./catalogSelection";
import type { RequirementJson } from "./projectorApi";
import { catalogBlocks, catalogOf } from "./requirementCatalog";
import { insertResolved } from "./requirementCommands";
import { requirementSections } from "./requirementHeading";
import { missingPrerequisites, type PageRequirement } from "./requirementModel";
import { resolveRequirements } from "./requirementResolver";
import { storageOf } from "./requirementRuntime";

/** The end of the section a heading of this text opens (before the next heading of its level or above), or -1 without such a heading. */
export function sectionEnd(doc: PMNode, heading: string): number {
  const wanted = heading.trim().toLowerCase();
  let level = 0, end = -1;
  doc.forEach((node, offset) => {
    if (node.type.name !== "heading") { if (level && end >= 0) end = offset + node.nodeSize; return; }
    const own = node.attrs.level as number;
    if (level && own <= level) level = 0;
    if (!level && node.textContent.trim().toLowerCase() === wanted) { level = own; end = offset + node.nodeSize; }
  });
  return end;
}

/**
 * „Doplnit prerekvizity“ (plan 3.5 D): the keys the page's blocks require and the page lacks, taken from the space's catalog with what
 * they need in turn (the insert panel's own resolution), each under the heading it has in the catalog (its module), else after the section
 * of the first requirement that needs it (beside it, at its level). Keys the catalog does not know are reported, not made up. With `only`, just those keys (a dependency's „Přidat“
 * in the block's detail, plan 3.5 B).
 */
export async function fillMissingPrerequisites(editor: Editor, items: readonly PageRequirement[], only?: readonly string[]) {
  const toast = useToast(), host = storageOf(editor).host, missing = missingPrerequisites(items).filter(entry => !only || only.includes(entry.key));
  if (!host || !editor.isEditable) return;
  if (!missing.length) { toast.success(t.fill.none); return; }
  try {
    const catalog = await catalogOf(editor, host);
    const resolution = resolveRequirements(catalog.definitions, missing.map(entry => entry.key), items.map(item => item.attrs.key).filter(Boolean));
    if ("refused" in resolution) { toast.warning(refusalText(resolution.refused)); return; }
    const blocks = await catalogBlocks(host, resolution.items.map(item => item.definition.key));
    const where = new Map<number, { definition: { contentJson: RequirementJson }; requiredBy: string | null }[]>();
    for (const item of resolution.items) {
      const contentJson = blocks.get(item.definition.key);
      if (!contentJson) continue;
      const doc = editor.state.doc, first = missing.find(entry => entry.key === item.definition.key)?.neededBy[0] ?? items.find(own => own.attrs.key === item.requiredBy);
      let at = item.definition.bundle ? sectionEnd(doc, item.definition.bundle) : -1;
      if (at < 0 && first) { at = endOfRequirement(doc, first.ancestors[0] ?? first.id); }
      if (at < 0) at = doc.content.size;
      where.set(at, [...(where.get(at) ?? []), { definition: { contentJson }, requiredBy: item.requiredBy }]);
    }
    // From the end of the page up, so an insertion does not move the places of the ones before it.
    for (const [at, group] of [...where].sort((a, b) => b[0] - a[0])) insertResolved(editor, group, at, { keepFocus: true });
    const unknown = missing.filter(entry => !catalog.definitions.some(definition => definition.key === entry.key));
    toast.success(t.fill.added(resolution.items.length));
    if (unknown.length) toast.warning(t.fill.unknown(unknown.map(entry => entry.key).join(", ")));
  } catch (failure) { toast.error(t.fill.failed(errorMessage(failure))); }
}

/** The end of the section of the requirement heading with this id (else of a legacy top-level block), -1 when there is none. */
export function endOfRequirement(doc: PMNode, id: string): number {
  let end = requirementSections(doc).find(section => section.id === id)?.end ?? -1;
  if (end < 0) doc.forEach((node, offset) => { if (node.type.name === "requirement" && node.attrs.id === id) end = offset + node.nodeSize; });
  return end;
}
