import { parseShellHash } from "../shellRoute";
import { cleanText, httpUrl } from "./boardTargets";

/**
 * What a paste or a drop onto a board turns into: a card of a page (or of one
 * section of it), a reference to a YouTrack issue or a note. Pure: no I/O, nothing read from `location`; `appPasteContext` is the one
 * place that does. A YouTrack reference is only an ID here: the `youtrackIssue` block of the editor needs the issue's title and URL,
 * which the host resolves (`resolvePasteItems` in boardBlockNodes.ts turns the references into blocks).
 */
export type PasteItem =
  | { kind: "page"; resourceId: string; /** The ID of a heading of the page (`heading-anchors`): a card of that one section. */ anchor?: string }
  | { kind: "youtrack"; issueId: string }
  | { kind: "note"; text: string }
  /** Any other object dragged in (a contact, a board, a row of Obsah): its live card (`boardDrop.ts`, never parsed from text). */
  | { kind: "object"; objectId: string; typeId: string };

export interface PasteContext {
  /** `location.origin` of this Atlas: a page address of another instance is plain text. */
  origin: string;
  /** The path the application is served under (`location.pathname`), "/" when omitted. */
  pathname?: string;
  /**
   * The configured YouTrack base URL(s), "https://youtrack.example/" or with a path. The web has no reader endpoint for them
   * (only the administration reads the setting), so the caller passes what it knows; without any, an issue URL stays text and
   * only a bare issue id makes a card.
   */
  youtrackBaseUrls?: readonly string[];
  /** false while the YouTrack module is off: issue ids and URLs are then plain text. Default true. */
  youtrack?: boolean;
}

export const maxPasteItems = 100;
/** The server refuses a longer `text` (BoardEditing.MaxText); a longer line becomes several notes instead of a refused batch. */
export const maxNoteText = 4000;

const pageId = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** The issue reference the YouTrack integration accepts (Atlas.YouTrack `IssueReference`). */
const issueId = /^[A-Za-z0-9][A-Za-z0-9_]*-[0-9]+$/;
const lineBreak = new RegExp("\r\n|[\r\n\u2028\u2029]");

interface Resolved { app?: URL; youtrack: boolean; bases: URL[] }

const withoutTrailingSlash = (path: string) => path.replace(/\/+$/, "");

function resolve(context: PasteContext): Resolved {
  const youtrack = context.youtrack !== false;
  const app = httpUrl(context.origin) && new URL(context.pathname ?? "/", context.origin);
  const bases = youtrack ? (context.youtrackBaseUrls ?? []).flatMap(base => httpUrl(base) ?? []) : [];
  return { app: app || undefined, youtrack, bases };
}

function pageReference(token: string, { app }: Resolved): PasteItem | undefined {
  if (!app || !/^(?:https?:\/\/|#\/|\/)/i.test(token)) return undefined;
  let url: URL;
  try { url = new URL(token, app); } catch { return undefined; }
  if (url.origin !== app.origin || url.username || url.password || withoutTrailingSlash(url.pathname) !== withoutTrailingSlash(app.pathname)) return undefined;
  const route = parseShellHash(url.hash);
  if (route.view !== "page" || !pageId.test(route.pageId)) return undefined;
  // The link of a section, `#/pages/<id>?heading=<heading id>` (DocumentEditor's heading and section links), is a card of that section.
  return { kind: "page", resourceId: route.pageId.toLowerCase(), ...(route.heading && { anchor: route.heading }) };
}

function issueReference(token: string, { youtrack, bases }: Resolved): PasteItem | undefined {
  if (!youtrack) return undefined;
  if (issueId.test(token)) return { kind: "youtrack", issueId: token };
  const url = /^https?:\/\//i.test(token) ? httpUrl(token) : undefined;
  if (!url) return undefined;
  for (const base of bases) {
    const prefix = `${withoutTrailingSlash(base.pathname)}/issue/`;
    if (url.origin !== base.origin || !url.pathname.startsWith(prefix)) continue;
    try {
      // The id is the first segment after /issue/: a title, a query or a fragment may follow it (the address bar of YouTrack).
      const id = decodeURIComponent(url.pathname.slice(prefix.length).split("/")[0]!);
      if (issueId.test(id)) return { kind: "youtrack", issueId: id };
    } catch { /* a malformed escape is no issue id */ }
  }
  return undefined;
}

export const isPageId = (value: string) => pageId.test(value);

const reference = (token: string, resolved: Resolved) => pageReference(token, resolved) ?? issueReference(token, resolved);

/** At most `maxNoteText` characters each, at a space where there is one, never between the halves of a surrogate pair. */
function* notes(line: string): Generator<string> {
  let rest = line;
  while (rest.length > maxNoteText) {
    let cut = rest.lastIndexOf(" ", maxNoteText);
    if (cut <= 0) {
      cut = maxNoteText;
      const last = rest.charCodeAt(cut - 1);
      if (last >= 0xd800 && last <= 0xdbff) cut--;
    }
    yield rest.slice(0, cut).trimEnd();
    rest = rest.slice(cut).trimStart();
  }
  if (rest) yield rest;
}

/**
 * The items of a text in the order of its lines, and how many beyond the first 100 were left out. A line made only of
 * references (separated by white space, commas or semicolons) is a card for each; any other non-empty line is one note, whole.
 * A page or an issue appears once however often and in whichever form it is pasted; equal notes stay.
 */
export function scanPaste(text: string, context: PasteContext): { items: PasteItem[]; omitted: number } {
  const resolved = resolve(context);
  const found: PasteItem[] = [];
  const seen = new Set<string>();
  const add = (item: PasteItem) => {
    const key = item.kind === "page" ? `page:${item.resourceId}#${item.anchor ?? ""}` : item.kind === "youtrack" ? `youtrack:${item.issueId.toLowerCase()}` : undefined;
    if (key) {
      if (seen.has(key)) return;
      seen.add(key);
    }
    found.push(item);
  };
  for (const raw of text.split(lineBreak)) {
    const line = cleanText(raw).trim();
    if (!line) continue;
    const references = line.split(/[\s,;]+/).filter(Boolean).map(token => reference(token, resolved));
    if (references.length && references.every(Boolean)) references.forEach(item => add(item!));
    else for (const chunk of notes(line)) add({ kind: "note", text: chunk });
  }
  return { items: found.slice(0, maxPasteItems), omitted: Math.max(0, found.length - maxPasteItems) };
}

/** `scanPaste` without the count of what was left out. */
export const parsePaste = (text: string, context: PasteContext): PasteItem[] => scanPaste(text, context).items;

/** The context of the running application; the YouTrack part is the caller's knowledge. */
export function appPasteContext(youtrack: Pick<PasteContext, "youtrack" | "youtrackBaseUrls"> = {}): PasteContext {
  return { origin: location.origin, pathname: location.pathname, ...youtrack };
}
