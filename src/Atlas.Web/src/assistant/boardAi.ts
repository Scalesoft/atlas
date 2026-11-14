import { watch } from "vue";
import type { MarkdownIntention } from "@atlas/markdown/core";
import { aiIntentAvailable, aiIntentLabels, aiText, readingIntents } from "../assist/aiIntents";
import { registerBoardIntentions, type BoardIntentionContext } from "../boards/boardIntentions";
import type { BoardItem } from "../boards/boardModel";
import t from "../i18n/assistant";
import { askAssistant } from "./assistantEntries";
import { useAssistant } from "./useAssistant";

// A password reference or concealed content in an item's Markdown: never sent (P11), the item gets no AI rows.
const hidden = /::?secret\{|conceal/i;

const own = (item: BoardItem) => (item.deleted ? "" : item.text?.trim() ?? "");
/** An item's text with, for a frame or a group, its members' texts under its title (what the person pointed at, P11). */
function contents(item: BoardItem, board: readonly BoardItem[], seen = new Set<string>()): string {
  seen.add(item.id);
  const members = board.filter(other => other.parent === item.id && !seen.has(other.id)).map(other => contents(other, board, seen));
  return [own(item), ...members].filter(Boolean).join("\n\n");
}

/**
 * P11 on boards: Zeptat se, Vysvětlit and Shrnout over the selected items in „Co s tím jde dělat“ (the board's own menu, P2), or over
 * the whole board when nothing is selected, with the same rule and limit as the page (aiIntents.ts). Their text (a note, a text, a
 * shape; a frame or group with its members' texts) goes to the assistant in Markdown. An item with no text (a page or contact card, a
 * component block, which keeps editor JSON; an unlabelled connector) is skipped, as an opaque element of a page is (P14); no rows only
 * when nothing readable remains, a secret is anywhere in it or the text is over the limit.
 */
export function boardAiRows({ items, board, canEdit }: Pick<BoardIntentionContext, "items" | "board" | "canEdit">): MarkdownIntention[] {
  const facts = () => ({ editing: canEdit, canAnswer: useAssistant().canAnswer.value, transform: false });
  const texts = (items.length ? items.map(item => contents(item, board)) : board.map(own)).filter(Boolean);
  if (!texts.length || texts.some(text => hidden.test(text))) return [];
  const text = aiText(texts.join("\n\n"));
  if (!text) return [];
  // `target` goes to the model (Czech); the row shows the same thing in the page language.
  const target = !items.length ? "Tabule" : items.length === 1 ? "Prvek tabule" : `${items.length} ${items.length <= 4 ? "prvky" : "prvků"} tabule`;
  const shown = !items.length ? t.board.board : items.length === 1 ? t.board.item : t.board.items(items.length);
  return readingIntents.filter(intent => aiIntentAvailable(intent, facts())).map((intent, index) => ({
    id: `ai.board.${intent}`, label: aiIntentLabels[intent], description: `AI · ${shown}`, group: "ai", priority: 100 + index,
    run: () => { if (aiIntentAvailable(intent, facts())) void askAssistant(intent, text, target); },
  }));
}

let release: (() => void) | undefined;
/**
 * Registers the rows while the assistant can answer and withdraws them when it cannot. The right button opens the board's menu only
 * on items it gives rows for (boardIntentions.hasBoardIntentions); elsewhere the browser's menu stays. Idempotent.
 */
export function startBoardAi() {
  if (release !== undefined) return;
  release = () => {};
  watch(useAssistant().canAnswer, can => {
    release?.();
    release = can ? registerBoardIntentions(boardAiRows) : () => {};
  }, { immediate: true });
}
