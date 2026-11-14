// The one list of Monaco features this package ships. Everything here is loaded lazily with the source
// editor chunk (`src/source/monaco/editor.ts`); nothing may import this module statically from a public
// entry (see tests/contracts/architecture.mjs and tests/package/bundle-budget.test.mjs).
//
// `editor.api` is the bare standalone editor. `editor.all` / the `monaco-editor` main entry would add
// every contribution (inline completions, code lens, color picker, sticky scroll, rename, inlay hints,
// diff breadcrumbs, …) and every language, roughly doubling the chunk. Add a contribution only when a
// source view needs it, and update the budgets in tests/package/bundle-budget.test.mjs deliberately.
//
// The chunk is shared by every source view (OpenAPI YAML/JSON, page Markdown). Language services that
// only one view needs (JSON mode + worker, monaco-yaml, the OpenAPI schema) live in that view's own lazy
// setup chunk and reach Monaco through the `setup(monaco, env)` hook of `mountSourceEditor`.
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js'

// Core cursor/selection commands and the codicon font the widgets (and the protected-block lock) draw with.
import 'monaco-editor/esm/vs/editor/browser/coreCommands.js'
import 'monaco-editor/esm/vs/base/browser/ui/codicons/codiconStyles.js'

// Editing
import 'monaco-editor/esm/vs/editor/contrib/clipboard/browser/clipboard.js'
import 'monaco-editor/esm/vs/editor/contrib/contextmenu/browser/contextmenu.js'
import 'monaco-editor/esm/vs/editor/contrib/cursorUndo/browser/cursorUndo.js'
import 'monaco-editor/esm/vs/editor/contrib/comment/browser/comment.js'
import 'monaco-editor/esm/vs/editor/contrib/indentation/browser/indentation.js'
// Alt+Up/Down moves the selected lines (a protected page block moves as a whole).
import 'monaco-editor/esm/vs/editor/contrib/linesOperations/browser/linesOperations.js'
import 'monaco-editor/esm/vs/editor/contrib/multicursor/browser/multicursor.js'
import 'monaco-editor/esm/vs/editor/contrib/wordOperations/browser/wordOperations.js'
// Explicit Format Document (Shift+Alt+F); stored source stays authoritative until the user asks.
import 'monaco-editor/esm/vs/editor/contrib/format/browser/formatActions.js'
// Ctrl+M (macOS: Ctrl+Shift+M) toggles Tab between inserting indentation and moving focus, so keyboard
// users can always leave the editor (WCAG 2.1.2, no keyboard trap).
import 'monaco-editor/esm/vs/editor/contrib/toggleTabFocusMode/browser/toggleTabFocusMode.js'

// Navigation and structure
import 'monaco-editor/esm/vs/editor/contrib/find/browser/findController.js'
import 'monaco-editor/esm/vs/editor/contrib/folding/browser/folding.js'
import 'monaco-editor/esm/vs/editor/contrib/bracketMatching/browser/bracketMatching.js'

// Assistance and diagnostics: hover (schema docs, decoration tooltips), completion, F8 marker navigation,
// inline messages (also the protected-block rejection) and the read-only notice.
import 'monaco-editor/esm/vs/editor/contrib/hover/browser/hoverContribution.js'
import 'monaco-editor/esm/vs/editor/contrib/suggest/browser/suggestController.js'
import 'monaco-editor/esm/vs/editor/contrib/gotoError/browser/gotoError.js'
import 'monaco-editor/esm/vs/editor/contrib/message/browser/messageController.js'
import 'monaco-editor/esm/vs/editor/contrib/readOnlyMessage/browser/contribution.js'

// Languages. Tokenizers are registered eagerly inside this chunk, not through the `*.contribution.js`
// lazy loaders, so opening an editor never waits for a second chunk after Monaco arrives.
import * as markdownLanguage from 'monaco-editor/esm/vs/basic-languages/markdown/markdown.js'
import * as yamlLanguage from 'monaco-editor/esm/vs/basic-languages/yaml/yaml.js'

const languages: { id: string; extensions: string[]; aliases: string[]; mimetypes?: string[]; grammar: typeof yamlLanguage }[] = [
  { id: 'yaml', extensions: ['.yaml', '.yml'], aliases: ['YAML', 'yaml', 'YML', 'yml'], mimetypes: ['application/x-yaml', 'text/x-yaml'], grammar: yamlLanguage },
  { id: 'markdown', extensions: ['.md', '.markdown', '.mdown', '.mkdn', '.mkd', '.mdwn', '.mdtxt', '.mdtext'], aliases: ['Markdown', 'markdown'], grammar: markdownLanguage },
]
for (const { grammar, ...language } of languages) {
  monaco.languages.register(language)
  monaco.languages.setMonarchTokensProvider(language.id, grammar.language)
  monaco.languages.setLanguageConfiguration(language.id, grammar.conf)
}

export { monaco }
