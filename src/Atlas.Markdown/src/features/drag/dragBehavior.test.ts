// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Editor, Node, type AnyExtension } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { NodeRangeSelection } from '@tiptap/extension-node-range'
import { NodeSelection } from '@tiptap/pm/state'
import { blockDragRange, blockIndexAt, commitBlockDrop, measureBlocks, resolveBlockDrop } from './blockDrop'
import { blockRangeForHandle, collectHoverSectionRanges, selectBlockRange } from './blockSelection'
import { DragPreview } from '../../extensions/DragPreview'
import { moveBlock, prepareBlockDrag } from './blockCommands'
import { moveWithCaret } from '../../extensions/KeyboardFirst'
import { createBlockSelectionUI } from './selectionUI'
import { startDragSourceMarker } from './sourceSession'
import { updateEdgeScroll, stopEdgeScroll } from './edgeScroll'

const editors: Editor[] = []
const frames = new Map<number, FrameRequestCallback>()
let frameId = 0
beforeEach(() => {
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { frames.set(++frameId, callback); return frameId })
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(id => { frames.delete(id) })
})
afterEach(() => {
  editors.splice(0).forEach(editor => editor.destroy())
  frames.clear()
  vi.restoreAllMocks()
  document.body.replaceChildren()
})
function mount(content = '<h1>First</h1><p>First content</p><h1>Second</h1><p>Second content</p>', extra: AnyExtension[] = []) {
  const element = document.createElement('div')
  document.body.appendChild(element)
  const editor = new Editor({ element, content, extensions: [StarterKit, DragPreview, ...extra] })
  editors.push(editor)
  return editor
}

