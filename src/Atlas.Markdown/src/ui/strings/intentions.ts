import { markdownStrings, registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `intentions` section of `MarkdownStrings` (see ../strings.ts): the Ctrl+Space menu. */
export interface IntentionsStrings {
  /** Heading and accessible label of the menu. */
  label: string
  /** The heading while typing filters the rows. */
  filtered: (filter: string) => string
  /** No row for the caret or selection (or none matches the filter). */
  empty: string
  /** The row that turns a chip back into its text. */
  chipToText: string
  /** The row that stops offering an underlined page title or issue key in this editor (R1: nothing is written to the document). */
  notOffered: string
  /** The rows of a selected element (a block, a chip); the delete row's word is core's also where the element's toolbar runs it (P12):
   *  a chip stands for something that stays (a person, an issue, a page, a password) and is removed from the text, a block is deleted. */
  copy: string
  delete: string
  remove: string
  /** The block menu's row that copies the block under it (Alt+Shift+↓). */
  duplicate: string
  /** The rows that turn the block under the caret into another kind of text block (Změnit na…). */
  turnInto: (kind: string) => string
  /** The task list's kind in Změnit na… (the old toolbar's word). */
  tasks: string
}

export const intentionsStrings: IntentionsStrings = registerMarkdownStrings('intentions', {
  label: 'Co s tím jde dělat',
  filtered: filter => `Co s tím jde dělat: „${filter}“`,
  empty: 'Tady není co nabídnout',
  chipToText: 'Převést na text',
  notOffered: 'Nenabízet',
  copy: 'Kopírovat',
  delete: markdownStrings.delete,
  remove: 'Odebrat',
  duplicate: 'Duplikovat',
  turnInto: kind => `Změnit na: ${kind}`,
  tasks: 'Úkoly',
} satisfies IntentionsStrings)
