// @vitest-environment jsdom

import { Editor } from '@tiptap/core'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownKit } from './createMarkdownKit'

const editors: Editor[] = []

const tableDocument = {
  type: 'doc',
  content: [
    {
      type: 'table',
      content: [
        {
          type: 'tableRow',
          content: [
            { type: 'tableHeader', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'A' }] }] },
            { type: 'tableHeader', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'B' }] }] },
          ],
        },
        {
          type: 'tableRow',
          content: [
            { type: 'tableCell', content: [{ type: 'paragraph', content: [{ type: 'text', text: '1' }] }] },
            { type: 'tableCell', content: [{ type: 'paragraph', content: [{ type: 'text', text: '2' }] }] },
          ],
        },
      ],
    },
  ],
}

beforeEach(() => {
  document.body.replaceChildren()
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
    callback(0)
    return 1
  })
  vi.stubGlobal('cancelAnimationFrame', () => undefined)
})

afterEach(() => {
  editors.splice(0).forEach(editor => editor.destroy())
  vi.unstubAllGlobals()
})

function mountTable(profile: 'markdown-safe' | 'rich'): HTMLElement {
  const element = document.createElement('div')
  document.body.appendChild(element)
  editors.push(new Editor({
    element,
    extensions: createMarkdownKit({ profile }).extensions,
    content: tableDocument,
  }))
  return element
}

function extension(kit: ReturnType<typeof createMarkdownKit>, name: string) {
  const result = kit.extensions.find(item => item.name === name)
  expect(result, `missing extension ${name}`).toBeDefined()
  return result!
}

describe('createMarkdownKit profiles', () => {
  it('defaults to a markdown-safe table schema and command surface', () => {
    const kit = createMarkdownKit()
    const names = kit.extensions.map(item => item.name)

    expect(kit.capabilities).toEqual({
      mergedCells: false,
      cellBackground: false,
      rowHeight: false,
    })
    expect(names).not.toContain('cellBackground')
    expect(names).not.toContain('rowHeight')
    expect(extension(kit, 'table').options.resizable).toBe(false)
    expect(extension(kit, 'tableControls').options.richTableFeatures).toBe(false)

    const guard = extension(kit, 'markdownSafeTableCommands')
    const commands = (guard.config.addCommands as Function).call(guard) as Record<string, () => (props: unknown) => boolean>
    expect(commands.mergeCells()({})).toBe(false)
    expect(commands.splitCell()({})).toBe(false)
    expect(commands.mergeOrSplit()({})).toBe(false)
  })

  it('enables explicitly lossy table behavior only in the rich profile', () => {
    const kit = createMarkdownKit({ profile: 'rich' })
    const names = kit.extensions.map(item => item.name)

    expect(kit.capabilities).toEqual({
      mergedCells: true,
      cellBackground: true,
      rowHeight: true,
    })
    expect(names).toContain('cellBackground')
    expect(names).toContain('rowHeight')
    expect(names).not.toContain('markdownSafeTableCommands')
    expect(extension(kit, 'table').options.resizable).toBe(true)
    expect(extension(kit, 'tableControls').options.richTableFeatures).toBe(true)
  })

  it('keeps ordinary row controls while hiding lossy row resize in markdown-safe mode', () => {
    const safe = mountTable('markdown-safe')
    const rich = mountTable('rich')

    expect(safe.querySelectorAll('[data-testid="table-row-chip"]')).toHaveLength(2)
    expect(safe.querySelectorAll('[data-testid="table-row-resize"]')).toHaveLength(0)
    expect(rich.querySelectorAll('[data-testid="table-row-chip"]')).toHaveLength(2)
    expect(rich.querySelectorAll('[data-testid="table-row-resize"]')).toHaveLength(2)
  })
})
