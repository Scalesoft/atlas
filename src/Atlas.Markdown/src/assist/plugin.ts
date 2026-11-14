import { Extension, type Editor } from '@tiptap/core'
import { Plugin, PluginKey, TextSelection, type SelectionBookmark, type Transaction } from '@tiptap/pm/state'
import { Mapping } from '@tiptap/pm/transform'
import { Decoration, DecorationSet, type EditorView } from '@tiptap/pm/view'
import { autoUpdate, computePosition, offset, shift } from '@floating-ui/dom'
import {
  closeMarkdownHistory, createIconElement, defineMarkdownCommand, openMarkdownContextPopover, type MarkdownCommand, type MarkdownDispatchOptions, type MarkdownIntention, type MarkdownPlugin, type MarkdownPluginController,
} from '../core'
import { assistDecorationsKey, assistDecorationsPlugin, assistMeta, inline, sameDecorations, widget, type AssistDecorationSpec } from './decorations'
import { AssistList } from './list'
import { AssistLayer, AssistMarker } from './marker'
import { assistMotion, collapse, finished, glow, linkIn, morph, reveal, underline, type AssistMotion } from './motion'
import { openMarkdownProposalPanel } from './proposal'
import { assistStrings } from './strings'
import { markdownAssistTargets } from './targets'
import { blockText, caretBlock, changedRange, mapRange, rangeText, textblockAt } from './text'
import type {
  AssistPluginOptions, AssistStrings, MarkdownAssistApplyContext, MarkdownAssistBlock, MarkdownAssistRange, MarkdownAssistResult, MarkdownAssistSuggestion, MarkdownAssistTarget, MarkdownAssistTrigger,
} from './types'

interface Item {
  readonly suggestion: MarkdownAssistResult
  /** undefined: the suggestion has no range; null: its range vanished. */
  range: MarkdownAssistRange | null | undefined
  readonly spanText?: string
}
interface Entry {
  readonly id: number
  pos: number
  text: string
  state: 'pending' | 'ready'
  generation: number
  abort?: AbortController
  /** Changes since the pending `suggest` was called; its ranges are mapped through them. */
  mapping?: Mapping
  items: Item[]
}
interface Attempt {
  readonly entry: Entry
  readonly item: Item
  tracked: (MarkdownAssistRange & { readonly text: string }) | null
  readonly recording: Mapping
  readonly abort: AbortController
  readonly conceals: number[]
  /** The suggestion's block as the attempt began. */
  readonly block: MarkdownAssistRange | null
  /**
   * The author's place outside the block, restored after the write: taken as the attempt begins and again right before
   * its first `dispatch` (the author may have typed on meanwhile), dropped when the author clicks during the attempt or
   * the write hands the text over. `at` is the number of recorded maps the bookmark has already seen.
   */
  author: { readonly bookmark: SelectionBookmark; readonly at: number } | null
  dispatched?: boolean
  /** The warning bar folding away under `effects.conceal`; cancelled (the bar returns) when nothing is written. */
  collapse?: { cancel(): void }
}
interface Effect { readonly id: number; range: MarkdownAssistRange }
interface Conceal extends Effect { readonly element: HTMLElement }

const CONCEAL_GLYPHS = '#$%&@*+=?!'
const remoteOrigin = (transaction: Transaction) => !!transaction.getMeta('y-sync$')
/** The runtime of each editor whose view the plugin is attached to (`assistMarkersFor`). */
const runtimes = new WeakMap<Editor, AssistRuntime>()

/**
 * One runtime per editor (plugin instance). Its view half lives exactly as long as the ProseMirror view:
 * the controller destroys and recreates the view on detach/attach, and so do the overlays.
 */
class AssistRuntime {
  readonly strings: AssistStrings
  editor?: Editor
  controller?: MarkdownPluginController
  private view?: EditorView
  private layer?: AssistLayer
  private entries: Entry[] = []
  private readonly markers = new Map<Entry, AssistMarker>()
  private readonly dismissed = new Set<string>()
  private readonly suppressed = new Set<string>()
  private caret: number | null = null
  private lastLeft: { left: number; entered: number | null } | null = null
  /** Blocks the author edited since their last analysis: only these are analysed when the caret leaves. */
  private edited: number[] = []
  private idleTimer: ReturnType<typeof setTimeout> | undefined
  private composing = false
  private shift = false
  private lastKey = ''
  private list?: { readonly entry: Entry; readonly ui: AssistList }
  private hit: MarkdownAssistRange | null = null
  private attempt?: Attempt
  private glows: Effect[] = []
  private conceals: Conceal[] = []
  private nextId = 0
  private status?: HTMLElement
  private statusHost?: HTMLElement
  private statusPlacement?: () => void
  private readonly bars = new Map<string, { element: HTMLElement; fresh: boolean }>()
  private decorationsQueued = false
  private observer?: MutationObserver
  private destroyed = false

  private readonly shown = new Set<string>()
  private reportShown(suggestion: MarkdownAssistResult): void {
    if (this.shown.has(suggestion.id)) return
    this.shown.add(suggestion.id)
    try { this.options.onShown?.(suggestion) } catch { /* host observation never changes an offer */ }
  }

  constructor(private readonly options: AssistPluginOptions) {
    this.strings = assistStrings(options.strings)
  }

  private get idleMs() { return Math.max(0, this.options.idleMs ?? 1500) }
  private get minLength() { return Math.max(0, this.options.minLength ?? 12) }
  private get editable() { return !!this.view && !!this.editor && !this.editor.isDestroyed && this.editor.isEditable }
  private motion(): AssistMotion { return assistMotion(this.view?.dom, this.options.reducedMotion) }

  /* ---------- lifecycle ---------- */

  attach(view: EditorView): { destroy: () => void } {
    this.view = view
    if (this.editor) runtimes.set(this.editor, this)
    this.caret = caretBlock(view.state)
    this.editor?.on('transaction', this.onTransaction)
    const Observer = view.dom.ownerDocument.defaultView?.MutationObserver
    this.observer = Observer ? new Observer(() => { if (!this.editable) this.reset(true) }) : undefined
    this.observer?.observe(view.dom, { attributes: true, attributeFilter: ['contenteditable'] })
    return { destroy: () => this.detach(view) }
  }

  private detach(view: EditorView): void {
    if (this.view !== view) return
    if (this.editor && runtimes.get(this.editor) === this) runtimes.delete(this.editor)
    this.reset(false)
    this.editor?.off('transaction', this.onTransaction)
    this.observer?.disconnect(); this.observer = undefined
    if (this.status && !this.statusHost) this.status.remove()
    this.layer?.destroy(); this.layer = undefined
    this.view = undefined
  }

