import type { EditorView } from '@tiptap/pm/view'
import { copyMarkdownOverlayTheme } from '../../core/overlayTheme'
import { registerMarkdownOverlayForElement } from '../../core/overlay'
import { dragStrings } from '../../ui/strings/drag'

// Preserve the rendered content without mounting another editor or plugin runtime.
const appearance = ['font', 'color', 'background-color', 'line-height', 'text-align', 'white-space',
  'padding', 'margin', 'border', 'border-radius', 'border-collapse', 'display', 'list-style-type', 'gap', 'grid-template-columns', 'position']
const control = [...appearance, 'top', 'right', 'width', 'height', 'max-width', 'overflow', 'text-overflow', 'vertical-align', 'translate',
  'appearance', 'opacity', 'visibility', 'align-items', 'justify-content', 'box-sizing', 'flex-shrink']

export function createDragCard(view: EditorView, blocks: HTMLElement[], options: { label?: string; width?: number; omit?: ReadonlySet<Element> } = {}) {
  const doc = view.dom.ownerDocument, win = doc.defaultView!
  const card = doc.createElement('div')
  card.className = 'md-drag-preview-clone'
  card.setAttribute('aria-hidden', 'true'); card.inert = true
  copyMarkdownOverlayTheme(view.dom, card)
  const label = doc.createElement('div'); label.className = 'md-drag-card-label'
  const count = blocks.length
  const section = /^H[1-6]$/.test(blocks[0]?.tagName ?? '') && count > 1
  label.textContent = options.label ?? (section ? dragStrings.sectionCountLabel(dragStrings.blockCountLabel(count)) : dragStrings.blockCountLabel(count))
  const crop = doc.createElement('div'); crop.className = 'md-drag-card-crop'
  const content = doc.createElement('div'); content.className = 'md-drag-card-content'
  const width = Math.max(160, options.width ?? view.dom.getBoundingClientRect().width)
  const previewWidth = Math.min(420, width, Math.max(160, win.innerWidth - 48))
  // Reflow long lines instead of shrinking an entire wide document into tiny text.
  const scale = Math.max(.85, previewWidth / width)
  card.style.width = `${previewWidth + 24}px`
  content.style.width = `${previewWidth / scale}px`; content.style.transform = `scale(${scale})`
  // Copy computed styles only for what the 190 px crop can show: a long section costs no more than its first screen.
  let shown = 0, height = 0
  for (const block of blocks.slice(0, 6)) {
    if (height * scale > 190) break
    height += block.getBoundingClientRect().height
    shown++
    const clone = block.cloneNode(true) as HTMLElement
    const originals = [block, ...block.querySelectorAll<HTMLElement>('*')]
    const copies = [clone, ...clone.querySelectorAll<HTMLElement>('*')]
    copies.forEach((copy, index) => {
      if (options.omit?.has(originals[index]!)) { copy.remove(); return }
      copy.removeAttribute('id'); copy.removeAttribute('contenteditable'); copy.removeAttribute('autofocus')
      copy.removeAttribute('data-testid'); copy.removeAttribute('data-dragging-source'); copy.removeAttribute('data-section-hover')
      copy.classList.remove('ProseMirror-selectednode', 'ProseMirror-selectednoderange')
      if (!copy.style) return
      const original = originals[index]!, style = win.getComputedStyle(original)
      // A control (a requirement's checkbox, its party chip, its state pill) is drawn by rules that match only inside the editor:
      // its box and placement too, and a hidden one stays hidden.
      for (const property of original.closest('button,input') ? control : appearance) copy.style.setProperty(property, style.getPropertyValue(property))
      if (copy instanceof win.HTMLInputElement) copy.checked = (original as HTMLInputElement).checked
      copy.style.animation = 'none'; copy.style.transition = 'none'
      if (copy instanceof win.HTMLCanvasElement && originals[index] instanceof win.HTMLCanvasElement) {
        try { copy.getContext('2d')?.drawImage(originals[index] as HTMLCanvasElement, 0, 0) } catch { /* keep the content's text fallback */ }
      }
    })
    clone.querySelectorAll('textarea,select,[role=toolbar],.md-context-toolbar,script,iframe').forEach(element => element.remove())
    clone.style.opacity = '1'; clone.style.transform = 'none'
    if (clone.tagName === 'TABLE') { clone.style.width = '100%'; clone.style.tableLayout = 'fixed' }
    content.append(clone)
  }
  crop.append(content); card.append(label, crop); doc.body.append(card)
  const release = registerMarkdownOverlayForElement(view.dom, card, { consumesEscape: false })
  const contentHeight = content.scrollHeight * scale
  crop.style.height = `${Math.min(190, Math.max(24, contentHeight))}px`
  crop.dataset.fade = String(contentHeight > 190 || count > shown)
  const cardHeight = card.offsetHeight
  function move(x: number, y: number) {
    // Position has no transition: the content stays attached to the pointer. `translate` only, no layout per move.
    const left = Math.max(8, Math.min(x + 18, win.innerWidth - previewWidth - 32))
    const top = Math.max(8, Math.min(y + 20, win.innerHeight - cardHeight - 8))
    card.style.translate = `${left}px ${top}px`
  }
  return { move, destroy() { release(); card.remove() } }
}
