// @vitest-environment jsdom
import { type Editor, type JSONContent } from '@tiptap/core'
import { Fragment } from '@tiptap/pm/model'
import { CellSelection, TableMap } from '@tiptap/pm/tables'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from '../../core'
import { createChartsPlugin, defaultChart, insertChart, insertChartFromSelection, normalizeChart, parseChartNumber, type ChartData } from './index'
import { chartRuntime, openChartEditor } from './runtime'

const canvasMocks = vi.hoisted(() => ({ cleanup: vi.fn(), render: vi.fn(), update: vi.fn() }))
vi.mock('./chartRenderer', () => ({ createChartRenderer: (...args: unknown[]) => { canvasMocks.render(...args); return { destroy: canvasMocks.cleanup, update: canvasMocks.update } } }))
const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.useRealTimers(); vi.clearAllMocks() })
const tableJSON = (): JSONContent => ({ type: 'table', content: [['Měsíc', 'Příjem', 'Výdaj'], ['Leden', '1 250,50', '100'], ['Únor', '2000', '150']].map((row, r) => ({ type: 'tableRow', content: row.map(text => ({ type: r === 0 ? 'tableHeader' : 'tableCell', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })) })) })
function mount(document: JSONContent = { type: 'doc', content: [tableJSON(), { type: 'paragraph' }] }) {
  let editor!: Editor
  const driver: MarkdownPlugin = { id: 'chart-test-driver', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
  const value = { format: 'atlas-rich', version: 1, document } as MarkdownRichValue
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createChartsPlugin(), driver], source: { kind: 'local', value } })
  controllers.push(controller)
  const host = window.document.createElement('div'); window.document.body.append(host); controller.attachView(host, { ariaLabel: 'Charts test editor' })
  return { controller, editor, host }
}
function selectTable(editor: Editor) {
  const map = TableMap.get(editor.state.doc.firstChild!)
  editor.view.dispatch(editor.state.tr.setSelection(CellSelection.create(editor.state.doc, 1 + map.map[0], 1 + map.map[map.map.length - 1])))
}
function chart(editor: Editor): ChartData {
  let data!: ChartData
  editor.state.doc.descendants(node => { if (node.type.name === 'chart') data = node.attrs.data })
  return data
}
function replaceCell(editor: Editor, row: number, column: number, text: string) {
  const table = editor.state.doc.firstChild!, map = TableMap.get(table), pos = 1 + map.map[row * map.width + column]
  const cell = editor.state.doc.nodeAt(pos)!
  editor.view.dispatch(editor.state.tr.insertText(text, pos + 2, pos + cell.nodeSize - 2))
}

describe('chart data', () => {
  it('parses Czech numbers, preserves gaps, rejects malformed numbers and negative pie values', () => {
    expect(parseChartNumber('1\u00a0234,50')).toBe(1234.5)
    expect(parseChartNumber('-0.25')).toBe(-0.25)
    expect(parseChartNumber(' ')).toBeNull()
    for (const invalid of ['1 2', '1,000.50', '12 Kč', 'NaN', '1,2,3', '1e3']) expect(() => parseChartNumber(invalid)).toThrow()
    expect(() => normalizeChart({ ...defaultChart, grid: [['X', 'Y'], ['A', 'abc']] })).toThrow(/Řádek 2, sloupec 2/)
    expect(() => normalizeChart({ ...defaultChart, kind: 'pie', grid: [['X', 'Y'], ['A', '-1']] })).toThrow(/nezáporné/)
  })
  it('clones input and rejects malformed source refs or oversized grids', () => {
    const data = normalizeChart(defaultChart); data.grid[1][1] = '9'
    expect(defaultChart.grid[1][1]).toBe('12')
    expect(() => normalizeChart({ ...defaultChart, version: 9 })).toThrow()
    expect(() => normalizeChart({ ...defaultChart, source: { tableId: 't', cells: [['a', 'a'], ['b', 'c'], ['d', 'e']] }, sourceStatus: 'linked' })).toThrow(/sloučené/)
    expect(() => normalizeChart({ ...defaultChart, grid: Array.from({ length: 202 }, () => ['X', '1']) })).toThrow()
  })
})

