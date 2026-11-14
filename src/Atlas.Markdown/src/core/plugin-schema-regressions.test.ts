// @vitest-environment jsdom

import { Extension, Node, type Editor, type JSONContent } from '@tiptap/core'
import { EditorState } from '@tiptap/pm/state'
import { afterEach, describe, expect, it } from 'vitest'
import { createMarkdownEditorController, defineMarkdownCommand, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from './index'

const controllers: MarkdownEditorController[] = []
afterEach(() => {
  controllers.splice(0).forEach(controller => controller.destroy())
  document.body.replaceChildren()
})

const plain = (): MarkdownRichValue => ({ format: 'atlas-rich', version: 1, document: {
  type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Plain' }] }],
} })

function rich(value: MarkdownRichValue, plugins: readonly MarkdownPlugin[]) {
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value }, plugins })
  controllers.push(controller)
  return controller
}

const Card = Node.create({
  name: 'schemaReviewCard', group: 'inline', inline: true, atom: true,
  addAttributes: () => ({ label: { default: '' } }),
  parseHTML: () => [{ tag: 'span[data-schema-review-card]' }],
  renderHTML: ({ node }) => ['span', { 'data-schema-review-card': '' }, node.attrs.label],
  renderText: ({ node }) => node.attrs.label,
  addStorage: () => ({ markdown: { serialize(state: { write(value: string): void }, node: { attrs: { label: string } }) { state.write(node.attrs.label) } } }),
})
const cardPlugin: MarkdownPlugin = {
  id: 'schema-review-card', apiVersion: 1, schemaVersion: 1, profiles: ['rich'],
  create: () => ({ extensions: [Card] }),
}
const annotationPlugin: MarkdownPlugin = {
  id: 'schema-review-annotations', apiVersion: 1, schemaVersion: 2, profiles: ['rich'],
  create: () => ({ extensions: [Extension.create({
    name: 'schemaReviewAnnotations',
    addGlobalAttributes: () => [{ types: ['schemaReviewCard'], attributes: { annotation: { default: null } } }],
  })] }),
}
const JsonBoundaryCard = Node.create({
  name: 'jsonBoundaryCard', group: 'block', atom: true,
  addAttributes: () => ({ payload: { default: null } }),
  parseHTML: () => [{ tag: 'div[data-json-boundary-card]' }],
  renderHTML: () => ['div', { 'data-json-boundary-card': '' }],
})
const setInvalidJson = defineMarkdownCommand({
  id: 'json-boundary.set-invalid',
  apply: () => ({ tr }) => { tr.setNodeMarkup(0, undefined, { payload: 42n }); return true },
})
const annotated: MarkdownRichValue = { ...plain(), plugins: [{ id: cardPlugin.id, version: 1 }], document: {
  type: 'doc', content: [{ type: 'paragraph', content: [{
    type: 'schemaReviewCard', attrs: { label: 'CARD-1', annotation: 'unversioned-payload' },
  }] }],
} }
const mergedTable = (): JSONContent => ({ type: 'table', content: [
  { type: 'tableRow', content: [{
    type: 'tableHeader', attrs: { colspan: 2, rowspan: 1, colwidth: null },
    content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Merged' }] }],
  }] },
  { type: 'tableRow', content: [
    { type: 'tableCell', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'A' }] }] },
    { type: 'tableCell', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'B' }] }] },
  ] },
] })

