// Protected blocks in a Markdown source editor (docs/page-source-editor.md, phase 3).
//
// A block the rich document cannot express in Markdown is shown as a fence at column 0:
//
//     ```md-protected b3
//     Chráněný blok: Graf „Tržby 2025“
//     …the block's text, to read and search (Ctrl+F finds it)…
//     ```
//
// It may be deleted or moved only as a whole. Every user edit path of Monaco (typing, IME, paste, cut,
// drop, multi-cursor, move lines, replace-all, snippets, `executeEdits`) ends in the model's
// `pushEditOperations`, so this module wraps it on this one model instance: an edit that touches no
// block passes untouched; otherwise the affected text is simulated and the edit is allowed only if each
// touched block's exact text (trailing blanks aside, like the session) still stands as complete lines,
// or the block is completely gone. Undo/redo bypass `pushEditOperations` (they restore earlier, valid
// states); a re-scan after every content change re-protects every known id whose body is intact again.
//
// The pure guard below has no Monaco dependency and is unit-tested; `attachProtectedBlocks` receives the
// Monaco namespace from the lazy editor chunk (type-only import here). The fence syntax (shared with the
// headless session, src/source/protectedFence.ts) is handed in by `mountSourceEditor` in `./source`: a runtime
// import from here would pull that module into the Monaco chunk, and the session along with it.
import type * as Monaco from 'monaco-editor/esm/vs/editor/editor.api.js'
import { isMacintosh } from 'monaco-editor/esm/vs/base/common/platform.js'
import type { MarkdownSourceProtectedRange, SourceEditorLabels, SourceProtectedBlock } from '../index'
import type { ProtectedFenceSyntax } from '../protectedFence'
import { registerMonacoStyle } from './styles'

/** A protected block found in the current text; lines are 1-based and include both fences. */
type ProtectedRange = Omit<MarkdownSourceProtectedRange, 'text'>

/** The exact text a known block must keep, line by line. */
export interface KnownBlock {
  readonly id: string
  readonly label: string
  readonly lines: readonly string[]
}

/** One replacement in Monaco coordinates (1-based lines and columns, end exclusive). */
export interface SourceEdit {
  readonly startLine: number
  readonly startColumn: number
  readonly endLine: number
  readonly endColumn: number
  readonly text: string | null
}

export type GuardVerdict = { readonly allowed: true } | { readonly allowed: false; readonly block: ProtectedRange }

interface Fence { readonly id: string; readonly startLine: number; readonly endLine: number }

/** Every complete `md-protected` fence in the text, in order. */
function scanFences(lines: readonly string[], fence: ProtectedFenceSyntax): Fence[] {
  const fences: Fence[] = []
  for (let index = 0; index < lines.length; index++) {
    const match = fence.open.exec(lines[index]!)
    if (!match) continue
    let end = index + 1
    while (end < lines.length && !fence.close.test(lines[end]!) && !fence.open.test(lines[end]!)) end++
    if (end >= lines.length || !fence.close.test(lines[end]!)) continue
    fences.push({ id: match[1]!, startLine: index + 1, endLine: end + 1 })
    index = end
  }
  return fences
}

const sameLines = (lines: readonly string[], start: number, expected: readonly string[], fence: ProtectedFenceSyntax) => {
  if (start + expected.length > lines.length) return false
  for (let offset = 0; offset < expected.length; offset++) if (!fence.sameLine(lines[start + offset]!, expected[offset]!)) return false
  return true
}

/**
 * Resolves the blocks a host asked to protect against the text: by their line hint when it still points
 * at the fence, else by the first fence with that id, else (absent from the text) by the canonical
 * form built from the label and the block's text, so the block is protected once it is pasted back.
 */
