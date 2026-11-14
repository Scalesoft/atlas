import { registerMarkdownStrings, type MarkdownStrings } from '../../core'

/** Default (Czech) text of the `emoji` section. */
export const emojiStrings: MarkdownStrings['emoji'] = registerMarkdownStrings('emoji', {
  pickerLabel: 'Emoji',
  emptyResults: 'Žádné emoji',
  pickerDescription: 'Vybrat Unicode emoji',
  fromText: (emoji, name) => `Emoji ${emoji} (${name})`,
} satisfies MarkdownStrings['emoji'])
