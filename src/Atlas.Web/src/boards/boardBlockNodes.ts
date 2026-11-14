import { api } from "../api";
import type { BoardBlockNode } from "./boardApi";
import type { PasteItem } from "./boardPaste";
import { httpUrl } from "./boardTargets";

/**
 * The nodes of the editor that what a board takes in becomes: a contact is the
 * editor's contacts block, a YouTrack issue is its `youtrackIssue` node, stored exactly as the editor's plugins store them, so the
 * board has no contact or task of its own. Only the node is made here; rendering, the wrapper a validator wants around an inline node
 * and the `block` item that carries it are the board's blocks module.
 */

/** What a paste, a drop or a picker hands the canvas: a card of a page (or of one section), a note, a block of the editor, or a
 * live card of an object (a directory contact). */
export type BoardInsert =
  | { kind: "page"; resourceId: string; anchor?: string }
  | { kind: "note"; text: string }
  | { kind: "block"; block: BoardBlockNode }
  | ({ kind: "object" } & PickedObject);
/** An object a picker chose to place as a live card: its id and type. */
export interface PickedObject { objectId: string; typeId: string }

/** The snapshot of an issue the editor's `youtrackIssue` node stores (`@atlas/markdown/plugins/youtrack`, `YouTrackIssue`). */
export interface YouTrackIssueSnapshot { id: string; key: string; title: string; url: string; state?: string | null }

/** The editor's rule for an issue field (`validateIssueText`): a non-blank single line of at most 2000 characters. */
const issueText = (value: unknown): string | null =>
  typeof value === "string" && value.trim() && value.length <= 2000 && !/[\u0000-\u001f\u007f]/.test(value) ? value.trim() : null;

/**
 * The `youtrackIssue` node for an issue, with the attributes the editor's plugin stores (`id`, `key`, `title`, `url`, `state`), checked
 * by the editor's own rules (`normalizeYouTrackIssue`: non-blank single lines, an http(s) URL without credentials). Null when the issue
 * does not pass. The node is inline in the editor: in a page it stands in a paragraph.
 */
export function youtrackNode(issue: YouTrackIssueSnapshot): BoardBlockNode | null {
  const id = issueText(issue.id), key = issueText(issue.key), title = issueText(issue.title);
  const url = typeof issue.url === "string" && issueText(issue.url) ? httpUrl(issue.url.trim())?.href : undefined;
  const state = issue.state == null ? null : issueText(issue.state);
  if (!id || !key || !title || !url || (issue.state != null && !state)) return null;
  return { type: "youtrackIssue", attrs: { id, key, title, url, state } };
}

/**
 * The contacts block of the editor for one contact: `contactNode` of `@atlas/markdown/plugins/contacts`, after its own
 * `normalizeContact`, so a contact that the editor would refuse is refused here (a TypeError with the editor's Czech message), not by
 * the server with a whole batch. The plugin is imported when this is first called.
 */
export async function contactBlock(contact: unknown): Promise<BoardBlockNode> {
  const { contactNode, normalizeContact } = await import("@atlas/markdown/plugins/contacts");
  return contactNode(normalizeContact(contact)) as BoardBlockNode;
}

/**
 * The paste or drop a person made, ready for the canvas: pages and notes as they are, each YouTrack reference as the `youtrackIssue`
 * block of the issue that `resolveIssue` answers (the host's resolve, in the context of the board's space). A reference that does not
 * resolve (no such issue in the allowed projects, YouTrack unavailable, a word that only looks like an ID) stays what was written,
 * a note, so nothing pasted is lost and no card is made of nothing. Every reference is asked at once: the board's resolver sends
 * them in one request.
 */
export function resolvePasteItems(
  items: readonly PasteItem[],
  resolveIssue: (issueId: string) => Promise<YouTrackIssueSnapshot | null>,
): Promise<BoardInsert[]> {
  return Promise.all(items.map(async (item): Promise<BoardInsert> => {
    if (item.kind !== "youtrack") return item;
    let block: BoardBlockNode | null = null;
    try {
      const issue = await resolveIssue(item.issueId);
      block = issue && youtrackNode(issue);
    } catch { /* unresolved: kept as text below */ }
    return block ? { kind: "block", block } : { kind: "note", text: item.issueId };
  }));
}

/**
 * The board's own issue resolve (its space's projects, as a page of the space has them): null for an issue that is not there. The
 * references asked in the same turn (a whole paste) go to the server in one request of at most 100, which answers them in order.
 */
export const youtrackResolver = (boardId: string) => {
  let batch: { references: string[]; answer: Promise<(YouTrackIssueSnapshot | null)[]> } | null = null;
  return (issueId: string): Promise<YouTrackIssueSnapshot | null> => {
    if (!batch || batch.references.length === 100) {
      const references: string[] = [];
      const answer = Promise.resolve().then(() => {
        if (batch?.references === references) batch = null;
        return api<(YouTrackIssueSnapshot | null)[]>(`/boards/${encodeURIComponent(boardId)}/youtrack/resolve`, { method: "POST", body: JSON.stringify({ references }) });
      });
      batch = { references, answer };
    }
    const index = batch.references.push(issueId) - 1;
    return batch.answer.then(issues => issues[index] ?? null);
  };
};
