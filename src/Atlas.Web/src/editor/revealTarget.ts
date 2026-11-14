/**
 * A place in the page that the host asks the editor to show (`DocumentEditor.reveal`), for example from a finding of
 * the publication check (plan §9, package E). Targets are computed from the saved document JSON, so they address
 * nodes by their child-index path from the document root; the editor verifies them against its live document and
 * does nothing when the place no longer exists.
 *
 * - `text`: a range inside a textblock, as offsets into its inline content (a text character, an inline atom and a
 *   hard break each count one, the ProseMirror positions inside the block);
 * - `page`: the first link to a page, a `pageReference` node or an `href` with `#/pages/<id>`;
 * - `heading`: a heading by its anchor id, or by its path when it has none.
 */
export type RevealTarget =
  | { readonly kind: "text"; readonly path: readonly number[]; readonly from: number; readonly to: number }
  | { readonly kind: "page"; readonly pageId: string }
  | { readonly kind: "heading"; readonly id: string | null; readonly path: readonly number[] };
