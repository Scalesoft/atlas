// @vitest-environment jsdom

import { Editor } from '@tiptap/core'
import Image from '@tiptap/extension-image'
import StarterKit from '@tiptap/starter-kit'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defaultImageUploadAccept, ImageUpload } from '../../extensions/ImageUpload'
import { pickAndUploadImage } from './imagePicker'

const editors: Editor[] = []
const file = new File(['image'], 'photo.png', { type: 'image/png' })

function createEditor() {
  const editor = new Editor({
    element: document.createElement('div'),
    extensions: [StarterKit, Image, ImageUpload],
    content: '<p>before</p><p>target</p>',
  })
  editors.push(editor)
  editor.commands.setTextSelection(9)
  return editor
}

function interceptPicker() {
  let input!: HTMLInputElement
  vi.spyOn(HTMLInputElement.prototype, 'click').mockImplementation(function (this: HTMLInputElement) {
    input = this
  })
  return {
    get input() { return input },
    selectFile() {
      Object.defineProperty(input, 'files', { value: [file] })
      input.dispatchEvent(new Event('change'))
    },
  }
}

afterEach(() => {
  editors.splice(0).forEach(editor => { if (!editor.isDestroyed) editor.destroy() })
  vi.restoreAllMocks()
})

describe('image file picker', () => {
  it('maps the target even while the native picker is still open', async () => {
    const editor = createEditor()
    const picker = interceptPicker()
    const upload = vi.fn(async () => ({ url: '/photo.png' }))
    pickAndUploadImage(editor, upload, defaultImageUploadAccept)
    editor.view.dispatch(editor.state.tr.insertText('prefix ', 1))
    editor.commands.setTextSelection(1)
    picker.selectFile()

    await vi.waitFor(() => expect(editor.getJSON().content?.map(node => node.type)).toEqual(['paragraph', 'image', 'paragraph']))
    expect((editor.getJSON() as any).content?.[0].content?.[0].text).toBe('prefix before')
    expect((editor.getJSON() as any).content?.[2].content?.[0].text).toBe('target')
    expect(editor.state.selection.from).toBe(1)
    expect(upload).toHaveBeenCalledTimes(1)
    expect(picker.input.onchange).toBeNull()
    expect(picker.input.oncancel).toBeNull()
  })

  it('releases picker callbacks when the target block is removed', async () => {
    const editor = createEditor()
    const picker = interceptPicker()
    const upload = vi.fn(async () => ({ url: '/photo.png' }))
    pickAndUploadImage(editor, upload, defaultImageUploadAccept)
    editor.view.dispatch(editor.state.tr.delete(8, 16))
    picker.selectFile()

    expect(picker.input.onchange).toBeNull()
    expect(picker.input.oncancel).toBeNull()
    expect(upload).not.toHaveBeenCalled()
    expect(editor.state.doc.textContent).toBe('before')
  })

  it('releases picker callbacks on native cancellation and teardown', () => {
    const editor = createEditor()
    const picker = interceptPicker()
    const upload = vi.fn(async () => ({ url: '/photo.png' }))
    pickAndUploadImage(editor, upload, defaultImageUploadAccept)
    picker.input.dispatchEvent(new Event('cancel'))
    expect(picker.input.onchange).toBeNull()
    expect(picker.input.oncancel).toBeNull()

    pickAndUploadImage(editor, upload, defaultImageUploadAccept)
    editor.destroy()
    expect(picker.input.onchange).toBeNull()
    expect(picker.input.oncancel).toBeNull()
    expect(upload).not.toHaveBeenCalled()
  })
})
