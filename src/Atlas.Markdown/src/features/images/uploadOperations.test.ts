// @vitest-environment jsdom

import { Editor, Extension } from '@tiptap/core'
import Image from '@tiptap/extension-image'
import StarterKit from '@tiptap/starter-kit'
import { Plugin } from '@tiptap/pm/state'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ImageUpload, setImageUploadService } from '../../extensions/ImageUpload'
import { prepareImageUpload } from './uploadOperations'
import { imageUploadErrorEvent, type UploadedImage } from './uploadService'

const editors: Editor[] = []
const file = new File(['image'], 'photo.png', { type: 'image/png' })

function createEditor(extra: Extension[] = []) {
  const editor = new Editor({
    element: document.createElement('div'),
    extensions: [StarterKit, Image, ImageUpload, ...extra],
    content: '<p>before</p><p>target</p><p>after</p>',
  })
  editors.push(editor)
  return editor
}

function deferredUpload() {
  let resolve!: (image: UploadedImage) => void
  let reject!: (error: Error) => void
  const promise = new Promise<UploadedImage>((yes, no) => { resolve = yes; reject = no })
  const upload = vi.fn((_file: File, _context: { signal: AbortSignal }) => promise)
  return { upload, resolve, reject }
}

function contents(editor: Editor) {
  return (editor.getJSON() as any).content?.map((node: any) => node.type === 'image' ? node.attrs?.src : node.content?.[0]?.text ?? '')
}

/** As y-tiptap 3.3 applies a collaborator's typing: one step replacing the whole top-level block it changed. */
function remoteInsert(editor: Editor, text: string, at: number) {
  const from = editor.state.doc.resolve(at).before(1), block = editor.state.doc.nodeAt(from)!
  const changed = editor.state.tr.insertText(text, at).doc.nodeAt(from)!
  editor.view.dispatch(editor.state.tr.replaceWith(from, from + block.nodeSize, changed).setMeta('y-sync$', { isChangeOrigin: true }))
}

afterEach(() => {
  editors.splice(0).forEach(editor => { if (!editor.isDestroyed) editor.destroy() })
  vi.restoreAllMocks()
})

