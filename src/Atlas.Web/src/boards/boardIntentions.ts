import type { MarkdownIntention, MarkdownPlugin } from "@atlas/markdown/core";
import type { BoardItem } from "./boardModel";

/**
 * Rows that a module adds to „Co s tím jde dělat“ on board items (P2), beside the board's own (boardCommands.actionCommands): the
 * place where the AI wave puts its intentions (P11, rows of `group: "ai"`), which a provider returns only while AI can answer now.
 * The menu is the page editor's (openMarkdownIntentionsMenu); a provider answering later adds its rows when it answers.
 */
export interface BoardIntentionContext {
  /** The selected items the menu was opened on; none: the menu is over the whole board. */
  readonly items: readonly BoardItem[];
  /** Every item on the board (a frame's or group's members, the whole board). */
  readonly board: readonly BoardItem[];
  readonly canEdit: boolean;
  /** Aborted when the menu closes. */
  readonly signal: AbortSignal;
}
export type BoardIntentionProvider = (context: BoardIntentionContext) => readonly MarkdownIntention[] | Promise<readonly MarkdownIntention[]>;

const providers = new Set<BoardIntentionProvider>();

/** Adds rows to the menu of every board; the returned function removes them. */
export function registerBoardIntentions(provider: BoardIntentionProvider): () => void {
  providers.add(provider);
  return () => { providers.delete(provider); };
}

/**
 * Some module offers rows for these items (an answer still on its way counts): the right button then opens the menu even where the
 * board itself offers nothing. An empty answer does not count, so the browser's menu stays (P2).
 */
export const hasBoardIntentions = (context: BoardIntentionContext) => boardIntentionRows(context).some(rows => rows instanceof Promise ? (rows.catch(() => {}), true) : rows.length > 0);

/** Every provider's answer for the context; one that throws is left out. */
export function boardIntentionRows(context: BoardIntentionContext): (readonly MarkdownIntention[] | Promise<readonly MarkdownIntention[]>)[] {
  return [...providers].flatMap(provider => { try { return [provider(context)]; } catch { return []; } });
}

/**
 * The same rows while a board text is being edited (P11: the reading intents also while editing; P14: as in a page): the text
 * editor's own „Co s tím jde dělat“ (Ctrl+Space, Alt+Enter) gets what the modules give for the selected text, else for the whole
 * `text()` (its Markdown), as one item of the board.
 */
export function boardTextIntentions(text: () => string): MarkdownPlugin {
  return { id: "atlas-board-text-intentions", apiVersion: 1, create: () => ({
    intentions: ({ editor, empty, text: selected, signal }) => {
      const item: BoardItem = { id: "text", kind: "text", x: 0, y: 0, width: 0, height: 0, z: 0, text: empty ? text() : selected.replace(/\uFFFC/gu, "") };
      const rows = boardIntentionRows({ items: [item], board: [item], canEdit: editor.isEditable, signal });
      return rows.some(row => row instanceof Promise)
        ? Promise.all(rows.map(row => Promise.resolve(row).catch(() => []))).then(lists => lists.flat())
        : (rows as (readonly MarkdownIntention[])[]).flat();
    },
  }) };
}
