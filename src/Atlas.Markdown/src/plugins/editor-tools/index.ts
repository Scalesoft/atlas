import { Extension, type Command, type Editor } from '@tiptap/core'
import { Plugin } from '@tiptap/pm/state'
import FindAndReplace, { FindAndReplacePluginKey, createSearchRegex, type SearchOptions } from '@tiptap/extension-find-and-replace'
import { HardBreakNode, InvisibleCharacter, ParagraphNode, SpaceCharacter } from '@tiptap/extension-invisible-characters'
import { createMarkdownStubbedPopover, defineMarkdownCommand, closeMarkdownHistory, isMarkdownComposition, type MarkdownPlugin } from '../../core'
import { editorToolsStrings } from './strings'
import { LazyInvisibleCharacters } from './invisibleCharacters'

export type MarkdownSearchOptions = Partial<SearchOptions>
export function editorSearchState(editor: Editor) {
  const state = FindAndReplacePluginKey.getState(editor.state)
  if (!state) throw new Error('Install createEditorToolsPlugin before using search.')
  return state
}
/** Upstream RE2 mode is linear-time: lookarounds and backreferences are unsupported. */
export function findInEditor(editor: Editor, term: string, options: MarkdownSearchOptions = {}): boolean {
  if (editor.isDestroyed) return false
  const settings = { caseSensitive: false, wholeWord: false, useRegex: false, ...options }
  const valid = !term || !!createSearchRegex(term, settings)
  editor.view.dispatch(editor.state.tr.setMeta(FindAndReplacePluginKey, { searchTerm: valid ? term : '', ...settings }).setMeta('addToHistory', false))
  return valid
}
/** Preserve the upstream match/replacement implementation and isolate both history boundaries. */
export function replaceInEditor(editor: Editor, replacement: string, all = false): boolean {
  if (editor.isDestroyed || !editor.isEditable || !editorSearchState(editor).results.length) return false
  editor.commands.setReplaceTerm(replacement)
  return all ? editor.commands.replaceAll() : editor.commands.replace()
}

const replacementHistory = 'markdownSearchReplacement'
const SafeFindAndReplace = FindAndReplace.extend({
  addCommands() {
    const upstream = this.parent!()
    const isolated = (command: Command): Command => props => {
      if (!props.editor.isEditable) return false
      const result = command(props)
      if (result && props.dispatch && props.tr.docChanged) closeMarkdownHistory(props.tr).setMeta(replacementHistory, true)
      return result
    }
    return { ...upstream,
      replace: () => isolated(upstream.replace!()),
      replaceAll: () => isolated(upstream.replaceAll!()),
    }
  },
  onTransaction(event) {
    this.parent?.(event)
    if (event.transaction.getMeta(replacementHistory) && !this.editor.isDestroyed) {
      this.editor.view.dispatch(closeMarkdownHistory(this.editor.state.tr).setMeta('addToHistory', false))
    }
  },
})

/** The panel opens at once as a stub; Vue and the panel load behind its entry (stubbedPopover.ts), once per page. */
const searchPopover = createMarkdownStubbedPopover(() => import('./searchPanel'))
export async function openEditorSearch(editor: Editor, anchor?: HTMLElement): Promise<void> {
  if (editor.isDestroyed || (anchor && !anchor.isConnected)) return
  await searchPopover.open({ editor, anchor: anchor ?? editor.view.dom, title: editorToolsStrings.search, readonly: true, trackSelection: false,
    interactionRoots: [editor.view.dom], className: 'md-search-panel', rows: 2, rowHeight: 34,
    render: (panel, container, close) => panel.searchRenderer(editor)(container, close) }).ready
}
export const showEditorSearch = defineMarkdownCommand({ id: 'editor-tools.search', apply: () => ({ editor, dispatch }) => {
  if (dispatch) void openEditorSearch(editor)
  return true
} })
export const toggleEditorInvisibleCharacters = defineMarkdownCommand({ id: 'editor-tools.invisible',
  active: editor => editor.storage.invisibleCharacters.visibility(),
  apply: () => ({ commands }) => commands.toggleInvisibleCharacters(),
})

export interface EditorToolsOptions { findAndReplace?: boolean; invisibleCharacters?: boolean }
export function createEditorToolsPlugin(options: EditorToolsOptions = {}): MarkdownPlugin {
  return { id: 'editor-tools', apiVersion: 1, profiles: ['markdown-safe', 'rich'], create() {
    const find = options.findAndReplace !== false; if (find) searchPopover.prefetchWhenIdle()
    const invisible = options.invisibleCharacters !== false
    return {
      extensions: [
        ...(find ? [SafeFindAndReplace.configure({ injectCSS: false, searchDebounceMs: 0 }), Extension.create({
          name: 'markdownSearchShortcut', addKeyboardShortcuts() { return { 'Mod-f': () => { void openEditorSearch(this.editor); return true } } },
          addProseMirrorPlugins() {
            const editor = this.editor
            return [new Plugin({ props: { attributes: { tabindex: '0' } }, view(view) {
              // ProseMirror's editable keydown handler is skipped in readonly.
              // Keep this listener on the owning view, never on the application document.
              const keydown = (event: KeyboardEvent) => {
                if (!editor.isEditable && !event.defaultPrevented && !isMarkdownComposition(event, editor) && !event.altKey && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'f') {
                  event.preventDefault(); void openEditorSearch(editor)
                }
              }
              view.dom.addEventListener('keydown', keydown)
              return { destroy() { view.dom.removeEventListener('keydown', keydown) } }
            } })]
          },
        })] : []),
        ...(invisible ? [LazyInvisibleCharacters.configure({ visible: false, injectCSS: false,
          builders: [new SpaceCharacter(), new ParagraphNode(), new HardBreakNode(),
            new InvisibleCharacter({ type: 'nbsp', predicate: character => character === '\u00a0' })],
        })] : []),
      ],
      commands: [...(find ? [showEditorSearch] : []), ...(invisible ? [toggleEditorInvisibleCharacters] : [])],
      actions: { toolbar: [
        ...(find ? [{ id: 'editor-tools.search', label: editorToolsStrings.search, icon: 'svg:search', command: showEditorSearch() }] : []),
        ...(invisible ? [{ id: 'editor-tools.invisible', label: editorToolsStrings.invisibleCharacters, icon: 'svg:pilcrow', command: toggleEditorInvisibleCharacters() }] : []),
      ] },
    }
  } }
}
export { createCodeBlockHighlighting, createStaticCodeHighlighting, markdownCodeLanguages, setCodeBlockLanguage } from './code'
export type { CodeBlockHighlightingOptions, MarkdownCodeLanguage } from './code'
