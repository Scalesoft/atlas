import type { MarkdownCollaborationAdapter } from "@atlas/markdown/core";

/**
 * Where an edited board text comes from and goes to: the one seam between `BoardTextEditor` and the board's data.
 *
 * Today a board text is one Markdown string per item (spec §3.4): `read` gives the text when editing starts and `commit` receives
 * the cleaned text once, when editing ends with a change (Esc, a click outside, Enter in an inline text, or the editor going away
 * mid-edit, so nothing typed is lost). `read` follows the item while it is edited: a colleague's text that arrived meanwhile is
 * merged into what goes out, never overwritten (BoardTextEditor, COL2-9).
 *
 * Later a text item may bind to a collaborative Yjs fragment of its own, so remote carets show inside a note as they do in a
 * page: the binding then carries that fragment's `collaboration` adapter, the editor binds to it instead of `read`, and shows
 * the collaborators' carets and colours with the page editor's presence plugin. `commit` still receives the Markdown
 * projection when editing ends, so the item's `text` stays current for search, readers and the static rendering.
 */
export interface TextBinding {
  read(): string;
  commit(markdown: string): void;
  /**
   * While editing: the text so far, after a pause in typing and at once when the page is hidden or left (a reload, a closed tab),
   * so nothing typed is lost to a reload (review cycle 3, U3-1). The editing goes on; `commit` at its end then only sends what
   * changed since. A binding without it (a contact field, whose save ends the editing) hears only `commit`.
   */
  save?(markdown: string): void;
  readonly collaboration?: MarkdownCollaborationAdapter;
}
