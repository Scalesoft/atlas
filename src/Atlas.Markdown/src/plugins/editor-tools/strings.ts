import { registerMarkdownStrings, type MarkdownStrings } from '../../core'

/** Default (Czech) text of the `editorTools` section that the plugin entry renders. */
const entry = {
  search: 'Hledat a nahradit',
  invisibleCharacters: 'Zobrazit neviditelné znaky',
  replace: 'Nahradit',
  replaceAll: 'Nahradit vše',
  codeLanguagePlaintext: 'Prostý text',
  codeLanguageCsharp: 'C#',
  codeLanguageSql: 'SQL',
  codeLanguageJson: 'JSON',
  codeLanguageXml: 'XML / HTML',
  codeLanguageJavascript: 'JavaScript',
  codeLanguageTypescript: 'TypeScript',
  codeLanguageBash: 'Shell',
} satisfies Partial<MarkdownStrings['editorTools']>
/** Keys registered here; ./runtimeStrings adds the rest when the lazy runtime loads. */
export type EditorToolsEntryKey = keyof typeof entry
export const editorToolsStrings: Pick<MarkdownStrings['editorTools'], EditorToolsEntryKey> = registerMarkdownStrings('editorTools', entry)
