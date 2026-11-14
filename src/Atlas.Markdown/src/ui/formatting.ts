/** Visible tools only; the content profile still determines what can be stored. */
export type MarkdownFormattingItem = 'headings' | 'bold' | 'italic' | 'underline' | 'strike'
  | 'highlight' | 'code' | 'link' | 'bullet-list' | 'ordered-list' | 'task-list' | 'timed-list'
  | 'blockquote' | 'code-block' | 'alignment'

export const formattingGroups: readonly (readonly MarkdownFormattingItem[])[] = [
  ['headings'],
  ['bold', 'italic', 'underline', 'strike', 'highlight', 'code'],
  ['link'],
  ['bullet-list', 'ordered-list', 'task-list', 'timed-list', 'blockquote', 'code-block'],
  ['alignment'],
]

export function formattingItems(compact: boolean, rich: boolean, configured?: readonly MarkdownFormattingItem[]): ReadonlySet<MarkdownFormattingItem> {
  const defaults = compact ? [...formattingGroups[1], ...formattingGroups[2]] : formattingGroups.flat()
  return new Set((configured ?? defaults).filter(item => rich || (item !== 'underline' && item !== 'highlight' && item !== 'timed-list')))
}