  destroy(): void {
    if (this.destroyed) return
    this.destroyed = true
    if (this.view) this.detach(this.view)
  }

  /** Everything disappears and every pending `suggest` or apply is aborted (readonly, detach, destroy). */
  private reset(redraw: boolean): void {
    clearTimeout(this.idleTimer)
    for (const entry of this.entries) entry.abort?.abort()
    this.entries = []
    this.attempt?.abort.abort(); this.attempt = undefined
    this.closeList(false)
    for (const marker of this.markers.values()) marker.remove(false)
    this.markers.clear()
    this.glows = []; this.conceals = []; this.hit = null; this.lastLeft = null; this.edited = []
    this.clearStatus()
    if (redraw) this.renderDecorations()
  }

  /**
   * The body-level layer, made when the first marker or headless status needs it: copying the theme reads the editor's computed
   * style, which at attach forced a style recalculation of the whole just-built page (30 ms of a 90 ms page switch).
   */
  private layerFor(view: EditorView): AssistLayer { return this.layer ??= new AssistLayer(view) }

  /* ---------- status ---------- */

  private statusElement(doc: Document): HTMLElement {
    if (!this.status) {
      const status = this.status = doc.createElement('div')
      status.className = 'md-assist-status'
      status.setAttribute('role', 'status')
      status.setAttribute('data-md-assist-status', '')
    }
    return this.status
  }
  /** The status panel (Vue editor) hosts the message; headless editors get it in the overlay layer. */
  mountStatus(container: HTMLElement): () => void {
    this.statusHost = container
    container.append(this.statusElement(container.ownerDocument))
    this.statusPlacement?.(); this.statusPlacement = undefined
    return () => {
      if (this.statusHost !== container) return
      this.statusHost = undefined
      if (this.layer && this.status) this.layer.element.append(this.status); else this.status?.remove()
    }
  }
  private showStatus(text: string, entry?: Entry): void {
    const view = this.view
    if (!view) return
    const status = this.statusElement(view.dom.ownerDocument)
    status.textContent = text
    if (!this.statusHost && !status.isConnected) this.layerFor(view).element.append(status)
    if (this.statusHost || !entry) return
    // Without a status panel, the note sits under the block it is about.
    this.statusPlacement?.()
    const reference = { getBoundingClientRect: () => (view.nodeDOM(entry.pos) as Element | null)?.getBoundingClientRect() ?? new DOMRect(), contextElement: view.dom }
    const place = () => void computePosition(reference, status, { strategy: 'fixed', placement: 'bottom-start', middleware: [offset(4), shift({ padding: 8 })] })
      .then(({ x, y }) => Object.assign(status.style, { left: `${Math.round(x)}px`, top: `${Math.round(y)}px` })).catch(() => undefined)
    this.statusPlacement = autoUpdate(reference, status, place, { elementResize: false, layoutShift: false })
  }
  private clearStatus(): void {
    if (this.status?.textContent) this.status.textContent = ''
    this.statusPlacement?.(); this.statusPlacement = undefined
  }

  /* ---------- transactions and triggers ---------- */

  private readonly onTransaction = ({ transaction, appendedTransactions }: { transaction: Transaction; appendedTransactions: Transaction[] }) => {
    const view = this.view
    if (!view || this.destroyed) return
    const all = [transaction, ...appendedTransactions]
    const docChanged = all.some(item => item.docChanged)
    const remote = all.some(remoteOrigin)
    const local = docChanged && !remote
    const mapping = new Mapping()
    for (const item of all) mapping.appendMapping(item.mapping)
    const previousCaret = this.caret === null ? null : mapping.map(this.caret, 1)
    if (docChanged) this.map(mapping, local)
    if (this.attempt && all.some(item => item.getMeta('pointer'))) this.attempt.author = null
    if (local && !this.attempt) this.markEdited(mapping)
    if (local && !this.attempt) this.clearStatus()
    if (this.list && (local || (!remote && all.some(item => item.selectionSet) && !all.some(item => item.getMeta(assistMeta))))) this.closeList()
    const caret = caretBlock(view.state)
    if (caret !== previousCaret) {
      if (previousCaret !== null && this.editable && !this.attempt && !this.isComposing()) this.leave(previousCaret, caret)
    }
    this.caret = caret
    if (local) this.restartIdle()
    if (local && !this.attempt && all.some(item => item.getMeta('uiEvent') === 'paste') && !(this.shift && this.lastKey !== 'Insert')) {
      const pasted = changedRange(mapping)
      if (pasted) this.analysePaste(pasted)
    }
    this.syncMarkers()
    for (const marker of this.markers.values()) marker.refresh()
    if (docChanged) this.queueDecorations()
  }

  private map(mapping: Mapping, local: boolean): void {
    const doc = this.view!.state.doc
    this.edited = this.edited.map(pos => mapping.mapResult(pos, 1)).filter(result => !result.deleted).map(result => result.pos)
    if (this.lastLeft) this.lastLeft = { left: mapping.map(this.lastLeft.left, 1), entered: this.lastLeft.entered === null ? null : mapping.map(this.lastLeft.entered, 1) }
    for (const entry of [...this.entries]) {
      const result = mapping.mapResult(entry.pos, 1)
      entry.pos = result.pos
      entry.mapping?.appendMapping(mapping)
      for (const item of entry.items) if (item.range) item.range = mapRange(mapping, item.range)
      const node = result.deleted ? null : textblockAt(doc, entry.pos)
      if (node && blockText(node) === entry.text) continue
      // A collaborator's edit keeps an open list, so Enter can report „Návrh už neodpovídá textu.“
      if (node && !local && this.list?.entry === entry) continue
      this.dropEntry(entry)
    }
    if (this.hit) this.hit = mapRange(mapping, this.hit)
    for (const effect of [...this.glows, ...this.conceals]) { const range = mapRange(mapping, effect.range); if (range) effect.range = range }
    if (this.attempt) {
      this.attempt.recording.appendMapping(mapping)
      const tracked = this.attempt.tracked
      if (tracked) { const range = mapRange(mapping, tracked); this.attempt.tracked = range ? { ...range, text: tracked.text } : null }
    }
  }

  private markEdited(mapping: Mapping): void {
    const changed = changedRange(mapping)
    const doc = this.view!.state.doc
    if (!changed) return
    doc.nodesBetween(Math.max(0, changed.from), Math.min(doc.content.size, changed.to), (node, pos) => {
      if (node.isTextblock) { if (!this.edited.includes(pos)) this.edited.push(pos); return false }
      return true
    })
  }

  private isComposing(): boolean { return this.composing || !!this.view?.composing }

  private restartIdle(): void {
    clearTimeout(this.idleTimer)
    if (this.editable) this.idleTimer = setTimeout(() => this.idle(), this.idleMs)
  }

