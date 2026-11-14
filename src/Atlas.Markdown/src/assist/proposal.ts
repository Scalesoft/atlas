import type { Editor, JSONContent } from '@tiptap/core'
import { DOMSerializer, Fragment, type Node as ProseMirrorNode } from '@tiptap/pm/model'
import { TextSelection, type Transaction } from '@tiptap/pm/state'
import { Mapping } from '@tiptap/pm/transform'
import { closeMarkdownHistory, dispatchMarkdownHistoryOperation, openMarkdownContextPopover, createIconElement } from '../core'
import { copyAssistTheme } from './marker'
import { assistMotion, collapse, DIFF_STAGGER, finished, glow, grow, solidify, strike } from './motion'
import { assistStrings } from './strings'
import { changedRange, countWords, diffWords, mapRange } from './text'
import type { MarkdownAssistRange, MarkdownProposalOptions, MarkdownProposalResult } from './types'

const SVG = 'http://www.w3.org/2000/svg'

function fragmentOf(editor: Editor, content: JSONContent): Fragment {
  return content.type === 'doc' ? Fragment.fromJSON(editor.schema, content.content ?? []) : Fragment.from(editor.schema.nodeFromJSON(content))
}
const textOf = (fragment: Fragment) => { const parts: string[] = []; fragment.forEach(node => parts.push(node.textContent)); return parts.join('\n') }
/** A single textblock: the proposal rewrites text and gets a word diff. */
const textual = (fragment: Fragment) => fragment.childCount === 1 && (fragment.firstChild!.isTextblock || fragment.firstChild!.isText)

/** What Použít writes: inline content when a textblock replaces a span inside one textblock. */
function replacement(editor: Editor, range: MarkdownAssistRange, after: JSONContent): Fragment {
  const doc = editor.state.doc
  const fragment = fragmentOf(editor, after)
  fragment.forEach(node => node.check())
  const $from = doc.resolve(range.from), $to = doc.resolve(range.to)
  if ($from.parent.isTextblock && $from.sameParent($to) && fragment.childCount === 1 && fragment.firstChild!.isTextblock) return fragment.firstChild!.content
  return fragment
}

/** The rectangle of the range for anchoring (its last line, from the block's left edge). */
function rangeRect(editor: Editor, range: MarkdownAssistRange): DOMRect {
  const view = editor.view, doc = view.dom.ownerDocument
  try {
    const start = view.domAtPos(range.from), end = view.domAtPos(range.to)
    const dom = doc.createRange(); dom.setStart(start.node, start.offset); dom.setEnd(end.node, end.offset)
    const rect = dom.getBoundingClientRect?.()
    if (rect && (rect.width || rect.height)) return rect
  } catch { /* fall back to the editor */ }
  return view.dom.getBoundingClientRect()
}

function renderInto(container: HTMLElement, editor: Editor, fragment: Fragment): void {
  container.append(DOMSerializer.fromSchema(editor.schema).serializeFragment(fragment, { document: container.ownerDocument }))
}

/**
 * Anchored before/after preview of a range: Použít / Upravit před použitím / Zrušit. One panel per editor
 * (the shared popover registry); Escape, a click outside, readonly and `signal` cancel. Použít writes one
 * separate undo step; a range that changed meanwhile is never overwritten („Návrh už neodpovídá textu.“).
 */
