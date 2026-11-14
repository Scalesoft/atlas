import t from "../i18n/assistant";
import { detectSecrets } from "./rules/secretDetector";

/*
 * P11 „AI“ (docs/interaction-rules.md): one set of intents over anything one can point at, one size limit and one availability rule
 * for every surface — the page editor's „Co s tím jde dělat“ and selection toolbar (writing.ts), the selection bar of a read page
 * (ReadonlySelectionActions), the board item menu (assistant/boardAi.ts) and the assistant panel. An intent that cannot answer now
 * is not offered at all (no notice); an intent that changes content is a proposal to confirm (Použít / Zahodit), one undo step.
 */

export type AiIntent = "ask" | "explain" | "summarize" | "rewrite" | "translate" | "complete" | "links";
/** Getters: read where a row is built, so the page language shows (not at import). */
export const aiIntentLabels: Readonly<Record<AiIntent, string>> = {
  get ask() { return t.intents.ask; }, get explain() { return t.intents.explain; }, get summarize() { return t.intents.summarize; }, get rewrite() { return t.intents.rewrite; },
  get translate() { return t.intents.translate; }, get complete() { return t.intents.complete; }, get links() { return t.intents.links; },
};
/** The intents that change nothing: the only ones reading offers. They answer in the assistant panel. */
export const readingIntents = ["ask", "explain", "summarize"] as const satisfies readonly AiIntent[];
export type ReadingIntent = typeof readingIntents[number];
export const isReadingIntent = (intent: AiIntent): intent is ReadingIntent => (readingIntents as readonly AiIntent[]).includes(intent);

/** The longest question the server takes (AiConversationLimits.QuestionMaxLength). */
export const QUESTION_MAX_LENGTH = 2000;
/** The longest name of a target in a prompt („Sekce „…““); a longer one is cut (aiTarget). */
export const AI_TARGET_MAX = 60;
/** What a question keeps beside the text: the person's own words about it (Zeptat se) and the fixed prompt or prefix with the target's name. */
const QUESTION_ROOM = 300, PROMPT_ROOM = 100;
/**
 * The one size limit of text pointed at (a selection, a section, an element, a board item): with the longest prompt and room for a
 * typed question it fits the assistant's question, and the writing endpoint (4 000). A longer text gets no AI row.
 */
export const AI_TEXT_LIMIT = QUESTION_MAX_LENGTH - QUESTION_ROOM - PROMPT_ROOM;
/** A target's name as a prompt says it, cut so the prompt stays within PROMPT_ROOM. */
export const aiTarget = (target: string) => (target.length <= AI_TARGET_MAX ? target : `${target.slice(0, AI_TARGET_MAX - 1)}…`);

/**
 * The one availability rule. The reading intents need an answer now: the assistant, or the agent with „S nástroji“ on
 * (`useAssistant().canAnswer`). The intents that change content exist only while editing and need the writing capability
 * (`editorTransform`); „Navrhnout odkazy a kontakty“ is the block's own suggestions of the assist module (rules first, the model
 * with `editorIntent`), so editing alone is enough.
 */
export function aiIntentAvailable(intent: AiIntent, facts: { readonly editing: boolean; readonly canAnswer: boolean; readonly transform: boolean }): boolean {
  if (isReadingIntent(intent)) return facts.canAnswer;
  if (!facts.editing) return false;
  return intent === "links" || facts.transform;
}

/** The text an intent may send: trimmed, non-empty, within the limit and without a detected secret; else null (no row). */
export function aiText(text: string | null | undefined): string | null {
  const value = text?.trim();
  return value && value.length <= AI_TEXT_LIMIT && !detectSecrets(value).length ? value : null;
}
