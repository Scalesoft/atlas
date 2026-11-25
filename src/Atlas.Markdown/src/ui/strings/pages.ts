/** Keys of the `pages` section of `MarkdownStrings`; the Czech defaults live in plugins/pages/. */
export interface PagesStrings {
  invalidPageReference: string
  /** Link label and tooltip when the host's resolver reports the target page is gone or unreadable. */
  pageUnavailable: string
  /** Link label and tooltip when the host's resolver call itself fails. */
  verificationFailed: string
  /** Slash menu item / toolbar action label, and suggestion group label. */
  label: string
  description: string
  suggestionHint: string
  /** Ctrl+Space row over selected words: search a page with them. */
  linkSelection: (text: string) => string
}