function resolveKnownBlocks(lines: readonly string[], blocks: readonly SourceProtectedBlock[], syntax: ProtectedFenceSyntax): Map<string, KnownBlock> {
  const known = new Map<string, KnownBlock>()
  const fences = scanFences(lines, syntax)
  for (const block of blocks) {
    if (!block.id || known.has(block.id)) continue
    const hinted = block.startLine && block.endLine
      ? fences.find(fence => fence.id === block.id && fence.startLine === block.startLine && fence.endLine === block.endLine)
      : undefined
    const fence = hinted ?? fences.find(item => item.id === block.id)
    const blockLines = fence
      ? lines.slice(fence.startLine - 1, fence.endLine)
      : block.label !== undefined ? syntax.lines(block.id, block.label, block.text) : undefined
    if (!blockLines) continue
    known.set(block.id, { id: block.id, label: block.label ?? blockLines.slice(1, -1).join(' '), lines: blockLines })
  }
  return known
}

/** The known blocks whose exact text stands in `lines`. A duplicated intact block is protected twice. */
function findProtectedRanges(lines: readonly string[], known: ReadonlyMap<string, KnownBlock>, syntax: ProtectedFenceSyntax): ProtectedRange[] {
  if (!known.size) return []
  const ranges: ProtectedRange[] = []
  for (const fence of scanFences(lines, syntax)) {
    const block = known.get(fence.id)
    if (block && fence.endLine - fence.startLine + 1 === block.lines.length && sameLines(lines, fence.startLine - 1, block.lines, syntax)) {
      ranges.push({ id: block.id, label: block.label, startLine: fence.startLine, endLine: fence.endLine })
    }
  }
  return ranges
}

const compare = (lineA: number, columnA: number, lineB: number, columnB: number) => lineA - lineB || columnA - columnB

/**
 * Whether an edit can change the block's text: it reaches into its lines, or touches either edge (an
 * insertion at the start of the opening fence or after the closing one; joining a neighbour line onto a
 * fence). Edits that end before the newline preceding the block, or start after the newline following
 * it, cannot.
 */
export function touchesRange(edit: SourceEdit, range: ProtectedRange, endLineLength: number): boolean {
  return compare(edit.endLine, edit.endColumn, range.startLine, 1) >= 0
    && compare(edit.startLine, edit.startColumn, range.endLine, endLineLength + 1) <= 0
}

/** Applies non-overlapping edits (Monaco's contract for one `pushEditOperations` call) to a copy. */
export function applySourceEdits(lines: readonly string[], edits: readonly SourceEdit[]): string[] {
  const starts: number[] = []
  let offset = 0
  for (const line of lines) { starts.push(offset); offset += line.length + 1 }
  const text = lines.join('\n')
  const position = (line: number, column: number) => {
    const index = Math.min(Math.max(line, 1), lines.length) - 1
    return Math.min(starts[index]! + Math.max(column, 1) - 1, starts[index]! + lines[index]!.length)
  }
  const ordered = edits.map((edit, index) => ({ index, start: position(edit.startLine, edit.startColumn), end: position(edit.endLine, edit.endColumn), text: (edit.text ?? '').replace(/\r\n?/g, '\n') }))
    .sort((a, b) => b.start - a.start || b.index - a.index)
  let result = text
  for (const edit of ordered) result = result.slice(0, edit.start) + edit.text + result.slice(edit.end)
  return result.split('\n')
}

/** Intact occurrences of the block and opening fences of its id whose body was damaged. */
function census(lines: readonly string[], block: KnownBlock, fence: ProtectedFenceSyntax): { intact: number; broken: number } {
  let intact = 0, broken = 0
  for (let index = 0; index < lines.length; index++) {
    const match = fence.open.exec(lines[index]!)
    if (!match || match[1] !== block.id) continue
    if (sameLines(lines, index, block.lines, fence)) { intact++; index += block.lines.length - 1 } else broken++
  }
  return { intact, broken }
}

/** Whether the deleted/replaced ranges together cover every character of the block. */
function covered(edits: readonly SourceEdit[], range: ProtectedRange, endLineLength: number): boolean {
  const spans = edits.filter(edit => compare(edit.startLine, edit.startColumn, edit.endLine, edit.endColumn) < 0)
    .map(edit => [edit.startLine, edit.startColumn, edit.endLine, edit.endColumn] as const)
    .sort((a, b) => compare(a[0], a[1], b[0], b[1]))
  let reachLine = range.startLine, reachColumn = 1
  for (const [startLine, startColumn, endLine, endColumn] of spans) {
    if (compare(startLine, startColumn, reachLine, reachColumn) > 0) break
    if (compare(endLine, endColumn, reachLine, reachColumn) > 0) { reachLine = endLine; reachColumn = endColumn }
  }
  return compare(reachLine, reachColumn, range.endLine, endLineLength + 1) >= 0
}

