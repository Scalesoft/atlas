// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest'
import type { MarkdownPlugin, MarkdownReadonlyJSONContent } from '../core'
import { renderMarkdownValue } from './staticRenderer'

describe('static renderer plugin lifecycle', () => {
  it('runs after insertion with validated JSON and aborts before cleanup', () => {
    const events: string[] = []
    let renderSignal!: AbortSignal
    const plugin: MarkdownPlugin = {
      id: 'static-probe', apiVersion: 1,
      create: ({ signal: lifetimeSignal }) => ({
        onRender(context) {
          renderSignal = context.signal
          expect(Object.isFrozen(context)).toBe(true)
          expect(Object.isFrozen(context.document)).toBe(true)
          expect(Object.isFrozen(context.document.content)).toBe(true)
          expect(Object.isFrozen(context.document.content?.[0])).toBe(true)
          const typedDocument: MarkdownReadonlyJSONContent = context.document
          if (false) {
            // @ts-expect-error public render documents are deeply readonly
            typedDocument.content?.push({ type: 'paragraph' })
            // @ts-expect-error render attribute maps are deeply readonly
            typedDocument.attrs!.label = 'changed'
          }
          expect(context.signal).toBe(lifetimeSignal)
          expect(context.ownerDocument).toBe(context.container.ownerDocument)
          expect(context.container.querySelector('strong')?.textContent).toBe('Rendered')
          expect(context.document).toMatchObject({ type: 'doc', content: [{ type: 'paragraph' }] })
          events.push('render')
          return () => events.push(`cleanup:${context.signal.aborted}`)
        },
      }),
    }
    const container = document.createElement('div')

    const cleanup = renderMarkdownValue('**Rendered**', 'markdown-safe', [plugin], container)
    expect(events).toEqual(['render'])
    expect(renderSignal.aborted).toBe(false)

    cleanup()
    cleanup()
    expect(renderSignal.aborted).toBe(true)
    expect(events).toEqual(['render', 'cleanup:true'])
    expect(container.innerHTML).toBe('')
  })

  it('rejects malformed hooks and non-synchronous cleanup at runtime', () => {
    const malformed: MarkdownPlugin = {
      id: 'malformed-render', apiVersion: 1,
      create: () => ({ onRender: 'not a function' } as never),
    }
    expect(() => renderMarkdownValue('Text', 'markdown-safe', [malformed], document.createElement('div')))
      .toThrow(/invalid plugin instance/)

    const cleanup = vi.fn()
    let signal!: AbortSignal
    const valid: MarkdownPlugin = {
      id: 'valid-render', apiVersion: 1,
      create: context => { signal = context.signal; return { onRender: () => cleanup } },
    }
    const asynchronous: MarkdownPlugin = {
      id: 'async-render-cleanup', apiVersion: 1,
      create: () => ({ onRender: (() => Promise.resolve()) as never }),
    }
    const container = document.createElement('div')

    expect(() => renderMarkdownValue('Text', 'markdown-safe', [valid, asynchronous], container))
      .toThrow(/cleanup must be a function/)
    expect(signal.aborted).toBe(true)
    expect(cleanup).toHaveBeenCalledOnce()
    expect(container.innerHTML).toBe('')
  })

  it('runs all cleanup callbacks and clears content when one cleanup fails', () => {
    const completed = vi.fn()
    const failing: MarkdownPlugin = {
      id: 'failing-cleanup', apiVersion: 1,
      create: () => ({ onRender: () => () => { throw new Error('render cleanup failed') } }),
    }
    const completing: MarkdownPlugin = {
      id: 'completing-cleanup', apiVersion: 1,
      create: () => ({ onRender: () => completed }),
    }
    const container = document.createElement('div')
    const cleanup = renderMarkdownValue('Text', 'markdown-safe', [failing, completing], container)

    expect(() => cleanup()).toThrow(/Static renderer cleanup failed/)
    expect(completed).toHaveBeenCalledOnce()
    expect(container.innerHTML).toBe('')
  })
})

describe('static renderer headings', () => {
  it('renders every heading level H1-H6 with its own element', () => {
    const value = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [1, 2, 3, 4, 5, 6].map(level =>
      ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text: `Level ${level}` }] })) } } as const
    const container = document.createElement('div')
    const cleanup = renderMarkdownValue(value, 'rich', [], container)
    expect([...container.querySelectorAll('h1,h2,h3,h4,h5,h6')].map(heading => `${heading.tagName}:${heading.textContent}`))
      .toEqual(['H1:Level 1', 'H2:Level 2', 'H3:Level 3', 'H4:Level 4', 'H5:Level 5', 'H6:Level 6'])
    cleanup()
  })
})

describe('static renderer callouts', () => {
  it('shows the same icon and label header as the editor', () => {
    const value = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [
      { type: 'callout', attrs: { variant: 'caution' }, content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Pozor na následky' }] }] },
    ] } } as const
    const container = document.createElement('div')
    const cleanup = renderMarkdownValue(value, 'rich', [], container)
    const header = container.querySelector('aside[data-callout][data-variant="caution"] > .md-callout-label[data-callout-label]')
    expect(header?.textContent).toBe('Pozor')
    expect(header?.querySelector('svg[aria-hidden="true"]')).not.toBeNull()
    cleanup()
  })
})
