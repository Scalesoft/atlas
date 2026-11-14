import type { Editor } from "@tiptap/core";
import type { Node as PMNode } from "@tiptap/pm/model";
import type { RequirementJson } from "./projectorApi";
import { headingIdPattern, sectionUrl } from "./requirementHeading";
import { crossPage, requiredKeys, resetRequirementJson, type BlockJson } from "./requirementModel";
import { storageOf } from "./requirementRuntime";
import type { RequirementAttributes } from "./requirementTypes";

/**
 * The level of a heading placed at `pos`: one below the page's heading above it (a module under its product), beside a requirement
 * heading above it (its level, as /Požadavek places one), at most 6.
 */
export function headingLevelAt(doc: PMNode, pos: number) {
  let level = 1, requirement = false;
  doc.forEach((node, offset) => { if (offset < pos && node.type.name === "heading") { level = node.attrs.level as number; requirement = node.attrs.state != null; } });
  return Math.min(6, requirement ? level : level + 1);
}

/**
 * Where a catalog requirement of `path` (product › category › module, the headings over it in the catalog) goes on a deployment page
 * (plan 3.5 C): at the end of the deepest section of the page along that path, the headings it lacks below it from `level` on;
 * null when the page has not even the first of them (then it goes where the caret is). Headings match by their text, case and
 * accents aside; a section ends at the next heading of its level or higher.
 */
export function placeOnPage(doc: PMNode, path: readonly string[]): { pos: number; level: number; missing: string[] } | null {
  const headings: { text: string; level: number; pos: number }[] = [];
  doc.forEach((node, pos) => { if (node.type.name === "heading") headings.push({ text: node.textContent.trim(), level: node.attrs.level as number, pos }); });
  const same = (a: string, b: string) => a.localeCompare(b, "cs", { sensitivity: "base" }) === 0;
  let end = doc.content.size, from = 0, level = 0, found = 0;
  for (const name of path) {
    const heading = headings.find(at => at.pos >= from && at.pos < end && at.level > level && same(at.text, name));
    if (!heading) break;
    end = Math.min(end, headings.find(at => at.pos > heading.pos && at.level <= heading.level)?.pos ?? end);
    ({ pos: from, level } = heading);
    found++;
  }
  return found ? { pos: end, level: Math.min(6, level + 1), missing: path.slice(found) } : null;
}

/**
 * A catalog definition (the server's block view of a requirement) as a requirement heading's section (model §1b): the heading of
 * `level` with its key as its id, its state, kind, service and provenance; the dependency line of its prerequisites (this page's by
 * `pageId`, another's by its reference), each named by `titles` (a key's title) else its key; its notes; a nested requirement one level
 * deeper after them. No facts line: a definition is reset for the page (no party, no deadline).
 */
export function requirementSection(block: BlockJson, level: number, pageId: string | undefined, titles: ReadonlyMap<string, string>): BlockJson[] {
  const attrs = (block.attrs ?? {}) as Partial<RequirementAttributes> & { key?: string; from?: string }, [title, ...rest] = block.content ?? [];
  const links = pageId ? requiredKeys({ requires: attrs.requires ?? "" }).flatMap(key => {
    const remote = crossPage(key), page = remote?.pageId ?? pageId, id = remote?.key ?? key;
    return headingIdPattern.test(id) ? [{ type: "pageReference", attrs: { id: page, title: titles.get(key) ?? titles.get(id) ?? id, url: sectionUrl(page, id) } }] : [];
  }) : [];
  const heading = { type: "heading", attrs: { level: Math.min(6, level), ...(attrs.key && headingIdPattern.test(attrs.key) ? { headingId: attrs.key } : {}),
    state: attrs.state ?? "planned", direction: attrs.direction ?? "internal", system: attrs.system ?? "", from: attrs.from ?? "" }, ...(title?.content ? { content: title.content } : {}) };
  const deps = links.length ? [{ type: "paragraph", attrs: { dependencies: true }, content: links.flatMap((link, index) => index ? [{ type: "text", text: ", " }, link] : [link]) }] : [];
  const notes = rest.filter(node => node.type !== "requirement"), nested = rest.filter(node => node.type === "requirement");
  return [heading, ...deps, ...notes, ...nested.flatMap(node => requirementSection(node, level + 1, pageId, titles))];
}

/**
 * Catalog requirements where the caret is (or at `at`, a place between top-level blocks), dependencies first, reset for this page, as
 * requirement headings one level below the heading above them, and brought into view. An item with a `heading` (a whole module) goes
 * under it, and so do the module's other items (`inModule`). With `place` an item with a `path` goes under its product's heading instead when the page has one (placeOnPage), the
 * headings it lacks made as it goes, so the next requirement of its module finds them.
 */
export function insertResolved(editor: Editor, items: readonly { definition: { contentJson: RequirementJson; key?: string; title?: string }; requiredBy: string | null; heading?: string; inModule?: boolean; path?: readonly string[] }[],
  at?: number, { keepFocus = false, place = false } = {}) {
  const level = headingLevelAt(editor.state.doc, at ?? editor.state.selection.from), pageId = storageOf(editor).host?.pageId();
  const titles = new Map(items.flatMap(item => item.definition.key ? [[item.definition.key, item.definition.title ?? item.definition.key] as const] : []));
  const heading = (text: string, depth: number) => ({ type: "heading", attrs: { level: Math.min(6, depth) }, content: [{ type: "text", text }] });
  const loose: unknown[] = [];
  // „Vložit a pokračovat“ keeps the focus in the panel for the next pick.
  const chain = keepFocus ? editor.chain() : editor.chain().focus();
  return items.length > 0 && editor.isEditable && chain.command(({ tr }) => {
    for (const item of items) {
      const block = resetRequirementJson(item.definition.contentJson);
      const spot = place && item.path?.length ? placeOnPage(tr.doc, item.path) : null;
      if (!spot) { loose.push(...(item.heading ? [heading(item.heading, level)] : []), ...requirementSection(block, item.heading || item.inModule ? level + 1 : level, pageId, titles)); continue; }
      tr.insert(spot.pos, [...spot.missing.map((text, depth) => heading(text, spot.level + depth)), ...requirementSection(block, spot.level + spot.missing.length, pageId, titles)]
        .map(json => editor.schema.nodeFromJSON(json)));
    }
    return true;
  }).command(({ tr, commands }) => !loose.length || (at === undefined ? commands.insertContent(loose) : commands.insertContentAt(tr.mapping.map(at), loose)))
    .scrollIntoView().run();
}
