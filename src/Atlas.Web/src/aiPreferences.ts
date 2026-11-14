import { api, put } from "./api";
import shellMore from "./i18n/shell-more";

/** "on" and "off" are chosen here; "rules" (rules only, without a model) arrives with the model layer and is kept as it is. */
export type EditorSuggestionsMode = "on" | "rules" | "off";
/** Odpověď v hledání: ask = on request (the card offers it), auto = while searching, never = no card (Q6). */
export type SearchAnswerMode = "ask" | "auto" | "never";
/** The Assistant panel's starting scope: the page or space it is opened on, or every wiki page the user may read (Q6). */
export type AssistantScopeMode = "context" | "wiki";
export interface AiPreferences {
  editorSuggestions: EditorSuggestionsMode;
  disabledSuggestionTypes: string[];
  /** Phase 3 (Q6); an older server omits both, the defaults are auto and context. */
  searchAnswer?: SearchAnswerMode;
  assistantScope?: AssistantScopeMode;
  /** CAS token of the saved preferences; `null` until the first save. */
  version: string | null;
}
export interface SuggestionTypeInfo { value: string; label: string; description: string; icon: string }

/** The kinds of suggestion the editor offers while typing, in the order the list shows them. */
export const suggestionTypes: SuggestionTypeInfo[] = [
  { value: "contact", get label() { return shellMore.suggest.contact.label; }, get description() { return shellMore.suggest.contact.description; }, icon: "user" },
  { value: "page-link", get label() { return shellMore.suggest.pageLink.label; }, get description() { return shellMore.suggest.pageLink.description; }, icon: "link" },
  { value: "issue", get label() { return shellMore.suggest.issue.label; }, get description() { return shellMore.suggest.issue.description; }, icon: "circle-check" },
  { value: "structure", get label() { return shellMore.suggest.structure.label; }, get description() { return shellMore.suggest.structure.description; }, icon: "list" },
  { value: "link-mention", get label() { return shellMore.suggest.mention.label; }, get description() { return shellMore.suggest.mention.description; }, icon: "quote" },
];

export const aiPreferencesApi = {
  load: (signal?: AbortSignal) => api<AiPreferences>("/me/ai-preferences", { signal }),
  save: (draft: { editorSuggestions: EditorSuggestionsMode; disabledSuggestionTypes: string[]; searchAnswer?: SearchAnswerMode; assistantScope?: AssistantScopeMode }, expectedVersion: string | null) =>
    put<AiPreferences>("/me/ai-preferences", { ...draft, expectedVersion }),
  /** DELETE /api/ai/conversations (Q4): all of the user's conversations; works without AI. */
  deleteAllConversations: () => api<void>("/ai/conversations", { method: "DELETE" }),
};
export type AiPreferencesApi = typeof aiPreferencesApi;

export interface SpaceAiSettings { included: boolean; version: string | null }
export const spaceAiApi = {
  load: (spaceId: string, signal?: AbortSignal) => api<SpaceAiSettings>(`/spaces/${encodeURIComponent(spaceId)}/ai-settings`, { signal }),
  save: (spaceId: string, included: boolean, expectedVersion: string | null) =>
    put<SpaceAiSettings>(`/spaces/${encodeURIComponent(spaceId)}/ai-settings`, { included, expectedVersion }),
};
export type SpaceAiApi = typeof spaceAiApi;