describe('linked charts', () => {
  it('queries without changes, inserts after selected table, updates values and keeps a fallback when source vanishes', () => {
    const { controller, editor } = mount(); selectTable(editor)
    const before = editor.getJSON()
    expect(controller.query(insertChartFromSelection()).enabled).toBe(true)
    expect(editor.getJSON()).toEqual(before)
    expect(controller.dispatch(insertChartFromSelection())).toBe(true)
    const original = chart(editor)
    expect(original.sourceStatus).toBe('linked'); expect(original.grid[1][1]).toBe('1 250,50')
    expect(new Set(original.source!.cells.flat()).size).toBe(9)
    expect(editor.state.doc.child(1).type.name).toBe('chart')
    replaceCell(editor, 1, 1, '3 500,25')
    expect(chart(editor).grid[1][1]).toBe('3 500,25')
    replaceCell(editor, 1, 1, 'chyba')
    expect(chart(editor).sourceStatus).toBe('invalid'); expect(chart(editor).grid[1][1]).toBe('3 500,25')
    replaceCell(editor, 1, 1, '4')
    expect(chart(editor).sourceStatus).toBe('linked')
    editor.view.dispatch(editor.state.tr.delete(0, editor.state.doc.firstChild!.nodeSize))
    expect(chart(editor).sourceStatus).toBe('missing'); expect(chart(editor).grid[1][1]).toBe('4')
    expect(controller.getProjection().markdownLossy).toBe(true)
    expect(controller.getProjection().markdown).toContain('Graf z tabulky')
  })
  it('walks the page again only for a change to a block holding a chart or a chart ID', () => {
    const { controller, editor } = mount(); selectTable(editor); controller.dispatch(insertChartFromSelection())
    // The chart's data made stale in place (no transaction): typing in the paragraph leaves the chart and the table untouched...
    const data = chart(editor), stale = { ...data, grid: data.grid.map(row => [...row]) }; stale.grid[1][1] = '7'
    ;(editor.state.doc.child(1).attrs as { data: ChartData }).data = stale
    editor.view.dispatch(editor.state.tr.insertText('psaní', editor.state.doc.content.size - 1))
    expect(chart(editor).grid[1][1]).toBe('7')
    // ...a change to the source table reads it again.
    replaceCell(editor, 1, 2, '120')
    expect(chart(editor).grid[1]).toEqual(['Leden', '1 250,50', '120'])
  })
  it('tracks original cell identities after row/column moves and duplicate-table paste', () => {
    const { controller, editor } = mount(); selectTable(editor); controller.dispatch(insertChartFromSelection())
    const original = chart(editor)
    const table = editor.state.doc.firstChild!
    const rows = [table.child(0), table.child(2), table.child(1)].map(row => row.copy(Fragment.fromArray([row.child(0), row.child(2), row.child(1)])))
    const reordered = table.copy(Fragment.fromArray(rows))
    editor.view.dispatch(editor.state.tr.replaceWith(0, table.nodeSize, reordered))
    expect(chart(editor).grid).toEqual(original.grid)
    replaceCell(editor, 2, 2, '999')
    expect(chart(editor).grid[1][1]).toBe('999')
    // Paste a table copy before the linked original. The original must retain IDs.
    const originalTable = editor.state.doc.firstChild!
    editor.view.dispatch(editor.state.tr.insert(0, originalTable))
    const tables = [editor.state.doc.child(0), editor.state.doc.child(1)]
    expect(tables[1].attrs.chartRef).toBe(original.source!.tableId)
    expect(tables[0].attrs.chartRef).not.toBe(tables[1].attrs.chartRef)
    replaceCell(editor, 1, 1, '777')
    expect(chart(editor).grid[1][1]).toBe('999')
  })
  it('undo/redo restores snapshots together with source values without append loops', () => {
    const { controller, editor } = mount(); selectTable(editor); controller.dispatch(insertChartFromSelection())
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    // Break the previous history group using the public history command timing boundary.
    vi.useFakeTimers(); vi.advanceTimersByTime(1000)
    replaceCell(editor, 1, 1, '50')
    expect(chart(editor).grid[1][1]).toBe('50')
    controller.dispatch({ type: 'undo' })
    expect(chart(editor).grid[1][1]).toBe('1 250,50')
    controller.dispatch({ type: 'redo' })
    expect(chart(editor).grid[1][1]).toBe('50')
    vi.useRealTimers()
  })
  it('rejects non-table and invalid-number selection; a standalone chart has a lossless Markdown form', () => {
    const { controller, editor } = mount()
    expect(controller.dispatch(insertChartFromSelection())).toBe(false)
    replaceCell(editor, 1, 1, 'not a number'); selectTable(editor)
    expect(controller.query(insertChartFromSelection()).enabled).toBe(false)
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    expect(controller.dispatch(insertChart(defaultChart))).toBe(true)
    const value = controller.getValue()
    controller.setValue(value)
    expect(chart(editor)).toEqual(defaultChart)
    expect(controller.getProjection().markdownLossy).toBe(false)
    expect(controller.getProjection().markdown).toContain('```a-chart\ntitle: Nový graf\n---\n| Období | Hodnota |')
  })
})

