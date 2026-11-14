// @vitest-environment jsdom

import { Extension, Mark, Node } from '@tiptap/core'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from './index'

const controllers: MarkdownEditorController[] = []
afterEach(() => {
  controllers.splice(0).forEach(controller => controller.destroy())
  document.body.replaceChildren()
  vi.restoreAllMocks()
})

const reference = Node.create({
  name: 'versionedReference', group: 'inline', inline: true, atom: true,
  addAttributes: () => ({ key: { default: '' } }),
  parseHTML: () => [{ tag: 'span[data-reference]' }],
  renderHTML: ({ node }) => ['span', { 'data-reference': '' }, node.attrs.key],
  renderText: ({ node }) => node.attrs.key,
  addStorage: () => ({ markdown: { serialize(state: { write(value: string): void }, node: { attrs: { key: string } }) { state.write(node.attrs.key) } } }),
})
const nodePlugin: MarkdownPlugin = {
  id: 'versioned-reference', apiVersion: 1, schemaVersion: 2,
  create: () => ({ extensions: [reference], markdownLossy: () => true }),
}
const plain: MarkdownRichValue = { format: 'atlas-rich', version: 1, document: {
  type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'plain' }] }],
} }

function create(value: MarkdownRichValue, plugins: readonly MarkdownPlugin[]) {
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value }, plugins })
  controllers.push(controller)
  return controller
}

describe('plugin schema and lifecycle regressions', () => {
  it('accepts legacy built-in documents while active plugins are configured', () => {
    expect(create(plain, [nodePlugin]).getValue().document).toEqual(plain.document)
  })

  it.each([{ label: 'missing', requirements: undefined }, { label: 'empty', requirements: [] }])('requires schema metadata for custom nodes when requirements are $label', ({ requirements }) => {
    const value: MarkdownRichValue = { ...plain, ...(requirements ? { plugins: requirements } : {}), document: {
      type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'versionedReference', attrs: { key: 'v1-data' } }] }],
    } }
    expect(() => create(value, [nodePlugin])).toThrow(/plugin|schema|version/i)
  })

  it('requires schema metadata for custom marks on otherwise built-in nodes', () => {
    const mark = Mark.create({ name: 'versionedMark', parseHTML: () => [{ tag: 'mark[data-versioned]' }], renderHTML: () => ['mark', { 'data-versioned': '' }, 0] })
    const plugin: MarkdownPlugin = { id: 'mark-version', apiVersion: 1, schemaVersion: 2, create: () => ({ extensions: [mark] }) }
    const value: MarkdownRichValue = { ...plain, document: {
      type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'v1-data', marks: [{ type: 'versionedMark' }] }] }],
    } }
    expect(() => create(value, [plugin])).toThrow(/plugin|schema|version/i)
  })

  it('requires schema metadata for custom attributes added to built-in nodes', () => {
    const attributes = Extension.create({
      name: 'versionedAttributes',
      addGlobalAttributes: () => [{ types: ['paragraph'], attributes: { customMeaning: { default: null } } }],
    })
    const plugin: MarkdownPlugin = { id: 'attribute-version', apiVersion: 1, schemaVersion: 2, create: () => ({ extensions: [attributes] }) }
    const value: MarkdownRichValue = { ...plain, document: {
      type: 'doc', content: [{ type: 'paragraph', attrs: { customMeaning: 'v1-data' }, content: [{ type: 'text', text: 'plain' }] }],
    } }
    expect(() => create(value, [plugin])).toThrow(/plugin|schema|version/i)
  })

  it('checks nested extension names instead of allowing a wrapper to replace a built-in node', () => {
    const paragraph = Node.create({ name: 'paragraph', group: 'block', content: 'inline*', parseHTML: () => [{ tag: 'p' }], renderHTML: () => ['p', 0] })
    const wrapper = Extension.create({ name: 'wrapper', addExtensions: () => [paragraph] })
    const plugin: MarkdownPlugin = { id: 'wrapped-schema', apiVersion: 1, schemaVersion: 1, create: () => ({ extensions: [wrapper] }) }
    expect(() => create(plain, [plugin])).toThrow(/Duplicate|collision/i)
  })

  it('rejects overlapping attribute ownership and global schema rewrites', () => {
    const attributePlugin = (id: string, attribute: string): MarkdownPlugin => ({
      id, apiVersion: 1, schemaVersion: 1,
      create: () => ({ extensions: [Extension.create({ name: id,
        addGlobalAttributes: () => [{ types: ['paragraph'], attributes: { [attribute]: { default: null } } }],
      })] }),
    })
    expect(() => create(plain, [attributePlugin('a', 'customMeaning'), attributePlugin('b', 'customMeaning')])).toThrow(/Duplicate schema attribute/)
    expect(() => create(plain, [attributePlugin('a', 'textAlign')])).toThrow(/Duplicate schema attribute/)
    const rewrite: MarkdownPlugin = { id: 'rewrite', apiVersion: 1, schemaVersion: 1, create: () => ({
      extensions: [Extension.create({ name: 'rewrite', extendNodeSchema: () => ({ draggable: true }) })],
    }) }
    expect(() => create(plain, [rewrite])).toThrow(/cannot rewrite/)
  })

  it('conservatively reports loss when a custom schema has no explicit fidelity check', () => {
    const plugin: MarkdownPlugin = { ...nodePlugin, create: () => ({ extensions: [reference] }) }
    const controller = create(plain, [plugin])
    const host = document.createElement('div')
    document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Loss detection' })
    expect(controller.getProjection().markdownLossy).toBe(false)
    controller.setValue({ ...plain, plugins: [{ id: plugin.id, version: 2 }], document: {
      type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'versionedReference', attrs: { key: 'reference' } }] }],
    } })
    expect(controller.getProjection().markdownLossy).toBe(true)
  })

  it('always detaches the view and plugin hooks when a projection serializer throws', () => {
    const serialize = vi.fn((state: { write(value: string): void }) => { state.write('reference') })
      .mockImplementationOnce(() => { throw new Error('Broken serializer') })
    const cleanup = vi.fn()
    const plugin: MarkdownPlugin = {
      id: 'broken-projection', apiVersion: 1, schemaVersion: 1,
      create: () => ({
        extensions: [reference.extend({ addStorage: () => ({ markdown: { serialize } }) })],
        markdownLossy: () => true,
        onAttach: () => cleanup,
      }),
    }
    const value: MarkdownRichValue = { ...plain, plugins: [{ id: plugin.id, version: 1 }], document: {
      type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'versionedReference', attrs: { key: 'reference' } }] }],
    } }
    const controller = create(value, [plugin])
    const host = document.createElement('div')
    document.body.append(host)
    const detach = controller.attachView(host, { ariaLabel: 'Projection failure' })
    expect(detach).toThrow(/Broken serializer|detach failed/)
    expect(cleanup).toHaveBeenCalledTimes(1)
    expect(host.querySelector('.tiptap')).toBeNull()
    expect(controller.state.lifecycle).toBe('idle')
    expect(() => controller.attachView(host, { ariaLabel: 'Reattached' })).not.toThrow()
  })
})
