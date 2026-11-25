import { Extension, type Editor, type JSONContent } from '@tiptap/core'
import type { Node as PMNode } from '@tiptap/pm/model'
import { Plugin, PluginKey, TextSelection, type EditorState, type Transaction } from '@tiptap/pm/state'
import { StepMap } from '@tiptap/pm/transform'
import { DecorationSet } from '@tiptap/pm/view'
import { rememberMarkdownRevertedText } from '../../features/chips/chipText'
import { markdownChangeMapping } from '../changeMapping'
import { openMarkdownIntentions, registerMarkdownIntentions } from '../../features/intentions/intentions'
import { createMarkdownModuleLoader, whenMarkdownIdle } from '../../features/modules/lazy'
import { typedTrigger } from './gate'
import type { MarkdownRecognitionEntry, MarkdownRecognitionHit } from './recognize'
import type { MarkdownLexicon, MarkdownLexiconSource } from './lexicon'
import type { MarkdownRecognitionEngine } from './engine'

/*
 * The linking engine's runtime in the editor (audit-engine §4), one per editor, for every kind of entity a plugin recognizes
 * (`registerMarkdownRecognizer`): people, issues, pages.
 * - Here, eager: the gate of a keystroke, the state and the registry; the index, the decisions, the underlines and the chips are the
 *   engine (engine.ts), loaded while the editor idles after the first render (P17); a keystroke before then is decided when it lands.
 * - Typing: the gate and the window of one keystroke (gate.ts), the pure decision (recognize.ts); `chip` and `unresolved` are written
 *   once the keystroke has landed, as a root transaction of their own (`queueMicrotask` → the text checked again →
 *   `dispatchMarkdownHistoryOperation`, never inside `appendTransaction`), so Backspace right after and Ctrl+Z take back only the chip;
 *   `pending` (an issue key) is asked of its recognizer with an AbortSignal, its range mapped through every change, and becomes a chip
 *   when the answer comes in time with the text unchanged; a late answer is only offered.
 * - Written text: the known names a while after the last change get a dotted underline (`suggest`, P15 „viditelné mezery“), only in
 *   the blocks that changed; a click offers them (the kind's list, else the actions menu), the actions menu converts them.
 * - Text turned back on purpose (chipText) is neither converted nor underlined again until it is edited.
 * - „Nenabízet“ on an underlined title or key (a kind with its own actions menu row) stops offering that text of that kind in this editor
 *   until it closes; the memory is the editor's, never the document's (R1).
 */

export interface MarkdownRecognizer {
  /** The entity kind (`person`, `issue`, `page`): its entries and hits. */
  readonly kind: string
  /** Several candidates of a whole name make one unresolved chip waiting for the pick (people); of other kinds they are only offered. */
  readonly unresolved?: boolean
  /** The entries of this kind, read again when the lexicon, a node of the document (not its text) or the recognizer changed (refreshMarkdownRecognition, an answer of `chip`). */
  readonly entries: (lexicon: MarkdownLexicon | undefined, doc: PMNode) => readonly MarkdownRecognitionEntry[]
  /** The chip of a hit (positions are the document's); a promise for what only the network knows (`pending`); null leaves the text. */
  readonly chip: (hit: MarkdownRecognitionHit, context: { readonly signal: AbortSignal }) => JSONContent | null | Promise<JSONContent | null>
  /** The actions menu row that converts an underlined hit; none: the kind offers its own rows. */
  readonly label?: (hit: MarkdownRecognitionHit) => string
  readonly icon?: string
  /** A click on its underline (true when it opened something); default: the actions menu there. */
  readonly offer?: (editor: Editor, hit: MarkdownRecognitionHit) => boolean
}

/** Underlines per document at most. */
export const RECOGNITION_UNDERLINE_LIMIT = 200
/** A pending key answered later than this (or with the caret elsewhere) is only offered: nothing jumps under the person typing on. */
export const RECOGNITION_LATE_MS = 1500
const IDLE_MS = 400

interface Range { from: number; to: number }
export interface Pending extends Range { id: number; text: string; at: number; auto: boolean; abort: AbortController }
interface State { underlines: DecorationSet; pending: readonly Pending[] }
export type RecognitionMeta = { underlines?: DecorationSet; pending?: Pending; settled?: number; dismiss?: Range }
export const recognitionKey = new PluginKey<State>('markdownRecognition')
/** A keystroke the gate let through (typedTrigger), with the document it landed in and when. */
export type TypedKeystroke = NonNullable<ReturnType<typeof typedTrigger>> & { readonly doc: PMNode; readonly at: number }

