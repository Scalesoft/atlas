// @vitest-environment jsdom

import { Editor } from '@tiptap/core'
import Image from '@tiptap/extension-image'
import StarterKit from '@tiptap/starter-kit'
import { Slice } from '@tiptap/pm/model'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ImageUpload, type ImageUploadOptions } from './ImageUpload'

const editors: Editor[] = []
const file = new File(['image'], 'photo.png', { type: 'image/png' })

function createEditor(options: Partial<ImageUploadOptions>) {
  const editor = new Editor({
    element: document.createElement('div'),
    extensions: [StarterKit, Image, ImageUpload.configure(options)],
    content: '<p>before</p><p>target</p>',
  })
  editors.push(editor)
  editor.commands.setTextSelection(9)
  return editor
}

function paste(editor: Editor, html = false) {
  const event = new Event('paste', { cancelable: true }) as ClipboardEvent
  Object.defineProperty(event, 'clipboardData', {
    value: {
      items: [{ kind: 'file', getAsFile: () => file }],
      types: html ? ['Files', 'text/html'] : ['Files'],
      getData: () => '',
    },
  })
  const handled = !!editor.view.someProp('handlePaste', handler => handler(editor.view, event, Slice.empty))
  return { event, handled }
}

afterEach(() => {
  editors.splice(0).forEach(editor => editor.destroy())
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('image upload input routes', () => {
  it('uses the host service before the legacy URL for paste', async () => {
    const upload = vi.fn(async () => ({ url: '/host.png' }))
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    const editor = createEditor({ upload, uploadUrl: '/legacy' })
    const result = paste(editor)

    expect(result.handled).toBe(true)
    expect(result.event.defaultPrevented).toBe(true)
    await vi.waitFor(() => expect(editor.getJSON().content?.[1].attrs?.src).toBe('/host.png'))
    expect(upload).toHaveBeenCalledWith(file, { signal: expect.any(AbortSignal) })
    expect(fetch).not.toHaveBeenCalled()
  })

  it('preserves the existing URL upload route and lets HTML paste continue', async () => {
    const fetch = vi.fn(async () => ({ ok: true, json: async () => ({ url: '/legacy.png' }) }))
    vi.stubGlobal('fetch', fetch)
    const editor = createEditor({ uploadUrl: '/legacy' })
    const result = paste(editor, true)

    expect(result.handled).toBe(false)
    expect(result.event.defaultPrevented).toBe(false)
    await vi.waitFor(() => expect(editor.getJSON().content?.[1].attrs?.src).toBe('/legacy.png'))
    expect(fetch).toHaveBeenCalledWith('/legacy', expect.objectContaining({ signal: expect.any(AbortSignal) }))
  })

  it('uses the drop position and does not intercept readonly paste or internal drag', async () => {
    const upload = vi.fn(async () => ({ url: '/drop.png' }))
    const editor = createEditor({ upload })
    editor.commands.setTextSelection(1)
    vi.spyOn(editor.view, 'posAtCoords').mockReturnValue({ pos: 9, inside: 8 })
    const event = new Event('drop', { cancelable: true }) as DragEvent
    Object.defineProperty(event, 'dataTransfer', { value: { files: [file] } })
    expect(editor.view.someProp('handleDrop', handler => handler(editor.view, event, Slice.empty, true))).toBeUndefined()
    expect(upload).not.toHaveBeenCalled()
    expect(editor.view.someProp('handleDrop', handler => handler(editor.view, event, Slice.empty, false))).toBe(true)
    await vi.waitFor(() => expect(editor.getJSON().content?.[1].attrs?.src).toBe('/drop.png'))

    editor.setEditable(false)
    expect(paste(editor).handled).toBe(false)
    expect(upload).toHaveBeenCalledTimes(1)
  })
})
