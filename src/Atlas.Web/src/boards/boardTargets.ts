import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";

/**
 * What a page card shows of its page: the view model and the normalizer
 * of the server's `target`, plus the small text rules the paste, the drop and the pickers share. Tolerant on purpose: the current
 * server answers `{ resourceId, type, title, available }` only, a missing field is `null`, and the session's own `{ title, available }`
 * reads as well. A target that is not available never carries content, whatever came with it. Pure, and idempotent: a view reads as itself.
 */
export interface PageTargetView { resourceId: string; title: string | null; spaceTitle: string | null; excerpt: string | null; available: boolean; /** The object's type when it is no page (a board, a contact in the relations graph). */ typeId?: string;
  /** The reader may edit the page: its card opens it for editing on the board. */ canEdit?: true;
  /** In the trash and readable by this reader (not a page without access): `spaceId` is where it is restored, `canRestore` whether this reader may. */
  trashed?: true; spaceId?: string; canRestore?: true }

/** What a card reads of its item: the canvas passes its item as it has it (a `BoardItemDto`, or the model's item with the session's `target`). */
export interface BoardCardItem {
  id: string;
  targetResourceId?: string | null;
  target?: unknown;
}

const maxField = 4096;
const maxExcerpt = 600;

const isRecord = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);
/** A character class of code point ranges, built at run time so that no invisible character stands in this source. */
const range = (from: number, to = from) => `${String.fromCharCode(from)}-${String.fromCharCode(to)}`;
/**
 * What the server refuses in board text (Rules.IsMultiLineText, 400 `invalid_board_change` for the whole batch): control
 * characters (tab and NUL included) and bidirectional controls.
 */
export const refusedChars = new RegExp(`[${[range(0, 0x1f), range(0x7f, 0x9f), range(0x61c), range(0x200e, 0x200f), range(0x202a, 0x202e), range(0x2066, 0x2069)].join("")}]`, "g");
/** A tab, with the spaces around it, is one space: a row pasted from a spreadsheet reads as words, and empty cells add nothing. */
const tabs = /[ \t]*\t[ \t]*/g;
/** Text the server accepts: a tab becomes a space (runs collapse), every other refused character goes. Line breaks are the caller's business. */
export const cleanText = (value: string) => value.replace(tabs, " ").replace(refusedChars, "");

/** A single-line text: trimmed, runs of white space (a line break too) one space, refused characters gone; empty or too long is none. */
function line(value: unknown, max = maxField): string | null {
  if (typeof value !== "string") return null;
  const text = cleanText(value.replace(/\s+/g, " ")).trim();
  return text && text.length <= max ? text : null;
}
/** A text of several lines (an address): line breaks kept as \n, refused characters gone, trimmed; empty or too long is none. */
function lines(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const text = value.split(/\r\n|[\r\n]/).map(part => cleanText(part).trim()).join("\n").trim();
  return text && text.length <= maxField ? text : null;
}

/** An absolute http(s) address without credentials, or undefined: the only kind a card or a pasted link may be. */
export function httpUrl(value: string): URL | undefined {
  try {
    const url = new URL(value);
    return (url.protocol === "https:" || url.protocol === "http:") && !url.username && !url.password ? url : undefined;
  } catch { return undefined; }
}

/** A page card's target; `resourceId` is the item's own (`targetResourceId`) when the answer has none. Null when it is no page target. */
export function readPageTarget(raw: unknown, resourceId?: string | null): PageTargetView | null {
  if (!isRecord(raw) || (raw.type !== undefined && raw.type !== "atlas.page")) return null;
  const id = line(raw.resourceId) ?? resourceId ?? null;
  if (!id) return null;
  const title = line(raw.title);
  // `available` as sent; without it, a page with a title is there and one without is not.
  if (!(typeof raw.available === "boolean" ? raw.available : title !== null)) {
    const spaceId = line(raw.spaceId);
    return { resourceId: id, title: null, spaceTitle: null, excerpt: null, available: false, ...(raw.trashed === true && { trashed: true as const, ...(spaceId && { spaceId }), ...(raw.canRestore === true && { canRestore: true as const }) }) };
  }
  const excerpt = typeof raw.excerpt === "string" ? raw.excerpt.replace(/\s+/g, " ").trim().slice(0, maxExcerpt) : "";
  const typeId = line(raw.typeId);
  return { resourceId: id, title, spaceTitle: line(raw.spaceTitle), excerpt: excerpt || null, available: true, ...(typeId && { typeId }), ...(raw.canEdit === true && { canEdit: true as const }) };
}

/** "title · organization · department": what the editor's contact shows under the name. */
export const contactSubtitle = (contact: { title?: string | null; organization?: string | null; department?: string | null }) =>
  [contact.title, contact.organization, contact.department].filter(Boolean).join(" · ");

/** Each text once, in order, at most twenty (the editor's contact holds at most that many phones and e-mails). */
function strings(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return [...new Set(value.flatMap(item => line(item) ?? []))].slice(0, 20);
}

/**
 * A contact as the editor's contact block holds it, cleaned: only the fields the board takes in (name, function, organization,
 * department, login, phones, e-mails, website, logo or photo, address, directory id), trimmed and single-line (an address keeps its lines),
 * without control or bidirectional characters, no empty field or list. It does not check the editor's patterns: the form of a new
 * contact and the picker do that with the editor's own `normalizeContact`. Null without a name.
 */
export function contactProps(raw: unknown): MarkdownContact | null {
  if (!isRecord(raw)) return null;
  const name = line(raw.name);
  if (!name) return null;
  const contact: MarkdownContact = { name };
  for (const key of ["title", "organization", "department", "username", "website", "photo", "address", "directoryId", "objectId"] as const) {
    const text = key === "address" ? lines(raw[key]) : line(raw[key], key === "username" ? 256 : maxField);
    if (text) contact[key] = text;
  }
  for (const key of ["emails", "phones"] as const) {
    const list = strings(raw[key]);
    if (list.length) contact[key] = list;
  }
  return contact;
}
