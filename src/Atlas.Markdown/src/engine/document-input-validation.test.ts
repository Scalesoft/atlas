// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, type MarkdownRichValue } from '../core'
import { createLegacyEditorController } from '../core/controller'
import { editorForController } from '../core/internal'
import { MarkdownPluginRegistry } from '../core/plugins/registry'
import { MarkdownDocumentCodec } from './documentCodec'
import { renderMarkdownValue } from './staticRenderer'

const cleanups: (() => void)[] = []
afterEach(() => {
  cleanups.splice(0).forEach(cleanup => cleanup())
  document.body.replaceChildren()
})

function markdownTable(rows: number, columns: number): string {
  const row = '| ' + Array(columns).fill('cell').join(' | ') + ' |'
  return [row, '| ' + Array(columns).fill('---').join(' | ') + ' |', ...Array(rows - 1).fill(row)].join('\n')
}

function richTable(rows: number, columns: number): MarkdownRichValue {
  return { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'table',
    content: Array.from({ length: rows }, (_, row) => ({ type: 'tableRow',
      content: Array.from({ length: columns }, () => ({ type: row ? 'tableCell' : 'tableHeader',
        content: [{ type: 'paragraph', content: [{ type: 'text', text: 'cell' }] }],
      })),
    })),
  }] } }
}

function host() {
  const element = document.createElement('div')
  document.body.append(element)
  return element
}

function local(value = 'Original') {
  const controller = createMarkdownEditorController({ profile: 'markdown-safe', source: { kind: 'local', value } })
  cleanups.push(() => controller.destroy())
  return controller
}

