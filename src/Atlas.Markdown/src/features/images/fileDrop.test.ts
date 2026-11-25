// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Editor, JSONContent } from '@tiptap/core'
import {
  createMarkdownEditorController, markdownImportNoticeEvent,
  type MarkdownEditorController, type MarkdownImportHandler, type MarkdownImportNoticeDetail, type MarkdownPlugin,
} from '../../core'
import { createAttachmentsPlugin } from '../../plugins/attachments'
import { createDiagramsPlugin } from '../../plugins/diagrams'
import { createMathPlugin } from '../../plugins/math'
import { cancelMarkdownFileDrops, canHandleMarkdownFiles, handleMarkdownFileDrop, markdownFileDropErrorEvent, markdownFileDropStateEvent } from './fileDrop'
import type { ImageUploadService } from './uploadService'
import type { AttachmentUploadService } from '../../plugins/attachments'
import { markdownStrings } from '../../ui/strings'

// Routing is under test, not diagram rendering. The real mermaid is a ~5000-module ESM graph that the diagram preview
// imports lazily; Node links it synchronously, so the import stalled whichever later test was running (4+ s on a loaded
// 64-CPU host) and tripped that test's wall-clock waits. Rendering is covered in plugins/diagrams with the same mock.
vi.mock('mermaid', () => ({ default: { initialize() {}, render: async () => ({ svg: '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"></svg>' }) } }))

const foreignWiki = import.meta.glob<string>('../../engine/fixtures/foreign-wiki/*.md', { eager: true, query: '?raw', import: 'default' })
const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.restoreAllMocks(); vi.unstubAllGlobals() })
function setup(upload?: ImageUploadService, attachment?: AttachmentUploadService, diagrams = true) {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
  const controller = createMarkdownEditorController({ profile: 'rich', imageUpload: upload,
    plugins: [capture, ...(attachment ? [createAttachmentsPlugin({ upload: attachment })] : []), ...(diagrams ? [createDiagramsPlugin()] : [])],
    source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: ['before', 'target', 'after'].map(text => ({ type: 'paragraph', content: [{ type: 'text', text }] })) } } },
  })
  controllers.push(controller); const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Files' })
  return { editor, controller }
}
function drop(editor: Editor, files: File[], position = 9) {
  const event = new Event('drop', { bubbles: true, cancelable: true }) as DragEvent
  Object.defineProperty(event, 'dataTransfer', { value: { files, types: ['Files'] } })
  return { event, handled: handleMarkdownFileDrop(editor, event, position) }
}
const nodes = (editor: Editor): JSONContent[] => editor.getJSON().content ?? []
/** As y-tiptap 3.3 applies a collaborator's typing: one step replacing the whole top-level block it changed. */
function remoteInsert(editor: Editor, text: string, at: number) {
  const from = editor.state.doc.resolve(at).before(1), block = editor.state.doc.nodeAt(from)!
  const changed = editor.state.tr.insertText(text, at).doc.nodeAt(from)!
  editor.view.dispatch(editor.state.tr.replaceWith(from, from + block.nodeSize, changed).setMeta('y-sync$', { isChangeOrigin: true }))
}

