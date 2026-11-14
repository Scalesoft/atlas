import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `suggestions` section of `MarkdownStrings` (see ../strings.ts). */
export interface SuggestionsStrings {
  /** Default empty-list message when the caller supplies no `emptyText`. */
  emptyResults: string
  /** Default error message when the caller supplies no `errorText`. */
  searchFailed: string
  /** Default idle-state hint when the caller supplies no `hintText`. */
  startTypingHint: string
}

export const suggestionsStrings: SuggestionsStrings = registerMarkdownStrings('suggestions', {
  emptyResults: 'Nic nenalezeno',
  searchFailed: 'Hledání se nezdařilo.',
  startTypingHint: 'Začněte psát pro hledání.',
} satisfies SuggestionsStrings)
