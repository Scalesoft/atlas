import { boardItemKinds, isBoardColor, type BoardChange, type BoardItemKind } from "./boardApi";
import { boundsOf, copyOf, isLineKind, pageTargetOf, type Point } from "./boardFlow";
import { copiedParents } from "./boardGroups";
import type { BoardItem } from "./boardModel";
import type { BoardViewItem } from "./boardSession";
import { cleanBoardText } from "./boardText";

/**
 * Copy and paste between boards (interaction spec §3.2): the items as JSON in Atlas's own clipboard type, and their text as plain
 * text for other applications; a block or a contact card as its Markdown, which a page pastes as the same component (D12).
 * What comes back from the clipboard is untrusted: only well-formed items of known kinds pass.
 */
export const boardClipboardType = "application/x-atlas-board+json";
const MARK = "atlas.board";
/** More than a paste is worth: the batch limit is 200, and the server holds 5000 items per board. */
const MAX_ITEMS = 1000;

export interface ClipboardPayload { json: string; text: string }

function plainText(item: BoardViewItem, markdown?: (item: BoardViewItem) => string | undefined): string {
  if (item.kind === "page") return pageTargetOf(item)?.title ?? "";
  return item.kind === "connector" ? "" : markdown?.(item) ?? item.text ?? "";
}

/** The selected items with the connectors whose both ends are among them, or null for an empty selection. `markdown`: the Markdown of an
 * item that has one beside its text (a block, a contact card). */
export function copyPayload(items: readonly BoardViewItem[], ids: readonly string[], markdown?: (item: BoardViewItem) => string | undefined): ClipboardPayload | null {
  const chosen = items.filter(item => ids.includes(item.id) && item.kind !== "connector");
  if (!chosen.length) return null;
  const copied = new Set(chosen.map(item => item.id));
  const links = items.filter(item => item.kind === "connector" && copied.has(item.from ?? "") && copied.has(item.to ?? ""));
  const records = [...chosen, ...links].map(item => ({ ...copyOf(item, { id: item.id }), ...(item.parent && { parent: item.parent }) }));
  return { json: JSON.stringify({ [MARK]: 1, items: records }), text: chosen.map(item => plainText(item, markdown)).filter(Boolean).join("\n\n") };
}

const finite = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value);
const text = (value: unknown) => (typeof value === "string" ? value : undefined);

/** The items of a payload this module wrote, or null for anything else. */
export function readPayload(json: string): BoardItem[] | null {
  let parsed: unknown;
  try { parsed = JSON.parse(json); } catch { return null; }
  const list = (parsed as { items?: unknown })?.items;
  if (!parsed || typeof parsed !== "object" || (parsed as Record<string, unknown>)[MARK] !== 1 || !Array.isArray(list)) return null;
  return list.slice(0, MAX_ITEMS).flatMap((raw): BoardItem[] => {
    const r = raw as Record<string, unknown>;
    if (!r || typeof r.id !== "string" || !boardItemKinds.includes(r.kind as BoardItemKind) || ![r.x, r.y, r.width, r.height, r.z].every(finite)) return [];
    const kind = r.kind as BoardItemKind;
    const item: BoardItem = { id: r.id, kind, x: r.x as number, y: r.y as number, width: r.width as number, height: r.height as number, z: r.z as number };
    if (text(r.text) !== undefined) item.text = cleanBoardText(r.text as string, { line: isLineKind(kind) });
    if (isBoardColor(r.color)) item.color = r.color;
    if (text(r.targetResourceId)) item.targetResourceId = r.targetResourceId as string;
    if (text(r.from) && text(r.to)) { item.from = r.from as string; item.to = r.to as string; }
    if (text(r.parent)) item.parent = r.parent as string;
    if (r.props && typeof r.props === "object" && !Array.isArray(r.props)) item.props = r.props as BoardItem["props"];
    return [item];
  });
}

/** The copied items as new ones, their bounds centred on `at`, on top from `z`, connectors joined to the copies of their ends and
 * members inside the copies of their containers. */
export function pasteChanges(copied: readonly BoardItem[], at: Point, newId: () => string, z: number): { changes: BoardChange[]; ids: string[] } {
  const cards = copied.filter(item => item.kind !== "connector");
  if (!cards.length) return { changes: [], ids: [] };
  const bounds = boundsOf(cards);
  const dx = Math.round(at.x - (bounds.x + bounds.width / 2)), dy = Math.round(at.y - (bounds.y + bounds.height / 2));
  const copies = new Map<string, string>();
  const changes: BoardChange[] = [];
  for (const item of [...cards].sort((a, b) => a.z - b.z)) {
    const id = newId();
    copies.set(item.id, id);
    changes.push({ type: "create", item: copyOf(item, { id, x: item.x + dx, y: item.y + dy, z: z++ }) });
  }
  for (const item of copied) {
    const from = copies.get(item.from ?? ""), to = copies.get(item.to ?? "");
    if (item.kind !== "connector" || !from || !to) continue;
    const id = newId();
    copies.set(item.id, id);
    changes.push({ type: "create", item: copyOf(item, { id, from, to }) });
  }
  return { changes: [...changes, ...copiedParents(copied, copies)], ids: [...copies.values()] };
}
