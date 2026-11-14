import { registerMarkdownStrings, type MarkdownStrings } from '../../core'

/** Default (Czech) text of the `toc` section that the plugin entry renders. */
const entry = {
  defaultTitle: 'Obsah dokumentu',
  slashDescription: 'Nadpisy stránky jako odkazy',
  titleRequired: 'Obsah vyžaduje název (nejvýše 200 znaků) a rozsah úrovní nadpisů 1–6.',
  headingWithoutText: 'Nadpis bez textu',
} satisfies Partial<MarkdownStrings['toc']>
/** Keys registered here; ./runtimeStrings adds the rest when the lazy runtime loads. */
export type TocEntryKey = keyof typeof entry
export const tocStrings: Pick<MarkdownStrings['toc'], TocEntryKey> = registerMarkdownStrings('toc', entry)
