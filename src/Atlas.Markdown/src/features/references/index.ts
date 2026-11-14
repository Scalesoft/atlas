import { Extension } from '@tiptap/core'
import type { Fragment, Node, Slice } from '@tiptap/pm/model'
import { NodeSelection, Plugin } from '@tiptap/pm/state'
import type { EditorView } from '@tiptap/pm/view'
import type { MarkdownReferenceProvider } from '../../core/plugins/types'
import { dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'
import { referencePaste, referenceValue } from './paste'
import type { ReferenceRuntime } from './runtime'

type LoadRuntime = () => Promise<typeof import('./runtime')>

/** Core-only factory. The loader seam keeps lazy lifecycle races deterministic in tests. */
export function createMarkdownReferenceExtension(providers: readonly MarkdownReferenceProvider[], loadRuntime: LoadRuntime = () => import('./runtime')) {
  return Extension.create({
    name: 'markdownReferences',
    priority: 1100,
    // The link field searches the providers' targets by name (FormattingToolbar).
    addStorage: () => ({ providers }),
    addProseMirrorPlugins() {
      const editor = this.editor
      let runtime: ReferenceRuntime | undefined, loading: Promise<ReferenceRuntime | undefined> | undefined
      let view: EditorView | undefined, alive = false, generation = 0, offerEpoch = 0, shift = false, editable = editor.isEditable
      const ready = () => alive && !editor.isDestroyed && editor.isEditable && Boolean(view?.dom.isConnected)
      const accepts = (node: Node) => providers.some(provider => provider.nodeType === node.type.name && referenceValue(provider, node))
      const selected = () => {
        const selection = editor.state.selection
        return selection instanceof NodeSelection && accepts(selection.node)
      }
      const ensureRuntime = (): Promise<ReferenceRuntime | undefined> => {
        if (loading) return loading
        const epoch = generation
        try {
          const pending = loadRuntime().then(module => {
            if (epoch !== generation || !ready()) return undefined
            runtime = new module.ReferenceRuntime(editor, providers)
            runtime.update()
            return runtime
          }).catch(() => undefined).finally(() => { if (loading === pending) loading = undefined })
          loading = pending
          return pending
        } catch { return Promise.resolve(undefined) }
      }
      const paste = (event: ClipboardEvent) => {
        const paste = referencePaste(editor, providers, event, shift)
        if (!paste || !ready()) return false
        const from = editor.state.selection.from, to = from + paste.url.length
        dispatchMarkdownHistoryOperation(editor, editor.state.tr.replaceSelectionWith(editor.schema.text(paste.url, paste.marks), false).setStoredMarks(null))
        const { doc, selection } = editor.state, epoch = generation, request = offerEpoch
        if (runtime) runtime.offerPaste(from, to, paste.url, paste.choices)
        else void ensureRuntime().then(current => {
          if (current && epoch === generation && request === offerEpoch && ready() && editor.state.doc === doc && editor.state.selection.eq(selection)) current.offerPaste(from, to, paste.url, paste.choices)
        })
        event.preventDefault()
        return true
      }
      // A chip is in the document (at the view's start, or written in later: a collaborative page's content arrives after the view):
      // the small reference UI then preloads once, as soon as the editor is editable, so a selected chip's toolbar comes without waiting (P17).
      // Only inserted content is walked, so typing in a long document stays cheap.
      let preload = false
      const sees = (content: Fragment) => { if (!preload) content.descendants(node => { preload ||= node.isAtom && accepts(node); return !preload }) }
      const key = (event: KeyboardEvent) => { shift = event.shiftKey && Boolean(event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'v'; return false }
      return [new Plugin({
        state: { init: () => null, apply: (transaction, _value, state) => {
          if (transaction.docChanged || transaction.selectionSet && !transaction.selection.eq(state.selection)) offerEpoch++
          if (!runtime && transaction.docChanged) transaction.steps.forEach(step => { const { slice } = step as { slice?: Slice }; if (slice) sees(slice.content) })
          runtime?.map(transaction); return null
        } },
        props: {
          handlePaste: (_view, event) => paste(event),
          handleDOMEvents: { keydown: (_view, event) => key(event), keyup: (_view, event) => key(event), blur: () => { shift = false; return false } },
        },
        view: current => {
          view = current; alive = true; generation++; editable = editor.isEditable
          const update = () => {
            if (editable !== editor.isEditable) { editable = editor.isEditable; offerEpoch++ }
            if (runtime) runtime.update()
            else if (ready() && preload) { preload = false; void ensureRuntime() }
            else if (ready() && selected()) void ensureRuntime()
          }
          sees(current.state.doc.content)
          update()
          return { update, destroy: () => {
            alive = false; generation++; offerEpoch++; shift = false; loading = undefined
            runtime?.destroy(); runtime = undefined; view = undefined
          } }
        },
      })]
    },
  })
}