describe('chart grid popover', () => {
  it('keeps invalid drafts in a labelled grid and commits only after a valid Save', () => {
    HTMLDialogElement.prototype.showModal = vi.fn()
    const { editor } = mount(), update = vi.fn(), abort = new AbortController()
    const close = openChartEditor({ container: document.createElement('div'), data: defaultChart, document: editor.getJSON(), signal: abort.signal, editor, update })
    const input = document.querySelector<HTMLInputElement>('input[aria-label="Řádek 1, řada 1"]')!
    input.value = 'špatně'; input.dispatchEvent(new Event('input'))
    document.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }))
    expect(update).not.toHaveBeenCalled(); expect(document.querySelector('[role="alert"]')!.textContent).toContain('není číslo')
    input.value = '15,5'; input.dispatchEvent(new Event('input'))
    document.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }))
    expect(update).toHaveBeenCalledTimes(1); expect(update.mock.calls[0][0].grid[1][1]).toBe('15,5')
    expect(document.querySelector('[role="dialog"]')).toBeNull(); close()
  })
  it('applies a valid draft when the person leaves the panel and keeps an invalid one open with its error (P4, D3)', () => {
    const { editor } = mount(), update = vi.fn(), abort = new AbortController()
    const outside = document.createElement('button'); document.body.append(outside)
    const open = () => openChartEditor({ container: document.createElement('div'), data: defaultChart, document: editor.getJSON(), signal: abort.signal, editor, update })
    const input = () => document.querySelector<HTMLInputElement>('input[aria-label="Řádek 1, řada 1"]')!
    const type = (value: string) => { input().value = value; input().dispatchEvent(new Event('input')) }
    const press = () => outside.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true }))
    open(); type('špatně'); press()
    expect(update).not.toHaveBeenCalled(); expect(document.querySelector('[role="dialog"]')).not.toBeNull()
    expect(document.querySelector('[role="alert"]')!.textContent).toContain('není číslo')
    type('15,5'); press()
    expect(update).toHaveBeenCalledTimes(1); expect(update.mock.calls[0][0].grid[1][1]).toBe('15,5')
    expect(document.querySelector('[role="dialog"]')).toBeNull()
    // The focus going to another control applies too.
    open(); type('7'); input().focus(); outside.focus()
    expect(update).toHaveBeenCalledTimes(2); expect(update.mock.calls[1][0].grid[1][1]).toBe('7')
    abort.abort()
  })
  it('keeps an invalid draft open on a click into the page text, and the press still reaches what was pressed (D3)', async () => {
    const { controller, editor } = mount({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Text' }] }, { type: 'paragraph' }] })
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    controller.dispatch(insertChart(defaultChart))
    let at = -1; editor.state.doc.descendants((node, pos) => { if (node.type.name === 'chart') at = pos })
    editor.commands.setNodeSelection(at)
    const update = vi.fn(), abort = new AbortController()
    openChartEditor({ container: editor.view.nodeDOM(at) as HTMLElement, data: defaultChart, document: editor.getJSON(), signal: abort.signal, editor, update })
    const input = document.querySelector<HTMLInputElement>('input[aria-label="Řádek 1, řada 1"]')!
    input.value = 'špatně'; input.dispatchEvent(new Event('input'))
    const paragraph = editor.view.dom.querySelector('p')!, reached = vi.fn()
    paragraph.addEventListener('pointerdown', reached)
    paragraph.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true }))
    // ProseMirror's own mousedown then puts the caret into the text.
    editor.commands.setTextSelection(2)
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(reached).toHaveBeenCalledTimes(1)
    expect(document.querySelector('[role="dialog"]')).not.toBeNull()
    expect(document.querySelector('[role="alert"]')!.textContent).toContain('není číslo')
    expect(update).not.toHaveBeenCalled()
    abort.abort()
  })
  it('cancels local cell edits on abort without touching the document', () => {
    HTMLDialogElement.prototype.showModal = vi.fn()
    const { editor } = mount(), update = vi.fn(), abort = new AbortController()
    openChartEditor({ container: document.createElement('div'), data: defaultChart, document: editor.getJSON(), signal: abort.signal, editor, update })
    const input = document.querySelector<HTMLInputElement>('input[aria-label="Řádek 1, řada 1"]')!
    input.value = '99'; input.dispatchEvent(new Event('input'))
    abort.abort()
    expect(document.querySelector('[role="dialog"]')).toBeNull(); expect(update).not.toHaveBeenCalled()
    expect(defaultChart.grid[1][1]).toBe('12')
  })
})

