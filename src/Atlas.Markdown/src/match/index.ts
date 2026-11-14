/**
 * `@atlas/markdown/match` — the slash menu's text matching (case and diacritics aside, its tiers) without the editor: a few
 * hundred bytes a host's shell can use for its own pickers and command palette before any editor loads. `./core` exports
 * the same functions.
 */
export { foldSearchText, pickNamedChoice, rankSuggestion, rankSuggestions } from '../features/suggestions/match'
export type { SuggestionRankInput, SuggestionRankOptions } from '../features/suggestions/match'