describe('pending image insertions', () => {
  it('stands a placeholder in the document while the upload runs and removes it when the image arrives', async () => {
    const editor = createEditor()
    const pending = deferredUpload()
    const result = prepareImageUpload(editor, 9, pending.upload)!.upload(file)
    await vi.waitFor(() => expect(editor.view.dom.querySelector('.md-file-drop-preview')).not.toBeNull())
    pending.resolve({ url: '/done.png' })
    expect(await result).toBe(true)
    expect(editor.view.dom.querySelector('.md-file-drop-preview')).toBeNull()
  })

  it('cancels old work and routes later native uploads through a replacement service', async () => {
    const editor = createEditor()
    const previous = deferredUpload()
    const previousOperation = prepareImageUpload(editor, 9, previous.upload)!
    const previousResult = previousOperation.upload(file)
    const nextUpload = vi.fn(async () => ({ url: '/next-service.png' }))

    setImageUploadService(editor, nextUpload)
    expect(previousOperation.signal.aborted).toBe(true)
    previous.resolve({ url: '/stale-service.png' })
    expect(await previousResult).toBe(false)

    const paste = new Event('paste', { cancelable: true }) as ClipboardEvent
    Object.defineProperty(paste, 'clipboardData', { value: { items: [file], types: [] } })
    const handled = editor.view.someProp('handlePaste', handler => handler(editor.view, paste, editor.state.doc.slice(0, 0)))
    expect(handled).toBe(true)
    await vi.waitFor(() => expect(editor.view.dom.querySelector('img')?.getAttribute('src')).toBe('/next-service.png'))
    expect(nextUpload).toHaveBeenCalledTimes(1)
  })

  it('leaves the caret after an image that replaced the empty line it stood in, so typing goes on (UX PG4-6)', async () => {
    const editor = new Editor({ element: document.createElement('div'), extensions: [StarterKit, Image, ImageUpload], content: '<p>before</p><p></p><p>after</p>' })
    editors.push(editor)
    editor.commands.setTextSelection(9)
    const operation = prepareImageUpload(editor, 9, async () => ({ url: '/photo.png' }))!
    expect(await operation.upload(file)).toBe(true)
    expect(contents(editor)).toEqual(['before', '/photo.png', 'after'])
    expect(editor.state.selection.constructor.name).toBe('TextSelection')
    editor.commands.insertContent('x')
    expect(contents(editor)).toEqual(['before', '/photo.png', 'xafter'])
  })

  it('keeps a pending target across a collaborative rewrite of the whole document (UX PG4-6)', async () => {
    const editor = createEditor()
    const request = deferredUpload()
    const operation = prepareImageUpload(editor, 9, request.upload)!
    const result = operation.upload(file)
    // As y-prosemirror applies a remote update: one step replacing 0..size, here with "x" typed into the first paragraph.
    const rewritten = editor.state.doc.copy(editor.state.doc.content.replaceChild(0, editor.schema.nodes.paragraph!.create(null, editor.schema.text('beforex'))))
    editor.view.dispatch(editor.state.tr.replaceWith(0, editor.state.doc.content.size, rewritten.content))
    expect(operation.signal.aborted).toBe(false)
    request.resolve({ url: '/photo.png' })
    expect(await result).toBe(true)
    expect(contents(editor)).toEqual(['beforex', '/photo.png', 'target', 'after'])
  })

  it('keeps a pending target inside a block a collaborator types into', async () => {
    const editor = createEditor()
    const request = deferredUpload()
    const operation = prepareImageUpload(editor, 11, request.upload)!
    const result = operation.upload(file)
    remoteInsert(editor, 'x', 15)
    expect(operation.signal.aborted).toBe(false)
    request.resolve({ url: '/photo.png' })
    expect(await result).toBe(true)
    expect(contents(editor)).toEqual(['before', 'ta', '/photo.png', 'rgetx', 'after'])
  })

  it('maps edits before the target and preserves the current selection on completion', async () => {
    const editor = createEditor()
    const request = deferredUpload()
    const operation = prepareImageUpload(editor, 9, request.upload)!
    const result = operation.upload(file)
    editor.view.dispatch(editor.state.tr.insertText('prefix ', 1))
    editor.view.dispatch(editor.state.tr.delete(2, 4))
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    request.resolve({ url: '/photo.png' })

    expect(await result).toBe(true)
    expect(contents(editor)).toEqual(['pfix before', '/photo.png', 'target', 'after'])
    expect(editor.state.selection.$from.parent.textContent).toBe('after')
    expect(editor.getJSON().content?.find(node => node.type === 'image')?.attrs?.alt).toBe('photo.png')
    expect(request.upload.mock.calls[0][1].signal).toBe(operation.signal)
  })

  it('maps appended transactions as well as the transaction originally dispatched', async () => {
    const appendText = Extension.create({
      name: 'appendTextForTest',
      addProseMirrorPlugins() {
        return [new Plugin({
          appendTransaction(transactions, _oldState, state) {
            return transactions.some(transaction => transaction.getMeta('appendPrefix'))
              ? state.tr.insertText('prefix ', 1)
              : null
          },
        })]
      },
    })
    const editor = createEditor([appendText])
    const request = deferredUpload()
    const operation = prepareImageUpload(editor, 9, request.upload)!
    const result = operation.upload(file)
    editor.view.dispatch(editor.state.tr.setMeta('appendPrefix', true))
    request.resolve({ url: '/photo.png' })

    expect(await result).toBe(true)
    expect(contents(editor)).toEqual(['prefix before', '/photo.png', 'target', 'after'])
  })

  it('aborts when the target block is deleted and ignores a service that resolves after cancellation', async () => {
    const editor = createEditor()
    const onError = vi.fn()
    editor.view.dom.addEventListener(imageUploadErrorEvent, onError)
    const request = deferredUpload()
    const operation = prepareImageUpload(editor, 9, request.upload)!
    const result = operation.upload(file)
    editor.view.dispatch(editor.state.tr.delete(8, 16))

    expect(operation.signal.aborted).toBe(true)
    request.resolve({ url: '/too-late.png' })
    expect(await result).toBe(false)
    expect(contents(editor)).toEqual(['before', 'after'])
    expect(onError).not.toHaveBeenCalled()
  })

  it('cancels a reserved target before a file has been selected', async () => {
    const editor = createEditor()
    const request = deferredUpload()
    const operation = prepareImageUpload(editor, 9, request.upload)!
    operation.cancel()

    expect(operation.signal.aborted).toBe(true)
    expect(await operation.upload(file)).toBe(false)
    expect(request.upload).not.toHaveBeenCalled()
    expect(contents(editor)).toEqual(['before', 'target', 'after'])
  })

  it('cancels both requests and file-picker targets when the view is destroyed', async () => {
    const editor = createEditor()
    const request = deferredUpload()
    const operation = prepareImageUpload(editor, 9, request.upload)!
    const picker = prepareImageUpload(editor, 17, request.upload)!
    const result = operation.upload(file)
    const onError = vi.fn()
    editor.view.dom.addEventListener(imageUploadErrorEvent, onError)
    editor.destroy()

    expect(operation.signal.aborted).toBe(true)
    expect(picker.signal.aborted).toBe(true)
    request.reject(new Error('Request rejected after teardown'))
    expect(await result).toBe(false)
    expect(await picker.upload(file)).toBe(false)
    expect(onError).not.toHaveBeenCalled()
  })

  it('aborts pending work when the editor becomes readonly', async () => {
    const editor = createEditor()
    const request = deferredUpload()
    const operation = prepareImageUpload(editor, 9, request.upload)!
    const result = operation.upload(file)
    editor.setEditable(false)
    request.resolve({ url: '/too-late.png' })

    expect(operation.signal.aborted).toBe(true)
    expect(await result).toBe(false)
    expect(prepareImageUpload(editor, 9, request.upload)).toBeUndefined()
    expect(contents(editor)).toEqual(['before', 'target', 'after'])
  })

  it('isolates concurrent editor instances and cancels requests when their view unmounts', async () => {
    const first = createEditor()
    const second = createEditor()
    const firstRequest = deferredUpload()
    const secondRequest = deferredUpload()
    const firstOperation = prepareImageUpload(first, 9, firstRequest.upload)!
    const secondOperation = prepareImageUpload(second, 9, secondRequest.upload)!
    const firstResult = firstOperation.upload(file)
    const secondResult = secondOperation.upload(file)
    first.unmount()

    expect(firstOperation.signal.aborted).toBe(true)
    expect(secondOperation.signal.aborted).toBe(false)
    firstRequest.resolve({ url: '/first.png' })
    secondRequest.resolve({ url: '/second.png' })
    expect(await firstResult).toBe(false)
    expect(await secondResult).toBe(true)
    expect(contents(second)).toEqual(['before', '/second.png', 'target', 'after'])

    first.mount(document.createElement('div'))
    const fresh = prepareImageUpload(first, 9, async () => ({ url: '/fresh.png' }))!
    expect(await fresh.upload(file)).toBe(true)
    expect(contents(first)).toEqual(['before', '/fresh.png', 'target', 'after'])
  })

  it('reports failure once, releases the target, and permits another upload', async () => {
    const editor = createEditor()
    const onError = vi.fn()
    editor.view.dom.addEventListener(imageUploadErrorEvent, onError)
    const request = deferredUpload()
    const operation = prepareImageUpload(editor, 9, request.upload)!
    const result = operation.upload(file)
    request.reject(new Error('Network failure'))

    expect(await result).toBe(false)
    expect(onError).toHaveBeenCalledTimes(1)
    expect((onError.mock.calls[0][0] as CustomEvent).detail.message).toContain('Upload obrázku selhal')
    expect(await operation.upload(file)).toBe(false)
    const retry = prepareImageUpload(editor, 9, async () => ({ url: '/retry.png', alt: 'Retry' }))!
    expect(await retry.upload(file)).toBe(true)
    expect(contents(editor)).toEqual(['before', '/retry.png', 'target', 'after'])
  })

  it('does not put target reservation into undo history', async () => {
    const editor = createEditor()
    const operation = prepareImageUpload(editor, 9, async () => ({ url: '/photo.png' }))!
    expect(await operation.upload(file)).toBe(true)
    expect(editor.commands.undo()).toBe(true)
    expect(contents(editor)).toEqual(['before', 'target', 'after'])
  })
})