describe('whole-editor file drop routing', () => {
  it('shows typed previews immediately without serializing them, follows typing, and replaces them in one undo step', async () => {
    let complete!: (value: { url: string }) => void
    const revoke = vi.fn()
    vi.stubGlobal('URL', class extends URL {
      static createObjectURL() { return 'blob:local-photo' }
      static revokeObjectURL = revoke
    })
    const { editor } = setup(() => new Promise(resolve => { complete = resolve }), {
      upload: async file => ({ name: file.name, url: '/report.pdf' }),
    })
    const original = editor.getJSON()
    drop(editor, [new File(['png'], 'PHOTO.PNG'), new File(['pdf'], 'report.pdf', { type: 'application/pdf' }),
      new File(['hello'], 'notes.txt'), new File(['flowchart LR\nA-->B'], 'flow.mmd')])
    const preview = editor.view.dom.querySelector('.md-file-drop-preview')!
    expect(preview).not.toBeNull()
    expect([...preview.querySelectorAll('[data-file-kind]')].map(node => node.getAttribute('data-file-kind')))
      .toEqual(['image', 'attachment', 'text', 'diagram'])
    expect(preview.querySelector('img')?.getAttribute('src')).toBe('blob:local-photo')
    expect(preview.textContent).toContain('report.pdf')
    expect(preview.getAttribute('aria-busy')).toBe('true')
    expect(editor.getJSON()).toEqual(original)
    editor.view.dispatch(editor.state.tr.insertText('prefix ', 1))
    expect(editor.view.dom.querySelector('.md-file-drop-preview')).toBe(preview)
    complete({ url: '/photo.png' })
    await vi.waitFor(() => expect(nodes(editor).some(node => node.type === 'attachment')).toBe(true))
    expect(editor.view.dom.querySelector('.md-file-drop-preview')).toBeNull()
    expect(revoke).toHaveBeenCalledExactlyOnceWith('blob:local-photo')
    expect(nodes(editor).map(node => node.type)).toEqual(['paragraph', 'image', 'attachment', 'paragraph', 'diagram', 'paragraph', 'paragraph'])
    editor.commands.undo()
    expect(editor.getText()).toBe('prefix before\n\ntarget\n\nafter')
    expect(editor.view.dom.querySelector('.md-file-drop-preview')).toBeNull()
  })

  it.each(['cancel', 'readonly', 'delete', 'destroy', 'failure'] as const)('cleans previews and local image URLs on %s', async reason => {
    let reject!: (error: Error) => void
    let signal!: AbortSignal
    const revoke = vi.fn()
    vi.stubGlobal('URL', class extends URL {
      static createObjectURL() { return 'blob:pending-photo' }
      static revokeObjectURL = revoke
    })
    const { editor, controller } = setup((_file, context) => {
      signal = context.signal
      return new Promise((_resolve, fail) => { reject = fail })
    })
    const dom = editor.view.dom
    drop(editor, [new File(['png'], 'photo.png', { type: 'image/png' })])
    expect(dom.querySelector('.md-file-drop-preview')).not.toBeNull()
    if (reason === 'cancel') cancelMarkdownFileDrops(editor)
    if (reason === 'readonly') editor.setEditable(false)
    if (reason === 'delete') editor.view.dispatch(editor.state.tr.delete(8, 16))
    if (reason === 'destroy') controller.destroy()
    if (reason === 'failure') reject(new Error('Offline'))
    await vi.waitFor(() => expect(revoke).toHaveBeenCalledExactlyOnceWith('blob:pending-photo'))
    if (reason !== 'destroy') expect(dom.querySelector('.md-file-drop-preview')).toBeNull()
    if (reason !== 'failure') expect(signal.aborted).toBe(true)
  })

  it('previews unsupported image types and disabled diagram modules as attachments', async () => {
    const { editor } = setup(undefined, { upload: () => new Promise(() => {}) }, false)
    drop(editor, [new File(['svg'], 'icon.svg', { type: 'image/svg+xml' }), new File(['graph'], 'flow.mmd')])
    const cards = editor.view.dom.querySelectorAll('[data-file-kind="attachment"]')
    expect(cards).toHaveLength(2)
    expect(editor.view.dom.querySelector('.md-file-drop-preview img')).toBeNull()
  })

  it('announces pending file counts and clears them on completion, failure and cancellation', async () => {
    const jobs: { complete: (value: { url: string }) => void; signal: AbortSignal }[] = []
    const upload = (_file: File, { signal }: { signal: AbortSignal }) => new Promise<{ url: string }>(complete => jobs.push({ complete, signal }))
    const { editor } = setup(upload)
    const states: number[] = []
    editor.view.dom.addEventListener(markdownFileDropStateEvent, event => states.push((event as CustomEvent).detail.pending))
    const file = new File(['png'], 'photo.png', { type: 'image/png' })
    drop(editor, [file, file]); expect(states).toEqual([2])
    jobs[1]!.complete({ url: '/second.png' }); jobs[0]!.complete({ url: '/first.png' })
    await vi.waitFor(() => expect(states).toEqual([2, 0]))
    drop(editor, [new File(['unknown'], 'file.bin')]); await vi.waitFor(() => expect(states).toEqual([2, 0, 1, 0]))
    drop(editor, [file]); editor.setEditable(false)
    await vi.waitFor(() => expect(states).toEqual([2, 0, 1, 0, 1, 0]))
    expect(jobs[2]!.signal.aborted).toBe(true); jobs[2]!.complete({ url: '/stale.png' })
  })
  it('recognizes image extensions without MIME and extensionless text/plain safely', async () => {
    const upload = vi.fn(async () => ({ url: '/files/photo.png' }))
    const { editor } = setup(upload)
    drop(editor, [new File(['png'], 'PHOTO.PNG'), new File(['<b>literal</b>'], 'README', { type: 'text/plain' })])
    await vi.waitFor(() => expect(editor.getText()).toContain('<b>literal</b>'))
    expect(upload).toHaveBeenCalledOnce(); expect(nodes(editor)[1]!.type).toBe('image')
    const imported = nodes(editor)[2]!
    expect(imported.content).toEqual([{ type: 'text', text: '<b>literal</b>' }])
  })
  it('imports mixed files in original order once despite out-of-order upload completion and intervening typing', async () => {
    let imageDone!: (value: { url: string }) => void
    const image = vi.fn((_file: File, _context: { signal: AbortSignal }) => new Promise<{ url: string }>(resolve => { imageDone = resolve }))
    const upload = vi.fn(async (file: File) => ({ name: file.name, url: '/files/report.pdf' }))
    const { editor } = setup(image, { upload })
    const files = [new File(['png'], 'photo.png', { type: 'image/png' }), new File(['# Imported'], 'notes.md'),
      new File(['**literal**\nnext'], 'plain.txt'), new File(['flowchart LR\nA-->B'], 'flow.mmd'), new File(['pdf'], 'report.pdf', { type: 'application/pdf' })]
    const result = drop(editor, files)
    expect(result.handled).toBe(true); expect(result.event.defaultPrevented).toBe(true)
    expect(handleMarkdownFileDrop(editor, result.event, 0)).toBe(true)
    expect(image).toHaveBeenCalledOnce(); expect(upload).not.toHaveBeenCalled()
    editor.view.dispatch(editor.state.tr.insertText('prefix ', 1))
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    imageDone({ url: '/files/photo.png' })
    await vi.waitFor(() => expect(nodes(editor).some(node => node.type === 'attachment')).toBe(true))
    expect(upload).toHaveBeenCalledOnce()
    expect(nodes(editor).map(node => node.type)).toEqual(['paragraph', 'image', 'heading', 'paragraph', 'paragraph', 'diagram', 'attachment', 'paragraph', 'paragraph'])
    expect(nodes(editor)[0]!.content![0]!.text).toBe('prefix before')
    expect(nodes(editor)[3]!.content![0]!.text).toBe('**literal**')
    expect(nodes(editor)[5]!.attrs!.data.source).toContain('A-->B')
    expect(editor.state.selection.$from.parent.textContent).toBe('after')
    editor.commands.undo()
    expect(nodes(editor).map(node => node.type)).toEqual(['paragraph', 'paragraph', 'paragraph'])
    expect(nodes(editor)[0]!.content![0]!.text).toBe('prefix before')
  })

  it('inserts the image when a collaborator types into the block holding the target', async () => {
    let complete!: (value: { url: string }) => void; let signal!: AbortSignal
    const { editor } = setup((_file, context) => { signal = context.signal; return new Promise(resolve => { complete = resolve }) })
    const states: number[] = []
    editor.view.dom.addEventListener(markdownFileDropStateEvent, event => states.push((event as CustomEvent).detail.pending))
    // The target is inside „target“, after „ta“; the collaborator's character lands at the paragraph's end.
    drop(editor, [new File(['png'], 'photo.png', { type: 'image/png' })], 11)
    remoteInsert(editor, 'x', 15)
    expect(signal.aborted).toBe(false)
    complete({ url: '/photo.png' })
    await vi.waitFor(() => expect(states).toEqual([1, 0]))
    expect(nodes(editor).map(node => node.type === 'image' ? node.attrs!.src : node.content?.[0]?.text)).toEqual(['before', 'ta', '/photo.png', 'rgetx', 'after'])
  })

  it('cancels pending mixed imports when the mapped target is deleted or editor becomes readonly', async () => {
    let complete!: (value: { url: string }) => void; let signal!: AbortSignal
    const upload = vi.fn((_file: File, context: { signal: AbortSignal }) => { signal = context.signal; return new Promise<{ url: string }>(resolve => { complete = resolve }) })
    const { editor } = setup(upload)
    const image = new File(['png'], 'photo.png', { type: 'image/png' })
    drop(editor, [image]); editor.view.dispatch(editor.state.tr.delete(8, 16))
    expect(signal.aborted).toBe(true); complete({ url: '/stale.png' }); await Promise.resolve()
    expect(nodes(editor).some(node => node.type === 'image')).toBe(false)
    drop(editor, [image], 1); editor.setEditable(false)
    await vi.waitFor(() => expect(signal.aborted).toBe(true))
    complete({ url: '/readonly.png' }); await Promise.resolve()
    expect(nodes(editor).some(node => node.type === 'image')).toBe(false)
    expect(drop(editor, [image]).handled).toBe(false)
    expect(canHandleMarkdownFiles(editor, [image])).toBe(false)
  })

  it('reports unsupported and invalid files while keeping successful imports and static plugin boundaries', async () => {
    const { editor } = setup(undefined, undefined, false)
    const error = vi.fn(); editor.view.dom.addEventListener(markdownFileDropErrorEvent, error)
    const result = drop(editor, [new File(['flowchart LR\nA-->B'], 'diagram.mermaid'), new File(['kept'], 'notes.txt')])
    expect(result.handled).toBe(true)
    await vi.waitFor(() => expect(editor.getText()).toContain('kept'), { timeout: 3000 })
    expect(error).toHaveBeenCalledOnce()
    expect((error.mock.calls[0]![0] as CustomEvent).detail.fileName).toBe('diagram.mermaid')
    expect(editor.schema.nodes.diagram).toBeUndefined()
    const textDrag = new Event('drop', { cancelable: true }) as DragEvent
    Object.defineProperty(textDrag, 'dataTransfer', { value: { files: [], types: ['text/plain'] } })
    expect(handleMarkdownFileDrop(editor, textDrag)).toBe(false); expect(textDrag.defaultPrevented).toBe(false)
  })

  it('imports GitHub wiki pages with GFM tables and H4-H6 headings instead of rejecting the whole file', async () => {
    const { editor } = setup(undefined, undefined, false)
    const error = vi.fn(); editor.view.dom.addEventListener(markdownFileDropErrorEvent, error)
    const page = '# Deployment Guide\n\n| Service | Port |\n|:--------|-----:|\n| api     | 8080 |\n\n#### Rollback\n\n##### Details\n\n###### Fine print\n\nRun the\nrollback job.'
    drop(editor, [new File([page], 'Deployment-Guide.md')])
    await vi.waitFor(() => expect(nodes(editor).some(node => node.type === 'table')).toBe(true))
    expect(error).not.toHaveBeenCalled()
    const imported = nodes(editor).slice(1, -2)
    expect(imported.map(node => [node.type, node.attrs?.level])).toEqual([
      ['heading', 1], ['table', undefined], ['heading', 4], ['heading', 5], ['heading', 6], ['paragraph', undefined],
    ])
    expect(imported.at(-1)!.content).toEqual([{ type: 'text', text: 'Run the rollback job.' }])
    editor.commands.undo()
    expect(nodes(editor).map(node => node.type)).toEqual(['paragraph', 'paragraph', 'paragraph'])
  })

  it('strips Wiki.js front matter from a dropped page and lets the host drop the duplicated title heading', async () => {
    const onMarkdownImport = vi.fn(({ title, leadingHeading }: { title?: string; leadingHeading?: string }) => ({ omitLeadingHeading: title === leadingHeading }))
    let editor!: Editor
    const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [capture], onMarkdownImport,
      source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } } })
    controllers.push(controller); const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Files' })
    drop(editor, [new File(['---\ntitle: Incident Policy\npublished: true\ntags: ops, incidents\n---\n\n# Incident Policy\nSeverity 1 incidents page the on-call engineer.'], 'incident-policy.md')], 1)
    await vi.waitFor(() => expect(editor.getText()).toContain('Severity 1'))
    expect(onMarkdownImport).toHaveBeenCalledExactlyOnceWith({ source: 'file', fileName: 'incident-policy.md', title: 'Incident Policy',
      leadingHeading: 'Incident Policy', frontMatter: { title: 'Incident Policy', published: true, tags: 'ops, incidents' } })
    expect(nodes(editor).map(node => node.type)).not.toContain('heading')
    expect(nodes(editor).map(node => node.type)).not.toContain('horizontalRule')
    expect(editor.getText()).not.toContain('published')
  })

  it('maps a dropped Obsidian page onto callouts, links and plugin nodes and reports what stayed text', async () => {
    const onMarkdownImport = vi.fn<MarkdownImportHandler>()
    let editor!: Editor
    const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [capture, createMathPlugin()], onMarkdownImport,
      resolveWikiLink: ({ target }) => target === 'Project Alpha' ? { href: '/wiki/project-alpha' } : null,
      resolveEmbed: ({ target }) => target === 'whiteboard.png' ? { src: '/files/whiteboard.png' } : null,
      source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } } })
    controllers.push(controller); const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Files' })
    const notices: MarkdownImportNoticeDetail[] = []
    host.addEventListener(markdownImportNoticeEvent, event => notices.push((event as CustomEvent<MarkdownImportNoticeDetail>).detail))
    const error = vi.fn(); editor.view.dom.addEventListener(markdownFileDropErrorEvent, error)
    drop(editor, [new File([foreignWiki['../../engine/fixtures/foreign-wiki/obsidian-meeting-notes.md']!], 'Meeting Notes.md')], 1)
    await vi.waitFor(() => expect(nodes(editor).some(node => node.type === 'callout')).toBe(true))
    expect(error).not.toHaveBeenCalled()
    expect(nodes(editor).map(node => node.type)).toEqual(['heading', 'paragraph', 'paragraph', 'image', 'paragraph', 'image', 'paragraph', 'callout', 'heading', 'callout', 'paragraph', 'taskList', 'mathBlock', 'codeBlock', 'heading', 'paragraph'])
    expect(nodes(editor).filter(node => node.type === 'image').map(node => node.attrs!.src)).toEqual(['/files/whiteboard.png', 'images/whiteboard.png'])
    expect(nodes(editor)[1]!.content!.find(node => node.marks?.some(mark => mark.type === 'link'))).toMatchObject({ text: 'Project Alpha' })
    const degraded = [
      { kind: 'wiki-link', count: 1, excerpt: '[[Nonexistent Page]]' },
      { kind: 'embed', count: 1, excerpt: '![[Project Alpha#Summary]]' },
      { kind: 'comment', count: 1, excerpt: '%%hidden comment%%' },
    ]
    expect(onMarkdownImport).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ source: 'file', fileName: 'Meeting Notes.md', title: 'Meeting Notes', degraded }))
    expect(notices).toEqual([{ source: 'file', fileName: 'Meeting Notes.md', degraded }])
    editor.commands.undo()
    expect(nodes(editor).map(node => node.type)).toEqual(['paragraph'])
  })

  it('imports dropped ```mermaid and ```math fences as plugin nodes, else as code blocks', async () => {
    const fences = '```mermaid\nflowchart LR\n  A --> B\n```\n\n```math\nx^2\n```'
    const withDiagrams = setup()
    drop(withDiagrams.editor, [new File([fences], 'fences.md')])
    await vi.waitFor(() => expect(nodes(withDiagrams.editor).some(node => node.type === 'diagram')).toBe(true))
    expect(nodes(withDiagrams.editor).find(node => node.type === 'diagram')!.attrs!.data).toEqual({ version: 1, source: 'flowchart LR\n  A --> B', caption: '' })
    // No math plugin here: the strict file parser falls back to the wrapped code block.
    expect(nodes(withDiagrams.editor).find(node => node.type === 'codeBlock')!.attrs!.language).toBe('math')
    const without = setup(undefined, undefined, false)
    drop(without.editor, [new File([fences], 'fences.md')])
    await vi.waitFor(() => expect(nodes(without.editor).filter(node => node.type === 'codeBlock').map(node => node.attrs!.language)).toEqual(['mermaid', 'math']))
  })

  it('reports a readable localized message instead of the engine error when Markdown still cannot be imported', async () => {
    const { editor } = setup(undefined, undefined, false)
    const error = vi.fn(); editor.view.dom.addEventListener(markdownFileDropErrorEvent, error)
    const parser = (editor.storage as unknown as { markdown: { parser: { parse(text: string): string } } }).markdown.parser
    vi.spyOn(parser, 'parse').mockReturnValue('<p>Kept?</p><marquee>unsupported</marquee>')
    drop(editor, [new File(['# Anything'], 'broken.md')])
    await vi.waitFor(() => expect(error).toHaveBeenCalledOnce())
    const detail = (error.mock.calls[0]![0] as CustomEvent).detail
    expect(detail.message).toBe(`broken.md: ${markdownStrings.markdownImportFailed}`)
    expect(detail.message).not.toContain('tiptap')
    expect(detail.error.cause).toBeInstanceOf(Error)
    expect(nodes(editor).map(node => node.type)).toEqual(['paragraph', 'paragraph', 'paragraph'])
  })

  it('uses attachment fallback for Mermaid files when diagrams are not enabled and aborts on destroy', async () => {
    let signal!: AbortSignal; let complete!: (value: { name: string; url: string }) => void
    const upload = vi.fn((_file: File, context: { signal: AbortSignal }) => { signal = context.signal; return new Promise<{ name: string; url: string }>(resolve => { complete = resolve }) })
    const { editor, controller } = setup(undefined, { upload }, false)
    drop(editor, [new File(['flowchart LR\nA-->B'], 'flow.mmd')])
    expect(upload).toHaveBeenCalledOnce(); controller.destroy(); expect(signal.aborted).toBe(true)
    complete({ name: 'flow.mmd', url: '/files/flow.mmd' }); await Promise.resolve()
  })
})
