import type { Transaction } from "@tiptap/pm/state";

/**
 * A change that came from the collaboration document (a collaborator's edit, or the shared undo) rather than from this editor. Read by
 * the key of @tiptap/y-tiptap's sync plugin, so a page without collaboration does not load Yjs for it (remoteChange.test.ts pins the key).
 */
export const isRemote = (tr: Transaction) => (tr.getMeta("y-sync$") as { isChangeOrigin?: boolean } | undefined)?.isChangeOrigin === true;
