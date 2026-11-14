import { registerMarkdownStrings, type MarkdownStrings } from '../../core'
import type { TocEntryKey } from './strings'
import './strings'

/** Default (Czech) text of the `toc` section that only the lazy runtime renders. */
export const tocStrings: MarkdownStrings['toc'] = registerMarkdownStrings('toc', {
  emptyDocument: 'Přidejte do dokumentu nadpisy, které se zobrazí v obsahu.',
  titleLabel: 'Název obsahu (Enter upraví)',
  rangeLabel: 'Rozsah nadpisů',
  allHeadings: 'Všechny nadpisy',
} satisfies Omit<MarkdownStrings['toc'], TocEntryKey>)