// The index, the decisions, the underlines and the chips (engine.ts) load while the editor idles after the first render (P17); the
// gate of a keystroke stays here, so one typed before they are in is decided when they land (catchUp), not lost.
const loadEngine = createMarkdownModuleLoader(() => import('./engine'))

const registered = new WeakMap<Editor, Map<string, MarkdownRecognizer>>()
const refreshers = new WeakMap<Editor, () => void>()
const matchers = new WeakMap<Editor, (text: string) => MarkdownRecognitionHit[]>()
/** Adds the recognizer of a kind to an editor; the returned function removes it. */
export function registerMarkdownRecognizer(editor: Editor, recognizer: MarkdownRecognizer): () => void {
  let kinds = registered.get(editor)
  if (!kinds) registered.set(editor, kinds = new Map())
  kinds.set(recognizer.kind, recognizer)
  refreshMarkdownRecognition(editor)
  return () => { if (kinds!.get(recognizer.kind) === recognizer) { kinds!.delete(recognizer.kind); refreshMarkdownRecognition(editor) } }
}
/** A recognizer's entries changed (its host answered): read them again soon and underline anew. */
export const refreshMarkdownRecognition = (editor: Editor): void => refreshers.get(editor)?.()

/** The underlined hit at `pos` (document positions), of `kind` when given. */
export function markdownRecognitionAt(state: EditorState, pos: number, kind?: string): MarkdownRecognitionHit | undefined {
  const found = recognitionKey.getState(state)?.underlines.find(pos, pos).find(decoration => decoration.from <= pos && pos <= decoration.to
    && (!kind || (decoration.spec as { hit: MarkdownRecognitionHit }).hit.kind === kind))
  return found && { ...(found.spec as { hit: MarkdownRecognitionHit }).hit, from: found.from, to: found.to }
}
/**
 * The candidates of `text` read as one whole name of `kind` (an unresolved chip's people, a selected name), from the index as it is now
 * (read again first when an entry changed); none before the engine has loaded.
 */
export function markdownRecognitionCandidates(editor: Editor, text: string, kind: string): readonly unknown[] {
  const name = text.replace(/\s+/gu, ' ').trim()
  return matchers.get(editor)?.(name).find(hit => hit.kind === kind && hit.from === 0 && hit.to === name.length)?.candidates ?? []
}
/** „Není to on“: the hit's text stays plain, neither underlined nor converted again until it is edited. */
export function dismissMarkdownRecognition(editor: Editor, range: Range): void {
  if (editor.isDestroyed) return
  const tr = rememberMarkdownRevertedText(editor.state.tr, range.from, range.to).setMeta(recognitionKey, { dismiss: range } satisfies RecognitionMeta).setMeta('addToHistory', false)
  // The caret a click on the underline left inside the phrase goes back to its end, where the typing goes on.
  const caret = tr.selection.from, to = tr.mapping.map(range.to)
  if (tr.selection.empty && caret > tr.mapping.map(range.from) && caret < to) tr.setSelection(TextSelection.create(tr.doc, to))
  editor.view.dispatch(tr)
}

/**
 * Where `next` differs from `before`: one range in `next` and the map of positions across it. Read per view update, not per
 * transaction: the scan forgets what it read without a transaction of its own (one per scan redrew the whole view, 4.0).
 */
function changed(before: PMNode, next: PMNode): { range: Range; map: StepMap } | undefined {
  const start = before.content.findDiffStart(next.content)
  if (start === null) return undefined
  let { a, b } = before.content.findDiffEnd(next.content)!
  // A repeated letter makes the ends overlap the start (as in textOnlyChange).
  if (a < start) { b += start - a; a = start }
  if (b < start) { a += start - b; b = start }
  return { range: { from: start, to: b }, map: new StepMap([start, a - start, b - start]) }
}

export interface MarkdownRecognitionOptions { lexicon?: MarkdownLexiconSource }

