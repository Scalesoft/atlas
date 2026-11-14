import { registerMarkdownStrings, type MarkdownStrings } from '../../core'

/** Default (Czech) text of the `pages` section. */
export const pagesStrings: MarkdownStrings['pages'] = registerMarkdownStrings('pages', {
  invalidPageReference: 'Neplatná reference stránky.',
  pageUnavailable: 'Stránka není dostupná',
  verificationFailed: 'Stránku se nepodařilo ověřit',
  label: 'Odkaz na stránku',
  description: 'Vyhledat stránku wiki',
  suggestionHint: 'Napište název stránky',
  linkSelection: text => `Odkaz na stránku „${text}“`,
} satisfies MarkdownStrings['pages'])
