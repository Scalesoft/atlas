/** Keys of the `editorTools` section of `MarkdownStrings`; the Czech defaults live in plugins/editor-tools/. */
export interface EditorToolsStrings {
  /** Toolbar button label and search/replace popover title. */
  search: string
  /** Toolbar button label: toggle rendering of invisible characters. */
  invisibleCharacters: string
  /** Selection toolbar label for a code block. */
  codeContext: string
  /** Code block toolbar button label and its language-picker popover title. */
  codeLanguage: string
  copyCode: string
  copyUnavailable: string
  copySuccess: string
  copyFailed: string
  searchFieldLabel: string
  caseSensitiveLabel: string
  wholeWordLabel: string
  regexLabel: string
  regexInfo: string
  regexInvalid: string
  /** "current / total" search match position, e.g. "3 / 12". */
  resultsStatus: (current: number, total: number) => string
  noResults: string
  previous: string
  next: string
  replaceWithLabel: string
  /** After „Nahradit vše“: how many matches it replaced, e.g. "Nahrazeno 5 výskytů". */
  replacedStatus: (count: number) => string
  replace: string
  replaceAll: string
  codeLanguagePlaintext: string
  codeLanguageCsharp: string
  codeLanguageSql: string
  codeLanguageJson: string
  codeLanguageXml: string
  codeLanguageJavascript: string
  codeLanguageTypescript: string
  codeLanguageBash: string
}
