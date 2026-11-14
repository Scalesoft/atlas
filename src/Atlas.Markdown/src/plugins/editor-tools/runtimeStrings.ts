import { registerMarkdownStrings, type MarkdownStrings } from '../../core'
import type { EditorToolsEntryKey } from './strings'
import './strings'

/** Default (Czech) text of the `editorTools` section that only the lazy runtime renders. */
export const editorToolsStrings: MarkdownStrings['editorTools'] = registerMarkdownStrings('editorTools', {
  codeContext: 'Kód',
  codeLanguage: 'Jazyk kódu',
  copyCode: 'Kopírovat kód',
  copyUnavailable: 'Kopírování není v tomto prostředí dostupné',
  copySuccess: 'Kód zkopírován',
  copyFailed: 'Kopírování selhalo',
  searchFieldLabel: 'Hledat',
  caseSensitiveLabel: 'Rozlišovat velikost písmen',
  wholeWordLabel: 'Celá slova',
  regexLabel: 'Regulární výraz (RE2)',
  regexInfo: 'RE2 nepodporuje zpětné reference ani lookaround. Náhrada je doslovný text.',
  regexInvalid: 'Neplatný nebo nepodporovaný regulární výraz.',
  resultsStatus: (current, total) => `${current} / ${total}`,
  noResults: 'Žádné výsledky',
  previous: 'Předchozí',
  next: 'Další',
  replaceWithLabel: 'Nahradit za',
  // The Czech plural (as Atlas's settings/plural.ts), which also agrees the verb: 1 výskyt, 2–4 výskyty, 5 výskytů.
  replacedStatus: count => count === 1 ? 'Nahrazen 1 výskyt' : count >= 2 && count <= 4 ? `Nahrazeny ${count} výskyty` : `Nahrazeno ${count.toLocaleString('cs-CZ')} výskytů`,
} satisfies Omit<MarkdownStrings['editorTools'], EditorToolsEntryKey>)