describe('lazy readonly chart rendering', () => {
  it('provides labelled visual and actual data table, then destroys chart resources', async () => {
    const container = document.createElement('div'), abort = new AbortController()
    const cleanup = await chartRuntime.render({ container, data: defaultChart, document: { type: 'doc' }, signal: abort.signal })
    await vi.waitFor(() => expect(canvasMocks.render).toHaveBeenCalledTimes(1))
    expect(container.querySelector('.md-chart-visual')!.getAttribute('aria-label')).toContain(defaultChart.title)
    expect(container.querySelector('caption')!.textContent).toBe(defaultChart.title)
    expect(container.querySelectorAll('th[scope="col"]')).toHaveLength(2)
    expect(container.querySelectorAll('td')[0].textContent).toBe('12')
    // A reader has no editing control; zoom and copy are for everyone.
    expect([...container.querySelectorAll('button')].filter(button => !button.classList.contains('md-read-action'))).toEqual([])
    if (typeof cleanup === 'function') cleanup()
    else cleanup?.destroy()
    expect(canvasMocks.cleanup).toHaveBeenCalledTimes(1)
  })
  it('does not create a canvas runtime after the consumer has aborted', async () => {
    const container = document.createElement('div'), abort = new AbortController(); abort.abort()
    const cleanup = await chartRuntime.render({ container, data: defaultChart, document: { type: 'doc' }, signal: abort.signal })
    await Promise.resolve()
    expect(canvasMocks.render).not.toHaveBeenCalled()
    if (typeof cleanup === 'function') cleanup()
    else cleanup?.destroy()
  })
  it('retains the same renderer during linked data edits and unrelated typing', async () => {
    const { controller, editor, host } = mount(); selectTable(editor); controller.dispatch(insertChartFromSelection())
    await vi.waitFor(() => expect(canvasMocks.render).toHaveBeenCalledTimes(1))
    const visual = host.querySelector('.md-chart-visual')!
    replaceCell(editor, 1, 1, '44')
    await vi.waitFor(() => expect(canvasMocks.update).toHaveBeenCalledTimes(1))
    expect(canvasMocks.update.mock.calls[0][0].grid[1][1]).toBe('44')
    expect(host.querySelector('.md-chart-visual')).toBe(visual)
    expect(canvasMocks.render).toHaveBeenCalledTimes(1); expect(canvasMocks.cleanup).not.toHaveBeenCalled()
    editor.view.dispatch(editor.state.tr.insertText('Unrelated', editor.state.doc.content.size - 1))
    expect(canvasMocks.update).toHaveBeenCalledTimes(1)
    editor.commands.setNodeSelection(editor.state.doc.firstChild!.nodeSize); editor.commands.deleteSelection()
    expect(canvasMocks.cleanup).toHaveBeenCalledTimes(1)
  })
})