  private idle(): void {
    const view = this.view
    if (!view || !this.editable || this.attempt || this.isComposing()) return
    const pos = caretBlock(view.state)
    const node = pos === null ? null : textblockAt(view.state.doc, pos)
    if (pos === null || !node) return
    const text = blockText(node)
    if (!this.edited.includes(pos) || text.trim().length < this.minLength || this.entryAt(pos)?.text === text) return
    this.analyse(pos, 'idle')
  }

  /** „Po dokončení odstavce“: a block the author edited and left. Merely passing through analyses nothing. */
  private leave(left: number, entered: number | null): void {
    this.lastLeft = { left, entered }
    const node = textblockAt(this.view!.state.doc, left)
    if (!node || !this.edited.includes(left)) return
    const text = blockText(node)
    if (!text.trim() || this.entryAt(left)?.text === text) return
    this.analyse(left, 'leave')
  }

  private analysePaste(range: MarkdownAssistRange): void {
    const doc = this.view!.state.doc
    const blocks: number[] = []
    doc.nodesBetween(range.from, Math.min(range.to, doc.content.size), (node, pos) => {
      if (node.isTextblock) { if (blocks.length < 20) blocks.push(pos); return false }
      return true
    })
    for (const pos of blocks) {
      const node = doc.nodeAt(pos)!
      const pasted = { from: Math.max(range.from, pos + 1), to: Math.min(range.to, pos + 1 + node.content.size) }
      if (blockText(node).trim()) this.analyse(pos, 'paste', pasted)
    }
  }

  private entryAt(pos: number): Entry | undefined { return this.entries.find(entry => entry.pos === pos) }

  private analyse(pos: number, trigger: MarkdownAssistTrigger, pasted?: MarkdownAssistRange, done?: (entry: Entry) => void): void {
    const view = this.view
    const node = view && textblockAt(view.state.doc, pos)
    if (!view || !node || !this.editable) return
    const text = blockText(node)
    let entry = this.entryAt(pos)
    if (entry) { entry.abort?.abort(); this.removeMarker(entry) }
    else { entry = { id: ++this.nextId, pos, text, state: 'pending', generation: 0, items: [] }; this.entries.push(entry) }
    const current = entry
    this.edited = this.edited.filter(item => item !== pos)
    current.text = text; current.items = []; current.state = 'pending'; current.mapping = new Mapping()
    const generation = ++current.generation
    const abort = current.abort = new AbortController()
    const size = node.content.size
    const block: MarkdownAssistBlock = Object.freeze({
      pos, node, text, trigger, ...(pasted ? { pasted: Object.freeze({ ...pasted }) } : {}),
      rangeOf(start: number, end: number) {
        const from = Math.max(0, Math.min(size, Math.trunc(start))), to = Math.max(from, Math.min(size, Math.trunc(end)))
        return Object.freeze({ from: pos + 1 + from, to: pos + 1 + to })
      },
    })
    const signal = abort.signal
    const results = this.options.sources.map(source => {
      try { return Promise.resolve(source.suggest(block, { signal })) } catch (error) { return Promise.reject(error) }
    })
    let completed = 0, opened = false
    const publish = (suggestions: unknown) => {
      completed++
      if (signal.aborted || current.generation !== generation || !this.entries.includes(current) || !this.view) return
      const doc = this.view.state.doc
      const blockNode = textblockAt(doc, current.pos)
      // A late source may not resurrect an already transformed block (including a mark-only write).
      if (blockNode?.eq(node) && Array.isArray(suggestions)) {
        const bounds = { from: current.pos + 1, to: current.pos + 1 + blockNode.content.size }
        const seen = new Set(current.items.map(item => item.suggestion.id))
        for (const suggestion of suggestions as readonly MarkdownAssistResult[]) {
          if (!validSuggestion(suggestion) || suggestion.available?.() === false || seen.has(suggestion.id) || this.dismissed.has(suggestion.id) || this.suppressed.has(suggestion.kind)) continue
          let range: MarkdownAssistRange | undefined
          if (suggestion.range) {
            const mapped = mapRange(current.mapping!, suggestion.range)
            if (!mapped || mapped.from < bounds.from || mapped.to > bounds.to) continue
            range = mapped
          }
          seen.add(suggestion.id)
          current.items.push({ suggestion, range, spanText: range && rangeText(doc, range) })
        }
        if (this.list?.entry === current) {
          this.list.ui.update(this.rows(current))
          current.items.forEach(item => this.reportShown(item.suggestion))
        }
        this.syncMarkers(); this.renderDecorations()
      }
      if (completed === results.length) { current.abort = undefined; current.mapping = undefined; current.state = 'ready' }
      if (!opened && (current.items.length || completed === results.length)) { opened = true; done?.(current) }
    }
    if (!results.length) { current.state = 'ready'; current.abort = undefined; current.mapping = undefined; done?.(current) }
    else results.forEach(result => { void result.then(publish, () => publish(undefined)) })
  }

  private dropEntry(entry: Entry): void {
    entry.abort?.abort()
    this.entries = this.entries.filter(item => item !== entry)
    if (this.list?.entry === entry) this.closeList()
    this.removeMarker(entry)
  }

  /* ---------- markers ---------- */

  /** The one block that may show a default marker: the caret's, else the block just finished (Enter). */
  private focusEntry(): Entry | undefined {
    const hasDefault = (entry?: Entry) => !!entry?.items.some(item => item.suggestion.tone !== 'warning')
    const caret = this.caret === null ? undefined : this.entryAt(this.caret)
    if (hasDefault(caret)) return caret
    if (this.lastLeft && this.lastLeft.entered === this.caret) {
      const left = this.entryAt(this.lastLeft.left)
      if (hasDefault(left)) return left
    }
    return undefined
  }