export const MarkdownRecognition = Extension.create<MarkdownRecognitionOptions>({
  name: 'markdownRecognition',
  addOptions: () => ({ lexicon: undefined }),
  addProseMirrorPlugins() {
    const editor = this.editor, source = this.options.lexicon
    const recognizers = () => registered.get(editor) ?? new Map<string, MarkdownRecognizer>()
    let engine: MarkdownRecognitionEngine | undefined, timer: ReturnType<typeof setTimeout> | undefined
    // The blocks changed since the last scan (its own record, not the state's: forgetting them needs no transaction).
    let dirty: Range[] = []
    const missed: TypedKeystroke[] = []
    // Once per editor: the engine, the keystrokes typed before it came decided, then every block underlined.
    const start = () => void loadEngine().then(module => {
      if (engine || editor.isDestroyed) return
      engine = module.createRecognitionEngine(editor, source, recognizers, refresh)
      engine.catchUp(missed.splice(0))
      engine.scan(dirty.splice(0))
    }, () => { missed.length = 0 })
    // Before the engine is in, nothing to scan: its first scan reads every block.
    const scan = () => { timer = undefined; if (!editor.isDestroyed && engine) engine.scan(dirty.splice(0)) }
    const schedule = (delay = IDLE_MS) => { if (timer) clearTimeout(timer); timer = setTimeout(scan, delay) }
    const refresh = () => { engine?.reread(); schedule(0) }

    return [new Plugin<State>({
      key: recognitionKey,
      state: {
        init: () => ({ underlines: DecorationSet.empty, pending: [] }),
        apply(tr, value) {
          const meta = tr.getMeta(recognitionKey) as RecognitionMeta | undefined
          let { underlines, pending } = value
          if (tr.docChanged) {
            const mapping = markdownChangeMapping(tr)
            underlines = underlines.map(mapping, tr.doc)
            pending = pending.flatMap(entry => {
              const from = mapping.mapResult(entry.from, 1), to = mapping.mapResult(entry.to, -1)
              if (!from.deleted && !to.deleted && from.pos < to.pos) return [{ ...entry, from: from.pos, to: to.pos }]
              entry.abort.abort()
              return []
            })
          }
          if (meta?.underlines) underlines = meta.underlines
          if (meta?.dismiss) underlines = underlines.remove(underlines.find(meta.dismiss.from, meta.dismiss.to))
          if (meta?.pending) pending = [...pending, meta.pending]
          if (meta?.settled !== undefined) pending = pending.filter(entry => entry.id !== meta.settled)
          return { underlines, pending }
        },
      },
      appendTransaction(transactions, old, state) {
        // The one check per keystroke: the gate here, the decision in the engine (a keystroke before it loads waits for it, first use).
        if (!editor.isEditable || !recognizers().size) return null
        const typed = typedTrigger(transactions, old, state)
        if (!typed) return null
        const keystroke = { ...typed, doc: state.doc, at: Date.now() }
        if (engine) engine.typed(keystroke)
        else {
          // Only the ones still young enough to be decided (catchUp), so a failed download holds no old documents.
          while (missed.length && keystroke.at - missed[0]!.at > RECOGNITION_LATE_MS) missed.shift()
          missed.push(keystroke); start()
        }
        return null
      },
      props: {
        decorations: state => recognitionKey.getState(state)!.underlines,
        handleClick(view, pos, event) {
          if (!(event.target as Element | null)?.closest?.('.md-recognition-candidate')) return false
          const hit = markdownRecognitionAt(view.state, pos)
          const recognizer = hit && recognizers().get(hit.kind)
          // After the click has put the caret: the list then stays open. The caret is set here: the browser's own one reaches the
          // state only with selectionchange, later, and the actions menu would be built for the old caret.
          if (hit && recognizer) queueMicrotask(() => { if (!recognizer.offer?.(editor, hit)) { editor.commands.setTextSelection(pos); openMarkdownIntentions(editor) } })
          return false
        },
      },
      view() {
        refreshers.set(editor, refresh)
        matchers.set(editor, text => engine?.match(text) ?? [])
        const unsubscribe = source?.subscribe?.(refresh), unregister = registerMarkdownIntentions(editor, context => engine?.rows(context) ?? [])
        let editable = editor.isEditable
        if (engine) refresh()
        else whenMarkdownIdle(start)
        return {
          update(view, previous) {
            const change = view.state.doc !== previous.doc ? changed(previous.doc, view.state.doc) : undefined
            // Read-only underlines nothing (editable again: every block anew), so nothing to remember for a scan that would clear it.
            if (change) dirty = editor.isEditable ? [...dirty.map(range => ({ from: change.map.map(range.from, -1), to: change.map.map(range.to, 1) })), change.range] : []
            // Read-only shows no underline, editable again all of them: every block anew.
            if (editor.isEditable !== editable) { editable = editor.isEditable; engine?.invalidate(); schedule() } else if (view.state.doc !== previous.doc) schedule()
          },
          destroy() {
            if (timer) clearTimeout(timer)
            unsubscribe?.(); unregister()
            if (refreshers.get(editor) === refresh) { refreshers.delete(editor); matchers.delete(editor) }
            recognitionKey.getState(editor.state)?.pending.forEach(entry => entry.abort.abort())
          },
        }
      },
    })]
  },
})