export function openMarkdownProposalPanel(options: MarkdownProposalOptions): Promise<MarkdownProposalResult> {
  const { editor } = options
  const strings = assistStrings(options.strings)
  return new Promise(resolve => {
    let settled = false
    const finish = (result: MarkdownProposalResult) => { if (!settled) { settled = true; resolve(result) } }
    const size = editor.isDestroyed ? -1 : editor.state.doc.content.size
    if (editor.isDestroyed || !editor.isEditable || options.signal?.aborted || options.available?.() === false || options.range.from < 0 || options.range.to > size || options.range.from > options.range.to) {
      finish('cancelled'); return
    }
    const view = editor.view
    const doc = view.dom.ownerDocument
    let range: MarkdownAssistRange = { ...options.range }
    let snapshot = editor.state.doc.slice(range.from, range.to)
    const pending = new AbortController()
    let vote: 'up' | 'down' | undefined
    let proposalBefore = options.before
    let proposalAfter = options.after
    let proposalSummary = options.summary
    let stale = false
    let writing = false
    // An invisible anchor in the editor's scroll container that measures the range: the panel follows scrolling.
    const anchor = doc.createElement('span')
    anchor.className = 'md-assist-anchor'
    anchor.setAttribute('aria-hidden', 'true')
    anchor.getBoundingClientRect = () => rangeRect(editor, range)
    ;(view.dom.parentElement ?? doc.body).append(anchor)

    let markStale = () => { stale = true }
    const track = ({ transaction, appendedTransactions }: { transaction: Transaction; appendedTransactions: Transaction[] }) => {
      if (writing) return
      const all = [transaction, ...appendedTransactions]
      if (!all.some(item => item.docChanged)) return
      const mapping = new Mapping(); all.forEach(item => mapping.appendMapping(item.mapping))
      const mapped = mapRange(mapping, range)
      if (mapped) { range = mapped; if (!editor.state.doc.slice(mapped.from, mapped.to).eq(snapshot)) markStale() }
      else markStale()
    }
    editor.on('transaction', track)
    let rendered = false
    const motion = () => assistMotion(view.dom, options.reducedMotion)

    openMarkdownContextPopover({
      editor, anchor, title: options.title, variant: 'panel', trackSelection: false, signal: options.signal, className: 'md-assist-proposal-popover',
      render(container, close) {
        rendered = true
        copyAssistTheme(view.dom, container.closest<HTMLElement>('.md-context-popover') ?? container)
        const panel = doc.createElement('div')
        panel.className = 'md-assist-proposal'
        panel.setAttribute('data-md-assist-proposal', '')
        // „Návrh, zatím neuložený“: the dashed outline is an SVG rect, so it can solidify on Použít.
        const dash = doc.createElementNS(SVG, 'svg')
        dash.setAttribute('class', 'md-assist-dash'); dash.setAttribute('aria-hidden', 'true')
        const rect = doc.createElementNS(SVG, 'rect')
        for (const [name, value] of [['x', '.5'], ['y', '.5'], ['rx', '11.5'], ['ry', '11.5']]) rect.setAttribute(name!, value!)
        rect.style.strokeDasharray = '6 5'
        dash.append(rect)
        const header = doc.createElement('div'); header.className = 'md-assist-proposal-header'
        const title = doc.createElement('b'); title.textContent = options.title
        header.append(title)
        if (options.ai) { const tag = doc.createElement('span'); tag.className = 'md-assist-ai-tag'; tag.textContent = strings.aiTag; header.append(tag) }

        const renderReady = () => {
        const diff = doc.createElement('div'); diff.className = 'md-assist-diff'
        const label = (text: string) => { const element = doc.createElement('span'); element.className = 'md-assist-diff-label'; element.textContent = text; return element }
        const before = doc.createElement('div'); before.className = 'md-assist-before'
        const after = doc.createElement('div'); after.className = 'md-assist-after'
        let afterFragment: Fragment
        try { afterFragment = fragmentOf(editor, proposalAfter!) } catch { afterFragment = Fragment.empty }
        let beforeFragment: Fragment | undefined
        if (typeof proposalBefore !== 'string') { try { beforeFragment = fragmentOf(editor, proposalBefore as JSONContent) } catch { beforeFragment = undefined } }
        const beforeText = typeof proposalBefore === 'string' ? proposalBefore as string : beforeFragment ? textOf(beforeFragment) : ''
        const afterText = textOf(afterFragment)
        let removed: number, added: number
        if (textual(afterFragment) && (!beforeFragment || textual(beforeFragment))) {
          const parts = diffWords(beforeText, afterText)
          for (const part of parts) {
            if (part.kind !== 'added') before.append(part.kind === 'removed' ? Object.assign(doc.createElement('del'), { textContent: part.text }) : part.text)
            if (part.kind !== 'removed') after.append(part.kind === 'added' ? Object.assign(doc.createElement('ins'), { textContent: part.text }) : part.text)
          }
          removed = countWords(parts.filter(part => part.kind === 'removed').map(part => part.text).join(' '))
          added = countWords(parts.filter(part => part.kind === 'added').map(part => part.text).join(' '))
        } else {
          // Structure changes (a table, a list): the old content struck through, the new one rendered.
          const removedPart = doc.createElement('del'); removedPart.className = 'md-assist-block'
          if (beforeFragment) renderInto(removedPart, editor, beforeFragment); else removedPart.textContent = beforeText
          before.append(removedPart)
          const addedPart = doc.createElement('ins'); addedPart.className = 'md-assist-block'
          renderInto(addedPart, editor, afterFragment)
          after.append(addedPart)
          removed = countWords(beforeText); added = countWords(afterText)
        }
        diff.append(label(strings.before), before, label(strings.after), after)
        const summary = doc.createElement('p'); summary.className = 'md-assist-summary'
        summary.textContent = proposalSummary ?? strings.summary(removed, added)
        const staleNote = doc.createElement('p'); staleNote.className = 'md-assist-proposal-stale'; staleNote.setAttribute('role', 'status')
        const actions = doc.createElement('div'); actions.className = 'md-assist-proposal-actions'
        const button = (text: string, primary: boolean, run: () => void) => {
          const element = doc.createElement('button')
          element.type = 'button'; element.className = primary ? 'md-btn-primary' : 'md-btn'
          element.textContent = text
          element.addEventListener('click', run)
          actions.append(element)
          return element
        }
        const writers: HTMLButtonElement[] = []
        const commit = (result: 'applied' | 'edited') => {
          if (stale || writing || !editor.isEditable || options.available?.() === false) return
          writing = true
          let mapping: Mapping | undefined
          try {
            if (options.write) {
              const before = editor.state.doc
              const recorder = new Mapping()
              const record = ({ transaction }: { transaction: Transaction }) => { if (transaction.docChanged) recorder.appendMapping(transaction.mapping) }
              editor.on('transaction', record)
              view.dispatch(closeMarkdownHistory(editor.state.tr).setMeta('addToHistory', false))
              let written = false
              try { written = options.write(range) === true } finally {
                view.dispatch(closeMarkdownHistory(editor.state.tr).setMeta('addToHistory', false))
                editor.off('transaction', record)
              }
              if (!written || editor.state.doc.eq(before)) { writing = false; return }
              mapping = recorder
            } else {
              const transaction = editor.state.tr.replaceWith(range.from, range.to, replacement(editor, range, proposalAfter!))
              mapping = transaction.mapping
              dispatchMarkdownHistoryOperation(editor, transaction)
            }
          } catch { writing = false; return }
          const changed = changedRange(mapping)
          if (result === 'edited' && changed) {
            const current = editor.state.doc
            const selection = TextSelection.between(current.resolve(Math.max(0, changed.from)), current.resolve(Math.min(current.content.size, changed.to)))
            view.dispatch(editor.state.tr.setSelection(selection).setMeta('addToHistory', false))
          }
          // Použít: the dashes join (solidify) while the panel folds into the text, then the change glows.
          panel.inert = true
          const current = motion()
          if (changed) glowRange(editor, changed, current)
          const popover = container.closest<HTMLElement>('.md-context-popover') ?? panel
          void Promise.all([finished(solidify(rect, current)), finished(collapse(popover, current.duration('solidify'), current))]).then(() => {
            finish(result)
            close()
            if (result === 'edited' && changed && !editor.isDestroyed) view.focus()
          })
        }
        writers.push(button(strings.apply, true, () => commit('applied')))
        if (options.allowEdit) writers.push(button(strings.edit, false, () => commit('edited')))
        if (options.load) button(strings.retry, false, () => { void load() })
        button(strings.discard, false, () => close())
        markStale = () => {
          stale = true
          staleNote.textContent = strings.stale
          for (const writer of writers) writer.disabled = true
        }
        // The pending Zrušit goes with the old content: a keyboard that was in the panel goes on at Použít, not on the page.
        const owned = panel.contains(doc.activeElement)
        panel.replaceChildren(dash, header, diff, summary, staleNote, actions)
        if (owned) (writers[0] ?? actions.querySelector('button'))?.focus()

        if (options.onFeedback) {
          const feedback = doc.createElement('div'); feedback.className = 'md-assist-feedback'; feedback.setAttribute('role', 'group'); feedback.setAttribute('aria-label', strings.proposalLabel)
          const choices: HTMLButtonElement[] = []
          for (const value of ['up', 'down'] as const) {
            const choice = doc.createElement('button'); choice.type = 'button'; choice.className = 'md-btn'; choice.setAttribute('aria-label', value === 'up' ? strings.helpful : strings.unhelpful); choice.setAttribute('aria-pressed', String(vote === value)); choice.append(createIconElement(doc, value === 'up' ? 'thumbs-up' : 'thumbs-down', 14)); choices.push(choice)
            choice.onclick = () => { if (vote === value) return; vote = value; choices.forEach((item, index) => item.setAttribute('aria-pressed', String((index === 0 ? 'up' : 'down') === vote))); try { options.onFeedback?.(value) } catch { /* host feedback is best effort */ } }
            feedback.append(choice)
          }
          panel.append(feedback)
        }
        if (stale) markStale()
        // Rozdíl: deletions are struck from the left, insertions grow, 40 ms apart.
        const current = motion()
        panel.querySelectorAll('del').forEach((element, index) => strike(element, current.scaled(index * DIFF_STAGGER), current))
        panel.querySelectorAll('ins').forEach((element, index) => grow(element, current.scaled(index * DIFF_STAGGER), current))
        }
        container.append(panel)
        const load = async () => {
          if (pending.signal.aborted || editor.isDestroyed || options.available?.() === false || range.from < 0 || range.to > editor.state.doc.content.size) return
          stale = false; snapshot = editor.state.doc.slice(range.from, range.to)
          const status = doc.createElement('p'); status.className = 'md-assist-summary'; status.setAttribute('role', 'status'); status.textContent = strings.loading
          const cancel = doc.createElement('button'); cancel.type = 'button'; cancel.className = 'md-btn'; cancel.textContent = strings.cancel; cancel.onclick = close
          const owned = panel.contains(doc.activeElement)
          panel.replaceChildren(dash, header, status, cancel)
          if (owned) cancel.focus()
          markStale = () => { stale = true; status.textContent = strings.stale }
          try {
            const result = await options.load!(pending.signal, { ...range })
            if (pending.signal.aborted || options.available?.() === false) return
            proposalAfter = result.after; proposalBefore = result.before ?? proposalBefore; proposalSummary = result.summary
            // Validate before offering any write, including schema content and attributes.
            const fragment = fragmentOf(editor, proposalAfter); fragment.forEach(node => node.check())
            renderReady()
          } catch {
            if (pending.signal.aborted || stale) return
            status.textContent = strings.loadFailed
            const retry = doc.createElement('button'); retry.type = 'button'; retry.className = 'md-btn'; retry.textContent = strings.retry; retry.onclick = () => { void load() }
            panel.insertBefore(retry, cancel)
          }
        }
        if (options.load) void load()
        else if (proposalAfter) renderReady()
        else close()
        return () => {
          pending.abort()
          editor.off('transaction', track)
          anchor.remove()
          finish('cancelled')
        }
      },
    })
    if (!rendered) { editor.off('transaction', track); anchor.remove(); finish('cancelled') }
  })
}

/** The afterglow of a written proposal: the textblocks it touched glow for 1.6 s. */
function glowRange(editor: Editor, range: MarkdownAssistRange, motion: ReturnType<typeof assistMotion>): void {
  const view = editor.view, doc = editor.state.doc
  const seen = new Set<ProseMirrorNode>()
  doc.nodesBetween(Math.max(0, range.from), Math.min(doc.content.size, range.to), (node, pos) => {
    if (!node.isBlock || seen.has(node)) return true
    if (node.isTextblock || node.type.name === 'table' || node.isAtom) {
      seen.add(node)
      const element = view.nodeDOM(pos)
      if (element instanceof Element) glow(element, motion)
      return false
    }
    return true
  })
}
