// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest'
import { reactive, readonly, ref } from 'vue'
import { toPlainMarkdownValue } from './useMarkdownEditor'
import type { MarkdownRichValue } from '../core/types'

function rich(document: unknown): MarkdownRichValue {
  return { format: 'atlas-rich', version: 1, document } as MarkdownRichValue
}

describe('Vue value JSON boundary', () => {
  it('clones nested mutable/readonly Vue proxies without retaining references', () => {
    const paragraph = reactive({ type: 'paragraph', content: [{ type: 'text', text: 'Příliš žluťoučký 🐎' }] })
    const value = ref(rich(readonly({ type: 'doc', content: [paragraph] })))
    const result = toPlainMarkdownValue(value.value)
    expect(structuredClone(result)).toEqual(result)
    paragraph.content[0]!.text = 'Changed'
    expect(result).toEqual(rich({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Příliš žluťoučký 🐎' }] }] }))
  })

  it.each(['text', '__v_raw'])('rejects the %s getter without invoking it', key => {
    const getter = vi.fn(() => 'should not execute')
    const value = rich(Object.defineProperty({ type: 'doc' }, key, { enumerable: true, get: getter }))
    expect(() => toPlainMarkdownValue(value)).toThrow(/data fields/i)
    expect(getter).not.toHaveBeenCalled()
  })

  it('rejects a cyclic reactive document with a clear error', () => {
    const doc = reactive<Record<string, unknown>>({ type: 'doc' })
    doc.content = [doc]
    expect(() => toPlainMarkdownValue(rich(doc))).toThrow(/acyclic/i)
  })

  it.each([
    () => ({ type: 'doc', [Symbol('hidden')]: 'invalid' }),
    () => Object.defineProperty({ type: 'doc' }, 'hidden', { value: 'invalid' }),
    () => ({ type: 'doc', content: new Array(2) }),
    () => ({ type: 'doc', attrs: { value: Infinity } }),
    () => ({ type: 'doc', attrs: { date: new Date() } }),
    () => ({ type: 'doc', attrs: { value: undefined } }),
  ])('does not sanitize invalid data into an apparently valid object', makeDocument => {
    expect(() => toPlainMarkdownValue(rich(makeDocument()))).toThrow(TypeError)
  })

  it('preserves repeated non-cyclic data and null-prototype records', () => {
    const node = Object.assign(Object.create(null), { type: 'paragraph' })
    const value = rich({ type: 'doc', content: [node, node] })
    expect(toPlainMarkdownValue(value)).toEqual(value)
  })
})
