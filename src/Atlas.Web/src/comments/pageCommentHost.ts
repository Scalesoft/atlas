import type { BoardBlockHost } from "../boards/editorBlocks";
import { createPageSource, createPeopleServices } from "../editorDirectories";
import { createYouTrackClient } from "../editor/editorSetup";
import { lexiconSource } from "../linking/lexicon";

let last: { key: string; host: BoardBlockHost } | undefined;

/**
 * The editor services of a page's comments: `[[` pages (its space first), YouTrack of its space and the page's people (`@`, contacts,
 * recognised names), as the page's own editor has them. The same object for the same page, so the editor's plugin list is built once per page.
 */
export function pageCommentHost(pageId: string, spaceId: string): BoardBlockHost {
  const key = `${pageId}:${spaceId}`;
  if (last?.key !== key) {
    last = { key, host: { youTrack: createYouTrackClient(() => spaceId), pages: createPageSource(() => [], { spaceId: () => spaceId, pageId: () => pageId }), people: createPeopleServices(() => pageId, () => spaceId), lexicon: lexiconSource(() => spaceId) } };
  }
  return last.host;
}
