// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Editor, Node } from '@tiptap/core'
import { MarkdownPluginRegistry } from '../core/plugins/registry'
import type { MarkdownPlugin } from '../core/plugins/types'
import { builtinMarkdownLossy, MarkdownDocumentCodec } from './documentCodec'
import { imagePresentation } from '../features/images/presentation'
import { createHeadingAnchorsPlugin } from '../plugins/heading-anchors'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownRichValue } from '../core'
import { registerCollaborationAdapter } from '../core/internal'

const editors: Editor[] = []
const controllers: MarkdownEditorController[] = []
afterEach(() => {
  controllers.splice(0).forEach(controller => controller.destroy())
  editors.splice(0).forEach(editor => editor.destroy())
  document.body.replaceChildren(); vi.restoreAllMocks()
})

describe('lazy document fidelity', () => {
  it('preserves plugin fidelity without invoking any content serializers', () => {
    const text = vi.fn(() => 'Probe text')
    const markdown = vi.fn((state: { write(value: string): void }) => { state.write('Probe markdown') })
    const lossy = vi.fn(() => true)
    const plugin: MarkdownPlugin = {
      id: 'test.lazy-fidelity', apiVersion: 1, schemaVersion: 1, profiles: ['rich'],
      create: () => ({
        extensions: [Node.create({
          name: 'fidelityProbe', group: 'block', atom: true,
          renderHTML: () => ['div'], renderText: text,
          addStorage: () => ({ markdown: { serialize: markdown } }),
        })],
        markdownLossy: lossy,
      }),
    }
    const registry = new MarkdownPluginRegistry([plugin], 'rich')
    const codec = new MarkdownDocumentCodec('rich', registry, { profile: 'rich' })
    const editor = new Editor({ element: null, extensions: codec.extensions,
      content: { type: 'doc', content: [{ type: 'fidelityProbe' }] } })
    editors.push(editor)
    const json = vi.spyOn(editor.state.doc, 'toJSON')

    expect(codec.getMarkdownLossy(editor.state.doc)).toBe(true)
    expect(codec.getMarkdownLossy(editor.state.doc)).toBe(true)
    expect(lossy).toHaveBeenCalledOnce()
    expect(text).not.toHaveBeenCalled()
    expect(markdown).not.toHaveBeenCalled()
    expect(json).not.toHaveBeenCalled()

    const projection = codec.project(editor, editor.state.doc)
    expect(projection).toMatchObject({ text: 'Probe text', markdown: 'Probe markdown', markdownLossy: true })
    expect(text).toHaveBeenCalledOnce()
    expect(markdown).toHaveBeenCalledOnce()
    expect(json).toHaveBeenCalledOnce()
    expect(lossy).toHaveBeenCalledOnce()
    // Public projections remain defensive copies even when fidelity was read first.
    ;(projection.rich.document.content as unknown as Array<{ type: string }>)[0].type = 'paragraph'
    expect(codec.project(editor, editor.state.doc).rich.document.content).toEqual([{ type: 'fidelityProbe' }])
    registry.destroy()
  })

  it('invalidates fidelity by the document and recomputes after returning to an older document', () => {
    const registry = new MarkdownPluginRegistry([], 'rich')
    const codec = new MarkdownDocumentCodec('rich', registry, { profile: 'rich' })
    const plain = codec.schema.nodeFromJSON({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Plain' }] }] })
    const underlined = codec.schema.nodeFromJSON({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Marked', marks: [{ type: 'underline' }] }] }] })
    expect(codec.getMarkdownLossy(plain)).toBe(false)
    expect(codec.getMarkdownLossy(underlined)).toBe(true)
    expect(codec.getMarkdownLossy(plain)).toBe(false)
    registry.destroy()
  })

  it('treats an image with an absent or default presentation as Markdown-safe', () => {
    const registry = new MarkdownPluginRegistry([], 'rich')
    const codec = new MarkdownDocumentCodec('rich', registry, { profile: 'rich' })
    const editor = new Editor({ element: null, extensions: codec.extensions })
    editors.push(editor)
    const image = (presentation?: unknown) => codec.schema.nodeFromJSON({ type: 'doc', content: [{ type: 'image', attrs: { src: '/a.png', alt: 'A', ...(presentation === undefined ? {} : { presentation }) } }] })
    const parsed = codec.parseMarkdown(editor, '![A](/a.png)')
    expect(parsed.child(0).attrs.presentation).toEqual(imagePresentation(null))
    for (const document of [parsed, image(), image(null), image({}), image({ ...imagePresentation(null) })]) {
      expect(codec.getMarkdownLossy(document)).toBe(false)
      expect(builtinMarkdownLossy(document)).toBe(false)
    }
    for (const presentation of [{ width: 50 }, { caption: 'Popisek' }, { naturalWidth: 640, naturalHeight: 480 }]) {
      expect(codec.getMarkdownLossy(image(presentation))).toBe(true)
    }
    registry.destroy()
  })

  it('ignores identity attributes only when asked, without caching that answer', () => {
    const registry = new MarkdownPluginRegistry([createHeadingAnchorsPlugin()], 'rich')
    const codec = new MarkdownDocumentCodec('rich', registry, { profile: 'rich' })
    const heading = (headingId: string | null) => codec.schema.nodeFromJSON({ type: 'doc', content: [{ type: 'heading', attrs: { level: 2, headingId }, content: [{ type: 'text', text: 'Nadpis' }] }] })
    const identified = heading('h-1')
    expect(codec.getMarkdownLossy(identified, { ignoreIdentity: true })).toBe(false)
    expect(codec.getMarkdownLossy(identified)).toBe(true)
    expect(codec.getMarkdownLossy(identified, { ignoreIdentity: true })).toBe(false)
    expect(codec.getMarkdownLossy(heading(null))).toBe(false)
    const aligned = codec.schema.nodeFromJSON({ type: 'doc', content: [{ type: 'heading', attrs: { level: 2, headingId: 'h-2', textAlign: 'right' }, content: [{ type: 'text', text: 'Vpravo' }] }] })
    expect(codec.getMarkdownLossy(aligned, { ignoreIdentity: true })).toBe(true)
    registry.destroy()
  })

  it('serializes a document or a single top-level block to Markdown', () => {
    const registry = new MarkdownPluginRegistry([], 'rich')
    const codec = new MarkdownDocumentCodec('rich', registry, { profile: 'rich' })
    const editor = new Editor({ element: null, extensions: codec.extensions })
    editors.push(editor)
    const document = codec.parseMarkdown(editor, '# Nadpis\n\nOdstavec s **tučným**.')
    expect(codec.serializeMarkdown(editor, document)).toBe('# Nadpis\n\nOdstavec s **tučným**.')
    expect(codec.serializeMarkdown(editor, document.child(1))).toBe('Odstavec s **tučným**.')
    registry.destroy()
  })

  it('still rejects invalid builtin attributes on a fidelity-only read', () => {
    const registry = new MarkdownPluginRegistry([], 'rich')
    const codec = new MarkdownDocumentCodec('rich', registry, { profile: 'rich' })
    const invalid = codec.schema.node('doc', null, codec.schema.node('paragraph', { textAlign: 'invalid' }))
    expect(() => codec.getMarkdownLossy(invalid)).toThrow(/alignment/)
    registry.destroy()
  })

  it('keeps local fidelity consistent before attachment, for buffered values, and after destruction', () => {
    const value = (underlined: boolean): MarkdownRichValue => ({ format: 'atlas-rich', version: 1, document: {
      type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Text',
        ...(underlined ? { marks: [{ type: 'underline' }] } : {}),
      }] }],
    } })
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: value(false) } })
    controllers.push(controller)
    expect(() => controller.getMarkdownLossy!()).toThrow(/Attach a view/)
    const host = document.createElement('div'); document.body.append(host)
    const detach = controller.attachView(host, { ariaLabel: 'Fidelity lifecycle' })
    expect(controller.getMarkdownLossy!()).toBe(false)
    detach()
    controller.setValue(value(true))
    expect(controller.getMarkdownLossy!()).toBe(true)
    expect(controller.getProjection().markdownLossy).toBe(true)
    controller.attachView(host, { ariaLabel: 'Fidelity remount' })
    expect(controller.getMarkdownLossy!()).toBe(true)
    controller.destroy()
    expect(controller.getMarkdownLossy!()).toBe(true)
    expect(controller.getProjection().markdownLossy).toBe(true)
  })

  it('requires an attached collaborative view and returns its last projection fidelity after destruction', () => {
    const adapter = registerCollaborationAdapter(publish => ({
      extensions: [], isRemoteTransaction: () => false,
      attachEditor: () => publish({ hydrated: true, writable: true }), detachEditor() {}, destroy() {},
    }))
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'collaborative', adapter } })
    controllers.push(controller)
    expect(() => controller.getMarkdownLossy!()).toThrow(/Attach a collaborative view/)
    const host = document.createElement('div'); document.body.append(host)
    const detach = controller.attachView(host, { ariaLabel: 'Collaborative fidelity' })
    expect(controller.getMarkdownLossy!()).toBe(controller.getProjection().markdownLossy)
    detach()
    expect(() => controller.getMarkdownLossy!()).toThrow(/Attach a collaborative view/)
    controller.destroy()
    expect(controller.getMarkdownLossy!()).toBe(controller.getProjection().markdownLossy)
  })
})