  private syncMarkers(): void {
    const view = this.view
    if (!view) return
    for (const entry of this.entries) {
      const before = entry.items.length
      entry.items = entry.items.filter(item => item.suggestion.available?.() !== false)
      if (this.list?.entry === entry && entry.items.length !== before) { if (entry.items.length) this.list.ui.update(this.rows(entry)); else this.closeList() }
    }
    if (this.attempt?.item.suggestion.available?.() === false) this.attempt.abort.abort()
    const desired = new Map<Entry, 'default' | 'warning'>()
    if (this.editable) {
      for (const entry of this.entries) if (entry.items.some(item => item.suggestion.tone === 'warning')) desired.set(entry, 'warning')
      const focus = this.focusEntry()
      if (focus && !desired.has(focus)) desired.set(focus, 'default')
      if (this.list && this.list.entry.items.length && !desired.has(this.list.entry)) desired.set(this.list.entry, 'default')
    }
    for (const [entry, marker] of this.markers) {
      if (desired.get(entry) !== marker.tone || !entry.items.length) this.removeMarker(entry)
    }
    for (const [entry, tone] of desired) {
      const lead = entry.items.find(item => tone !== 'warning' || item.suggestion.tone === 'warning') ?? entry.items[0]!
      const existing = this.markers.get(entry)
      if (existing) { existing.setContent(lead.suggestion.icon, this.strings.marker(lead.suggestion.title)); continue }
      this.markers.set(entry, new AssistMarker({
        view, layer: this.layerFor(view), tone, icon: lead.suggestion.icon, label: this.strings.marker(lead.suggestion.title), reducedMotion: this.options.reducedMotion,
        block: () => { const dom = this.entries.includes(entry) ? view.nodeDOM(entry.pos) : null; return dom instanceof Element ? dom : null },
        pos: () => entry.pos,
        isOpen: () => this.list?.entry === entry,
        onShown: () => this.reportShown(lead.suggestion),
        onPress: wasOpen => { if (wasOpen || this.list?.entry === entry) this.closeList(); else this.openList(entry) },
      }))
    }
  }

  /** Visible markers of the textblock at `pos` or of the textblocks inside the block at `pos`, in document order. */
  markersFor(pos: number): HTMLButtonElement[] {
    const doc = this.view?.state.doc
    const node = doc && Number.isInteger(pos) && pos >= 0 && pos < doc.content.size ? doc.nodeAt(pos) : null
    if (!node?.isBlock) return []
    const end = pos + node.nodeSize
    return [...this.markers].filter(([entry, marker]) => entry.pos >= pos && entry.pos < end && marker.visible)
      .sort(([left], [right]) => left.pos - right.pos).map(([, marker]) => marker.element)
  }

  private removeMarker(entry: Entry): void {
    const marker = this.markers.get(entry)
    if (!marker) return
    this.markers.delete(entry)
    marker.remove()
  }

  /* ---------- list ---------- */

  /**
   * Rows of the Ctrl+Space / Alt+Enter menu (core intentions): the suggestions of the caret's block, analysed now when it has none
   * yet (the request that Alt+Enter used to make); Enter applies one as its list does. The list with Delete and „Nenabízet“ stays
   * at the block's marker.
   */
  intentions(signal: AbortSignal): readonly MarkdownIntention[] | Promise<readonly MarkdownIntention[]> {
    const view = this.view
    if (!view || !this.editable || this.isComposing()) return []
    this.closeList(false)
    const caret = caretBlock(view.state)
    const rowsOf = (entry: Entry): MarkdownIntention[] => entry.items.filter(item => !item.suggestion.informational).map((item, index) => (this.reportShown(item.suggestion), {
      id: `assist:${item.suggestion.id}`, label: item.suggestion.title, group: 'ai', priority: index,
      ...(item.suggestion.description ?? item.suggestion.reason ? { description: item.suggestion.description ?? item.suggestion.reason } : {}),
      icon: `svg:${item.suggestion.icon}`, run: () => this.apply(entry, item),
    }))
    const entry = caret === null ? undefined : this.entryAt(caret)
    // The caret's block, else the one whose marker shows (the block just finished with Enter).
    const target = entry?.items.length ? entry : this.focusEntry()
    if (target) return rowsOf(target)
    if (caret === null || entry?.state === 'ready' || !textblockAt(view.state.doc, caret)) return []
    return new Promise(settle => {
      signal.addEventListener('abort', () => settle([]), { once: true })
      this.analyse(caret, 'request', undefined, done => settle(rowsOf(done)))
    })
  }

  private openList(entry: Entry): void {
    const view = this.view
    if (!view || !this.editable) return
    this.closeList(false)
    const marker = this.markers.get(entry)
    const anchor = () => {
      const visible = marker && !marker.element.hidden && marker.element.isConnected ? marker.element : view.nodeDOM(entry.pos)
      return visible instanceof Element ? visible.getBoundingClientRect() : new DOMRect()
    }
    const ui = new AssistList({
      editor: this.editor!, strings: this.strings, rows: this.rows(entry), anchor, reducedMotion: this.options.reducedMotion,
      onSelect: index => { const item = entry.items[index]; if (item?.suggestion.informational) this.closeList(); else if (item) void this.apply(entry, item) },
      onActive: index => this.setHit(entry.items[index]?.range ?? null),
      onDismissRow: index => { const item = entry.items[index]; if (item) this.dismiss(entry, item, 'suggestion') },
      onSuppressRow: index => { const item = entry.items[index]; if (item) this.dismiss(entry, item, 'kind') },
      onDismissed: () => { if (this.list?.ui === ui) this.listClosed() },
    })
    this.list = { entry, ui }
    entry.items.forEach(item => this.reportShown(item.suggestion))
    view.dom.addEventListener('keydown', this.onListKey, true)
    this.markers.get(entry)?.setExpanded(true)
    this.setHit(entry.items[0]?.range ?? null)
  }

  private rows(entry: Entry) {
    return entry.items.map(({ suggestion }) => ({ title: suggestion.title, description: suggestion.description, icon: suggestion.icon, ai: suggestion.ai === true, reason: suggestion.reason, avatar: suggestion.avatar }))
  }

  private readonly onListKey = (event: KeyboardEvent) => { this.list?.ui.keydown(event) }

  private closeList(animated = true): void {
    const list = this.list
    if (!list) return
    list.ui.close(animated)
    this.listClosed()
  }

  private listClosed(): void {
    const list = this.list
    if (!list) return
    this.list = undefined
    this.view?.dom.removeEventListener('keydown', this.onListKey, true)
    this.markers.get(list.entry)?.setExpanded(false)
    // An entry kept open across a collaborator's edit is stale now.
    const node = this.view && textblockAt(this.view.state.doc, list.entry.pos)
    if (this.entries.includes(list.entry) && (!node || blockText(node) !== list.entry.text)) this.dropEntry(list.entry)
    this.setHit(null)
    this.syncMarkers()
  }

  private setHit(range: MarkdownAssistRange | null): void {
    const same = range && this.hit ? range.from === this.hit.from && range.to === this.hit.to : range === this.hit
    if (same) return
    this.hit = range
    // Also reached from the transaction handler (a list closed by typing): never dispatch from inside it.
    this.queueDecorations()
  }