describe('drag document behavior and native gesture lifetime', () => {
  it('selects an atomic component as a node, but extends from it as a block range', () => {
    const editor = mount('<hr><p>Next</p>')
    selectBlockRange(editor, 0)
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    selectBlockRange(editor, 1, true)
    expect(editor.state.selection).toBeInstanceOf(NodeRangeSelection)
    expect(editor.state.selection.from).toBe(0)
    expect(editor.state.selection.to).toBe(7)
    selectBlockRange(editor, 0, false, true, true)
    expect(editor.state.selection).toBeInstanceOf(NodeRangeSelection)
    expect(editor.commands.undo()).toBe(false)
  })
  it('keyboard reorder keeps the same selected blocks as hover and pointer dragging', () => {
    const editor = mount('<p>One</p><p>Two</p><p>Three</p><p>Four</p>')
    const before = editor.getJSON()
    selectBlockRange(editor, 0)
    selectBlockRange(editor, 5, true)
    expect(blockRangeForHandle(editor.state, 5)).toEqual({ from: 0, to: 10 })
    expect(collectHoverSectionRanges(editor, 5, editor.state.doc.nodeAt(5)!)).toEqual([{ from: 0, to: 5 }, { from: 5, to: 10 }])
    expect(moveBlock(editor, 5, 1)).toBe(7)
    expect(editor.state.doc.content.content.map(node => node.textContent)).toEqual(['Three', 'One', 'Two', 'Four'])
    expect(moveBlock(editor, 7, 1)).toBe(13)
    expect(editor.state.doc.content.content.map(node => node.textContent)).toEqual(['Three', 'Four', 'One', 'Two'])
    editor.commands.undo()
    expect(editor.state.doc.content.content.map(node => node.textContent)).toEqual(['Three', 'One', 'Two', 'Four'])
    editor.commands.undo()
    expect(editor.getJSON()).toEqual(before)
  })

  it('switching to readonly cancels the native slice and removes the source markers', async () => {
    const editor = mount('<p>One</p><p>Two</p>')
    const range = prepareBlockDrag(editor, 0)!
    const event = new Event('dragstart', { bubbles: true }) as DragEvent
    Object.defineProperty(event, 'dataTransfer', { value: { setDragImage: vi.fn() } })
    startDragSourceMarker(editor.view, [editor.view.dom.firstElementChild as HTMLElement], event, range)
    document.dispatchEvent(event)
    expect(editor.view.dragging).not.toBeNull()
    editor.setEditable(false)
    await Promise.resolve()
    expect(editor.view.dragging).toBeNull()
    expect(blockDragRange(editor.view)).toBeNull()
    expect(editor.view.dom.querySelector('[data-dragging-source]')).toBeNull()
    expect(document.querySelector('.md-drag-preview-clone')).toBeNull()
  })

  it('moves a heading and its content together and undoes the move in one step', () => {
    const editor = mount()
    const before = editor.getJSON()
    expect(moveBlock(editor, 0, 1)).toBeGreaterThan(0)
    expect(editor.state.doc.content.content.map(node => node.textContent)).toEqual(['Second', 'Second content', 'First', 'First content'])
    editor.commands.undo()
    expect(editor.getJSON()).toEqual(before)
  })

  // Owner 2026-11-04 („sekce se chovají stejně“, Notion's toggle headings): a heading moves with its body, up to the next heading of the same
  // or a higher level, by the grip's drop and by the keyboard; the blocks around it stay in their order.
  const sections = '<p>Intro</p><h2>A</h2><p>a body</p><h3>A.1</h3><p>a.1 body</p><h2>B</h2><p>b body</p><h1>C</h1><p>c body</p>'
  const texts = (editor: Editor) => editor.state.doc.content.content.map(node => node.textContent)

  it('dropping a heading by its grip moves its whole section, sub-headings included, and nothing else', () => {
    const editor = mount(sections)
    const from = editor.state.doc.firstChild!.nodeSize
    const range = prepareBlockDrag(editor, from)!
    expect(editor.state.doc.slice(range.from, range.to).content.childCount).toBe(4)
    startDragSourceMarker(editor.view, [editor.view.nodeDOM(from) as HTMLElement], new Event('dragstart') as DragEvent, range)
    let beforeC = 0
    editor.state.doc.forEach((node, pos) => { if (node.textContent === 'C') beforeC = pos })
    const landed = commitBlockDrop(editor, beforeC)!
    expect(texts(editor)).toEqual(['Intro', 'B', 'b body', 'A', 'a body', 'A.1', 'a.1 body', 'C', 'c body'])
    expect(editor.state.doc.slice(landed.from, landed.to).content.childCount).toBe(4)
    // The caret at the moved heading (no hidden block selection), one undo step back.
    expect(editor.state.selection.empty && editor.state.selection.$from.parent.textContent).toBe('A')
    expect(blockDragRange(editor.view)).toBeNull()
    editor.commands.undo()
    expect(texts(editor)).toEqual(['Intro', 'A', 'a body', 'A.1', 'a.1 body', 'B', 'b body', 'C', 'c body'])
  })

  it('the keyboard move (Mod+Shift+↓ / ↑) with the caret in a heading moves its section over the next one, and back', () => {
    const editor = mount(sections)
    const before = texts(editor)
    editor.commands.setTextSelection(editor.state.doc.firstChild!.nodeSize + 2)
    expect(moveWithCaret(editor, 1)).toBe(true)
    expect(texts(editor)).toEqual(['Intro', 'B', 'b body', 'A', 'a body', 'A.1', 'a.1 body', 'C', 'c body'])
    expect(editor.state.selection.$from.parent.textContent).toBe('A')
    expect(moveWithCaret(editor, -1)).toBe(true)
    expect(texts(editor)).toEqual(before)
  })

  it('frames only the blocks that show: a block a folded heading hides (no box) does not stretch the frame to the window edge', () => {
    const editor = mount('<h2>A</h2><p>folded away</p><p>shown</p><h2>B</h2>')
    editor.view.dom.parentElement!.classList.add('md-editor')
    const boxes = [new DOMRect(100, 50, 400, 20), new DOMRect(0, 0, 0, 0), new DOMRect(100, 80, 400, 20)]
    ;[...editor.view.dom.children].slice(0, 3).forEach((element, index) => { element.getBoundingClientRect = () => boxes[index]! })
    const ui = createBlockSelectionUI(editor, { grip: () => null, target: () => {} })
    selectBlockRange(editor, 0)
    editor.view.focus()
    ui.focus()
    const frame = document.querySelector<HTMLElement>('.md-block-selection-frame')!
    expect([frame.dataset.visible, frame.style.left, frame.style.width]).toEqual(['true', '92px', '416px'])
    ui.destroy()
  })

  it('the grip picks up a heading section even when text inside it is selected', () => {
    const editor = mount()
    const range = prepareBlockDrag(editor, 0)!
    expect(editor.state.doc.slice(range.from, range.to).content.childCount).toBe(2)
    editor.commands.setTextSelection({ from: 1, to: 3 })
    expect(prepareBlockDrag(editor, 0)).toEqual(range)
    expect(editor.state.selection).toBeInstanceOf(NodeRangeSelection)
  })

  it('text across blocks cannot silently truncate a heading section; a deliberate single-heading selection can', () => {
    const editor = mount('<h2>Title</h2><p>One</p><p>Two</p><h2>Next</h2>')
    editor.commands.setTextSelection({ from: 1, to: 10 })
    expect(prepareBlockDrag(editor, 0)).toEqual({ from: 0, to: 17 })
    selectBlockRange(editor, 0, false, false)
    expect(prepareBlockDrag(editor, 0)).toEqual({ from: 0, to: 7 })
    selectBlockRange(editor, 0)
    expect(prepareBlockDrag(editor, 0)).toEqual({ from: 0, to: 17 })
  })

  it('repeated Shift clicks extend and shrink around the initial block, like table axis selections', () => {
    const editor = mount('<p>One</p><p>Two</p><p>Three</p><p>Four</p>')
    selectBlockRange(editor, 5)
    selectBlockRange(editor, 17, true)
    expect(blockRangeForHandle(editor.state, 10)).toEqual({ from: 5, to: 23 })
    selectBlockRange(editor, 10, true)
    expect(blockRangeForHandle(editor.state, 5)).toEqual({ from: 5, to: 17 })
    selectBlockRange(editor, 0, true)
    expect(blockRangeForHandle(editor.state, 5)).toEqual({ from: 0, to: 10 })
  })

  it('the floating card includes the actual group content, its line\'s controls as drawn, and no duplicate ids or editable text', () => {
    const editor = mount()
    const range = prepareBlockDrag(editor, 0)!
    // A requirement heading as drawn, outside the editor's DOM (its observer would read the widgets as typed content): its
    // checkbox (checked by property, as the view sets it) and a chip.
    const heading = document.createElement('h1'); heading.id = 'original-heading'; heading.textContent = 'First'
    const check = document.createElement('input'); check.type = 'checkbox'; check.checked = true
    const chip = document.createElement('button'); chip.textContent = 'neřešeno'
    heading.prepend(check); heading.append(chip); document.body.append(heading)
    const blocks = [heading, editor.view.dom.children[1]] as HTMLElement[]
    const event = new Event('dragstart', { bubbles: true }) as DragEvent
    const setDragImage = vi.fn()
    // Configurable, as a browser's prototype getter is.
    Object.defineProperty(event, 'dataTransfer', { value: { setDragImage }, configurable: true })
    startDragSourceMarker(editor.view, blocks, event, range)
    // BlockHandle hides the event's dataTransfer from Tiptap's dragHandler; the session still installs its image with it.
    Object.defineProperty(event, 'dataTransfer', { value: null })
    document.dispatchEvent(event)
    expect(setDragImage).toHaveBeenCalled()
    expect(editor.view.dragging?.move).toBe(true)
    const card = document.querySelector<HTMLElement>('.md-drag-preview-clone')!
    expect(card.textContent).toContain('2 bloky')
    expect(card.textContent).toContain('First content')
    expect(card.textContent).toContain('neřešeno')
    expect(card.querySelector<HTMLInputElement>('input[type=checkbox]')?.checked).toBe(true)
    expect(card.inert).toBe(true)
    expect(card.querySelector('[id],[contenteditable]')).toBeNull()
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    expect(document.querySelector('.md-drag-preview-clone')).toBeNull()
    expect(blockDragRange(editor.view)).toBeNull()
  })

  it('moves a horizontal rule alone, preserving all adjacent content and undo', () => {
    const editor = mount('<p>Before</p><hr><p>After</p>')
    const before = editor.getJSON()
    const from = editor.state.doc.firstChild!.nodeSize
    const range = prepareBlockDrag(editor, from)!
    expect(range).toEqual({ from, to: from + 1 })
    startDragSourceMarker(editor.view, [editor.view.nodeDOM(from) as HTMLElement], new Event('dragstart') as DragEvent, range)
    expect(commitBlockDrop(editor, 0)).toEqual({ from: 0, to: 1 })
    expect(editor.state.doc.content.content.map(node => node.type.name)).toEqual(['horizontalRule', 'paragraph', 'paragraph'])
    editor.commands.undo()
    expect(editor.getJSON()).toEqual(before)
  })

  it('ignores unrelated multi-block selections when another grip is dragged', () => {
    const editor = mount('<p>One</p><p>Two</p><p>Three</p>')
    editor.commands.setTextSelection({ from: 1, to: 7 })
    expect(prepareBlockDrag(editor, 10)).toEqual({ from: 10, to: 17 })
  })

  it('removes the drag image, native listeners and pending frames on unmount', () => {
    const editor = mount()
    const event = new Event('dragstart', { bubbles: true }) as DragEvent
    const setDragImage = vi.fn()
    Object.defineProperty(event, 'dataTransfer', { value: { setDragImage } })
    startDragSourceMarker(editor.view, [editor.view.dom.firstElementChild as HTMLElement], event, { from: 0, to: editor.state.doc.firstChild!.nodeSize })
    expect(document.querySelector('.md-drag-preview-clone')).not.toBeNull()
    editor.destroy()
    document.dispatchEvent(event)
    expect(setDragImage).not.toHaveBeenCalled()
    expect(document.querySelector('.md-drag-preview-clone')).toBeNull()
    expect(frames.size).toBe(0)
  })

  /** jsdom lays nothing out: each top-level block gets a 30 px box every 40 px, or none when `hidden`. */
  function layOut(editor: Editor, hidden: ReadonlySet<number> = new Set()) {
    vi.spyOn(editor.view.dom, 'getBoundingClientRect').mockReturnValue({ top: 0, bottom: 1000, left: 0, width: 500, height: 1000 } as DOMRect)
    return [...editor.view.dom.children].map((element, index) => vi.spyOn(element, 'getBoundingClientRect').mockReturnValue(
      (hidden.has(index) ? { top: 0, bottom: 0, width: 0, height: 0 } : { top: index * 40, bottom: index * 40 + 30, width: 500, height: 30 }) as DOMRect))
  }
  function dragEvent(type: string, clientY: number) {
    const event = new Event(type, { bubbles: true, cancelable: true }) as DragEvent
    Object.defineProperty(event, 'clientY', { value: clientY })
    return event
  }
  function runFrames() { const pending = [...frames.values()]; frames.clear(); pending.forEach(callback => callback(0)) }

  it('dispatches no transaction while the pointer moves; the drop is the only edit', () => {
    const editor = mount('<p>One</p><p>Two</p><p>Three</p><p>Four</p>')
    layOut(editor)
    startDragSourceMarker(editor.view, [editor.view.dom.firstElementChild as HTMLElement], new Event('dragstart') as DragEvent, prepareBlockDrag(editor, 0))
    const transactions = vi.fn()
    editor.on('transaction', transactions)
    const over = editor.view.dom.children[2]!
    for (const y of [20, 60, 100, 115, 116, 117]) {
      const event = dragEvent('dragover', y)
      over.dispatchEvent(event)
      expect(event.defaultPrevented).toBe(true)
    }
    // Six events, one frame: the hit test and the preview run once.
    expect(frames.size).toBe(1)
    runFrames()
    expect(transactions).not.toHaveBeenCalled()
    over.dispatchEvent(dragEvent('drop', 117))
    expect(transactions).toHaveBeenCalled()
    expect(editor.state.doc.content.content.map(node => node.textContent)).toEqual(['Two', 'Three', 'One', 'Four'])
  })

  it('hit-tests cached boxes by binary search and treats a folded section as one target', () => {
    expect([-5, 14, 15, 16, 54, 56, 500].map(y => blockIndexAt([{ top: 0, bottom: 30 }, { top: 40, bottom: 70 }], y))).toEqual([0, 0, 1, 1, 1, 2, 2])
    // Heading, its two folded body blocks, the next heading, a paragraph.
    const editor = mount('<h2>A</h2><p>a1</p><p>a2</p><h2>B</h2><p>b1</p>')
    const boxes = layOut(editor, new Set([1, 2]))
    const blocks = measureBlocks(editor.view)
    const sizes = editor.state.doc.content.content.map(node => node.nodeSize)
    const at = (index: number) => sizes.slice(0, index).reduce((sum, size) => sum + size, 0)
    expect(blocks.map(block => [block.from, block.to])).toEqual([[0, at(3)], [at(3), at(4)], [at(4), at(5)]])
    startDragSourceMarker(editor.view, [editor.view.dom.children[4] as HTMLElement], new Event('dragstart') as DragEvent, prepareBlockDrag(editor, at(4)))
    boxes.forEach(box => box.mockClear())
    // Just below A's heading: the drop goes after the hidden tail, before B, at B's height.
    const target = resolveBlockDrop(editor.view, 40, undefined, blocks)!
    expect(target.position).toBe(at(3))
    expect(target.y).toBe(75)
    expect(boxes.every(box => box.mock.calls.length === 0)).toBe(true)
  })

  it('hidden blocks at the start of the page stay behind "insert at start"', () => {
    const editor = mount('<p>h1</p><p>h2</p><p>One</p><p>Two</p>')
    layOut(editor, new Set([0, 1]))
    expect(measureBlocks(editor.view)[0]!.from).toBe(0)
    startDragSourceMarker(editor.view, [editor.view.dom.children[3] as HTMLElement], new Event('dragstart') as DragEvent, prepareBlockDrag(editor, 13))
    expect(resolveBlockDrop(editor.view, 0)!.position).toBe(0)
  })

  it('a drop over a NodeView that stops events still moves the blocks the guide showed', () => {
    const Stopper = Node.create({ name: 'stopper', group: 'block', atom: true,
      parseHTML: () => [{ tag: 'div[data-stopper]' }], renderHTML: () => ['div', { 'data-stopper': '' }],
      addNodeView: () => () => {
        const dom = document.createElement('div'); dom.dataset.stopper = ''; dom.append(document.createElement('button'))
        return { dom, stopEvent: () => true }
      } })
    const editor = mount('<p>One</p><p>Two</p><div data-stopper></div><p>Four</p>', [Stopper])
    layOut(editor)
    startDragSourceMarker(editor.view, [editor.view.dom.firstElementChild as HTMLElement], new Event('dragstart') as DragEvent, prepareBlockDrag(editor, 0))
    const button = editor.view.dom.querySelector('[data-stopper] button')!
    button.dispatchEvent(dragEvent('dragover', 100))
    runFrames()
    const drop = dragEvent('drop', 100)
    button.dispatchEvent(drop)
    expect(drop.defaultPrevented).toBe(true)
    expect(editor.state.doc.content.content.map(node => node.type.name === 'stopper' ? '[stopper]' : node.textContent)).toEqual(['Two', '[stopper]', 'One', 'Four'])
    expect(blockDragRange(editor.view)).toBeNull()
  })

  it('drops by the pointer\'s height over the gutter beside the text (the paper\'s padding, outside view.dom), not outside the paper', () => {
    const editor = mount('<p>One</p><p>Two</p><p>Three</p><p>Four</p>')
    const paper = document.createElement('div'); paper.className = 'paper-inner'
    editor.view.dom.parentElement!.before(paper); paper.append(editor.view.dom.parentElement!)
    layOut(editor)
    startDragSourceMarker(editor.view, [editor.view.dom.firstElementChild as HTMLElement], new Event('dragstart') as DragEvent, prepareBlockDrag(editor, 0))
    const outside = dragEvent('dragover', 100)
    document.body.dispatchEvent(outside)
    expect(outside.defaultPrevented).toBe(false)
    const over = dragEvent('dragover', 100)
    paper.dispatchEvent(over)
    expect(over.defaultPrevented).toBe(true)
    runFrames()
    expect(document.querySelector('.md-block-drop-guide')).not.toBeNull()
    // Leaving the paper through the gutter ends the preview, as leaving the text does.
    document.body.dispatchEvent(dragEvent('dragover', 100))
    expect(document.querySelector('.md-block-drop-guide')).toBeNull()
    paper.dispatchEvent(dragEvent('dragover', 100))
    runFrames()
    const drop = dragEvent('drop', 100)
    paper.dispatchEvent(drop)
    expect(drop.defaultPrevented).toBe(true)
    expect(editor.state.doc.content.content.map(node => node.textContent)).toEqual(['Two', 'Three', 'One', 'Four'])
    // The focus returns to the editor (Ctrl+Z undoes the move) once the redrawn page has been drawn.
    const focus = vi.spyOn(editor.view, 'focus')
    runFrames(); runFrames()
    expect(focus).toHaveBeenCalled()
  })

  it('measures the blocks again after a resize, a redraw of the blocks and a document change, and only then', async () => {
    const win = document.defaultView!
    const resized: Array<() => void> = []
    Object.defineProperty(win, 'ResizeObserver', { configurable: true, value: class { constructor(callback: () => void) { resized.push(callback) } observe() {} disconnect() {} } })
    try {
      const editor = mount('<p>One</p><p>Two</p><p>Three</p><p>Four</p>')
      layOut(editor)
      startDragSourceMarker(editor.view, [editor.view.dom.firstElementChild as HTMLElement], new Event('dragstart') as DragEvent, prepareBlockDrag(editor, 0))
      const measured = vi.spyOn(editor.view, 'nodeDOM')
      const move = () => { editor.view.dom.children[2]!.dispatchEvent(dragEvent('dragover', 100)); runFrames(); return measured.mock.calls.length }
      const once = move()
      expect(once).toBeGreaterThan(0)
      expect(move()).toBe(once)
      resized.forEach(callback => callback())
      const afterResize = move()
      expect(afterResize).toBeGreaterThan(once)
      editor.view.dom.append(document.createElement('div'))
      await new Promise(resolve => setTimeout(resolve))
      const afterRedraw = move()
      expect(afterRedraw).toBeGreaterThan(afterResize)
      expect(move()).toBe(afterRedraw)
      editor.view.dispatch(editor.state.tr.insertText('!', 18))
      expect(move()).toBeGreaterThan(afterRedraw)
    } finally {
      delete (win as unknown as { ResizeObserver?: unknown }).ResizeObserver
    }
  })

  it('leaves text and file drags to the view', () => {
    const editor = mount('<p>One</p><p>Two</p>')
    // The view's drop cursor maps the pointer (jsdom has no hit testing): proof that the event reached the view.
    const hitTests = vi.fn(() => null)
    Object.defineProperty(document, 'elementFromPoint', { configurable: true, value: hitTests })
    const reached = vi.fn()
    editor.view.dom.parentElement!.addEventListener('dragover', reached)
    editor.view.dom.parentElement!.addEventListener('drop', reached)
    editor.view.dom.firstElementChild!.dispatchEvent(dragEvent('dragover', 10))
    const drop = dragEvent('drop', 10)
    editor.view.dom.firstElementChild!.dispatchEvent(drop)
    expect(reached).toHaveBeenCalledTimes(2)
    expect(hitTests).toHaveBeenCalled()
    delete (document as { elementFromPoint?: unknown }).elementFromPoint
    expect(frames.size).toBe(0)
    expect(editor.state.doc.content.content.map(node => node.textContent)).toEqual(['One', 'Two'])
  })

  it('takes the gap back when the drag ends: no block keeps a transform', () => {
    const runs: Array<{ element: Element; keyframes: Keyframe[]; options: KeyframeAnimationOptions; cancel: () => void; cancelled: boolean; onfinish: null }> = []
    Object.defineProperty(HTMLElement.prototype, 'animate', { configurable: true, value(this: Element, keyframes: Keyframe[], options: KeyframeAnimationOptions) {
      const run = { element: this, keyframes, options, cancelled: false, onfinish: null, cancel() { run.cancelled = true } }
      runs.push(run)
      return run
    } })
    try {
      const editor = mount('<p>One</p><p>Two</p><p>Three</p><p>Four</p>')
      layOut(editor)
      startDragSourceMarker(editor.view, [editor.view.dom.firstElementChild as HTMLElement], new Event('dragstart') as DragEvent, prepareBlockDrag(editor, 0))
      editor.view.dom.children[2]!.dispatchEvent(dragEvent('dragover', 100))
      runFrames()
      const blocks = [...editor.view.dom.children]
      const lastRun = (element: Element) => runs.filter(run => run.element === element).at(-1)
      expect(blocks.map(element => lastRun(element)?.keyframes.at(-1)?.transform)).toEqual(['translateY(-20px)', 'translateY(-20px)', 'translateY(-20px)', 'translateY(20px)'])
      document.dispatchEvent(new Event('dragend'))
      // Each block either returns to its place (no fill keeps it) or has its offset cancelled.
      for (const element of blocks) {
        const run = lastRun(element)!
        expect(run.cancelled || (run.keyframes.at(-1)?.transform === 'translateY(0px)' && run.options.fill !== 'forwards')).toBe(true)
        expect(runs.filter(other => other.element === element && other.options.fill === 'forwards').every(other => other.cancelled)).toBe(true)
      }
      expect(document.querySelector('.md-block-drop-guide')).toBeNull()
    } finally {
      delete (HTMLElement.prototype as { animate?: unknown }).animate
    }
  })

  it('keeps auto-scroll local to each editor view', () => {
    const first = mount('<p>First</p>')
    const second = mount('<p>Second</p>')
    for (const editor of [first, second]) {
      const container = editor.view.dom.parentElement!
      container.style.overflowY = 'auto'
      Object.defineProperties(container, { scrollHeight: { value: 600 }, clientHeight: { value: 200 } })
      vi.spyOn(container, 'getBoundingClientRect').mockReturnValue({ top: 0, bottom: 200 } as DOMRect)
      updateEdgeScroll(editor.view, 199)
    }
    stopEdgeScroll(second.view)
    const pending = [...frames.values()]
    frames.clear()
    pending.forEach(callback => callback(0))
    expect(first.view.dom.parentElement!.scrollTop).toBeGreaterThan(0)
    expect(second.view.dom.parentElement!.scrollTop).toBe(0)
    first.destroy()
    expect(frames.size).toBe(0)
  })

  it('keeps edge-scroll speed frame-rate independent and clamps resumed frames', () => {
    const editor = mount('<p>Frame rate</p>')
    const container = editor.view.dom.parentElement!
    container.style.overflowY = 'auto'
    Object.defineProperties(container, {
      scrollHeight: { value: 2_000 },
      clientHeight: { value: 200 },
    })
    vi.spyOn(container, 'getBoundingClientRect').mockReturnValue({ top: 0, bottom: 200 } as DOMRect)

    const renderAt = (timestamp: number) => {
      const pending = [...frames.values()]
      frames.clear()
      expect(pending).toHaveLength(1)
      pending[0](timestamp)
    }
    const distanceAt = (interval: number, count: number) => {
      container.scrollTop = 0
      stopEdgeScroll(editor.view)
      updateEdgeScroll(editor.view, 199)
      for (let index = 0; index < count; index++) renderAt(index * interval)
      stopEdgeScroll(editor.view)
      return container.scrollTop
    }

    const at60Hz = distanceAt(1000 / 60, 60)
    const at30Hz = distanceAt(1000 / 30, 30)
    expect(Math.abs(at60Hz - at30Hz)).toBeLessThan(6)

    container.scrollTop = 0
    updateEdgeScroll(editor.view, 199)
    renderAt(0)
    const beforeResume = container.scrollTop
    renderAt(2_000)
    expect(container.scrollTop - beforeResume).toBeLessThanOrEqual(12)
    stopEdgeScroll(editor.view)
  })
})
