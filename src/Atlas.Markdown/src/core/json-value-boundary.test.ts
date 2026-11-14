import { reactive } from 'vue'
import { describe, expect, it } from 'vitest'
import { createMarkdownEditorController, type MarkdownRichValue } from './index'

function valueWithLink(href: unknown): MarkdownRichValue {
  return {
    format: 'atlas-rich', version: 1,
    document: { type: 'doc', content: [{ type: 'paragraph', content: [{
      type: 'text', text: 'Link', marks: [{ type: 'link', attrs: { href } }],
    }] }] },
  } as unknown as MarkdownRichValue
}

describe('rich JSON value boundary', () => {
  it.each([
    ['BigInt', 42n],
    ['function', () => 'not JSON'],
    ['symbol', Symbol('not JSON')],
    ['undefined', undefined],
    ['NaN', Number.NaN],
    ['positive infinity', Number.POSITIVE_INFINITY],
    ['negative infinity', Number.NEGATIVE_INFINITY],
    ['Date instance', new Date('2026-09-04T00:00:00Z')],
  ])('rejects a non-JSON %s attribute before it reaches the editor', (_label, href) => {
    expect(() => createMarkdownEditorController({
      profile: 'rich', source: { kind: 'local', value: valueWithLink(href) },
    })).toThrow(/JSON/)
  })

  it('rejects sparse and decorated arrays instead of silently normalizing them', () => {
    const sparse = new Array(2)
    sparse[1] = 120
    expect(() => createMarkdownEditorController({
      profile: 'rich', source: { kind: 'local', value: valueWithLink(sparse) },
    })).toThrow(/sparse JSON array/)

    const decorated = [120] as number[] & { label?: string }
    decorated.label = 'not serialized by JSON'
    expect(() => createMarkdownEditorController({
      profile: 'rich', source: { kind: 'local', value: valueWithLink(decorated) },
    })).toThrow(/JSON array items/)

    const outOfRangeKey = [120] as unknown[] & Record<string, unknown>
    outOfRangeKey['4294967295'] = 'not an array index'
    expect(() => createMarkdownEditorController({
      profile: 'rich', source: { kind: 'local', value: valueWithLink(outOfRangeKey) },
    })).toThrow(/JSON array items/)
  })

  it('rejects accessors without invoking them', () => {
    let invoked = false
    const attrs = Object.create(null) as Record<string, unknown>
    Object.defineProperty(attrs, 'href', { enumerable: true, get: () => { invoked = true; return 'https://example.test/' } })
    const value = valueWithLink('https://example.test/')
    const document = value.document as { content: Array<{ content: Array<{ marks: Array<{ attrs: Record<string, unknown> }> }> }> }
    document.content[0].content[0].marks[0].attrs = attrs

    expect(() => createMarkdownEditorController({
      profile: 'rich', source: { kind: 'local', value },
    })).toThrow(/JSON data fields/)
    expect(invoked).toBe(false)

    const array = ['https://example.test/']
    Object.defineProperty(array, '0', { enumerable: true, get: () => { invoked = true; return 'https://attacker.test/' } })
    expect(() => createMarkdownEditorController({
      profile: 'rich', source: { kind: 'local', value: valueWithLink(array) },
    })).toThrow(/JSON array data fields/)
    expect(invoked).toBe(false)
  })

  it('rejects a root accessor before migration without invoking it', () => {
    let invoked = false
    const value: Record<string, unknown> = {
      version: 1,
      document: { type: 'doc', content: [{ type: 'paragraph' }] },
    }
    Object.defineProperty(value, 'format', {
      enumerable: true,
      get: () => { invoked = true; return 'atlas-rich' },
    })

    expect(() => createMarkdownEditorController({
      profile: 'rich', source: { kind: 'local', value: value as unknown as MarkdownRichValue },
    })).toThrow(/JSON data fields/)
    expect(invoked).toBe(false)
  })

  it('rejects unknown rich envelope fields instead of silently dropping them', () => {
    const value = { ...valueWithLink('https://example.test/'), chartDataset: { series: [1, 2, 3] } }
    expect(() => createMarkdownEditorController({
      profile: 'rich', source: { kind: 'local', value: value as unknown as MarkdownRichValue },
    })).toThrow(/Unsupported rich document field/)
  })

  it('continues to accept framework proxies around valid JSON data', () => {
    const value = reactive(valueWithLink('https://example.test/')) as MarkdownRichValue
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value } })
    expect(controller.getValue()).toEqual(valueWithLink('https://example.test/'))
    controller.destroy()
  })
})