  private dismiss(entry: Entry, item: Item, scope: 'suggestion' | 'kind'): void {
    const { suggestion } = item
    if (scope === 'suggestion') this.dismissed.add(suggestion.id)
    else this.suppressed.add(suggestion.kind)
    for (const current of this.entries) {
      current.items = current.items.filter(other => other !== item && !(scope === 'kind' && other.suggestion.kind === suggestion.kind))
    }
    this.options.onDismissed?.(suggestion, scope)
    if (this.list?.entry === entry) {
      if (entry.items.length) { this.list.ui.update(this.rows(entry)) } else this.closeList()
    }
    this.syncMarkers()
    this.renderDecorations()
  }

  /* ---------- apply ---------- */

  private valid(entry: Entry, item: Item): boolean {
    if (item.suggestion.available?.() === false) return false
    const view = this.view
    const node = view && this.entries.includes(entry) ? textblockAt(view.state.doc, entry.pos) : null
    if (!view || !node) return false
    if (item.range === undefined) return blockText(node) === entry.text
    return !!item.range && rangeText(view.state.doc, item.range) === item.spanText
  }

  private async apply(entry: Entry, item: Item): Promise<void> {
    if (!this.editable) return
    this.closeList()
    if (!this.valid(entry, item)) {
      this.showStatus(this.strings.stale, entry)
      for (const current of this.entries) current.items = current.items.filter(other => other !== item)
      this.syncMarkers(); this.renderDecorations()
      return
    }
    const suggestion = item.suggestion
    if (suggestion.informational) return
    const applied = await this.run(entry, item, context => suggestion.apply(context), true)
    if (!applied) return
    this.retire(entry, item)
    this.options.onApplied?.(suggestion)
  }

  /**
   * An applied suggestion leaves its block's list together with its alternatives for the same words (suggestions whose
   * range overlaps it). A write that changes the block's text drops the whole entry anyway; this covers writes that keep
   * the text — a mark such as a link over the words, or content inserted after the block — so the marker never offers
   * what was just done. The other suggestions of the block stay; nothing is dismissed for the session, so a later edit
   * of the block may offer them again.
   */
  private retire(entry: Entry, item: Item): void {
    if (!this.entries.includes(entry)) return
    const span = item.range
    entry.items = entry.items.filter(other => other !== item && !(span && other.range && other.range.from < span.to && span.from < other.range.to))
    if (this.list?.entry === entry) {
      if (entry.items.length) { this.list.ui.update(this.rows(entry)) } else this.closeList()
    }
    this.syncMarkers()
    this.renderDecorations()
  }

  private async secondary(entry: Entry, item: Item, run: (context: MarkdownAssistApplyContext) => void | Promise<void>): Promise<void> {
    if (!this.editable) return
    await this.run(entry, item, async context => { await run(context); return false }, false)
    // A secondary action („Je to jen příklad“) resolves the warning for this session.
    if (this.entries.includes(entry) && entry.items.includes(item)) this.dismiss(entry, item, 'suggestion')
  }

  private closeHistory(): void {
    const view = this.view
    if (view && this.editor && !this.editor.isDestroyed) view.dispatch(closeMarkdownHistory(view.state.tr).setMeta('addToHistory', false).setMeta(assistMeta, true))
  }

  /** One attempt: the host's writes form one undo step, bracketed before and after. */
  private async run(entry: Entry, item: Item, write: (context: MarkdownAssistApplyContext) => boolean | Promise<boolean>, history: boolean): Promise<boolean> {
    const view = this.view!, editor = this.editor!
    const fromWidth = item.range ? spanWidth(view, item.range) : 0
    const blockNode = textblockAt(view.state.doc, entry.pos)
    const block = blockNode ? { from: entry.pos, to: entry.pos + blockNode.nodeSize } : null
    const attempt: Attempt = {
      entry, item, tracked: item.range ? { ...item.range, text: item.spanText ?? '' } : null, recording: new Mapping(), abort: new AbortController(), conceals: [],
      block, author: null,
    }
    this.authorPlace(attempt)
    this.attempt?.abort.abort()
    this.attempt = attempt
    const runtime = this
    const context: MarkdownAssistApplyContext = {
      editor,
      get controller(): MarkdownPluginController {
        if (!runtime.controller) throw new Error('The assist plugin needs a Markdown editor controller to apply suggestions.')
        return runtime.controller
      },
      signal: attempt.abort.signal,
      get range() {
        const tracked = attempt.tracked
        if (!tracked || !runtime.view) return null
        return rangeText(runtime.view.state.doc, tracked) === tracked.text ? { from: tracked.from, to: tracked.to } : null
      },
      dispatch: (command, options) => this.dispatchCommand(command, options),
      propose: async proposal => {
        const blockNode = runtime.view && textblockAt(runtime.view.state.doc, entry.pos)
        const range = proposal.range ?? context.range ?? (blockNode ? { from: entry.pos, to: entry.pos + blockNode.nodeSize } : null)
        if (!range || attempt.abort.signal.aborted) return false
        const result = await openMarkdownProposalPanel({ ...proposal, editor, range, signal: attempt.abort.signal, reducedMotion: this.options.reducedMotion, strings: this.options.strings })
        if (result === 'edited') attempt.author = null
        return result !== 'cancelled'
      },
      effects: { conceal: range => this.conceal(attempt, range) },
    }
    if (history) this.closeHistory()
    let applied = false
    try { applied = (await write(context)) === true } catch { applied = false }
    if (applied && this.attempt === attempt && this.view === view) this.restoreCaret(view, attempt)
    if (!applied) attempt.collapse?.cancel()
    if (this.attempt === attempt) this.attempt = undefined
    if (attempt.conceals.length) {
      this.conceals = this.conceals.filter(conceal => !attempt.conceals.includes(conceal.id))
      this.renderDecorations()
    }
    if (!this.view || this.view !== view) return false
    if (history) this.closeHistory()
    if (applied) this.afterglow(attempt.recording, fromWidth)
    return applied
  }

  /**
   * After a successful write the author keeps typing where they were (prototype „Návrhy v editoru“, moment 5): a caret
   * that was outside the suggestion's block goes back, mapped through the write, when the write took it into the block
   * or the written range. A selection only, no history entry; nothing when the host already restored it, the author
   * moved it meanwhile or the suggestion follows the write (`caret: 'follow'`).
   */
  private restoreCaret(view: EditorView, attempt: Attempt): void {
    const { author, recording } = attempt
    const block = this.attemptBlock(attempt)
    if (!author || !block) return
    const { selection } = view.state
    const within = (range: MarkdownAssistRange | null) => !!range && selection.from >= range.from && selection.to <= range.to
    // A caret outside both (the host put it back, the write left it elsewhere) stays.
    if (!within(block) && !within(changedRange(recording))) return
    view.dispatch(view.state.tr.setSelection(author.bookmark.map(recording.slice(author.at)).resolve(view.state.doc)).setMeta('addToHistory', false).setMeta(assistMeta, true))
  }

