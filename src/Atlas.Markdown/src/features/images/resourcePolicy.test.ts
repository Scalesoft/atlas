// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Editor, createDocument } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin } from '../../core'
import { imageSource, normalizeResourcePolicy, checkFileBudget, checkPixelBudget, type MarkdownResourcePolicy } from './resourcePolicy'
import { createResourceQueue } from './resourceQueue'
import { imageHeaderDimensions, checkImageFileBudget } from './imageFileBudget'
import { prepareMarkdownValue } from '../../engine/staticRenderer'
import { handleMarkdownFileDrop, markdownFileDropErrorEvent } from './fileDrop'
import { prepareImageUpload } from './uploadOperations'
import { exportImage } from './imageView'
import { imagePresentation } from './presentation'

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.restoreAllMocks() })
const value = (src: string) => ({ format: 'atlas-rich' as const, version: 1 as const, document: { type: 'doc', content: [{ type: 'image', attrs: { src } }] } })
function setup(policy: MarkdownResourcePolicy, upload = vi.fn(async () => ({ url: '/valid.png' }))) {
  let editor!: Editor
  const plugin: MarkdownPlugin = { id: 'resource-test', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
  const controller = createMarkdownEditorController({ profile: 'rich', resourcePolicy: policy, imageUpload: upload,
    plugins: [plugin], source: { kind: 'local', value: value('/valid.png') } })
  controllers.push(controller); const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Resource policy' })
  return { controller, editor, host, upload }
}
describe('image source policy', () => {
  it.each(['javascript:alert(1)', 'data:image/svg+xml;base64,AAAA', 'file:///tmp/a.png', 'https://user:secret@host/image', '\\evil.test\\a', 'https://host/\na.png'])('rejects active or ambiguous source %s', src => {
    expect(() => imageSource(src)).toThrow()
    expect(() => prepareMarkdownValue(value(src), 'rich', [], document)).toThrow()
  })
  it('accepts compatible relative, HTTP, raster data and same-origin blob sources', () => {
    for (const src of ['/photo.png', '../photo.png', 'https://example.test/a.png', 'data:image/png;base64,AAAA', 'blob:https://example.test/123']) expect(imageSource(src)).toBe(src)
  })
  it('applies the same origin policy when parsing Markdown and HTML', () => {
    const policy = { imageSources: { allowedOrigins: [] } }
    expect(() => prepareMarkdownValue('![x](https://evil.test/image.png)', 'markdown-safe', [], document, policy)).toThrow()
    const { editor } = setup(policy)
    expect(() => createDocument('<img src="https://evil.test/image.png" alt="x">', editor.schema).check()).toThrow()
  })
  it('round-trips an allowed raster data image from rich through Markdown without losing the image', () => {
    const source = 'data:image/png;base64,AAAA', { controller } = setup({})
    controller.setValue(value(source))
    const projection = controller.getProjection()
    expect(projection.markdownLossy).toBe(false)
    const host = document.createElement('div')
    const prepared = prepareMarkdownValue(projection.markdown, 'markdown-safe', [], document)
    try { prepared.mount(host); expect(host.querySelector('img')?.getAttribute('src')).toBe(source) }
    finally { prepared.dispose() }
  })
  it('rejects forbidden data images after parsing instead of silently dropping them', () => {
    expect(() => prepareMarkdownValue('![x](data:image/png;base64,AAAA)', 'markdown-safe', [], document, { imageSources: { allowDataImages: false } })).toThrow()
    const { editor } = setup({})
    expect(() => createDocument('<img src="data:image/svg+xml;base64,AAAA">', editor.schema).check()).toThrow()
  })
  it('copies host policy and applies explicit origin/type restrictions', () => {
    const origins = ['https://example.test']
    const normalized = normalizeResourcePolicy({ imageSources: { allowedOrigins: origins, allowRelative: false, allowDataImages: false, allowBlobImages: false } })
    origins.push('https://evil.test')
    expect(normalized.imageSources!.allowedOrigins).toEqual(['https://example.test'])
    for (const src of ['/a', '//evil.test/a', '//example.test/a', 'data:image/png;base64,AAAA', 'blob:https://example.test/id']) expect(() => imageSource(src, normalized)).toThrow()
    expect(imageSource('https://example.test/a', normalized)).toBe('https://example.test/a')
  })
  it('keeps document and readonly target intact after rejected rich replacement', () => {
    const { controller } = setup({ imageSources: { allowedOrigins: ['https://example.test'] } })
    const before = controller.getValue()
    expect(() => controller.setValue(value('https://evil.test/x.png'))).toThrow()
    expect(controller.getValue()).toEqual(before)
    expect(() => prepareMarkdownValue(value('https://evil.test/x.png'), 'rich', [], document, { imageSources: { allowedOrigins: [] } })).toThrow()
  })
  it('rejects a direct image transaction even when commands bypass JSON conversion', () => {
    const { editor } = setup({ imageSources: { allowedOrigins: [] } })
    const before = editor.state.doc
    // ProseMirror validates the attributes when it creates a node (prosemirror-model 1.25.12), before any transaction exists...
    expect(() => editor.state.tr.setNodeMarkup(0, undefined, { ...before.firstChild!.attrs, src: 'https://evil.test/image' })).toThrow()
    // ...and the transaction filter still refuses a node whose attributes were changed after that check.
    const tr = editor.state.tr.setNodeMarkup(0, undefined, { ...before.firstChild!.attrs })
    ;(tr.doc.firstChild!.attrs as Record<string, unknown>).src = 'https://evil.test/image'
    editor.view.dispatch(tr)
    expect(editor.state.doc).toBe(before)
  })
  it('checks only the top-level blocks a transaction makes', () => {
    const { editor } = setup({ imageSources: { allowedOrigins: [] } })
    editor.view.dispatch(editor.state.tr.insert(editor.state.doc.content.size, editor.schema.nodes.paragraph.create()))
    // An accepted block is not walked again: its source changed in place (no transaction) does not refuse typing elsewhere...
    ;(editor.state.doc.firstChild!.attrs as Record<string, unknown>).src = 'https://evil.test/kept'
    editor.view.dispatch(editor.state.tr.insertText('typed', editor.state.doc.content.size - 1))
    expect(editor.state.doc.lastChild!.textContent).toBe('typed')
    // ...while a block the transaction makes is checked.
    const before = editor.state.doc
    expect(() => editor.schema.nodes.image.create({ src: 'https://evil.test/new' })).toThrow()
    const created = editor.schema.nodes.image.create({ src: '/new' })
    ;(created.attrs as Record<string, unknown>).src = 'https://evil.test/new'
    editor.view.dispatch(editor.state.tr.insert(0, created))
    expect(editor.state.doc).toBe(before)
  })
  it('revalidates mutable image attrs after an earlier valid transaction', () => {
    const { editor } = setup({ imageSources: { allowedOrigins: [] } })
    editor.view.dispatch(editor.state.tr.insertText('safe', editor.state.doc.content.size))
    ;(editor.state.doc.firstChild!.attrs as Record<string, unknown>).src = 'javascript:alert(1)'
    expect(() => editor.state.doc.check()).toThrow()
  })
  it('refuses invalid policies and budgets without truncating', () => {
    for (const limit of [0, -1, Infinity, NaN, 1.5]) expect(() => normalizeResourcePolicy({ maxFiles: limit })).toThrow()
    expect(() => checkFileBudget([{ size: 2 }, { size: 3 }], { maxTotalFileBytes: 4 })).toThrow()
    expect(() => checkFileBudget([{ size: 5 * 1024 * 1024 + 1, name: 'notes.md' }], {})).toThrow()
    expect(() => checkPixelBudget(10, 10, 100)).not.toThrow()
    expect(() => checkPixelBudget(11, 10, 100)).toThrow()
  })
  it('rejects a whole oversized file batch before any resolver runs', () => {
    const { editor, upload } = setup({ maxFiles: 1 })
    const errors = vi.fn(); editor.view.dom.addEventListener(markdownFileDropErrorEvent, errors)
    const event = new Event('drop', { cancelable: true }) as DragEvent
    Object.defineProperty(event, 'dataTransfer', { value: { files: [new File(['x'], 'a.png', { type: 'image/png' }), new File(['x'], 'b.png', { type: 'image/png' })] } })
    expect(handleMarkdownFileDrop(editor, event, 0)).toBe(true)
    expect(event.defaultPrevented).toBe(true); expect(upload).not.toHaveBeenCalled(); expect(errors).toHaveBeenCalledOnce()
  })
  it('rejects disallowed upload response without replacing existing content', async () => {
    const { editor } = setup({ imageSources: { allowedOrigins: [] } })
    const upload = prepareImageUpload(editor, 0, async () => ({ url: 'https://evil.test/upload' }))!
    const before = editor.state.doc.toJSON()
    expect(await upload.upload(new File(['x'], 'a.png'))).toBe(false)
    expect(editor.state.doc.toJSON()).toEqual(before)
  })
})
describe('bounded resource queue', () => {
  it('starts at most two jobs, preserves queued order and removes canceled queued work', async () => {
    const run = createResourceQueue(), done: Array<() => void> = [], started: number[] = []
    const controllers = Array.from({ length: 5 }, () => new AbortController())
    const results = controllers.map((controller, index) => run(controller.signal, () => { started.push(index); return new Promise<void>(resolve => done.push(resolve)) }))
    const canceled = expect(results[2]).rejects.toMatchObject({ name: 'AbortError' })
    expect(started).toEqual([0, 1]); controllers[2]!.abort(); await canceled
    done[1]!(); await results[1]; await Promise.resolve(); expect(started).toEqual([0, 1, 3])
    done[0]!(); await results[0]; await Promise.resolve(); expect(started).toEqual([0, 1, 3, 4])
    done[2]!(); done[3]!(); await Promise.all([results[3], results[4]])
  })
  it('releases a slot after synchronous throw and never starts pre-aborted work', async () => {
    const run = createResourceQueue(), controller = new AbortController(), work = vi.fn(async () => 1)
    await expect(run(controller.signal, () => { throw new Error('failed') })).rejects.toThrow('failed')
    controller.abort(); await expect(run(controller.signal, work)).rejects.toMatchObject({ name: 'AbortError' }); expect(work).not.toHaveBeenCalled()
    expect(await run(new AbortController().signal, work)).toBe(1)
  })
})
describe('metadata image bounds', () => {
  function png(width: number, height: number) {
    const bytes = new Uint8Array(24); bytes.set([137, 80, 78, 71, 13, 10, 26, 10]); bytes.set([73, 72, 68, 82], 12)
    const view = new DataView(bytes.buffer); view.setUint32(16, width); view.setUint32(20, height); return bytes
  }
  it('checks PNG metadata before decoding and caps the read', async () => {
    const slice = vi.fn(() => ({ arrayBuffer: async () => png(10000, 10000).buffer }))
    await expect(checkImageFileBudget({ slice } as unknown as File, { maxImagePixels: 100 }, new AbortController().signal)).rejects.toThrow('Rozměry')
    expect(slice).toHaveBeenCalledWith(0, 256 * 1024)
  })
  it('rejects truncated/unknown metadata under an opt-in pixel policy', async () => {
    expect(imageHeaderDimensions(new Uint8Array([0xff, 0xd8, 0xff, 0xe1, 0xff, 0xff]))).toBeNull()
    const file = { slice: () => ({ arrayBuffer: async () => new ArrayBuffer(0) }) } as unknown as File
    await expect(checkImageFileBudget(file, { maxImagePixels: 100 }, new AbortController().signal)).rejects.toThrow('hlavičky')
    await expect(checkImageFileBudget(file, {}, new AbortController().signal)).resolves.toBeUndefined()
  })
  it('reads GIF, JPEG and WebP dimensions using unsigned widths', () => {
    const gif = new Uint8Array([71, 73, 70, 56, 57, 97, 0xff, 0xff, 2, 0])
    expect(imageHeaderDimensions(gif)).toEqual({ width: 65535, height: 2 })
    expect(imageHeaderDimensions(new Uint8Array([255,216,255,192,0,8,8,0,20,0,30,0]))).toEqual({ width: 30, height: 20 })
    const webp = new Uint8Array(30); webp.set(new TextEncoder().encode('RIFF')); webp.set(new TextEncoder().encode('WEBPVP8X'), 8); webp[24] = 99; webp[27] = 49
    expect(imageHeaderDimensions(webp)).toEqual({ width: 100, height: 50 })
  })
  it('refuses a canvas allocation above its configured pixel budget', async () => {
    const { editor } = setup({ maxCanvasPixels: 99 })
    Object.defineProperty(HTMLImageElement.prototype, 'decode', { configurable: true, value: vi.fn(async () => {}) })
    vi.spyOn(HTMLImageElement.prototype, 'naturalWidth', 'get').mockReturnValue(10)
    vi.spyOn(HTMLImageElement.prototype, 'naturalHeight', 'get').mockReturnValue(10)
    const canvas = vi.spyOn(HTMLCanvasElement.prototype, 'getContext')
    await expect(exportImage(editor, '/valid.png', imagePresentation({}), new AbortController().signal)).rejects.toThrow('Rozměry')
    expect(canvas).not.toHaveBeenCalled()
    Reflect.deleteProperty(HTMLImageElement.prototype, 'decode')
  })
})
