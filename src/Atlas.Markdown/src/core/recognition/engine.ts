import type { Editor, JSONContent } from '@tiptap/core'
import type { Node as PMNode } from '@tiptap/pm/model'
import { Mapping } from '@tiptap/pm/transform'
import { Decoration, DecorationSet } from '@tiptap/pm/view'
import { dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'
import { isMarkdownRevertedText, markMarkdownAutoConversion } from '../../features/chips/chipText'
import type { MarkdownIntention } from '../../features/intentions/intentions'
import { intentionsStrings } from '../../ui/strings/intentions'
import { isProseBlock, proseText, RECOGNITION_ENDS } from './gate'
import { foldMarkdownText } from './text'
import { buildRecognitionIndex, recognizeTail, recognizeText, type MarkdownRecognitionHit, type MarkdownRecognitionIndex } from './recognize'
import type { MarkdownLexicon, MarkdownLexiconSource } from './lexicon'
import { markdownRecognitionAt, recognitionKey, RECOGNITION_LATE_MS, RECOGNITION_UNDERLINE_LIMIT, type MarkdownRecognizer, type Pending, type RecognitionMeta, type TypedKeystroke } from './runtime'

/*
 * The lazy part of the linking runtime (runtime.ts, P17): the index, the decision of a keystroke, the underlines, the chips written and
 * the actions menu rows. It loads while the editor idles after the first render; a keystroke before then is decided when it lands.
 */

interface Range { from: number; to: number }

export interface MarkdownRecognitionEngine {
  /** A keystroke the gate let through: its chip once it has landed. */
  typed(keystroke: TypedKeystroke): void
  /** The keystrokes typed before the engine loaded, oldest first: decided now, those older than RECOGNITION_LATE_MS only underlined. */
  catchUp(missed: readonly TypedKeystroke[]): void
  /** Underlines the blocks in `dirty` (every block after `invalidate`, a new index or the first scan); a transaction only when they changed. */
  scan(dirty: readonly Range[]): void
  rows(context: { empty: boolean }): MarkdownIntention[]
  match(text: string): MarkdownRecognitionHit[]
  /** Every block anew at the next scan (editable again: all of them, read-only: none). */
  invalidate(): void
  /** The entries again at the next scan: the lexicon or a recognizer's entries changed (refreshMarkdownRecognition). */
  reread(): void
}

/** No node in `from`–`to` but text and the blocks that hold the whole range. */
function onlyText(doc: PMNode, from: number, to: number): boolean {
  let only = true
  doc.nodesBetween(from, to, (node, pos) => {
    if (!only || node.isText) return false
    if (pos < from && pos + node.nodeSize > to) return true
    only = false
    return false
  })
  return only
}
/** `next` differs from `before` at most in text: an edit of the words, which no recognizer's entries read (they read nodes, MarkdownRecognizer). */
export function textOnlyChange(before: PMNode, next: PMNode): boolean {
  if (before === next) return true
  const start = before.content.findDiffStart(next.content)
  if (start === null) return true
  let { a: endA, b: endB } = before.content.findDiffEnd(next.content)!
  // A repeated letter makes the ends overlap the start (ProseMirror's own DOM diff does the same).
  if (endA < start) { endB += start - endA; endA = start }
  if (endB < start) { endA += start - endB; endB = start }
  return onlyText(before, start, endA) && onlyText(next, start, endB)
}

const underlineKey = (decoration: Decoration) => `${decoration.from} ${decoration.to} ${(decoration.spec as { hit: MarkdownRecognitionHit }).hit.kind} ${(decoration.spec as { hit: MarkdownRecognitionHit }).hit.text}`
/** The same underlines: the same ranges of the same kind and text. */
function sameUnderlines(old: readonly Decoration[], added: readonly Decoration[]): boolean {
  if (old.length !== added.length) return false
  const keys = new Set(old.map(underlineKey))
  return added.every(decoration => keys.has(underlineKey(decoration)))
}

export function createRecognitionEngine(editor: Editor, source: MarkdownLexiconSource | undefined, recognizers: () => ReadonlyMap<string, MarkdownRecognizer>, refresh: () => void): MarkdownRecognitionEngine {
  // `rescan`: the index changed since the last scan, so every block is underlined anew.
  let lexicon = source?.read(), index: MarkdownRecognitionIndex | undefined, signature = '', rescan = false, ids = 0, kinds = ''
  // What the index was built from; `stale`: a recognizer answered or asked to be read again since (its entries may have changed).
  let built: { lexicon: MarkdownLexicon | undefined; recognizers: MarkdownRecognizer[]; doc: PMNode } | undefined, stale = true
  const muted = new Set<string>(), mutedKey = (hit: { kind: string; text: string }) => `${hit.kind}
${foldMarkdownText(hit.text)}`
  const set = (meta: RecognitionMeta) => { if (!editor.isDestroyed) editor.view.dispatch(editor.state.tr.setMeta(recognitionKey, meta).setMeta('addToHistory', false)) }

  /**
   * The index again when an entry changed; true when it did. The entries are read only when their inputs changed: the lexicon, the
   * recognizers, an answer or a refresh, or a node of the document other than text (typing changes none: 15–25 ms a key at 4× CPU).
   */
  function rebuild(doc: PMNode): boolean {
    const current = source?.read(), list = [...recognizers().values()]
    if (index && built && !stale && current === built.lexicon && list.length === built.recognizers.length && list.every((recognizer, at) => recognizer === built!.recognizers[at])
      && textOnlyChange(built.doc, doc)) { built.doc = doc; return false }
    lexicon = current
    built = { lexicon, recognizers: list, doc }; stale = false
    kinds = list.map(recognizer => recognizer.kind).join(' ')
    const entries = list.flatMap(recognizer => { try { return recognizer.entries(lexicon, doc) } catch { return [] } })
    const next = `${lexicon?.version ?? ''}\n${kinds}\n${entries.map(entry => `${entry.kind}\t${entry.id}\t${entry.form}\t${entry.auto}\t${entry.name}`).join('\n')}`
    if (index && next === signature) return false
    signature = next
    rescan = true
    index = buildRecognitionIndex(entries, new Set(list.filter(recognizer => recognizer.unresolved).map(recognizer => recognizer.kind)))
    return true
  }

  /** Writes a chip in place of `from`–`to` if the text is still `text`; `trigger`: the end of what Backspace right after takes back. */
  function write(content: JSONContent, range: Range, text: string, trigger?: number): Mapping | undefined {
    const state = editor.state
    if (!editor.isEditable || state.doc.textBetween(range.from, range.to, ' ', '￼') !== text) return
    let node: PMNode
    try { node = state.schema.nodeFromJSON(content); node.check() } catch { return }
    const tr = state.tr.replaceWith(range.from, range.to, node)
    // An automatic conversion is an undo step of its own that Backspace right after takes back (chipText); a picked one is just a step.
    dispatchMarkdownHistoryOperation(editor, trigger === undefined ? tr : markMarkdownAutoConversion(tr, { from: range.from, to: tr.mapping.map(trigger), text }))
    return tr.mapping
  }

  /** Asks the recognizer for a pending hit or a picked underline; the range follows every change until the answer. */
  function resolve(entry: Pending, hit: MarkdownRecognitionHit, recognizer: MarkdownRecognizer) {
    void Promise.resolve().then(() => recognizer.chip(hit, { signal: entry.abort.signal })).then(content => {
      stale = true
      if (editor.isDestroyed) return
      const now = recognitionKey.getState(editor.state)!.pending.find(pending => pending.id === entry.id)
      set({ settled: entry.id })
      if (!now || !content || entry.abort.signal.aborted || isMarkdownRevertedText(editor.state, now.from, now.to)) return
      if (!now.auto) { write(content, now, now.text); editor.view.focus(); return }
      const { selection, doc } = editor.state
      if (Date.now() - now.at > RECOGNITION_LATE_MS || selection.$from.parent !== doc.resolve(now.from).parent) { refresh(); return }
      write(content, now, now.text, RECOGNITION_ENDS.test(doc.textBetween(now.to, Math.min(now.to + 1, doc.content.size))) ? now.to + 1 : now.to)
    }, () => { if (!editor.isDestroyed) set({ settled: entry.id }) })
  }

  /** An underline picked (its actions menu row): converted when its recognizer answers, as a step of its own. */
  function pick(hit: MarkdownRecognitionHit, recognizer: MarkdownRecognizer) {
    const entry: Pending = { id: ++ids, from: hit.from, to: hit.to, text: hit.text, at: Date.now(), auto: false, abort: new AbortController() }
    set({ pending: entry })
    resolve(entry, hit, recognizer)
  }

  /**
   * The chip of a keystroke, written once it has landed (as its own root transaction: a history boundary an appended transaction would
   * carry never reaches collaborative undo, which would then take the typing back with the chip). `mapping`: the changes since a missed
   * keystroke; with it the caret may have moved on in the meantime, and Backspace takes the chip back only while it has not.
   */
  function land({ start, caret, text, doc, at: typedAt }: TypedKeystroke, mapping?: Mapping): Mapping | undefined {
    const hit = recognizeTail(text, index, lexicon)
    const recognizer = hit && recognizers().get(hit.kind)
    if (!hit || !recognizer || muted.has(mutedKey(hit)) || (hit.decision !== 'chip' && hit.decision !== 'unresolved' && hit.decision !== 'pending')) return
    const written = doc.textBetween(start + hit.from, caret)
    const from = mapping?.map(start + hit.from, 1) ?? start + hit.from, to = mapping?.map(start + hit.to, -1) ?? start + hit.to, end = mapping?.map(caret) ?? caret
    if (editor.isDestroyed || !editor.isEditable) return
    const now = editor.state, still = now.selection.empty && now.selection.from === end
    if ((!mapping && !still) || now.doc.textBetween(from, end) !== written || isMarkdownRevertedText(now, from, to)) return
    const found = { ...hit, from, to }
    if (hit.decision === 'pending') {
      const entry: Pending = { id: ++ids, from, to, text: hit.text, at: typedAt, auto: true, abort: new AbortController() }
      set({ pending: entry })
      resolve(entry, found, recognizer)
      return
    }
    const content = recognizer.chip(found, { signal: new AbortController().signal })
    return content && !(content instanceof Promise) ? write(content, { from, to }, hit.text, still ? end : undefined) : undefined
  }

  return {
    // A recognizer registered since the last index (a plugin a task after the first render) is in before the keystroke is decided.
    typed: keystroke => queueMicrotask(() => { if ([...recognizers().keys()].join(' ') !== kinds) rebuild(editor.state.doc); land(keystroke) }),
    catchUp(missed) {
      if (!missed.length || editor.isDestroyed) return
      rebuild(editor.state.doc)
      // Oldest first, each through the chips written before it; the newest last, so Backspace right after takes back its chip.
      const mapping = new Mapping()
      for (const keystroke of missed) {
        if (Date.now() - keystroke.at > RECOGNITION_LATE_MS) continue
        const written = land(keystroke, mapping)
        if (written) mapping.appendMapping(written)
      }
    },
    rows({ empty }) {
      const hit = empty ? markdownRecognitionAt(editor.state, editor.state.selection.from) : undefined
      const recognizer = hit && recognizers().get(hit.kind)
      return hit && recognizer?.label ? [{ id: `recognition.${hit.kind}`, label: recognizer.label(hit), icon: recognizer.icon, group: 'recognition', priority: 5, run: () => pick(hit, recognizer) },
        { id: `recognition.${hit.kind}.mute`, label: intentionsStrings.notOffered, icon: 'svg:x', group: 'recognition', priority: 6, run: () => { muted.add(mutedKey(hit)); rescan = true; refresh() } }] : []
    },
    match: text => { if (rebuild(editor.state.doc)) refresh(); return index ? recognizeText(text, index) : [] },
    invalidate: () => { rescan = true },
    reread: () => { stale = true },
    scan(dirty) {
      if (editor.isDestroyed) return
      const { doc } = editor.state, state = recognitionKey.getState(editor.state)!
      rebuild(doc)
      const full = rescan
      rescan = false
      let underlines = full ? DecorationSet.empty : state.underlines, count = underlines.find().length
      const blocks = new Map<number, PMNode>()
      const visit = (node: PMNode, pos: number) => { if (node.isTextblock) { blocks.set(pos, node); return false } return true }
      if (full) doc.descendants(visit)
      else for (const range of dirty) doc.nodesBetween(Math.min(range.from, doc.content.size), Math.min(range.to, doc.content.size), visit)
      for (const [pos, block] of blocks) {
        const old = underlines.find(pos, pos + block.nodeSize)
        count -= old.length
        const added = !editor.isEditable || !index || !isProseBlock(block) || count >= RECOGNITION_UNDERLINE_LIMIT ? [] : recognizeText(proseText(block), index, RECOGNITION_UNDERLINE_LIMIT - count).flatMap(hit => {
          const from = pos + 1 + hit.from, to = pos + 1 + hit.to
          if (hit.decision === 'none' || !recognizers().has(hit.kind) || muted.has(mutedKey(hit)) || isMarkdownRevertedText(editor.state, from, to)) return []
          return [Decoration.inline(from, to, { class: 'md-recognition-candidate', 'data-recognition': hit.kind }, { hit })]
        })
        count += added.length
        // The block's underlines as they were: kept, so a scan that changes none needs no transaction.
        if (!sameUnderlines(old, added)) underlines = underlines.remove(old).add(doc, added)
      }
      // No underline changed: no transaction, so typing that links nothing never redraws the view for the scan.
      if (underlines !== state.underlines) set({ underlines })
    },
  }
}
