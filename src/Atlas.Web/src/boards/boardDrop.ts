import type { PasteItem } from "./boardPaste";
import { refusedChars } from "./boardTargets";

/** The most items one paste or drop makes on a board. */
export const maxPasteItems = 100;
const pageId = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const isPageId = (value: string) => pageId.test(value);

/** What a drop onto a board may carry, in the order it is read. */
export const atlasPageDragType = "application/x-atlas-page";
/** One section of a page: what a heading handle or an outline item writes, `pageSectionPayload`. */
export const atlasPageSectionDragType = "application/x-atlas-page-section";

/** The payload of `atlasPageSectionDragType`: JSON `{ resourceId, anchor }`, the page and the ID of the heading (`heading-anchors`). */
export const pageSectionPayload = (resourceId: string, anchor: string) => JSON.stringify({ resourceId, anchor });
export function readSection(payload: string): PasteItem | undefined {
  try {
    const { resourceId, anchor } = JSON.parse(payload) as { resourceId?: unknown; anchor?: unknown };
    if (typeof resourceId !== "string" || !isPageId(resourceId) || typeof anchor !== "string" || !anchor || anchor.length > 200 || anchor.search(refusedChars) !== -1) return undefined;
    return { kind: "page", resourceId: resourceId.toLowerCase(), anchor };
  } catch { return undefined; }
}

/**
 * Any object dragged out of a page, the space's Obsah or a relations view, e.g. into the tree as a reference node:
 * JSON `{ id, typeId, title?, anchor? }`, written by `objectPayload`; several
 * objects at once (the contacts of a page's contact list) are a JSON array of them, of which the tree takes the first.
 */
export const atlasObjectDragType = "application/x-atlas-object";
export interface DraggedObject { readonly id: string; readonly typeId: string; readonly title?: string; readonly anchor?: string }

export const objectPayload = (object: DraggedObject | readonly DraggedObject[]) => JSON.stringify(object);

/**
 * The one writer of a dragged object (spec 2026-10-27 §13.3): Obsah, a relations view, the tree, a contact card of a page, a
 * requirement block, a deployment board's strip. The tree takes it as a reference node. By default a board also takes it as a
 * card: a page carries the page payload the tree and the boards read already, and the title is the text for any other target.
 * `bare` writes the object alone (a drag inside the editor must not paste its name).
 */
export function startObjectDrag(event: Pick<DragEvent, "dataTransfer">, object: DraggedObject | readonly DraggedObject[], options: { effect?: DataTransfer["effectAllowed"]; bare?: boolean } = {}) {
  const data = event.dataTransfer;
  const objects = Array.isArray(object) ? object as readonly DraggedObject[] : [object as DraggedObject];
  if (!data || !objects.length) return;
  data.effectAllowed = options.effect ?? "copyMove";
  data.setData(atlasObjectDragType, objectPayload(objects.length === 1 ? objects[0]! : objects));
  if (options.bare) return;
  const pages = objects.filter(entry => entry.typeId === "atlas.page").map(entry => entry.id);
  if (pages.length) data.setData(atlasPageDragType, pages.join(" "));
  const titles = objects.flatMap(entry => (entry.title ? [entry.title] : []));
  if (titles.length) data.setData("text/plain", titles.join("\n"));
}

function objectOf(raw: unknown): DraggedObject | undefined {
  if (!raw || typeof raw !== "object") return undefined;
  const { id, typeId, title, anchor } = raw as Record<string, unknown>;
  if (typeof id !== "string" || !isPageId(id) || typeof typeId !== "string" || !/^[a-z][a-z0-9.-]{0,63}$/.test(typeId)) return undefined;
  if (anchor !== undefined && (typeof anchor !== "string" || !anchor || anchor.length > 200 || anchor.search(refusedChars) !== -1)) return undefined;
  return { id: id.toLowerCase(), typeId, ...(typeof title === "string" && { title: title.slice(0, 400) }), ...(anchor !== undefined && { anchor }) };
}
/** The objects of an `atlasObjectDragType` payload, at most `maxPasteItems`; anything malformed is left out (the payload may come
 * from another tab). */
export function readObjectsPayload(payload: string): DraggedObject[] {
  try {
    const raw = JSON.parse(payload) as unknown;
    return (Array.isArray(raw) ? raw.slice(0, maxPasteItems) : [raw]).flatMap(entry => objectOf(entry) ?? []);
  } catch { return []; }
}
/** The (first) object of an `atlasObjectDragType` payload, or undefined. */
export const readObjectPayload = (payload: string): DraggedObject | undefined => readObjectsPayload(payload)[0];

/** The part of `DataTransfer` that is read: a fake of it is enough in a test. */
export interface DropSource { readonly types: readonly string[]; getData(type: string): string }

/** What the tree accepts from outside as a reference node (spec 2026-10-27 §13.3): an object, a page's section, a page. */
export const referenceDropTypes = [atlasObjectDragType, atlasPageSectionDragType, atlasPageDragType] as const;
export const carriesReference = (types: readonly string[]) => referenceDropTypes.some(type => Array.from(types).includes(type));
export interface ReferenceDrop { readonly targetId: string; readonly typeId: string; readonly title?: string; readonly anchor?: string }

/** The page ids of the tree's drag payload (`atlasPageDragType`), in lower case and once each; anything that is no id is left out. */
export const pageIdsOf = (data: DropSource) => [...new Set(data.getData(atlasPageDragType).split(/[\s,]+/).filter(isPageId).map(id => id.toLowerCase()))];

/** The target of a reference node a drop carries, read in the order of `referenceDropTypes`; undefined for anything else. */
export function readReferenceDrop(data: DropSource): ReferenceDrop | undefined {
  const object = readObjectPayload(data.getData(atlasObjectDragType));
  if (object) return { targetId: object.id, typeId: object.typeId, ...(object.title && { title: object.title }), ...(object.anchor && { anchor: object.anchor }) };
  const section = readSection(data.getData(atlasPageSectionDragType));
  if (section?.kind === "page") return { targetId: section.resourceId, typeId: "atlas.page", ...(section.anchor && { anchor: section.anchor }) };
  const [page] = pageIdsOf(data);
  return page ? { targetId: page, typeId: "atlas.page" } : undefined;
}