describe('chart edited where it is shown', () => {
  async function shown() {
    HTMLDialogElement.prototype.showModal = vi.fn()
    const { editor } = mount(), update = vi.fn(), abort = new AbortController(), container = document.createElement('div'); document.body.append(container)
    await chartRuntime.render({ container, data: defaultChart, document: editor.getJSON(), signal: abort.signal, editor, update })
    return { editor, update, container }
  }
  it('edits the title in place: Enter saves, Esc cancels', async () => {
    const { update, container } = await shown()
    const title = container.querySelector<HTMLElement>('.md-chart-title')!
    title.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    let input = title.querySelector('input')!
    input.value = 'Zrušeno'; input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    expect(update).not.toHaveBeenCalled(); expect(title.textContent).toBe(defaultChart.title)
    title.click(); input = title.querySelector('input')!
    input.value = 'Nový název'; input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    expect(update).toHaveBeenCalledTimes(1); expect(update.mock.calls[0][0].title).toBe('Nový název'); expect(update.mock.calls[0][0].grid).toEqual(defaultChart.grid)
  })
  it('shows the type as a chip that opens the choices and applies at once', async () => {
    const { update, container } = await shown()
    const chip = container.querySelector<HTMLButtonElement>('.md-chart-kind-chip')!
    expect(chip.textContent).toBe('Sloupcový'); chip.click()
    document.querySelector<HTMLButtonElement>('button[aria-label="Spojnicový"]')!.click()
    expect(update.mock.calls[0][0].kind).toBe('line')
  })
  it('opens the data editor from the data table, focuses the first cell and applies with Ctrl+Enter', async () => {
    const { update, container } = await shown()
    container.querySelector<HTMLElement>('table.md-chart-data')!.click()
    const first = document.querySelector<HTMLInputElement>('.md-chart-inspector input[aria-label="Název kategorií"]')!
    expect(first.autofocus).toBe(true); expect(document.activeElement).toBe(first)
    const cell = document.querySelector<HTMLInputElement>('input[aria-label="Řádek 1, řada 1"]')!
    cell.value = '20'; cell.dispatchEvent(new Event('input'))
    cell.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true, bubbles: true }))
    expect(update.mock.calls[0][0].grid[1][1]).toBe('20')
  })
  it('moves the caret into the source table of a linked chart', () => {
    HTMLDialogElement.prototype.showModal = vi.fn()
    const { controller, editor } = mount(); selectTable(editor); controller.dispatch(insertChartFromSelection())
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    const abort = new AbortController()
    openChartEditor({ container: document.createElement('div'), data: chart(editor), document: editor.getJSON(), signal: abort.signal, editor, update: vi.fn() })
    document.querySelector<HTMLButtonElement>('.md-chart-inspector .md-module-actions button')!.click()
    expect(editor.state.selection.$from.node(-3).type.name).toBe('table')
    expect(editor.state.selection.$from.parent.textContent).toBe('Měsíc')
    expect(document.querySelector('[role="dialog"]')).toBeNull()
  })
})
