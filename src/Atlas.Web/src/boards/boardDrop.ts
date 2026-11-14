import { isPageId, maxPasteItems, scanPaste, type PasteContext, type PasteItem } from "./boardPaste";
import { refusedChars } from "./boardTargets";

/** What a drop onto a board may carry, in the order it is read. */
export const atlasPageDragType = "application/x-atlas-page";
/** One section of a page: what a heading handle or an outline item writes, `pageSectionPayload`. */
export const atlasPageSectionDragType = "application/x-atlas-page-section";

/** The payload of `atlasPageSectionDragType`: JSON `{ resourceId, anchor }`, the page and the ID of the heading (`heading-anchors`). */
export const pageSectionPayload = (resourceId: string, anchor: string) => JSON.stringify({ resourceId, anchor });
function readSection(payload: string): PasteItem | undefined {
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
const pageIdsOf = (data: DropSource) => [...new Set(data.getData(atlasPageDragType).split(/[\s,]+/).filter(isPageId).map(id => id.toLowerCase()))];

/** The target of a reference node a drop carries, read in the order of `referenceDropTypes`; undefined for anything else. */
export function readReferenceDrop(data: DropSource): ReferenceDrop | undefined {
  const object = readObjectPayload(data.getData(atlasObjectDragType));
  if (object) return { targetId: object.id, typeId: object.typeId, ...(object.title && { title: object.title }), ...(object.anchor && { anchor: object.anchor }) };
  const section = readSection(data.getData(atlasPageSectionDragType));
  if (section?.kind === "page") return { targetId: section.resourceId, typeId: "atlas.page", ...(section.anchor && { anchor: section.anchor }) };
  const [page] = pageIdsOf(data);
  return page ? { targetId: page, typeId: "atlas.page" } : undefined;
}

const textTypes = [atlasObjectDragType, atlasPageSectionDragType, atlasPageDragType, "text/uri-list", "text/plain"];

/**
 * Whether `dragover` should accept the drag. Browsers hide the data until the drop, so only the types can tell; a drag of files
 * alone (an image, no support yet) or of html alone is not accepted.
 */
export function dropAccepts(types: readonly string[]): boolean {
  const carried = Array.from(types);
  return textTypes.some(type => carried.includes(type));
}

/**
 * The items a drop makes, and how many beyond the first 100 were left out. First that yields anything wins, so the link a browser
 * adds to a drag of the page tree's row never doubles its card:
 * 1. an object (`atlasObjectDragType`): a page as its card (at its heading or block), anything else as its live object card,
 * 2. the section of a page (`atlasPageSectionDragType`),
 * 3. the page id(s) of the tree's drag payload (`atlasPageDragType`),
 * 4. the links of `text/uri-list` (a search result, an address), read as a paste,
 * 5. `text/plain`, read as a paste (links and issue ids make cards, any other line a note).
 */
export function scanDrop(data: DropSource, context: PasteContext): { items: PasteItem[]; omitted: number } {
  const objects = readObjectsPayload(data.getData(atlasObjectDragType));
  if (objects.length) {
    const items = objects.map((object): PasteItem => (object.typeId === "atlas.page" ? { kind: "page", resourceId: object.id, ...(object.anchor && { anchor: object.anchor }) }
      : { kind: "object", objectId: object.id, typeId: object.typeId }));
    // The same object twice in one drag is one card.
    return { items: items.filter((item, index) => item.kind !== "object" || items.findIndex(other => other.kind === "object" && other.objectId === item.objectId) === index), omitted: 0 };
  }
  const section = readSection(data.getData(atlasPageSectionDragType));
  if (section) return { items: [section], omitted: 0 };
  const ids = pageIdsOf(data);
  if (ids.length) return { items: ids.slice(0, maxPasteItems).map(resourceId => ({ kind: "page", resourceId })), omitted: Math.max(0, ids.length - maxPasteItems) };
  // RFC 2483: a line starting with # is a comment.
  const links = data.getData("text/uri-list").split(/\r\n|[\r\n]/).filter(line => line.trim() && !line.startsWith("#")).join("\n");
  const fromLinks = scanPaste(links, context);
  return fromLinks.items.length ? fromLinks : scanPaste(data.getData("text/plain"), context);
}

/** `scanDrop` without the count of what was left out. */
export const readDrop = (data: DropSource, context: PasteContext): PasteItem[] => scanDrop(data, context).items;

/**
 * The contacts of an insertion a board shows already as object cards (spec 2026-10-30 §7, §8): they are placed anyway, the person is
 * only told („Kontakt už tu je – vložen podruhé.“).
 */
export function placedContacts(inserted: readonly { kind: string; objectId?: string; typeId?: string }[], items: readonly { kind: string; props?: { objectId?: string } }[]): string[] {
  const placed = new Set(items.flatMap(item => (item.kind === "object" && item.props?.objectId ? [item.props.objectId.toLowerCase()] : [])));
  return inserted.flatMap(item => (item.kind === "object" && item.typeId === "atlas.contact" && item.objectId && placed.has(item.objectId.toLowerCase()) ? [item.objectId] : []));
}
