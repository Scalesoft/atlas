// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { defaultChart, type ChartData } from './data'
import { openChartTypePicker } from './typePicker'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren() })
function setup(data: ChartData = defaultChart) {
  const host = document.createElement('div'); document.body.append(host)
  const editor = new Editor({ element: host, extensions: [StarterKit], content: '<p>Graf</p>' }); editors.push(editor)
  const anchor = document.createElement('button'); document.body.append(anchor); anchor.focus()
  const update = vi.fn(), abort = new AbortController()
  const close = openChartTypePicker({ editor, anchor, signal: abort.signal, data, update })
  const pick = (label: string) => document.querySelector<HTMLButtonElement>(`.md-chart-types [aria-label="${label}"]`)!
  return { editor, anchor, update, abort, close, pick }
}

describe('chart type picker', () => {
  it('focuses the current icon choice, navigates the grid by keyboard and writes a validated choice', () => {
    const { pick, update, anchor } = setup({ ...defaultChart, kind: 'line' })
    const selected = pick('Spojnicový')
    expect(document.activeElement).toBe(selected)
    expect(selected.getAttribute('aria-checked')).toBe('true')
    expect(selected.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true')
    selected.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }))
    expect(document.activeElement).toBe(pick('Prstencový'))
    pick('Prstencový').click()
    expect(update).toHaveBeenCalledExactlyOnceWith({ ...defaultChart, kind: 'doughnut' })
    expect(document.querySelector('[role=menu]')).toBeNull()
    expect(document.activeElement).toBe(anchor)
  })
  it('keeps invalid circular conversion open without changing data, then accepts a compatible kind', () => {
    const data = { ...defaultChart, grid: [['Období', 'Hodnota'], ['Leden', '-12']] }
    const { pick, update } = setup(data)
    pick('Koláčový').click()
    expect(update).not.toHaveBeenCalled()
    expect(document.querySelector('[role=alert]')?.textContent).toContain('nezáporné')
    pick('Spojnicový').click()
    expect(update).toHaveBeenCalledExactlyOnceWith({ ...data, kind: 'line' })
  })
  it('closes the current choice without a redundant undo operation, and ignores stale readonly actions', () => {
    const first = setup()
    first.pick('Sloupcový').click()
    expect(first.update).not.toHaveBeenCalled()
    expect(document.querySelector('[role=menu]')).toBeNull()
    const second = setup(); const stale = second.pick('Spojnicový')
    second.editor.setEditable(false); stale.click()
    expect(second.update).not.toHaveBeenCalled()
    second.close()
  })
})