/**
 * Decides one `pushEditOperations` call. Edits touching no protected range are always allowed. Otherwise
 * the whole call is simulated; for every touched block it is allowed only when no opening fence of that
 * id is left with a damaged body and the block either still stands as complete lines (moved, re-indented
 * around, pasted over by itself) or was covered completely by the edit (deleted, cut, pasted over).
 */
function checkProtectedEdit(lines: readonly string[], known: ReadonlyMap<string, KnownBlock>, ranges: readonly ProtectedRange[], edits: readonly SourceEdit[], fence: ProtectedFenceSyntax): GuardVerdict {
  const touched = ranges.filter(range => edits.some(edit => touchesRange(edit, range, lines[range.endLine - 1]?.length ?? 0)))
  if (!touched.length) return { allowed: true }
  const after = applySourceEdits(lines, edits)
  for (const range of touched) {
    const block = known.get(range.id)
    if (!block) continue
    const before = census(lines, block, fence)
    const now = census(after, block, fence)
    if (now.broken > before.broken) return { allowed: false, block: range }
    if (now.intact >= before.intact) continue
    if (covered(edits, range, lines[range.endLine - 1]?.length ?? 0)) continue
    return { allowed: false, block: range }
  }
  return { allowed: true }
}

/** The label without the `Chráněný blok: ` prefix the session writes into the fence. */
const shortLabel = (label: string, fence: ProtectedFenceSyntax) =>
  (label.startsWith(fence.labelPrefix) ? label.slice(fence.labelPrefix.length) : label).trim() || label

/** The guard bound to one fence syntax (the shared `protectedFence`). */
export function createProtectedFenceGuard(fence: ProtectedFenceSyntax) {
  return {
    /** Every complete `md-protected` fence in the text, in order. */
    scanFences: (lines: readonly string[]) => scanFences(lines, fence),
    resolveKnownBlocks: (lines: readonly string[], blocks: readonly SourceProtectedBlock[]) => resolveKnownBlocks(lines, blocks, fence),
    findProtectedRanges: (lines: readonly string[], known: ReadonlyMap<string, KnownBlock>) => findProtectedRanges(lines, known, fence),
    checkProtectedEdit: (lines: readonly string[], known: ReadonlyMap<string, KnownBlock>, ranges: readonly ProtectedRange[], edits: readonly SourceEdit[]) =>
      checkProtectedEdit(lines, known, ranges, edits, fence),
    shortLabel: (label: string) => shortLabel(label, fence),
  }
}

export const PROTECTED_EDIT_REJECTED = 'Chráněný blok nelze upravovat po částech. Vyberte celý blok'
export const SELECT_PROTECTED_BLOCK = 'Vybrat chráněný blok'

registerMonacoStyle(`
.monaco-editor .md-source-protected { background: color-mix(in srgb, var(--accent-soft, rgba(46, 111, 219, .12)) 80%, transparent); }
.monaco-editor .md-source-protected-glyph { color: var(--text-dim, #586574); cursor: pointer; display: flex; align-items: center; justify-content: center; }
.md-source-live { position: absolute; width: 1px; height: 1px; margin: -1px; padding: 0; overflow: hidden; clip: rect(0 0 0 0); clip-path: inset(50%); white-space: nowrap; border: 0; }
`)

export interface ProtectedBlocksHandle {
  /** Replaces the protected set (resolved against the current text); `[]` turns protection off. */
  set(blocks: readonly SourceProtectedBlock[]): void
  ranges(): readonly ProtectedRange[]
  dispose(): void
}

export interface ProtectedBlocksOptions {
  /** The shared fence syntax, passed down from the `./source` entry. */
  fence: ProtectedFenceSyntax
  onRejected?: (block: SourceProtectedBlock) => void
  /** Overrides of the defaults below (from `mountSourceEditor`'s `labels`). */
  labels?: SourceEditorLabels
}