  /** The suggestion's block mapped to the current document (content inserted right after it counts as the block). */
  private attemptBlock({ block, recording }: Attempt): MarkdownAssistRange | null {
    return block && { from: recording.map(block.from, -1), to: recording.map(block.to, 1) }
  }

  /** Takes the author's place when it lies outside the suggestion's block (see `Attempt.author`). */
  private authorPlace(attempt: Attempt, preservePrepared = false): void {
    const block = this.attemptBlock(attempt), { selection } = this.view!.state
    // A mark command prepares this exact selection itself; it is not the author's new typing place.
    const prepared = attempt.tracked && selection.from === attempt.tracked.from && selection.to === attempt.tracked.to
    if (preservePrepared && prepared) return
    attempt.author = block && attempt.item.suggestion.caret !== 'follow' && (selection.to <= block.from || selection.from >= block.to)
      ? { bookmark: selection.getBookmark(), at: attempt.recording.maps.length } : null
  }

  private dispatchCommand(command: MarkdownCommand, options?: MarkdownDispatchOptions): boolean {
    const view = this.view, controller = this.controller, attempt = this.attempt
    if (!view || !controller || !this.editable) return false
    if (attempt && !attempt.dispatched) { attempt.dispatched = true; this.authorPlace(attempt, true) }
    const range = options?.replaceRange
    if (range && range.from >= 0 && range.to >= range.from && range.to <= view.state.doc.content.size) {
      // Commands insert at the selection: put it on the span, as the slash menu's query would be.
      const { doc } = view.state
      view.dispatch(view.state.tr.setSelection(TextSelection.between(doc.resolve(range.from), doc.resolve(range.to))).setMeta('addToHistory', false).setMeta(assistMeta, true))
    }
    this.closeHistory()
    const applied = controller.dispatch(command, options)
    this.closeHistory()
    return applied
  }

  /** Morph into an inserted inline component, draw a new link, or let the changed text glow. */
  private afterglow(recording: Mapping, fromWidth: number): void {
    const view = this.view
    const changed = changedRange(recording)
    if (!view || !changed) return
    const doc = view.state.doc
    const range = { from: Math.max(0, changed.from), to: Math.min(doc.content.size, changed.to) }
    if (range.to <= range.from) return
    const motion = this.motion()
    let atom: number | undefined
    const links: Element[] = []
    doc.nodesBetween(range.from, range.to, (node, pos) => {
      if (atom !== undefined) return false
      if (node.isInline && !node.isText && node.type.name !== 'hardBreak') { atom = pos; return false }
      if (node.isText && node.marks.some(mark => mark.type.name === 'link')) {
        const { node: dom } = view.domAtPos(Math.max(pos, range.from) + 1)
        const anchor = (dom.nodeType === 1 ? dom as Element : dom.parentElement)?.closest('a')
        if (anchor && !links.includes(anchor)) links.push(anchor)
      }
      return true
    })
    if (atom !== undefined) {
      const element = view.nodeDOM(atom)
      if (element instanceof Element) { morph(element, fromWidth, element.getBoundingClientRect().width, motion); glow(element, motion) }
      return
    }
    if (links.length) {
      for (const link of links) void finished(linkIn(link, motion)).then(() => glow(link, this.motion()))
      return
    }
    const $from = doc.resolve(range.from), $to = doc.resolve(range.to)
    if ($from.parent.isTextblock && $from.sameParent($to)) {
      const effect = { id: ++this.nextId, range }
      this.glows.push(effect)
      this.renderDecorations()
      for (const element of view.dom.querySelectorAll(`[data-md-assist-glow="${effect.id}"]`)) glow(element, motion)
      setTimeout(() => { this.glows = this.glows.filter(item => item !== effect); this.renderDecorations() }, motion.duration('glow'))
      return
    }
    doc.nodesBetween(range.from, range.to, (node, pos) => {
      if (!node.isBlock) return false
      const element = view.nodeDOM(pos)
      if (element instanceof Element) glow(element, motion)
      return false
    })
  }

  /** Characters scramble to dots every 60 ms (~700 ms), decoration only; reduced motion shows the dots at once. */
  private conceal(attempt: Attempt, range: MarkdownAssistRange): Promise<void> {
    const view = this.view
    if (!view || this.attempt !== attempt || range.to <= range.from || range.to > view.state.doc.content.size) return Promise.resolve()
    const motion = this.motion()
    // Prototype order (moment 7): the warning bar folds away as the characters start to scramble.
    const bar = this.bars.get(`bar:${attempt.entry.id}:${attempt.item.suggestion.id}`)?.element
    if (bar?.isConnected && !attempt.collapse) attempt.collapse = collapse(bar, motion.duration('base'), motion)
    const text = [...rangeText(view.state.doc, range)]
    const element = view.dom.ownerDocument.createElement('span')
    element.className = 'md-assist-conceal-glyphs'
    element.setAttribute('aria-hidden', 'true')
    const conceal: Conceal = { id: ++this.nextId, range, element }
    attempt.conceals.push(conceal.id)
    this.conceals.push(conceal)
    const dots = '•'.repeat(text.length)
    const wait = (milliseconds: number) => new Promise<void>(resolve => setTimeout(resolve, milliseconds))
    if (motion.reduced) {
      element.textContent = dots
      this.renderDecorations()
      return wait(Math.min(160, motion.scaled(160)))
    }
    element.textContent = text.join('')
    this.renderDecorations()
    return (async () => {
      for (let round = 0; round < 7; round++) {
        element.textContent = text.map((_, index) => index < round * 2 ? '•' : CONCEAL_GLYPHS[Math.floor(Math.random() * CONCEAL_GLYPHS.length)]).join('')
        await wait(motion.scaled(60))
      }
      element.textContent = dots
      await wait(motion.scaled(120))
    })()
  }

  /* ---------- decorations ---------- */

  private queueDecorations(): void {
    if (this.decorationsQueued) return
    this.decorationsQueued = true
    queueMicrotask(() => { this.decorationsQueued = false; this.renderDecorations() })
  }

