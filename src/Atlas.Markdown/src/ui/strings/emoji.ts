/** Keys of the `emoji` section of `MarkdownStrings`; the Czech defaults live in plugins/emoji/. */
export interface EmojiStrings {
  /** Suggestion list label, and slash-menu item title. */
  pickerLabel: string
  emptyResults: string
  /** Slash-menu item description. */
  pickerDescription: string
  /** Ctrl+Space row on a written shortcode: „Emoji 😄 (smile)“. */
  fromText: (emoji: string, name: string) => string
}
