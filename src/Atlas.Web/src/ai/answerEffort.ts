import t from "../i18n/ai-answer";

/** How much of the wiki one answer reads (server AnswerEffort): more sources for the model to summarize, more input tokens. */
export type AnswerEffort = "quick" | "balanced" | "thorough" | "deep" | "max";
/** The steps of the effort picker, in order; the source counts are the server's AnswerEffort.Take. */
export const EFFORT_STEPS: readonly { value: AnswerEffort; label: string; sources: number }[] = [
  { value: "quick", get label() { return t.effort.quick; }, sources: 6 },
  { value: "balanced", get label() { return t.effort.balanced; }, sources: 8 },
  { value: "thorough", get label() { return t.effort.thorough; }, sources: 10 },
  { value: "deep", get label() { return t.effort.deep; }, sources: 12 },
  { value: "max", get label() { return t.effort.max; }, sources: 14 },
];
