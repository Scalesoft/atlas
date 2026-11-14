/** Keys of the `youtrack` section of `MarkdownStrings`; the Czech defaults live in plugins/youtrack/. */
export interface YouTrackStrings {
  /** Accessible name for the issue picker dialog. */
  pickerLabel: string
  /** Dialog heading; also the slash/toolbar action label. */
  pickerHeading: string
  queryLabel: string
  queryPlaceholder: string
  /** Accessible name for the picker's result list. */
  resultsLabel: string
  /** Status text summarizing the result count (0, 1, or many). */
  searchResultsStatus: (count: number) => string
  searchFailedStatus: string
  /** Slash/toolbar action description. */
  actionDescription: string
  /** Ctrl+Space row over text that looks like an issue key: find that issue and put it in place of the text. */
  issueFromText: (key: string) => string
  /** Error surfaced through a `md-youtrack-error` DOM event when insertion fails. */
  insertFailed: string
  /** Tooltip for an issue link that carries a saved state snapshot. */
  savedStateTitle: (title: string, state: string) => string
}
