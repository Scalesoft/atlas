import type { Editor, NodeViewRendererProps } from '@tiptap/core'
import { NodeSelection } from '@tiptap/pm/state'
import { downloadMarkdownFile } from '../modules/forms'
import { isMarkdownComposition } from '../modules/composition'
import { observeMarkdownEditability } from '../modules/editability'
import { checkPixelBudget, imageSource, resourcePolicyFor } from './resourcePolicy'
import { runResourceTask } from './resourceQueue'
import { createMarkdownContextToolbar, openMarkdownContextPopover, markdownContextIcon, type MarkdownContextToolbar } from '../modules/contextToolbar'
import { registerMarkdownOverlay } from '../../core/overlay'
import { copyMarkdownOverlayTheme } from '../../core/overlayTheme'
import { defaultImagePresentation, imagePresentation, imageStyle, imageFilter, imageViewportStyle, imageCropStyle, imageGeometry, type ImagePresentation } from './presentation'
import { renderImagePanel, imagePanelTitles, type ImagePanel } from './imageControls'
import { dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'
import { imageCropInsets, moveImageCropCorner, type CropCorner } from './imageCrop'
import { markdownStrings } from '../../ui/strings'
import { imagesStrings } from '../../ui/strings/images'

export function createImageView({ node: initial, editor, getPos, view }: NodeViewRendererProps) {
  const doc = view.dom.ownerDocument
  let node = initial
  const dom = doc.createElement('figure'); dom.className = 'md-image-view'; dom.contentEditable = 'false'
  // Fetched as it nears the viewport (plan 3.6-G); the PDF path sets it eager, a browser print loads lazy images itself.
  const img = doc.createElement('img'); img.draggable = false; img.loading = 'lazy'
  const viewport = doc.createElement('span'); viewport.className = 'md-image-viewport'
  const crop = doc.createElement('span'); crop.className = 'md-image-crop'; crop.append(img); viewport.append(crop)
  const caption = doc.createElement('figcaption')
  const status = doc.createElement('span'); status.className = 'md-image-status'; status.setAttribute('role', 'status')
  const abort = new AbortController()
  let closePanel: (() => void) | undefined
  let closeZoom: (() => void) | undefined
  let toolbar: MarkdownContextToolbar | undefined
  let draft: ImagePresentation | undefined
  let persistDimensionsForSrc: string | undefined
  let exportRequest: AbortController | undefined
  let loadedSrc: string | undefined
  let rejectedSrc: string | undefined
  let stopDrag: (() => void) | undefined
  let cropping = false
  let keepingPanel = false
  let cropPanel: ReturnType<typeof renderImagePanel> | undefined
  let cropButton: HTMLButtonElement | undefined
  const presentation = () => {
    const p = imagePresentation(node.attrs.presentation)
    return loadedSrc === node.attrs.src && img.naturalWidth && img.naturalHeight ? { ...p, naturalWidth: img.naturalWidth, naturalHeight: img.naturalHeight } : p
  }
  const update = (attrs: Record<string, unknown>, history = true) => {
    const pos = getPos()
    if (abort.signal.aborted || pos === undefined || editor.isDestroyed || !editor.isEditable || editor.state.doc.nodeAt(pos)?.type.name !== 'image') return
    if (attrs.presentation && !imagePresentation(attrs.presentation).naturalWidth) persistDimensionsForSrc = String(attrs.src ?? node.attrs.src)
    const transaction = editor.state.tr.setNodeMarkup(pos, undefined, { ...editor.state.doc.nodeAt(pos)!.attrs, ...attrs })
    if (history) dispatchMarkdownHistoryOperation(editor, transaction)
    else editor.view.dispatch(transaction.setMeta('addToHistory', false))
  }
  const select = () => {
    const pos = getPos()
    if (abort.signal.aborted || editor.isDestroyed || pos === undefined || editor.state.doc.nodeAt(pos)?.type.name !== 'image') return
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, pos))); editor.view.focus(); toolbar?.refresh()
  }
  const zoom = () => {
    closePanel?.()
    closeZoom?.()
    const dialog = doc.createElement('dialog'); dialog.className = 'md-image-zoom'; dialog.setAttribute('aria-label', imagesStrings.zoomedImageLabel)
    const preview = viewport.cloneNode(true) as HTMLElement; preview.style.width = '100%'; preview.querySelectorAll('button').forEach(button => button.remove())
    const close = doc.createElement('button'); close.type = 'button'; close.textContent = markdownStrings.close; close.className = 'md-image-zoom-close'
    dialog.append(close, preview); copyMarkdownOverlayTheme(dom, dialog); doc.body.append(dialog)
    const unregister = registerMarkdownOverlay(editor, dialog, { modal: true })
    closeZoom = () => { unregister(); dialog.remove(); closeZoom = undefined }
    close.onclick = closeZoom; dialog.addEventListener('cancel', e => { e.preventDefault(); closeZoom?.() }); dialog.showModal()
  }
  const openPanel = (panel: ImagePanel, anchor: HTMLElement) => {
    const p = presentation()
    closePanel = openMarkdownContextPopover({ editor, anchor, positionAnchor: panel === 'crop' ? viewport : undefined, title: imagePanelTitles[panel], signal: abort.signal, className: `md-image-panel md-image-panel-${panel}`, interactionRoots: panel === 'crop' ? [viewport] : undefined,
      render(container, close) {
        cropping = panel === 'crop'; if (cropping) draft = p
        const controls = renderImagePanel({ container, panel, initial: p, src: node.attrs.src, alt: node.attrs.alt ?? '', close,
          preview(value) { draft = value; render() },
          apply(value, source, alt, keepOpen) {
            const src = imageSource(source, resourcePolicyFor(editor))
            const next = imagePresentation({ ...value, ...(src !== node.attrs.src ? { naturalWidth: 0, naturalHeight: 0 } : {}) })
            if (!keepOpen) close(); keepingPanel = !!keepOpen
            try { update({ src, alt, presentation: next }) } finally { keepingPanel = false }
          },
        })
        if (cropping) cropPanel = controls
        render()
        return () => { controls.destroy(); stopDrag?.(); cropPanel = undefined; cropping = false; draft = undefined; closePanel = undefined; render() }
      },
    })
  }
  const cropMask = doc.createElement('span'); cropMask.className = 'md-image-crop-mask'; cropMask.setAttribute('aria-hidden', 'true')
  const cropSelection = doc.createElement('span'); cropSelection.className = 'md-image-crop-selection'; cropMask.append(cropSelection); viewport.append(cropMask)
  const cropHandles: HTMLButtonElement[] = []
  for (const [corner, id, label] of [
    ['top-left', 'nw', imagesStrings.cornerTopLeft], ['top-right', 'ne', imagesStrings.cornerTopRight],
    ['bottom-left', 'sw', imagesStrings.cornerBottomLeft], ['bottom-right', 'se', imagesStrings.cornerBottomRight],
  ] as const) {
    const handle = doc.createElement('button'); handle.type = 'button'; handle.className = 'md-image-crop-handle'; handle.dataset.corner = id
    handle.setAttribute('aria-label', imagesStrings.cropCornerLabel(label)); handle.title = imagesStrings.dragCornerHint
    const change = (initial: ImagePresentation, dx: number, dy: number) => cropPanel?.setDraft(moveImageCropCorner(initial, corner as CropCorner, dx, dy))
    handle.onkeydown = event => {
      if (isMarkdownComposition(event, editor) || !cropping || !draft || !editor.isEditable || !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return
      event.preventDefault(); event.stopPropagation()
      const step = event.shiftKey ? .2 : 1
      change(draft, event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0, event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0)
    }
    handle.onpointerdown = event => {
      if (!cropping || !draft || !editor.isEditable || event.button !== 0) return
      event.preventDefault(); event.stopPropagation(); stopDrag?.(); handle.focus({ preventScroll: true })
      const bounds = viewport.getBoundingClientRect(); if (!bounds.width || !bounds.height) return
      const initialDraft = { ...draft }; const initialNode = node
      const live = () => cropping && !abort.signal.aborted && editor.isEditable && node.eq(initialNode)
      const move = (next: PointerEvent) => {
        if (next.pointerId !== event.pointerId) return
        if (!live()) { stopDrag?.(); return }
        change(initialDraft, (next.clientX - event.clientX) / bounds.width * 100, (next.clientY - event.clientY) / bounds.height * 100)
      }
      const finish = (next: PointerEvent) => { if (next.pointerId === event.pointerId) { move(next); stopDrag?.() } }
      const cancel = (next: PointerEvent) => { if (next.pointerId === event.pointerId) { stopDrag?.(); if (live()) cropPanel?.setDraft(initialDraft) } }
      stopDrag = () => { doc.removeEventListener('pointermove', move); doc.removeEventListener('pointerup', finish); doc.removeEventListener('pointercancel', cancel); stopDrag = undefined }
      doc.addEventListener('pointermove', move); doc.addEventListener('pointerup', finish); doc.addEventListener('pointercancel', cancel)
    }
    cropHandles.push(handle); viewport.append(handle)
  }
  const download = () => {
    exportRequest?.abort(); const request = new AbortController(); exportRequest = request
    status.textContent = imagesStrings.preparingImage
    void exportImage(editor, node.attrs.src, presentation(), request.signal).then(() => { if (!request.signal.aborted) status.textContent = '' })
      .catch(error => { if (!request.signal.aborted) status.textContent = error instanceof Error ? error.message : imagesStrings.exportFailed })
      .finally(() => { if (exportRequest === request) exportRequest = undefined })
  }
  const handles: HTMLButtonElement[] = []
  // Right first keeps the existing keyboard resize entry point stable.
  for (const side of ['right', 'left'] as const) {
    const resize = doc.createElement('button'); resize.type = 'button'; resize.className = `md-image-resize md-image-resize-${side}`
    resize.setAttribute('aria-label', side === 'right' ? imagesStrings.resizeWidthLabel : imagesStrings.resizeWidthFromLeftLabel); resize.title = imagesStrings.resizeHint
    resize.onkeydown = e => {
      if (isMarkdownComposition(e, editor)) return
      if (['ArrowLeft', 'ArrowRight'].includes(e.key) && editor.isEditable) {
        e.preventDefault(); e.stopPropagation(); const p = presentation(); const direction = (e.key === 'ArrowLeft' ? -1 : 1) * (side === 'left' ? -1 : 1)
        update({ presentation: { ...p, width: Math.max(10, Math.min(100, p.width + direction * (e.shiftKey ? 1 : 5))) } })
      }
    }
    resize.onpointerdown = event => {
      if (!editor.isEditable || abort.signal.aborted || event.button !== 0) return
      event.preventDefault(); event.stopPropagation(); stopDrag?.(); closePanel?.(); select()
      const startX = event.clientX; const p = presentation(); const initialNode = node
      const width = dom.getBoundingClientRect().width
      if (width <= 0) return
      let next = p.width
      dom.classList.add('md-image-resizing')
      const live = () => !abort.signal.aborted && editor.isEditable && node.eq(initialNode)
      const move = (e: PointerEvent) => {
        if (e.pointerId !== event.pointerId) return
        if (!live()) { cancel(); return }
        const multiplier = p.align === 'center' ? 2 : 1
        next = Math.max(10, Math.min(100, p.width + (e.clientX - startX) / width * 100 * (side === 'left' ? -1 : 1) * multiplier))
        viewport.style.width = `${next}%`; status.textContent = `${Math.round(next)} %`
      }
      const finish = (e: PointerEvent) => { if (e.pointerId !== event.pointerId) return; stopDrag?.(); if (live() && Math.round(next) !== p.width) update({ presentation: { ...p, width: Math.round(next) } }); render() }
      const cancel = (e?: PointerEvent) => { if (e && e.pointerId !== event.pointerId) return; stopDrag?.(); render() }
      const escape = (e: KeyboardEvent) => { if (!isMarkdownComposition(e, editor) && e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); cancel() } }
      stopDrag = () => { doc.removeEventListener('pointermove', move); doc.removeEventListener('pointerup', finish); doc.removeEventListener('pointercancel', cancel); doc.removeEventListener('keydown', escape, true); dom.classList.remove('md-image-resizing'); status.textContent = ''; stopDrag = undefined }
      doc.addEventListener('pointermove', move); doc.addEventListener('pointerup', finish); doc.addEventListener('pointercancel', cancel); doc.addEventListener('keydown', escape, true)
    }
    handles.push(resize); viewport.append(resize)
  }
  dom.append(viewport, caption, status)
  // The second click of a double click zooms (below) instead of closing the edit the first one opened.
  img.onclick = event => { select(); if (event.detail > 1) event.preventDefault() }
  img.ondblclick = zoom
  img.onload = () => {
    if (abort.signal.aborted || !img.naturalWidth || !img.naturalHeight) return
    try { checkPixelBudget(img.naturalWidth, img.naturalHeight, resourcePolicyFor(editor).maxImagePixels) }
    catch (error) { rejectedSrc = img.getAttribute('src') ?? undefined; img.removeAttribute('src'); status.textContent = (error as Error).message; return }
    loadedSrc = img.getAttribute('src') ?? undefined; render()
    if (persistDimensionsForSrc === node.attrs.src) { persistDimensionsForSrc = undefined; if (editor.isEditable) update({ presentation: presentation() }, false) }
  }
  function render() {
    if (abort.signal.aborted) return
    let source = ''
    try { source = imageSource(node.attrs.src, resourcePolicyFor(editor)) }
    catch (error) { status.textContent = (error as Error).message }
    if (source === rejectedSrc) source = ''
    if (img.getAttribute('src') !== source) { loadedSrc = undefined; if (source) img.setAttribute('src', source); else img.removeAttribute('src') }
    const current = draft ?? presentation()
    const p = cropping ? { ...current, cropLeft: 0, cropRight: 0, cropTop: 0, cropBottom: 0 } : current
    img.alt = node.attrs.alt ?? ''; img.style.cssText = imageStyle(p)
    viewport.style.cssText = imageViewportStyle(p); viewport.style.overflow = 'visible'; crop.style.cssText = imageCropStyle(p)
    caption.textContent = p.caption; caption.hidden = !p.caption
    dom.classList.toggle('md-image-readonly', !editor.isEditable)
    dom.dataset.cropMode = String(cropping)
    cropButton?.setAttribute('aria-pressed', String(cropping))
    handles.forEach(handle => { handle.hidden = !editor.isEditable || cropping; handle.setAttribute('aria-valuetext', `${Math.round(p.width)} %`) })
    cropMask.hidden = !cropping
    const insets = imageCropInsets(current)
    Object.assign(cropSelection.style, { left: `${insets.left}%`, top: `${insets.top}%`, right: `${insets.right}%`, bottom: `${insets.bottom}%` })
    cropHandles.forEach(handle => {
      handle.hidden = !cropping || !editor.isEditable
      const corner = handle.dataset.corner!
      Object.assign(handle.style, { left: `${corner.endsWith('w') ? insets.left : 100 - insets.right}%`, top: `${corner.startsWith('n') ? insets.top : 100 - insets.bottom}%` })
    })
  }
  const syncEditable = () => { if (!editor.isEditable) { stopDrag?.(); closePanel?.() }; if (!stopDrag) render() }
  const stopEditable = observeMarkdownEditability(editor, view.dom, syncEditable)
  // The edit where the image is shown (P1): a click on it or on its caption, Enter or F2 open its caption and alternative text there
  // (its size is the handles beside it); the same gesture closes them again (an open popover marks its anchor `aria-expanded`).
  const edit = (anchor: HTMLElement = viewport) => {
    // While cropping, a press on the picture belongs to the crop.
    if (cropping) return
    const again = anchor.getAttribute('aria-expanded') === 'true'
    closePanel?.()
    if (!again) openPanel('text', anchor)
  }
  caption.onclick = () => { if (editor.isEditable) { select(); edit(caption) } }
  queueMicrotask(() => {
    if (abort.signal.aborted || editor.isDestroyed) return
    toolbar = createMarkdownContextToolbar({ editor, anchor: viewport, getPos, label: imagesStrings.toolbarLabel, signal: abort.signal, placement: 'inside-top-end', onActivate: () => edit() })
    toolbar.element.classList.add('md-image-toolbar')
    toolbar.addButton({ label: imagePanelTitles.layout, icon: 'alignCenter', onClick: button => openPanel('layout', button) })
    toolbar.addSeparator()
    cropButton = toolbar.addButton({ label: imagePanelTitles.crop, icon: 'crop', onClick: button => openPanel('crop', button) })
    cropButton.setAttribute('aria-pressed', 'false')
    toolbar.addButton({ label: imagePanelTitles.effects, icon: 'sliders', onClick: button => openPanel('effects', button) })
    toolbar.addButton({ label: imagesStrings.rotateLabel, icon: 'rotate', onClick: () => { const p = presentation(); update({ presentation: { ...p, rotate: (p.rotate + 90) % 360 } }) } })
    toolbar.addButton({ label: imagesStrings.flipLabel, icon: 'flip', onClick: () => { const p = presentation(); update({ presentation: { ...p, flip: !p.flip } }) } })
    toolbar.addSeparator()
    toolbar.addButton({ label: imagePanelTitles.text, icon: 'text', onClick: button => openPanel('text', button) })
    toolbar.addButton({ label: imagesStrings.zoomLabel, icon: 'expand', onClick: zoom })
    toolbar.addButton({ label: imagesStrings.moreOptionsLabel, icon: 'more', onClick: anchor => {
      closePanel?.()
      closePanel = openMarkdownContextPopover({ editor, anchor, title: imagesStrings.moreOptionsTitle, variant: 'menu', signal: abort.signal, className: 'md-image-panel md-image-more', render(container, close) {
        const action = (label: string, icon: string, run: () => void, danger = false) => { const button = doc.createElement('button'); button.type = 'button'; button.setAttribute('role', 'menuitem'); button.className = `md-image-menu-action${danger ? ' danger' : ''}`; button.append(markdownContextIcon(doc, icon), label); button.onclick = () => { close(); run() }; container.append(button) }
        const separator = () => { const rule = doc.createElement('div'); rule.className = 'md-image-menu-separator'; rule.setAttribute('role', 'separator'); container.append(rule) }
        action(imagePanelTitles.replace, 'replace', () => openPanel('replace', anchor))
        action(imagesStrings.downloadEditedLabel, 'download', download)
        separator()
        action(imagesStrings.resetAppearanceLabel, 'reset', () => { const p = presentation(); update({ presentation: { ...defaultImagePresentation, caption: p.caption, naturalWidth: p.naturalWidth, naturalHeight: p.naturalHeight } }) })
        separator()
        action(imagesStrings.removeImageLabel, 'trash', () => { const pos = getPos(); if (pos !== undefined && editor.isEditable && !editor.isDestroyed) dispatchMarkdownHistoryOperation(editor, editor.state.tr.delete(pos, pos + node.nodeSize)) }, true)
        return () => { closePanel = undefined }
      } })
    } })
    toolbar.refresh()
  })
  render()
  return { dom, stopEvent: (event: Event) => !!(event.target as HTMLElement).closest('button'), ignoreMutation: () => true,
    update(next: typeof node) {
      if (next.type !== node.type) return false
      if (node.eq(next)) { node = next; return true }
      if (!node.eq(next)) { stopDrag?.(); exportRequest?.abort(); exportRequest = undefined; status.textContent = ''; closeZoom?.(); if (!keepingPanel) closePanel?.() }
      node = next; render(); return true
    },
    destroy() { abort.abort(); stopEditable(); exportRequest?.abort(); stopDrag?.(); closePanel?.(); closeZoom?.(); toolbar?.destroy(); img.onload = null },
  }
}
export async function exportImage(editor: Editor, src: string, p: ImagePresentation, signal: AbortSignal) {
  return runResourceTask(editor, signal, () => exportImageTask(editor, src, p, signal))
}
async function exportImageTask(editor: Editor, src: string, p: ImagePresentation, signal: AbortSignal) {
  if (signal.aborted || editor.isDestroyed) return
  const doc = editor.view.dom.ownerDocument
  const policy = resourcePolicyFor(editor)
  const image = doc.createElement('img'); image.crossOrigin = 'anonymous'; image.src = imageSource(src, policy)
  let canvas: HTMLCanvasElement | undefined
  try {
  const cancelLoad = () => image.removeAttribute('src')
  signal.addEventListener('abort', cancelLoad, { once: true })
  try { await image.decode() } catch { if (signal.aborted) return; throw new Error(imagesStrings.downloadRequiresCors) }
  finally { signal.removeEventListener('abort', cancelLoad) }
  if (signal.aborted) return
  checkPixelBudget(image.naturalWidth, image.naturalHeight, policy.maxImagePixels)
  const geometry = imageGeometry(p, image.naturalWidth, image.naturalHeight)
  if (!geometry) throw new Error(imagesStrings.invalidImageGeometry)
  const { cropWidth: sw, cropHeight: sh, sourceX, sourceY, outputWidth, outputHeight } = geometry
  checkPixelBudget(Math.max(1, Math.round(outputWidth)), Math.max(1, Math.round(outputHeight)), policy.maxCanvasPixels)
  canvas = doc.createElement('canvas')
  canvas.width = Math.max(1, Math.round(outputWidth)); canvas.height = Math.max(1, Math.round(outputHeight))
  const context = canvas.getContext('2d'); if (!context) throw new Error(imagesStrings.exportNotSupported)
  context.translate(canvas.width / 2, canvas.height / 2)
  context.scale(canvas.width / outputWidth, canvas.height / outputHeight)
  context.rotate(p.rotate * Math.PI / 180); context.scale(p.flip ? -1 : 1, 1); context.filter = imageFilter(p)
  context.drawImage(image, sourceX, sourceY, sw, sh, -sw / 2, -sh / 2, sw, sh)
  let blob: Blob | null
  try { blob = await new Promise<Blob | null>(resolve => canvas!.toBlob(resolve, 'image/png')) }
  catch { throw new Error(imagesStrings.downloadRequiresCors) }
  if (!signal.aborted && !editor.isDestroyed) {
    if (!blob) throw new Error(imagesStrings.pngCreationFailed)
    downloadMarkdownFile(doc, 'obrazek.png', blob)
  }
  } finally { image.removeAttribute('src'); if (canvas) { canvas.width = 0; canvas.height = 0 } }
}
