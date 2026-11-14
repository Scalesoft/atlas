// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Editor, JSONContent } from '@tiptap/core'
import { createMarkdownEditorController, type LocalMarkdownEditorController, type MarkdownPlugin } from '../../core'
import { imagePresentation } from './presentation'
import { exportImage } from './imageView'
import { downloadMarkdownFile } from '../modules/forms'
import { undoDepth } from '@tiptap/pm/history'

vi.mock('../modules/forms', async importOriginal => ({ ...await importOriginal<typeof import('../modules/forms')>(), downloadMarkdownFile: vi.fn() }))
const controllers: LocalMarkdownEditorController<'rich'>[] = []
function mount(presentation: Record<string, string | number | boolean> = {}) {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'image-test', apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor } }) }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [capture], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'image', attrs: { src: '/source.png', alt: 'Obrázek', presentation } }] } } } })
  controllers.push(controller); const container = document.createElement('div'); document.body.append(container); controller.attachView(container, { ariaLabel: 'Obrázek' })
  return { editor, controller, container, data: () => (controller.getValue().document as JSONContent).content![0]!.attrs!.presentation }
}
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.restoreAllMocks(); vi.clearAllMocks() })
describe('image viewport and editing lifetime', () => {
  it('edits the caption and alternative text where the image is shown: a click, Enter or F2, the same gesture closes them (P1)', async () => {
    const { container, editor } = mount()
    await Promise.resolve()
    const image = container.querySelector('img')!
    const panel = () => document.querySelector<HTMLElement>('.md-image-panel-text')
    image.click()
    expect(editor.state.selection.from).toBe(0)
    expect(panel()?.querySelector('textarea')?.value).toBe('Obrázek')
    image.click()
    expect(panel()).toBeNull()
    for (const key of ['Enter', 'F2']) {
      editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
      expect(panel(), key).not.toBeNull()
      document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
      expect(panel(), key).toBeNull()
    }
  })
  it('edits the caption where it is shown, and a double click zooms without closing and reopening the edit first', async () => {
    const { container, editor } = mount({ caption: 'Popisek' })
    await Promise.resolve()
    const caption = container.querySelector<HTMLElement>('figcaption')!
    const panel = () => document.querySelector<HTMLElement>('.md-image-panel-text')
    caption.click()
    expect(editor.state.selection.from).toBe(0)
    expect(caption.getAttribute('aria-expanded')).toBe('true')
    expect(panel()?.querySelector('textarea')?.value).toBe('Obrázek')
    caption.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true })); caption.click()
    expect(panel()).toBeNull()
    HTMLDialogElement.prototype.showModal ??= function () {}
    const image = container.querySelector('img')!
    const click = (detail: number) => image.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail }))
    click(1); expect(panel()).not.toBeNull()
    // The second click of the double click leaves the panel alone; the double click then closes it and zooms.
    click(2); expect(panel()).not.toBeNull()
    image.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true }))
    expect(panel()).toBeNull()
    expect(document.querySelector('dialog.md-image-zoom')).not.toBeNull()
  })
  it('retains image DOM without rewriting its attributes on selection-only transactions', async () => {
    const { editor, container } = mount()
    editor.commands.insertContentAt(editor.state.doc.content.size, { type: 'paragraph', content: [{ type: 'text', text: 'after' }] })
    await Promise.resolve()
    const image = container.querySelector('img')!, viewport = container.querySelector('.md-image-viewport')!
    const changes: MutationRecord[] = [], observer = new MutationObserver(records => changes.push(...records))
    observer.observe(image, { attributes: true }); observer.observe(viewport, { attributes: true })
    editor.commands.setTextSelection(2); await Promise.resolve(); observer.disconnect()
    expect(container.querySelector('img')).toBe(image); expect(changes).toEqual([])
  })
  it('learns natural dimensions without document mutation and persists them on explicit resize', () => {
    const { container, data } = mount({ width: 60, rotate: 90, cropLeft: 25 })
    const image = container.querySelector('img')!
    Object.defineProperties(image, { naturalWidth: { value: 1200 }, naturalHeight: { value: 800 } })
    image.dispatchEvent(new Event('load'))
    expect(data()).toEqual({ width: 60, rotate: 90, cropLeft: 25 })
    expect(container.querySelector<HTMLElement>('.md-image-viewport')!.style.aspectRatio.replace(/\s/g, '')).toBe('800/900')
    container.querySelector('.md-image-resize')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }))
    expect(data()).toMatchObject({ naturalWidth: 1200, naturalHeight: 800, width: 55, rotate: 90, cropLeft: 25 })
  })
  it('cancels a pointer preview when the editor becomes readonly without persisting its dimensions', () => {
    const { container, controller, data } = mount({ width: 50, naturalWidth: 1200, naturalHeight: 800 })
    const figure = container.querySelector<HTMLElement>('.md-image-view')!
    vi.spyOn(figure, 'getBoundingClientRect').mockReturnValue({ width: 1000 } as DOMRect)
    const event = (type: string, x: number) => { const event = new MouseEvent(type, { clientX: x, button: 0, bubbles: true }); Object.defineProperty(event, 'pointerId', { value: 7 }); return event }
    container.querySelector('.md-image-resize')!.dispatchEvent(event('pointerdown', 100))
    document.dispatchEvent(event('pointermove', 300))
    expect(container.querySelector<HTMLElement>('.md-image-viewport')!.style.width).toBe('90%')
    controller.setEditable(false); document.dispatchEvent(event('pointerup', 300))
    expect(data().width).toBe(50)
    expect(container.querySelector<HTMLElement>('.md-image-viewport')!.style.width).toBe('50%')
  })
  it('cancels resizing with Escape and adds no undo step for a handle click without movement', () => {
    const { container, editor, data } = mount({ width: 50, naturalWidth: 1200, naturalHeight: 800 })
    // Normalize the trailing paragraph before measuring the user's gesture.
    editor.view.dispatch(editor.state.tr.insert(1, editor.schema.nodes.paragraph!.create()).setMeta('addToHistory', false))
    const depth = undoDepth(editor.state)
    const figure = container.querySelector<HTMLElement>('.md-image-view')!
    vi.spyOn(figure, 'getBoundingClientRect').mockReturnValue({ width: 1000 } as DOMRect)
    const pointer = (type: string, x: number) => { const event = new MouseEvent(type, { clientX: x, button: 0, bubbles: true }); Object.defineProperty(event, 'pointerId', { value: 7 }); return event }
    const handle = container.querySelector('.md-image-resize-right')!
    handle.dispatchEvent(pointer('pointerdown', 100))
    document.dispatchEvent(pointer('pointermove', 200))
    expect(container.querySelector<HTMLElement>('.md-image-viewport')!.style.width).toBe('70%')
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    document.dispatchEvent(pointer('pointerup', 200))
    expect(data().width).toBe(50)
    expect(container.querySelector<HTMLElement>('.md-image-viewport')!.style.width).toBe('50%')
    expect(undoDepth(editor.state)).toBe(depth)
    handle.dispatchEvent(pointer('pointerdown', 100)); document.dispatchEvent(pointer('pointerup', 100))
    expect(undoDepth(editor.state)).toBe(depth)
  })
  it('serializes matching cropped viewport geometry for readonly HTML', () => {
    const { editor } = mount({ naturalWidth: 1200, naturalHeight: 800, cropLeft: 25, rotate: 90, caption: 'Popisek' })
    const rendered = document.createElement('div'); rendered.innerHTML = editor.getHTML()
    expect(rendered.querySelector<HTMLElement>('.md-image-viewport')!.style.aspectRatio.replace(/\s/g, '')).toBe('800/900')
    expect(rendered.querySelector<HTMLElement>('.md-image-crop')!.style.transform).toContain('rotate(90deg)')
    expect(rendered.querySelector('figcaption')!.textContent).toBe('Popisek')
    expect(rendered.querySelector('img')!.getAttribute('src')).toBe('/source.png')
  })
  it('keeps controls above the selected image and provides two proportional side handles', async () => {
    const { container } = mount()
    await Promise.resolve()
    expect(container.querySelector('.md-image-controls')).toBeNull()
    expect(container.querySelectorAll('.md-image-viewport > .md-image-resize')).toHaveLength(2)
    const toolbar = document.querySelector<HTMLElement>('.md-image-toolbar')!
    expect(toolbar.parentElement).toBe(document.body)
    expect(toolbar.querySelector('[aria-label="Oříznout obrázek"]')).not.toBeNull()
    expect(toolbar.querySelector('[aria-label="Vzhled obrázku"]')).not.toBeNull()
    expect(document.querySelector('dialog')).toBeNull()
  })
  it('previews effects without changing the document and cancels them on Escape', async () => {
    const { container, data } = mount({ brightness: 100 })
    await Promise.resolve()
    document.querySelector<HTMLButtonElement>('[aria-label="Vzhled obrázku"]')!.click()
    const range = document.querySelector<HTMLInputElement>('.md-image-panel input[name="brightness"]')!
    range.value = '140'; range.dispatchEvent(new Event('input', { bubbles: true }))
    expect(container.querySelector('img')!.style.filter).toContain('brightness(140%)')
    expect(data().brightness).toBe(100)
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    expect(document.querySelector('.md-image-panel')).toBeNull()
    expect(container.querySelector('img')!.style.filter).toContain('brightness(100%)')
    expect(data().brightness).toBe(100)
  })
  it('applies all effect adjustments as one undoable edit', async () => {
    const { editor, data } = mount({ brightness: 100, contrast: 100 })
    await Promise.resolve()
    document.querySelector<HTMLButtonElement>('[aria-label="Vzhled obrázku"]')!.click()
    for (const [key, value] of [['brightness', '135'], ['contrast', '120']]) {
      const input = document.querySelector<HTMLInputElement>(`.md-image-panel input[name="${key}"]`)!
      input.value = value!; input.dispatchEvent(new Event('input', { bubbles: true }))
    }
    document.querySelector<HTMLFormElement>('.md-image-panel form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    expect(data()).toMatchObject({ brightness: 135, contrast: 120 })
    expect(document.querySelector('.md-image-panel')).toBeNull()
    editor.commands.undo()
    expect(data()).toMatchObject({ brightness: 100, contrast: 100 })
  })
  it('keeps rapid consecutive applies separate from each other and adjacent typing', async () => {
    const { editor, data } = mount({ brightness: 100 })
    await Promise.resolve()
    editor.view.dispatch(editor.state.tr.insert(1, editor.schema.nodes.paragraph!.create(null, editor.schema.text('Base'))).setMeta('addToHistory', false))
    vi.spyOn(Date, 'now').mockReturnValue(1000)
    editor.view.dispatch(editor.state.tr.insertText('Before ', 2))
    for (const value of ['130', '150']) {
      document.querySelector<HTMLButtonElement>('[aria-label="Vzhled obrázku"]')!.click()
      const input = document.querySelector<HTMLInputElement>('.md-image-panel input[name=brightness]')!
      input.value = value; input.dispatchEvent(new Event('input', { bubbles: true }))
      document.querySelector<HTMLFormElement>('.md-image-panel form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    }
    editor.view.dispatch(editor.state.tr.insertText('After ', 2))
    editor.commands.undo()
    expect(data().brightness).toBe(150); expect(editor.state.doc.textContent).toBe('Before Base')
    editor.commands.undo()
    expect(data().brightness).toBe(130); expect(editor.state.doc.textContent).toBe('Before Base')
    editor.commands.undo()
    expect(data().brightness).toBe(100); expect(editor.state.doc.textContent).toBe('Before Base')
    editor.commands.undo()
    expect(editor.state.doc.textContent).toBe('Base')
  })
  it('applies size and alignment on the click and keeps the panel open for the next choice', async () => {
    const { data } = mount({ width: 60 })
    await Promise.resolve()
    document.querySelector<HTMLButtonElement>('[aria-label="Velikost a zarovnání"]')!.click()
    const choice = (text: string) => [...document.querySelectorAll<HTMLButtonElement>('.md-image-panel .md-image-choice')].find(button => button.textContent === text)!
    choice('25 %').click()
    expect(data().width).toBe(25)
    choice('Vpravo').click()
    expect(data().align).toBe('right')
    expect(document.querySelector('.md-image-panel')).not.toBeNull()
    expect(document.querySelector('.md-image-panel-apply')).toBeNull()
  })
  it('discards an open panel draft when editing becomes readonly', async () => {
    const { controller, data, container } = mount({ width: 60 })
    await Promise.resolve()
    document.querySelector<HTMLButtonElement>('[aria-label="Vzhled obrázku"]')!.click()
    const sepia = [...document.querySelectorAll<HTMLButtonElement>('.md-image-panel .md-image-choice')].find(button => button.textContent?.includes('Sépie'))!
    sepia.click()
    expect(data().filter).toBeUndefined()
    controller.setEditable(false)
    await Promise.resolve()
    expect(document.querySelector('.md-image-panel')).toBeNull()
    expect(container.querySelector<HTMLElement>('.md-image-viewport')!.style.width).toBe('60%')
    expect(data().filter).toBeUndefined()
  })
  it('calculates ratio crops from source geometry and persists only on apply', async () => {
    const { data } = mount({ naturalWidth: 1200, naturalHeight: 800 })
    await Promise.resolve()
    document.querySelector<HTMLButtonElement>('[aria-label="Oříznout obrázek"]')!.click()
    const square = Array.from(document.querySelectorAll<HTMLButtonElement>('.md-image-panel .md-image-choice')).find(button => button.textContent === '1 : 1')!
    square.click()
    expect(data().cropLeft).toBeUndefined()
    document.querySelector<HTMLFormElement>('.md-image-panel form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    expect(data().cropLeft).toBeCloseTo(16.6667, 3)
    expect(data().cropRight).toBeCloseTo(16.6667, 3)
  })
  it('switches resize handles for corner crop handles and keeps their draft until Apply', async () => {
    const { container, editor, data } = mount({ naturalWidth: 1200, naturalHeight: 800, cropLeft: 10, rotate: 90 })
    await Promise.resolve()
    document.querySelector<HTMLButtonElement>('[aria-label="Oříznout obrázek"]')!.click()
    expect(container.querySelector('.md-image-view')!.getAttribute('data-crop-mode')).toBe('true')
    expect([...container.querySelectorAll<HTMLButtonElement>('.md-image-resize')].every(handle => handle.hidden)).toBe(true)
    expect(container.querySelectorAll('.md-image-crop-handle:not([hidden])')).toHaveLength(4)
    expect(document.querySelector('.md-image-panel input[type=range]')).toBeNull()
    const handle = container.querySelector<HTMLButtonElement>('[data-corner=nw]')!
    handle.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }))
    handle.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
    expect(data()).not.toHaveProperty('cropBottom')
    expect(container.querySelector<HTMLElement>('.md-image-crop-selection')!.style.left).toBe('1%')
    document.querySelector<HTMLFormElement>('.md-image-panel form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    expect(data()).toMatchObject({ cropBottom: 1, cropLeft: 11 })
    expect(container.querySelectorAll('.md-image-crop-handle:not([hidden])')).toHaveLength(0)
    editor.commands.undo()
    expect(data()).toMatchObject({ cropLeft: 10 }); expect(data()).not.toHaveProperty('cropBottom')
  })
  it('keeps the crop panel through pointer gestures on the image, applies it on an outside click (D3) and drops it when readonly', async () => {
    const { container, controller, data } = mount({ naturalWidth: 1200, naturalHeight: 800 })
    await Promise.resolve()
    const viewport = container.querySelector<HTMLElement>('.md-image-viewport')!
    vi.spyOn(viewport, 'getBoundingClientRect').mockReturnValue({ width: 1000, height: 800 } as DOMRect)
    const pointer = (type: string, x: number, y: number) => { const event = new MouseEvent(type, { clientX: x, clientY: y, button: 0, bubbles: true }); Object.defineProperty(event, 'pointerId', { value: 7 }); return event }
    document.querySelector<HTMLButtonElement>('[aria-label="Oříznout obrázek"]')!.click()
    const handle = container.querySelector('[data-corner=se]')!
    handle.dispatchEvent(pointer('pointerdown', 1000, 800))
    document.dispatchEvent(pointer('pointermove', 800, 600))
    document.dispatchEvent(pointer('pointerup', 800, 600))
    expect(document.querySelector('.md-image-panel-crop')).not.toBeNull()
    expect(container.querySelector<HTMLElement>('.md-image-crop-selection')!.style.right).toBe('20%')
    expect(data()).not.toHaveProperty('cropRight')
    // A click outside a „Použít“ panel applies its changed draft (D3), as „Použít“ does.
    document.body.dispatchEvent(pointer('pointerdown', 1, 1))
    expect(document.querySelector('.md-image-panel-crop')).toBeNull()
    const applied = data().cropRight
    expect(applied).toBeGreaterThan(0)
    document.querySelector<HTMLButtonElement>('[aria-label="Oříznout obrázek"]')!.click()
    handle.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }))
    controller.setEditable(false)
    await Promise.resolve()
    expect(container.querySelector('.md-image-view')!.getAttribute('data-crop-mode')).toBe('false')
    expect(data().cropRight).toBe(applied)
  })
  it('decorates all more-menu actions and supports arrow-key navigation', async () => {
    mount(); await Promise.resolve()
    document.querySelector<HTMLButtonElement>('[aria-label="Další možnosti obrázku"]')!.click()
    const actions = [...document.querySelectorAll<HTMLButtonElement>('.md-image-menu-action')]
    expect(actions).toHaveLength(4)
    expect(actions.every(button => !!button.querySelector('svg[aria-hidden=true]'))).toBe(true)
    expect(document.querySelectorAll('.md-image-menu-separator')).toHaveLength(2)
    actions[0]!.focus(); actions[0]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))
    expect(document.activeElement).toBe(actions[1])
  })
  it('stores loaded replacement dimensions without adding another user undo step', async () => {
    const { editor, container, data } = mount({ naturalWidth: 1200, naturalHeight: 800 })
    await Promise.resolve()
    const depth = undoDepth(editor.state)
    document.querySelector<HTMLButtonElement>('[aria-label="Další možnosti obrázku"]')!.click()
    Array.from(document.querySelectorAll<HTMLButtonElement>('.md-image-menu-action')).find(button => button.textContent === 'Nahradit obrázek')!.click()
    const panel = document.querySelector('.md-image-panel-replace')!
    panel.querySelector('input')!.value = 'https://example.com/replacement.png'
    panel.querySelector('input')!.dispatchEvent(new Event('input', { bubbles: true }))
    panel.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    const image = container.querySelector('img')!
    Object.defineProperties(image, { naturalWidth: { value: 640 }, naturalHeight: { value: 480 } })
    image.dispatchEvent(new Event('load'))
    expect(data()).toMatchObject({ naturalWidth: 640, naturalHeight: 480 })
    expect(undoDepth(editor.state)).toBe(depth + 1)
    editor.commands.undo()
    expect(editor.state.doc.firstChild!.attrs.src).toBe('/source.png')
    expect(data()).toMatchObject({ naturalWidth: 1200, naturalHeight: 800 })
  })
})
describe('edited PNG export', () => {
  function canvasMocks() {
    const context = { translate: vi.fn(), rotate: vi.fn(), scale: vi.fn(), drawImage: vi.fn(), filter: '' }
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(context as unknown as CanvasRenderingContext2D)
    Object.defineProperty(HTMLImageElement.prototype, 'decode', { configurable: true, value: vi.fn().mockResolvedValue(undefined) })
    vi.spyOn(HTMLImageElement.prototype, 'naturalWidth', 'get').mockReturnValue(1200)
    vi.spyOn(HTMLImageElement.prototype, 'naturalHeight', 'get').mockReturnValue(800)
    const editor = { view: { dom: document.createElement('div') }, isDestroyed: false } as unknown as Editor
    return { context, editor }
  }
  it('exports the same source crop, rotation and flip without baking empty margins', async () => {
    const { context, editor } = canvasMocks()
    let size: number[] = []
    vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation(function (this: HTMLCanvasElement, callback) { size = [this.width, this.height]; callback(new Blob(['PNG'])) })
    await exportImage(editor, '/source.png', imagePresentation({ cropLeft: 10, cropRight: 15, cropTop: 25, rotate: 90, flip: true }), new AbortController().signal)
    expect(size).toEqual([600, 900])
    expect(context.drawImage).toHaveBeenCalledWith(expect.any(HTMLImageElement), 120, 200, 900, 600, -450, -300, 900, 600)
    expect(context.rotate).toHaveBeenCalledWith(Math.PI / 2)
    expect(context.scale).toHaveBeenLastCalledWith(-1, 1)
    expect(downloadMarkdownFile).toHaveBeenCalledTimes(1)
  })
  it('does not download an encoded result after the node has been removed or replaced', async () => {
    const { editor } = canvasMocks(); const abort = new AbortController(); let encode!: BlobCallback
    vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation(callback => { encode = callback })
    const pending = exportImage(editor, '/source.png', imagePresentation(null), abort.signal)
    await Promise.resolve(); abort.abort(); encode(new Blob(['PNG'])); await pending
    expect(downloadMarkdownFile).not.toHaveBeenCalled()
  })
})
