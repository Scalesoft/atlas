import { describe, expect, it, vi } from 'vitest'
import { Schema } from '@tiptap/pm/model'
import { createRuntimeDocumentDataValidator } from './runtimeDocumentData'
import { clonePlainData, deepFreezePlainData } from '../core/plainData'

const schema = new Schema({ nodes: { doc: { content: 'paragraph+' }, paragraph: { content: 'text*', attrs: { data: { default: null } } }, text: {} },
  marks: { annotation: { attrs: { data: { default: null } } } } })
const document = (data: unknown) => schema.node('doc', null, [schema.node('paragraph', { data }, [schema.text('Text')])])
const accepted = (operation: () => void) => { try { operation(); return true } catch { return false } }

describe('streaming PM JSON data validation', () => {
  it.each([
    null, true, 'Unicode český 👩🏽‍💻', 1.5, { nested: [1, null, { text: 'Valid' }] },
    { number: NaN }, { number: Infinity }, { missing: undefined }, { method() {} }, { value: new Date() },
    { [Symbol('hidden')]: 'invalid' }, new Array(2), Object.assign([1], { extra: 2 }),
  ])('matches the detached JSON reference for %j', data => {
    const doc = document(data), validate = createRuntimeDocumentDataValidator()
    expect(accepted(() => validate(doc))).toBe(accepted(() => { clonePlainData(doc.toJSON()) }))
  })

  it('rechecks mutable nested attrs after a successful prior validation', () => {
    const data: Record<string, unknown> = { nested: { color: 'blue' } }, doc = document(data), validate = createRuntimeDocumentDataValidator()
    validate(doc)
    ;(data.nested as Record<string, unknown>).color = Infinity
    expect(() => validate(doc)).toThrow(/finite/)
    data.nested = { self: data }
    expect(() => validate(doc)).toThrow(/acyclic/)
  })

  it('does not invoke accessors or serialize the document', () => {
    const getter = vi.fn(() => 'secret'), attrs = Object.defineProperty({}, 'getter', { enumerable: true, get: getter })
    const doc = document(attrs), json = vi.spyOn(doc, 'toJSON')
    expect(() => createRuntimeDocumentDataValidator()(doc)).toThrow(/data fields/)
    expect(getter).not.toHaveBeenCalled(); expect(json).not.toHaveBeenCalled()
  })

  it('permits frozen shared values without freezing any live mutable attributes', () => {
    const immutable = deepFreezePlainData({ nested: [1, 2, { okay: true }] })
    const mutable = { immutable, text: 'before' }, doc = document(mutable), validate = createRuntimeDocumentDataValidator()
    validate(doc); validate(doc)
    expect(Object.isFrozen(doc.firstChild!.attrs)).toBe(false)
    expect(Object.isFrozen(mutable)).toBe(false)
    mutable.text = 'after'; validate(doc)
  })

  it('checks attributes on marks as well as nodes', () => {
    const attrs = { okay: 1 } as Record<string, unknown>
    const doc = schema.node('doc', null, [schema.node('paragraph', null, [schema.text('Marked', [schema.mark('annotation', { data: attrs })])])])
    const validate = createRuntimeDocumentDataValidator()
    validate(doc); attrs.okay = undefined
    expect(() => validate(doc)).toThrow()
  })

  it('matches full JSON validation through 200 deterministic text/attribute/mark variations', () => {
    const validate = createRuntimeDocumentDataValidator()
    for (let index = 0; index < 200; index++) {
      const data = { index, nested: [{ value: index % 11 === 0 ? Infinity : `value-${index}` }], enabled: index % 2 === 0 }
      const marks = index % 3 ? [] : [schema.mark('annotation', { data: { values: [index, index % 7 ? null : undefined] } })]
      const doc = schema.node('doc', null, [schema.node('paragraph', { data }, [schema.text(`Sample ${index}`, marks)])])
      expect(accepted(() => validate(doc)), `case ${index}`).toBe(accepted(() => { clonePlainData(doc.toJSON()) }))
    }
  })
})