  private desiredDecorations(): AssistDecorationSpec[] {
    const view = this.view!
    const doc = view.state.doc
    const size = doc.content.size
    const valid = (range: MarkdownAssistRange | null | undefined): range is MarkdownAssistRange => !!range && range.from >= 0 && range.to <= size && range.from < range.to
    const specs: AssistDecorationSpec[] = []
    if (!this.editable) return specs
    for (const entry of this.entries) {
      const warnings = entry.items.filter(item => item.suggestion.tone === 'warning')
      const node = textblockAt(doc, entry.pos)
      if (!warnings.length || !node) continue
      for (const item of warnings) if (valid(item.range)) specs.push(inline(item.range.from, item.range.to, `tone:${entry.id}:${item.suggestion.id}`, { class: 'md-assist-tone-warning' }))
      const lead = warnings[0]!
      const key = `bar:${entry.id}:${lead.suggestion.id}`
      specs.push(widget(entry.pos + node.nodeSize, key, () => this.warningBar(key, entry, lead), -1))
    }
    if (valid(this.hit)) specs.push(inline(this.hit.from, this.hit.to, 'hit', { class: 'md-assist-hit' }))
    for (const effect of this.glows) if (valid(effect.range)) specs.push(inline(effect.range.from, effect.range.to, `glow:${effect.id}`, { class: 'md-assist-glow', 'data-md-assist-glow': String(effect.id) }))
    for (const conceal of this.conceals) {
      if (!valid(conceal.range)) continue
      specs.push(inline(conceal.range.from, conceal.range.to, `concealed:${conceal.id}`, { class: 'md-assist-concealed' }))
      specs.push(widget(conceal.range.from, `conceal:${conceal.id}`, () => {
        const holder = view.dom.ownerDocument.createElement('span')
        holder.className = 'md-assist-conceal'
        holder.append(conceal.element)
        return holder
      }, -1))
    }
    return specs
  }

  /** Replaces the decoration set when it differs, then starts the entrance motion of new pieces. */
  private renderDecorations(): void {
    const view = this.view
    if (!view || this.destroyed || !this.editor || this.editor.isDestroyed) return
    const specs = this.desiredDecorations()
    const hitting = specs.some(spec => spec.key === 'hit')
    const hitBefore = hitting ? new Set(view.dom.querySelectorAll('.md-assist-hit')) : null
    if (!sameDecorations(view.state, specs)) {
      const set = DecorationSet.create(view.state.doc, specs.map(spec => spec.decoration))
      view.dispatch(view.state.tr.setMeta(assistDecorationsKey, set).setMeta('addToHistory', false).setMeta(assistMeta, true))
    }
    // Read only for a piece that enters: the motion tokens come from computed style, which on every keystroke forced a style
    // recalculation of the whole document (~10 ms on a 2 MB page, UX EP2-4).
    let motion: AssistMotion | undefined
    const entering = () => motion ??= this.motion()
    if (hitBefore) for (const element of view.dom.querySelectorAll('.md-assist-hit')) if (!hitBefore.has(element)) underline(element, entering())
    const keys = new Set(specs.map(spec => spec.key))
    for (const [key, bar] of this.bars) {
      if (!keys.has(key)) { this.bars.delete(key); continue }
      if (bar.fresh && bar.element.isConnected) { bar.fresh = false; reveal(bar.element, entering().duration('slow'), entering()) }
    }
  }

  /** The warning bar under a block (moment 7): a `role="status"` widget, never document content. */
  private warningBar(key: string, entry: Entry, item: Item): HTMLElement {
    const existing = this.bars.get(key)
    if (existing) return existing.element
    const doc = this.view!.dom.ownerDocument
    const { suggestion } = item
    const bar = doc.createElement('div')
    bar.className = 'md-assist-warning'
    bar.setAttribute('role', 'status')
    bar.setAttribute('contenteditable', 'false')
    bar.setAttribute('data-md-assist-warning', '')
    const text = doc.createElement('div'); text.className = 'md-assist-warning-text'
    const icon = createIconElement(doc, suggestion.icon, 16)
    const message = doc.createElement('span')
    const title = doc.createElement('b'); title.textContent = suggestion.title
    message.append(title)
    if (suggestion.description) message.append(' ', suggestion.description)
    text.append(icon, message)
    const actions = doc.createElement('div'); actions.className = 'md-assist-warning-actions'
    const button = (label: string, primary: boolean, run: () => void, iconName?: MarkdownAssistSuggestion['applyIcon']) => {
      const element = doc.createElement('button')
      element.type = 'button'; element.className = primary ? 'md-btn-primary' : 'md-btn'
      if (iconName) element.append(createIconElement(doc, iconName, 14))
      element.append(label)
      element.addEventListener('mousedown', event => event.preventDefault())
      element.addEventListener('click', run)
      actions.append(element)
    }
    button(suggestion.applyLabel ?? this.strings.apply, true, () => void this.apply(entry, item), suggestion.applyIcon)
    for (const action of suggestion.secondary ?? []) button(action.label, false, () => void this.secondary(entry, item, context => action.run(context)))
    bar.append(text, actions)
    this.bars.set(key, { element: bar, fresh: true })
    return bar
  }

  /* ---------- DOM events from the ProseMirror plugin ---------- */

  keydown(event: KeyboardEvent): void {
    this.shift = event.shiftKey; this.lastKey = event.key
    if (this.attempt && /^(Arrow(?:Left|Right|Up|Down)|Home|End|PageUp|PageDown)$/.test(event.key)) this.attempt.author = null
  }
  keyup(event: KeyboardEvent): void { if (event.key === 'Shift') this.shift = false }
  compositionStart(): void { this.composing = true; clearTimeout(this.idleTimer) }
  compositionEnd(): void { this.composing = false; this.restartIdle() }
}

function validSuggestion(value: unknown): value is MarkdownAssistResult {
  if (!value || typeof value !== 'object') return false
  const suggestion = value as MarkdownAssistResult
  return typeof suggestion.id === 'string' && !!suggestion.id && typeof suggestion.kind === 'string' && !!suggestion.kind
    && typeof suggestion.title === 'string' && !!suggestion.title.trim() && typeof suggestion.icon === 'string'
    && (suggestion.informational === true ? suggestion.apply === undefined : typeof suggestion.apply === 'function')
}

/** The rendered width of a span (the start of the morph); 0 when the engine has no layout. */
function spanWidth(view: EditorView, range: MarkdownAssistRange): number {
  try {
    const start = view.domAtPos(range.from), end = view.domAtPos(range.to)
    const dom = view.dom.ownerDocument.createRange()
    dom.setStart(start.node, start.offset); dom.setEnd(end.node, end.offset)
    return dom.getBoundingClientRect?.().width ?? 0
  } catch { return 0 }
}

/**
 * The visible assist markers of the textblock at `pos` (position directly before it, as `data-md-assist-pos` and
 * `MarkdownAssistBlock.pos`), or of every textblock inside the block at `pos` (a list, a quote), in document order.
 * For a host's keyboard path of a block (`registerMarkdownBlockKeyboardHook`, Alt+F10). Empty for an editor without the
 * plugin or a position that starts no block.
 */
export function assistMarkersFor(editor: Editor, pos: number): HTMLButtonElement[] {
  return runtimes.get(editor)?.markersFor(pos) ?? []
}

