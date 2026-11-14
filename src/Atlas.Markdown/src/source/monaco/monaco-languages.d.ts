// Monaco ships declarations for its `*.contribution.js` loaders, not for the Monarch grammars that
// slim.ts registers directly.
declare module 'monaco-editor/esm/vs/basic-languages/*' {
  import type { languages } from 'monaco-editor/esm/vs/editor/editor.api.js'
  export const conf: languages.LanguageConfiguration
  export const language: languages.IMonarchLanguage
}

// Monaco's own platform detection: the same answer its `CtrlCmd` keybindings are resolved with.
declare module 'monaco-editor/esm/vs/base/common/platform.js' {
  export const isMacintosh: boolean
}