const markdownText = (text: string) => text.replace(/[\\`*_{}[\]()#+\-.!|<>~]/g, character => `\\${character}`)

/** Wires protection into one mounted editor and its model. Returns a handle owned by `editor.ts`. */
export function attachProtectedBlocks(monaco: typeof Monaco, editor: Monaco.editor.IStandaloneCodeEditor, model: Monaco.editor.ITextModel, host: HTMLElement, options: ProtectedBlocksOptions): ProtectedBlocksHandle {
  const doc = host.ownerDocument
  const { checkProtectedEdit, findProtectedRanges, resolveKnownBlocks, shortLabel } = createProtectedFenceGuard(options.fence)
  const labelPrefix = options.fence.labelPrefix
  // The label follows the platform Monaco resolves the `CtrlCmd` keybinding below with.
  const shortcut = isMacintosh ? '⇧⌘B' : 'Ctrl+Shift+B'
  const rejectedMessage = options.labels?.protectedEditRejectedMessage?.(shortcut) ?? `${PROTECTED_EDIT_REJECTED} (${shortcut}) a smažte nebo přesuňte ho vcelku.`
  let known = new Map<string, KnownBlock>()
  let ranges: ProtectedRange[] = []
  let disposed = false
  const decorations = editor.createDecorationsCollection()
  const inBlock = editor.createContextKey<boolean>('atlasInProtectedBlock', false)

  // Live regions next to the editor: an assertive one for rejections, a polite one for the cursor.
  const liveRegion = (role: 'alert' | 'status') => {
    const element = doc.createElement('div')
    element.className = 'md-source-live'
    element.setAttribute('role', role)
    element.setAttribute('aria-live', role === 'alert' ? 'assertive' : 'polite')
    element.setAttribute('aria-atomic', 'true')
    host.append(element)
    return element
  }
  const alertRegion = liveRegion('alert')
  const statusRegion = liveRegion('status')
  const timers = new Set<ReturnType<typeof setTimeout>>()
  const later = (callback: () => void, delay: number) => {
    const timer = setTimeout(() => { timers.delete(timer); if (!disposed) callback() }, delay)
    timers.add(timer)
    return timer
  }
  // Clearing first makes screen readers repeat an identical message.
  const announce = (region: HTMLElement, text: string) => { region.textContent = ''; later(() => { region.textContent = text }, 30) }

  const rangeAt = (line: number | undefined) => line === undefined ? undefined : ranges.find(range => line >= range.startLine && line <= range.endLine)
  const publicBlock = (range: ProtectedRange): SourceProtectedBlock => ({ id: range.id, label: range.label, startLine: range.startLine, endLine: range.endLine })

  let cursorBlock: string | undefined
  let cursorTimer: ReturnType<typeof setTimeout> | undefined
  const updateCursor = () => {
    const block = rangeAt(editor.getPosition()?.lineNumber)
    inBlock.set(Boolean(block))
    const key = block && `${block.id}:${block.startLine}`
    if (key === cursorBlock) return
    cursorBlock = key
    if (cursorTimer) { clearTimeout(cursorTimer); timers.delete(cursorTimer) }
    if (block) cursorTimer = later(() => announce(statusRegion, `${labelPrefix}${shortLabel(block.label)}, pouze pro čtení`), 250)
  }

  let scannedVersion = -1
  const rescan = () => {
    scannedVersion = model.getVersionId()
    // Without known blocks there is nothing to find: skip copying every line on each keystroke.
    ranges = known.size ? findProtectedRanges(model.getLinesContent(), known) : []
    decorations.set(ranges.map(range => {
      const hover = { value: options.labels?.protectedBlockHover?.(markdownText(shortLabel(range.label)), markdownText(shortcut)) ?? `**Chráněný blok** · ${markdownText(shortLabel(range.label))}\n\nLze ho jen celý smazat nebo přesunout. ${markdownText(shortcut)} vybere celý blok.` }
      return {
        range: new monaco.Range(range.startLine, 1, range.endLine, model.getLineMaxColumn(range.endLine)),
        options: {
          description: 'atlas-protected-block',
          stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
          isWholeLine: true,
          className: 'md-source-protected',
          hoverMessage: hover,
          glyphMarginClassName: 'codicon codicon-lock md-source-protected-glyph',
          glyphMarginHoverMessage: hover,
          overviewRuler: { color: 'rgba(128, 138, 156, 0.7)', position: monaco.editor.OverviewRulerLane.Left },
        },
      }
    }))
    updateCursor()
  }

  let lastRejection = { key: '', at: 0 }
  const reject = (range: ProtectedRange) => {
    options.onRejected?.(publicBlock(range))
    const now = Date.now()
    const key = `${range.id}:${range.startLine}`
    // Holding a key inside a block must not flood the screen reader.
    if (key !== lastRejection.key || now - lastRejection.at > 1500) announce(alertRegion, rejectedMessage)
    lastRejection = { key, at: now }
    const position = editor.getPosition()
    const messages = editor.getContribution('editor.contrib.messageController') as { showMessage?(message: string, position: Monaco.IPosition): void } | null
    if (position) messages?.showMessage?.(rejectedMessage, position)
  }

  // Monaco's internal callers pass a fourth `group` argument (undo grouping); forward every argument.
  const push = model.pushEditOperations
  const guarded = function (this: unknown, ...args: unknown[]) {
    const operations = args[1] as Monaco.editor.IIdentifiedSingleEditOperation[] | undefined
    // Content events can be deferred inside a compound operation; never judge by stale ranges.
    if (!disposed && scannedVersion !== model.getVersionId()) rescan()
    if (!disposed && ranges.length && operations?.length) {
      const edits = operations.map(operation => ({
        startLine: operation.range.startLineNumber, startColumn: operation.range.startColumn,
        endLine: operation.range.endLineNumber, endColumn: operation.range.endColumn, text: operation.text,
      }))
      // Cheap pre-check per keystroke; the full simulation only runs for edits at a block.
      if (ranges.some(range => edits.some(edit => touchesRange(edit, range, model.getLineMaxColumn(range.endLine) - 1)))) {
        const verdict = checkProtectedEdit(model.getLinesContent(), known, ranges, edits)
        if (!verdict.allowed) { reject(verdict.block); return null }
      }
    }
    return (push as (...values: unknown[]) => Monaco.Selection[] | null).apply(model, args)
  } as typeof push
  model.pushEditOperations = guarded

  const select = (range: ProtectedRange) => {
    // Including the following newline makes cut/paste move whole lines.
    const selection = range.endLine < model.getLineCount()
      ? new monaco.Selection(range.startLine, 1, range.endLine + 1, 1)
      : new monaco.Selection(range.startLine, 1, range.endLine, model.getLineMaxColumn(range.endLine))
    editor.setSelection(selection)
    editor.revealRangeInCenterIfOutsideViewport(selection)
    editor.focus()
  }

  const subscriptions: Monaco.IDisposable[] = [
    model.onDidChangeContent(rescan),
    editor.onDidChangeCursorPosition(updateCursor),
    editor.onMouseDown(event => {
      if (event.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN) return
      const block = rangeAt(event.target.position?.lineNumber)
      if (!block) return
      event.event.preventDefault()
      select(block)
    }),
    editor.addAction({
      id: 'atlas.source.selectProtectedBlock',
      label: options.labels?.selectProtectedBlockLabel ?? SELECT_PROTECTED_BLOCK,
      keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyB],
      precondition: 'atlasInProtectedBlock',
      contextMenuGroupId: '9_cutcopypaste',
      contextMenuOrder: 0,
      run: () => { const block = rangeAt(editor.getPosition()?.lineNumber); if (block) select(block) },
    }),
  ]

  return {
    set(blocks) {
      if (disposed) return
      known = resolveKnownBlocks(model.getLinesContent(), blocks)
      editor.updateOptions({ glyphMargin: known.size > 0 })
      rescan()
    },
    ranges: () => ranges,
    dispose() {
      if (disposed) return
      disposed = true
      for (const timer of timers) clearTimeout(timer)
      timers.clear()
      subscriptions.forEach(subscription => subscription.dispose())
      decorations.clear()
      if (model.pushEditOperations === guarded) model.pushEditOperations = push
      alertRegion.remove(); statusRegion.remove()
    },
  }
}