describe('parsed document input validation', () => {
  it('rejects oversized initial Markdown before ready or view insertion', () => {
    const controller = local(markdownTable(34, 30))
    const states: string[] = []
    controller.subscribe(state => states.push(state.lifecycle))
    const element = host()
    expect(() => controller.attachView(element, { ariaLabel: 'Input' })).toThrow(/limit/)
    expect(states).not.toContain('ready')
    expect(controller.state.lifecycle).toBe('failed')
    expect(element.querySelector('table')).toBeNull()
  })

  it('accepts exactly 1,000 Markdown cells for initial and replacement values', () => {
    const controller = local(markdownTable(40, 25))
    const element = host()
    controller.attachView(element, { ariaLabel: 'Input' })
    expect(controller.state.lifecycle).toBe('ready')
    expect(element.querySelectorAll('td,th')).toHaveLength(1_000)
    expect(controller.getValue()).toContain('cell')
    controller.setValue('Replacement')
    controller.setValue(markdownTable(25, 40))
    expect(element.querySelectorAll('td,th')).toHaveLength(1_000)
    expect(() => controller.getProjection()).not.toThrow()
  })

  it.each([false, true])('retains value, revision and undo after invalid replacement (detached=%s)', detached => {
    const controller = local()
    const element = host()
    const detach = controller.attachView(element, { ariaLabel: 'Input' })
    controller.dispatch({ type: 'set-heading', level: 2 })
    const value = controller.getValue()
    const revision = controller.state.contentRevision
    if (detached) detach()
    expect(() => controller.setValue(markdownTable(34, 30))).toThrow(/limit/)
    expect(controller.getValue()).toBe(value)
    expect(controller.state.contentRevision).toBe(revision)
    if (detached) controller.attachView(element, { ariaLabel: 'Input' })
    expect(element.querySelector('table')).toBeNull()
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
    expect(controller.getValue()).toBe('Original')
    editorForController(controller)!.commands.insertContent('next')
    expect(controller.getValue()).toContain('next')
  })

  it('keeps rich validation aligned with the parsed Markdown boundary', () => {
    expect(() => createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: richTable(34, 30) } }))
      .toThrow(/limit/)
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: richTable(40, 25) } })
    cleanups.push(() => controller.destroy())
    const element = host()
    controller.attachView(element, { ariaLabel: 'Rich input' })
    expect(element.querySelectorAll('td,th')).toHaveLength(1_000)
    const value = controller.getValue()
    const revision = controller.state.contentRevision
    expect(() => controller.setValue(richTable(34, 30))).toThrow(/limit/)
    expect(controller.getValue()).toEqual(value)
    expect(controller.state.contentRevision).toBe(revision)
  })

  it.each([false, true])('validates legacy rich Markdown before replacement (detached=%s)', detached => {
    const { controller, replaceMarkdown } = createLegacyEditorController({ initialMarkdown: 'Original', profile: 'rich' })
    cleanups.push(() => controller.destroy())
    const element = host()
    const detach = controller.attachView(element, { ariaLabel: 'Legacy' })
    const before = controller.getProjection()
    if (detached) detach()
    expect(() => replaceMarkdown(markdownTable(34, 30))).toThrow(/limit/)
    expect(controller.getProjection()).toEqual(before)
    if (detached) controller.attachView(element, { ariaLabel: 'Legacy' })
    expect(element.textContent).toBe('Original')
    replaceMarkdown(markdownTable(2, 2))
    expect(element.querySelectorAll('td,th')).toHaveLength(4)
  })

  it('still rejects unknown generated HTML instead of weakening strict parsing for tables', () => {
    const controller = local()
    controller.attachView(host(), { ariaLabel: 'Strict parser' })
    const editor = editorForController(controller)!
    const registry = new MarkdownPluginRegistry([], 'markdown-safe')
    cleanups.push(() => registry.destroy())
    const codec = new MarkdownDocumentCodec('markdown-safe', registry, { profile: 'markdown-safe' })
    const parser = (editor.storage as unknown as { markdown: { parser: { parse(content: string): string } } }).markdown.parser
    const spy = vi.spyOn(parser, 'parse').mockReturnValue('<table><tbody><tr><td><unsupported>Content</unsupported></td></tr></tbody></table>')
    try { expect(() => codec.parse(editor, 'Input')).toThrow(/Invalid HTML content/) }
    finally { spy.mockRestore() }
    expect(controller.getValue()).toBe('Original')
  })

  it('does not impose final geometry checks on the live table repair transaction chain', () => {
    const controller = local(markdownTable(2, 2))
    const element = host()
    controller.attachView(element, { ariaLabel: 'Table repair' })
    const editor = editorForController(controller)!
    // Removing the first row requires the Markdown table append plugin to turn
    // the remaining body cells into a new header before the value is read.
    expect(controller.dispatch({ type: 'delete-row' })).toBe(true)
    expect(element.querySelectorAll('th')).toHaveLength(2)
    expect(() => controller.getValue()).not.toThrow()
    editor.commands.goToNextCell()
    editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }))
    expect(element.querySelectorAll('tr')).toHaveLength(2)
    expect(() => controller.getProjection()).not.toThrow()
  })
})

describe('static parsed input validation', () => {
  it.each(['markdown-safe', 'rich'] as const)('retains the old static render after a rejected %s input', profile => {
    const element = host()
    const render = vi.fn()
    const plugin = { id: 'validation-lifecycle', apiVersion: 1 as const, create: () => ({ onRender: render }) }
    const cleanup = renderMarkdownValue('**Original**', 'markdown-safe', [], element)
    cleanups.push(cleanup)
    const oldNode = element.firstChild
    const value = profile === 'rich' ? richTable(34, 30) : markdownTable(34, 30)
    expect(() => renderMarkdownValue(value, profile, [plugin], element)).toThrow(/limit/)
    expect(element.firstChild).toBe(oldNode)
    expect(element.textContent).toBe('Original')
    expect(render).not.toHaveBeenCalled()
  })

  it.each(['markdown-safe', 'rich'] as const)('renders exactly 1,000 %s cells', profile => {
    const element = host()
    const value = profile === 'rich' ? richTable(40, 25) : markdownTable(40, 25)
    cleanups.push(renderMarkdownValue(value, profile, [], element))
    expect(element.querySelectorAll('td,th')).toHaveLength(1_000)
  })
})
