import { shallowRef } from "vue";

/**
 * The page edited over the open board (BoardPageEditor, COL2-9), asked for by a page card's editing (Enter, F2, a double-click; a preview edits in place). Held here, not by the card:
 * the canvas unmounts a card scrolled out of view, and the editing must outlive that. One at a time; BoardView shows it.
 */
export const editedPage = shallowRef<{ pageId: string; title: string | null } | null>(null);

export function editPage(pageId: string, title: string | null) {
  editedPage.value = { pageId, title };
}
