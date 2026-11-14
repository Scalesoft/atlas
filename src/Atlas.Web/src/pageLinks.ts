import { buildShellHashEncoded, type ShellRoute } from "./shellRoute";

/** A document link must not carry the current omnisearch or authentication query. */
export function pageLink(pageId: string, heading?: string) {
  return contentLink({ view: "page", pageId, heading });
}
/** A block of a page (a requirement), revealed once the page has rendered it (U4-2). */
export function pageBlockLink(pageId: string, block: string) {
  return contentLink({ view: "page", pageId, block });
}
/** A comment of a page: the page opens at it with the comment highlighted (a notification of a mention). */
export function pageCommentLink(pageId: string, comment: string) {
  return contentLink({ view: "page", pageId, comment });
}
export function spaceLink(spaceId: string) {
  return contentLink({ view: "space", spaceId });
}
/** A board (module Boards). */
export function boardLink(boardId: string) {
  return contentLink({ view: "board", boardId });
}
/** The address of a node of the space tree, library or search: a board's, or a page's; a reference node's is its target's (a page
 * at its heading or block, a board), none for another object (it opens in the dock). */
export function contentItemLink(item: { id: string; type?: string; targetId?: string | null; targetTypeId?: string | null; anchor?: string | null }) {
  if (item.type === "reference") {
    if (!item.targetId) return undefined;
    if (item.targetTypeId === "atlas.board") return boardLink(item.targetId);
    return item.targetTypeId === "atlas.page" ? pageLink(item.targetId, item.anchor ?? undefined) : undefined;
  }
  return item.type === "board" ? boardLink(item.id) : pageLink(item.id);
}
export function isPlainNavigationClick(event: MouseEvent): boolean {
  return !event.defaultPrevented && event.button === 0
    && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey;
}
function contentLink(route: ShellRoute) {
  const url = new URL(location.href);
  url.search = "";
  url.hash = buildShellHashEncoded(route);
  return url.href;
}
