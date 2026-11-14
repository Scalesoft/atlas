import { outlineStrings } from '../../ui/strings/outline'

/** A bounded, passive mirror of already rendered content. No second editor, plugin
 * runtime, network connection or awareness publisher is created for a preview. */
const excluded = 'script,style,link,iframe,object,embed,audio,video,button,textarea,select,[role="toolbar"],.md-context-toolbar,.md-context-popover,.md-diagram-source-editor,.column-resize-handle,.md-table-rails,.tt-table-controls,.md-image-controls'
let nextMirror = 0

export function previewSources(root: HTMLElement, heading: HTMLElement): HTMLElement[] {
  if (!root.contains(heading)) return []
  const sources = [heading]
  let current = heading
  let chars = heading.textContent?.length ?? 0
  while (sources.length < 8 && chars < 8000) {
    let next = current.nextElementSibling as HTMLElement | null
    while (!next && current.parentElement && current.parentElement !== root) {
      current = current.parentElement
      next = current.nextElementSibling as HTMLElement | null
    }
    if (!next || next.matches('h1,h2,h3,h4,h5,h6') || next.querySelector('h1,h2,h3,h4,h5,h6')) break
    sources.push(next); chars += next.textContent?.length ?? 0; current = next
  }
  return sources
}

export function createLiveOutlinePreview(root: HTMLElement, host: HTMLElement, heading: () => HTMLElement | null) {
  const win = root.ownerDocument.defaultView!
  const prefix = `md-outline-preview-${++nextMirror}-`
  let disposed = false
  let frame = 0
  let canvasTimer: number | undefined
  let canvases: Array<[HTMLCanvasElement, HTMLCanvasElement]> = []
  let observed: HTMLElement[] = []
  let lastFingerprint = ''
  const observer = new win.MutationObserver(records => {
    // Highlighting is local UI and must not recreate the mirrored component.
    if (records.some(record => record.attributeName !== 'data-md-outline-target')) schedule()
  })
  const resize = win.ResizeObserver ? new win.ResizeObserver(schedule) : null
  function copyCanvases() {
    for (const [source, target] of canvases) {
      if (!target.parentNode) continue
      try {
        if (target.width !== source.width) target.width = source.width
        if (target.height !== source.height) target.height = source.height
        const context = target.getContext('2d')
        context?.clearRect(0, 0, target.width, target.height)
        if (source.width && source.height) context?.drawImage(source, 0, 0)
      } catch { target.replaceWith(root.ownerDocument.createTextNode(outlineStrings.previewUnavailable)) }
    }
  }
  function render() {
    frame = 0
    if (disposed) return
    const selected = heading()
    const sources = selected ? previewSources(root, selected) : []
    if (sources.length !== observed.length || sources.some((source, i) => source !== observed[i])) {
      observer.disconnect(); resize?.disconnect(); observed = sources
      for (const source of sources) {
        observer.observe(source, { childList: true, subtree: true, characterData: true, attributes: true })
        resize?.observe(source)
      }
      lastFingerprint = ''
    }
    // Prose reflows into the small preview. Only spatial components retain their
    // source width; shrinking a whole page would make its text unreadable.
    host.style.width = '100%'
    const fingerprint = `${root.getBoundingClientRect().width}:` + sources.map(source => source.outerHTML.replace(/ data-md-outline-target="[^"]*"/g, '')).join('')
    if (fingerprint === lastFingerprint) { copyCanvases(); return }
    lastFingerprint = fingerprint
    let budget = 1600
    canvases = []
    const clone = (source: Node, depth = 0): Node | null => {
      if (--budget < 0 || depth > 32) return null
      if (source.nodeType === 3) return root.ownerDocument.createTextNode((source.textContent ?? '').slice(0, 8000))
      if (source.nodeType !== 1) return null
      const element = source as HTMLElement
      if (element.matches(excluded)) return null
      const target = element.cloneNode(false) as HTMLElement
      for (const attribute of [...target.attributes]) {
        const name = attribute.name
        if (name.startsWith('on') || name.startsWith('data-md-overlay') || ['contenteditable', 'autofocus', 'tabindex', 'data-md-outline-target', 'aria-live', 'aria-controls', 'aria-owns', 'name'].includes(name)) target.removeAttribute(name)
        else if (name === 'id') target.id = prefix + attribute.value
        else if (attribute.value.includes('url(#')) target.setAttribute(name, attribute.value.replace(/url\(#/g, `url(#${prefix}`))
        else if ((name === 'href' || name === 'xlink:href') && attribute.value.startsWith('#')) target.setAttribute(name, `#${prefix}${attribute.value.slice(1)}`)
      }
      // The preview is inert as a whole, including reader links and checkboxes.
      if (element.tagName === 'INPUT') {
        const input = target as HTMLInputElement
        input.disabled = true; input.checked = (element as HTMLInputElement).checked
      }
      target.classList.remove('ProseMirror-selectednode', 'selectedCell', 'md-outline-highlight')
      if (element.matches('table,[data-md-module]')) {
        target.style.width = `${Math.max(240, element.getBoundingClientRect().width)}px`
        target.style.maxWidth = 'none'
      }
      if (element.tagName === 'CANVAS') canvases.push([element as HTMLCanvasElement, target as HTMLCanvasElement])
      for (const child of [...source.childNodes]) { const next = clone(child, depth + 1); if (next) target.appendChild(next); if (budget < 0) break }
      return target
    }
    const fragment = root.ownerDocument.createDocumentFragment()
    for (const source of sources) { const next = clone(source); if (next) fragment.appendChild(next) }
    host.replaceChildren(fragment)
    copyCanvases()
    if (canvasTimer !== undefined) win.clearInterval(canvasTimer)
    canvasTimer = canvases.length ? win.setInterval(copyCanvases, 160) : undefined
  }
  function schedule() { if (!disposed && !frame) frame = win.requestAnimationFrame(render) }
  // Image load does not necessarily produce a DOM mutation.
  root.addEventListener('load', schedule, true)
  schedule()
  return {
    refresh: schedule,
    destroy() {
      disposed = true; observer.disconnect(); resize?.disconnect()
      if (frame) win.cancelAnimationFrame(frame)
      if (canvasTimer !== undefined) win.clearInterval(canvasTimer)
      root.removeEventListener('load', schedule, true)
      host.replaceChildren(); canvases = []; observed = []
    },
  }
}