const assistPluginKey = new PluginKey('markdownAssist')

/** Block suggestions as a plugin: extensions, Alt+Enter and a status panel only; no document nodes, no schema. */
export function createAssistPlugin(options: AssistPluginOptions): MarkdownPlugin {
  if (!options || !Array.isArray(options.sources)) throw new TypeError('createAssistPlugin needs an array of sources.')
  for (const source of options.sources) {
    if (!source || typeof source.id !== 'string' || typeof source.suggest !== 'function') throw new TypeError('Every assist source needs an id and a suggest function.')
  }
  return {
    id: 'assist',
    apiVersion: 1,
    create({ signal }) {
      const emptyIcons = new WeakMap<Node, () => void>()
      const menu = options.selectionMenu
      // The host's AI actions for one target, each behind the menu's gate and its own (P11: hidden when AI cannot answer now).
      const actionsFor = (editor: Editor, target: MarkdownAssistTarget) => {
        const range = { from: target.from, to: target.to }
        if (!menu || !editor.isEditable || !menu.available(editor, range, target)) return []
        return menu.actions.filter(action => (action.targets ?? ['selection']).includes(target.kind) && (action.available?.(editor, range, target) ?? true))
      }
      const selection = menu && defineMarkdownCommand({ id: 'assist.selection', apply: () => ({ editor, dispatch }) => {
        const range = { from: editor.state.selection.from, to: editor.state.selection.to }
        const target: MarkdownAssistTarget = { kind: 'selection', ...range, label: 'Výběr' }
        if (editor.isDestroyed || !editor.isEditable || range.from === range.to || !actionsFor(editor, target).length) return false
        if (!dispatch) return true
        const doc = editor.view.dom.ownerDocument
        const anchor = doc.createElement('span'); anchor.className = 'md-assist-anchor'; anchor.setAttribute('aria-hidden', 'true')
        anchor.getBoundingClientRect = () => { try { const rect = editor.view.coordsAtPos(range.from); return new DOMRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top) } catch { return editor.view.dom.getBoundingClientRect() } }
        editor.view.dom.parentElement!.append(anchor)
        openMarkdownContextPopover({ editor, anchor, title: menu.label, variant: 'menu', signal, render(container, close) {
          for (const action of actionsFor(editor, target)) {
            const button = doc.createElement('button'); button.type = 'button'; button.className = 'md-context-menu-item'; button.setAttribute('role', 'menuitem'); button.textContent = action.label
            button.onclick = () => { close(); if (actionsFor(editor, target).includes(action)) void Promise.resolve(action.run({ editor, range, signal, target })).catch(() => {}) }
            container.append(button)
          }
          return () => anchor.remove()
        } })
        return true
      } })
      const runtime = new AssistRuntime(options)
      signal.addEventListener('abort', () => runtime.destroy(), { once: true })
      const extension = Extension.create({
        name: 'markdownAssist',
        addProseMirrorPlugins() {
          runtime.editor = this.editor
          return [assistDecorationsPlugin(), new Plugin({
            props: { decorations: state => {
              const action = options.emptyAction, editor = this.editor
              if (!action || editor.isDestroyed || !editor.isEditable || !action.available(editor)) return DecorationSet.empty
              return DecorationSet.create(state.doc, [Decoration.widget(1, () => {
                const button = editor.view.dom.ownerDocument.createElement('button'); button.type = 'button'; button.className = 'md-assist-empty'; button.dataset.mdAssistEmpty = ''; button.textContent = action.label
                if (action.renderIcon) { const icon = button.ownerDocument.createElement('span'); button.prepend(icon); const cleanup = action.renderIcon(icon); if (typeof cleanup === 'function') emptyIcons.set(button, cleanup) }
                button.onclick = () => { if (editor.isEditable && action.available(editor)) void Promise.resolve(action.run({ editor, signal })).catch(() => {}) }
                return button
              }, { key: 'assist-empty', side: -1, ignoreSelection: true, stopEvent: () => true, destroy: node => { const cleanup = emptyIcons.get(node); cleanup?.(); emptyIcons.delete(node) } })])
            } },
          }), new Plugin({
            key: assistPluginKey,
            view: view => runtime.attach(view),
            props: { handleDOMEvents: {
              keydown: (_view, event) => { runtime.keydown(event); return false },
              keyup: (_view, event) => { runtime.keyup(event); return false },
              compositionstart: () => { runtime.compositionStart(); return false },
              compositionend: () => { runtime.compositionEnd(); return false },
            } },
          })]
        },
      })
      return {
        extensions: [extension],
        // Alt+Enter (and Ctrl+Space) open core's „Co s tím jde dělat“ with these rows, first on Alt+Enter: the block's suggestions,
        // and the host's AI actions for every target at the caret (P11: the selection or element, the heading's section or the
        // block, the page), each only while it can answer now.
        intentions: ({ editor, signal: menuSignal }) => {
          // A fallback target (the heading alone) offers only what the target before it (its section) cannot.
          let before: readonly unknown[] = []
          const offered = (target: MarkdownAssistTarget) => {
            const own = actionsFor(editor, target), previous = before
            before = own
            return target.fallback ? own.filter(action => !previous.includes(action)) : own
          }
          const actions: MarkdownIntention[] = menu ? markdownAssistTargets(editor).flatMap((target, order) => offered(target).map((action, index) => {
            const range = { from: target.from, to: target.to }
            return {
              id: target.kind === 'selection' ? `assist.selection.${action.id}` : `assist.${target.kind}.${action.id}`, label: action.label,
              description: target.kind === 'selection' ? menu.label : `${menu.label} · ${target.label}`, group: 'ai', priority: 100 + order * 50 + index,
              ...(action.intent ? { keywords: [action.intent] } : {}), ...(menu.icon ? { icon: `svg:${menu.icon}` } : {}),
              run: () => { if (actionsFor(editor, target).includes(action)) return action.run({ editor, range, signal, target }) },
            }
          })) : []
          const suggestions = runtime.intentions(menuSignal)
          return suggestions instanceof Promise ? suggestions.then(rows => [...rows, ...actions]) : [...suggestions, ...actions]
        },
        ...(selection && menu ? { commands: [selection], actions: { selection: [{ id: 'assist.selection', label: menu.label, ...(menu.icon ? { icon: `svg:${menu.icon}` } : {}), ...(menu.renderIcon ? { renderIcon: menu.renderIcon } : {}), hideWhenDisabled: true, command: selection() }] } } : {}),
        panels: [{ id: 'assist.status', label: runtime.strings.statusLabel, placement: 'status', mount: ({ container }) => runtime.mountStatus(container) }],
        onCreate: ({ controller }) => { runtime.controller = controller },
      }
    },
  }
}
