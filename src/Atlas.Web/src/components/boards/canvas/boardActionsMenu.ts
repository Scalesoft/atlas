import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/forest.css";
import "@atlas/markdown/themes/midnight.css";
import "@atlas/markdown/themes/ocean.css";

/**
 * The chunk of „Co s tím jde dělat“ on a board (P2): the page editor's own menu (its list, keys, filter and motion) and the styles it
 * is drawn with, which a board that never edits text has not loaded. Loaded through actionsMenuChunk.ts; the canvas opens it on the
 * selection (BoardCanvas `openActions`).
 */
export { openMarkdownIntentionsMenu as openActionsMenu, formatMarkdownShortcut } from "@atlas/markdown/core";