describe('plugin schema boundaries', () => {
  it('requires a schema version only when extensions really change the schema', () => {
    expect(() => rich(plain(), [{
      id: 'schema-review-unversioned', apiVersion: 1, profiles: ['rich'],
      create: () => ({ extensions: [Card.extend({ name: 'schemaReviewUnversioned' })] }),
    }])).toThrow(/must declare its schema version when changing the document schema/)
  })

  it('attributes cross-plugin fields to their declaration regardless of plugin order', () => {
    for (const plugins of [[annotationPlugin, cardPlugin], [cardPlugin, annotationPlugin]]) {
      expect(() => rich(annotated, plugins)).toThrow(/metadata is required for schema-review-annotations/)
    }
    const versioned = { ...annotated, plugins: [
      { id: cardPlugin.id, version: 1 }, { id: annotationPlugin.id, version: 2 },
    ] }
    expect(rich(versioned, [annotationPlugin, cardPlugin]).getValue()).toEqual({
      ...versioned, plugins: [
        { id: annotationPlugin.id, version: 2 }, { id: cardPlugin.id, version: 1 },
      ],
    })
  })

  it('does not turn behavior-only Tiptap extensions into persisted schema dependencies', () => {
    const shortcut: MarkdownPlugin = {
      id: 'schema-review-shortcuts', apiVersion: 1,
      create: () => ({ extensions: [Extension.create({
        name: 'schemaReviewShortcuts', addKeyboardShortcuts: () => ({ 'Mod-Alt-b': () => false }),
      })] }),
    }
    const controller = rich(plain(), [shortcut])
    expect(controller.schema.plugins).toEqual([])
    expect(controller.getValue()).toEqual(plain())

    const legacyValue = { ...plain(), plugins: [{ id: shortcut.id, version: 7 }] }
    const legacyVersioned = rich(legacyValue, [{ ...shortcut, schemaVersion: 7 }])
    expect(legacyVersioned.schema.plugins).toEqual([])
    expect(legacyVersioned.getValue()).toEqual(plain())
  })

  it('does not apply the schema loss guard to behavior-only extensions', () => {
    const insertText = defineMarkdownCommand({
      id: 'schema-review-behavior.insert', apply: () => ({ commands }) => commands.insertContent('Retained'),
    })
    const behavior: MarkdownPlugin = {
      id: 'schema-review-behavior', apiVersion: 1,
      create: () => ({
        extensions: [Extension.create({ name: 'schemaReviewBehavior' })], commands: [insertText],
        markdownLossy: () => true,
      }),
    }
    const controller = createMarkdownEditorController({
      profile: 'markdown-safe', source: { kind: 'local', value: '' }, plugins: [behavior],
    })
    controllers.push(controller)
    const host = document.createElement('div')
    document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Behavior-only plugin' })
    expect(controller.dispatch(insertText())).toBe(true)
    expect(controller.getValue()).toBe('Retained')
  })

  it('blocks behavior-only commands from creating lossy built-in markdown-safe content', () => {
    const insertMerged = defineMarkdownCommand({
      id: 'schema-review-behavior.insert-merged',
      apply: () => ({ commands }) => commands.insertContent(mergedTable()),
    })
    const behavior: MarkdownPlugin = {
      id: 'schema-review-builtins', apiVersion: 1,
      create: () => ({ commands: [insertMerged] }),
    }
    const controller = createMarkdownEditorController({
      profile: 'markdown-safe', source: { kind: 'local', value: '' }, plugins: [behavior],
    })
    controllers.push(controller)
    const host = document.createElement('div')
    document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Built-in fidelity guard' })

    // Tiptap reports that the command ran; the fidelity filter owns commit acceptance.
    expect(controller.dispatch(insertMerged())).toBe(true)
    expect(controller.state.contentRevision).toBe(0)
    expect(controller.getValue()).toBe('')
  })

  it('refuses to serialize lossy markdown-safe state even when a transaction filter was bypassed', () => {
    let editor!: Editor
    const capture: MarkdownPlugin = {
      id: 'schema-review-capture', apiVersion: 1,
      create: () => ({ onCreate: context => { editor = context.editor } }),
    }
    const controller = createMarkdownEditorController({
      profile: 'markdown-safe', source: { kind: 'local', value: '' }, plugins: [capture],
    })
    controllers.push(controller)
    const host = document.createElement('div')
    document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Serialization fidelity guard' })
    const original = editor.state
    const mergedDocument = editor.schema.nodeFromJSON({ type: 'doc', content: [mergedTable()] })
    editor.view.updateState(EditorState.create({ schema: editor.schema, doc: mergedDocument }))
    expect(() => controller.getValue()).toThrow(/cannot round-trip/)
    editor.view.updateState(original)
  })

  it('requires content plugins to opt into markdown-safe and declare fidelity', () => {
    const plugin = (profiles?: MarkdownPlugin['profiles'], markdownLossy?: () => boolean): MarkdownPlugin => ({
      id: 'schema-review-safe', apiVersion: 1, schemaVersion: 1, profiles,
      create: () => ({ extensions: [Card.extend({ name: 'schemaReviewSafeCard' })], markdownLossy }),
    })
    expect(() => createMarkdownEditorController({
      profile: 'markdown-safe', source: { kind: 'local', value: '' }, plugins: [plugin()],
    })).toThrow(/explicitly support markdown-safe/)
    expect(() => createMarkdownEditorController({
      profile: 'markdown-safe', source: { kind: 'local', value: '' }, plugins: [plugin(['markdown-safe'])],
    })).toThrow(/round-trip fidelity/)
  })

  it('rejects a known-lossy markdown-safe transaction without changing the persisted value', () => {
    const insert = defineMarkdownCommand({
      id: 'schema-review-safe.insert',
      apply: () => ({ commands }) => commands.insertContent({ type: 'schemaReviewSafeCard', attrs: { label: 'CARD-1' } }),
    })
    const plugin: MarkdownPlugin = {
      id: 'schema-review-safe', apiVersion: 1, schemaVersion: 1, profiles: ['markdown-safe'],
      create: () => ({
        extensions: [Card.extend({ name: 'schemaReviewSafeCard' })], commands: [insert],
        markdownLossy(document) {
          let lossy = false
          document.descendants(node => { lossy ||= node.type.name === 'schemaReviewSafeCard' })
          return lossy
        },
      }),
    }
    const controller = createMarkdownEditorController({
      profile: 'markdown-safe', source: { kind: 'local', value: '' }, plugins: [plugin],
    })
    controllers.push(controller)
    const host = document.createElement('div')
    document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Safe plugin guard' })

    expect(controller.dispatch(insert())).toBe(true)
    expect(controller.state.contentRevision).toBe(0)
    expect(controller.getValue()).toBe('')
    expect(controller.getProjection().rich.document).toEqual({ type: 'doc', content: [{ type: 'paragraph' }] })
  })

  it('rejects non-JSON plugin transactions and validates rich output after a filter bypass', () => {
    let editor!: Editor
    const plugin: MarkdownPlugin = {
      id: 'json-boundary', apiVersion: 1, schemaVersion: 1, profiles: ['rich'],
      create: () => ({
        extensions: [JsonBoundaryCard], commands: [setInvalidJson],
        onCreate: context => { editor = context.editor },
      }),
    }
    const initial: MarkdownRichValue = {
      format: 'atlas-rich', version: 1, plugins: [{ id: plugin.id, version: 1 }],
      document: { type: 'doc', content: [{ type: 'jsonBoundaryCard', attrs: { payload: 'valid' } }] },
    }
    const controller = rich(initial, [plugin])
    const host = document.createElement('div')
    document.body.append(host)
    controller.attachView(host, { ariaLabel: 'JSON boundary plugin' })
    const originalValue = controller.getValue()

    expect(controller.dispatch(setInvalidJson())).toBe(true)
    expect(controller.state.contentRevision).toBe(0)
    expect(controller.getValue()).toEqual(originalValue)

    const originalState = editor.state
    const invalid = editor.schema.node('doc', null,
      editor.schema.node('jsonBoundaryCard', { payload: 42n }))
    editor.view.updateState(EditorState.create({ schema: editor.schema, doc: invalid }))
    expect(() => controller.getValue()).toThrow(/JSON data only/)
    editor.view.updateState(originalState)
  })
})
