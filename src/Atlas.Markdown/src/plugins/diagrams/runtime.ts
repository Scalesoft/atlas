import { markdownStrings, addMarkdownReadActions, copyMarkdownText, zoomMarkdownBlock, createMarkdownModuleLoader, isMarkdownComposition, retainMarkdownDraft, takeMarkdownDraft, trackMarkdownComposition, type MarkdownModuleContext, type MarkdownModuleRuntime } from '../../core'
import { diagramsStrings } from './runtimeStrings'
import { DIAGRAM_SOURCE_LIMIT, normalizeDiagram, type MarkdownDiagramData } from './index'
import styles from './style.css?inline'

const loadMermaid = createMarkdownModuleLoader(async () => (await import('mermaid')).default)
let nextDiagram = 0
let rendering: Promise<unknown> = Promise.resolve()
const editors = new WeakMap<HTMLElement, (anchor?: HTMLElement) => void>()

async function decodedImage(image: HTMLImageElement, signal: AbortSignal): Promise<void> {
  if (signal.aborted) throw new DOMException('Diagram cancelled.', 'AbortError')
  if (typeof image.decode === 'function') { await image.decode(); return }
  if (image.complete && image.naturalWidth > 0) return
  await new Promise<void>((resolve, reject) => {
    const cleanup = () => { image.removeEventListener('load', loaded); image.removeEventListener('error', failed); signal.removeEventListener('abort', aborted) }
    const loaded = () => { cleanup(); resolve() }
    const failed = () => { cleanup(); reject(new Error('Generated diagram image could not be decoded.')) }
    const aborted = () => { cleanup(); reject(new DOMException('Diagram cancelled.', 'AbortError')) }
    image.addEventListener('load', loaded, { once: true }); image.addEventListener('error', failed, { once: true }); signal.addEventListener('abort', aborted, { once: true })
  })
}

/**
 * Mermaid sizes its SVG to the container (`width="100%"`, `useMaxWidth`). As an image such an SVG has no natural
 * size, so a two-node flow was stretched to the whole column with huge text. The viewBox size becomes its natural
 * size; the stylesheet then caps it at the column width (and at 640 px height), keeping the proportions.
 */
export function naturalSizeSvg(svg: string, view: (Window & typeof globalThis) | null): string {
  if (!view?.DOMParser || !view.XMLSerializer) return svg
  const parsed = new view.DOMParser().parseFromString(svg, 'image/svg+xml')
  const root = parsed.documentElement
  if (root?.localName !== 'svg' || parsed.getElementsByTagName('parsererror').length) return svg
  const box = (root.getAttribute('viewBox') ?? '').trim().split(/[\s,]+/).map(Number)
  const width = box[2] ?? 0, height = box[3] ?? 0
  if (box.length !== 4 || !box.every(Number.isFinite) || width <= 0 || height <= 0) return svg
  root.setAttribute('width', String(Math.ceil(width))); root.setAttribute('height', String(Math.ceil(height)))
  ;(root as unknown as SVGSVGElement).style?.removeProperty('max-width')
  return new view.XMLSerializer().serializeToString(root)
}

/** One 1-px canvas per document turns a computed colour the browser keeps as oklch() or color-mix() into sRGB; false without one. */
const pixels = new WeakMap<Document, CanvasRenderingContext2D | false>()
function pixelOf(doc: Document) {
  let pixel = pixels.get(doc)
  if (pixel === undefined) pixels.set(doc, pixel = doc.createElement('canvas').getContext?.('2d', { willReadFrequently: true }) ?? false)
  return pixel
}

/** Inline custom properties of a style attribute (`--accent: …`): the only part of an inline style a theme lives in. */
const customProperties = (style: string | null) => (style ?? '').split(';').map(part => part.trim()).filter(part => part.startsWith('--')).join(';')
/**
 * Whether a change of an ancestor can change the diagram's theme: its class or data-theme, or a custom property of its inline style.
 * A board pans, zooms and drags by restyling ancestors (transform, position) on every frame; reading the theme after each of those
 * forced a full style recalculation per diagram, tens of milliseconds each on a board of components (owner, 28. 10.: slow boards).
 */
function themeMutation(record: MutationRecord) {
  if (record.attributeName !== 'style') return true
  return customProperties(record.oldValue) !== customProperties((record.target as Element).getAttribute('style'))
}

const hex = (channels: ArrayLike<number>) => `#${Array.from(channels).slice(0, 3).map(channel => Math.min(255, Number(channel)).toString(16).padStart(2, '0')).join('')}`

/** Mermaid derives further colors from hex values, so normalize editor CSS colors first. */
function diagramTheme(container: HTMLElement) {
  const doc = container.ownerDocument, view = doc.defaultView
  const style = view?.getComputedStyle(container)
  const probe = doc.createElement('span'); probe.hidden = true; container.append(probe)
  const color = (token: string, fallback: string) => {
    const value = style?.getPropertyValue(token).trim() || fallback
    if (/^#[\da-f]{6}$/i.test(value)) return value
    if (/^#[\da-f]{3}$/i.test(value)) return `#${value.slice(1).split('').map(character => character.repeat(2)).join('')}`
    probe.style.color = value
    const computed = view?.getComputedStyle(probe).color ?? ''
    const rgb = computed.match(/^rgba?\(\s*(\d+)[,\s]+(\d+)[,\s]+(\d+)/)
    if (rgb) return hex(rgb.slice(1, 4).map(Number))
    // A browser keeps a computed oklch() or color-mix() colour as it is, not as rgb(): one painted pixel gives it in sRGB.
    const pixel = computed ? pixelOf(doc) : false
    if (!pixel) return fallback
    pixel.clearRect(0, 0, 1, 1)
    pixel.fillStyle = computed
    pixel.fillRect(0, 0, 1, 1)
    const painted = pixel.getImageData(0, 0, 1, 1).data
    return painted[3] ? hex(painted) : fallback
  }
  const background = color('--paper', '#ffffff'), primary = color('--accent-soft', '#e3f4f7')
  const secondary = color('--bg-2', '#edf5f6'), border = color('--accent-line', '#9dcbd6')
  const text = color('--text', '#102f36'), muted = color('--text-dim', '#49676e')
  probe.remove()
  return {
    fontFamily: style?.getPropertyValue('--font-sans').trim() || 'Inter, ui-sans-serif, sans-serif', fontSize: '14px',
    background, primaryColor: primary, primaryBorderColor: border, primaryTextColor: text,
    secondaryColor: secondary, secondaryBorderColor: border, secondaryTextColor: text,
    tertiaryColor: background, tertiaryBorderColor: border, tertiaryTextColor: text,
    textColor: text, lineColor: muted, edgeLabelBackground: background,
    clusterBkg: secondary, clusterBorder: border, nodeBorder: border,
    actorBkg: primary, actorBorder: border, actorTextColor: text, actorLineColor: border,
    signalColor: muted, signalTextColor: text, labelBoxBkgColor: secondary, labelBoxBorderColor: border,
    labelTextColor: text, loopTextColor: text, noteBkgColor: secondary, noteTextColor: text, noteBorderColor: border,
    activationBkgColor: primary, activationBorderColor: border,
  }
}

export const diagramRuntime: MarkdownModuleRuntime<MarkdownDiagramData> = {
  // The drawing is read: a click selects the block, Enter or a click on its source opens the source panel.
  editOnClick: false,
  render(initial) {
    let context = initial
    const { container, signal } = context, doc = container.ownerDocument, view = doc.defaultView
    const style = doc.createElement('style'); style.textContent = styles
    const figure = doc.createElement('figure'); figure.className = 'md-diagram'
    const visual = doc.createElement('div'); visual.className = 'md-diagram-visual'
    const fallback = doc.createElement('pre'); fallback.textContent = context.data.source; visual.append(fallback)
    const caption = doc.createElement('figcaption')
    const status = doc.createElement('p'); status.className = 'md-diagram-status'; status.setAttribute('role', 'status')
    const details = doc.createElement('details'), summary = doc.createElement('summary'), sourcePreview = doc.createElement('pre')
    summary.textContent = diagramsStrings.sourceSummary; details.append(summary, sourcePreview)
    const panel = doc.createElement('div'); panel.className = 'md-diagram-source-editor'; panel.hidden = true
    // A click inside the open panel neither reselects the block nor toggles the panel (block.ts edits on a block click).
    panel.dataset.mdNoSelect = ''
    const panelHead = doc.createElement('div'); panelHead.className = 'md-diagram-editor-head'
    const sourceLabel = doc.createElement('label'); sourceLabel.textContent = diagramsStrings.sourceLabel
    const close = doc.createElement('button'); close.type = 'button'; close.textContent = diagramsStrings.hideSource
    const textarea = doc.createElement('textarea'); textarea.rows = 8; textarea.maxLength = DIAGRAM_SOURCE_LIMIT; textarea.spellcheck = false
    textarea.setAttribute('aria-label', diagramsStrings.sourceLabel); textarea.autocapitalize = 'off'; textarea.autocomplete = 'off'; sourceLabel.append(textarea)
    const title = doc.createElement('span'); title.textContent = diagramsStrings.sourceEditorTitle; panelHead.append(title, close)
    const captionLabel = doc.createElement('label'); captionLabel.textContent = diagramsStrings.captionLabel
    const captionInput = doc.createElement('input'); captionInput.maxLength = 500; captionInput.placeholder = diagramsStrings.captionPlaceholder; captionLabel.append(captionInput)
    const hint = doc.createElement('p'); hint.className = 'md-diagram-editor-hint'; hint.textContent = diagramsStrings.editHint
    panel.append(panelHead, sourceLabel, captionLabel, hint)
    const composition = trackMarkdownComposition(panel)
    // The source disclosure and the read actions share a line (outside <details>, whose closed content is not rendered); the open panel replaces both.
    const foot = doc.createElement('div'); foot.className = 'md-diagram-foot'; foot.append(details)
    figure.append(visual, caption, status, foot, panel); container.append(style, figure)
    // What a reader may do with the diagram too (J1.3): enlarge the drawing, take its source.
    const zoom = (opener: HTMLElement) => {
      const drawn = visual.querySelector('img, svg'); if (!drawn) return
      zoomMarkdownBlock({ opener, editor: context.editor, signal, label: context.data.caption || diagramsStrings.title, render(body) { body.append(drawn.cloneNode(true)) } })
    }
    visual.addEventListener('dblclick', () => zoom(visual.closest<HTMLElement>('[data-md-module]') ?? visual))
    addMarkdownReadActions(foot, signal, [
      { label: markdownStrings.modules.zoomBlock, icon: 'maximize-2', run: zoom },
      { label: markdownStrings.modules.copyWhat(diagramsStrings.sourceSummary.toLowerCase()), icon: 'copy', run: button => copyMarkdownText(button, context.data.source) },
    ])
    let stopped = false, generation = 0, timer: number | undefined, hasVisual = false, anchor: HTMLElement | undefined
    const renderLifetime = new AbortController()
    let preparation: Promise<boolean> = Promise.resolve(false), finishTimer: ((ready: boolean) => void) | undefined
    let themeSignature = JSON.stringify(diagramTheme(container))
    const editable = () => !stopped && !signal.aborted && !!context.editor?.isEditable && !!context.update
    const showEditor = (show: boolean) => {
      if (panel.hidden === show) context.closeHistory?.()
      // The panel closes with the keyboard in it (a second Enter on the selected block): the focus goes back to the block, not to a hidden field.
      const keyboardInside = !show && !panel.hidden && panel.contains(doc.activeElement)
      panel.hidden = !show; foot.hidden = show; anchor?.setAttribute('aria-expanded', String(show))
      if (show) textarea.focus(); else if (keyboardInside) returnFocus()
    }
    editors.set(figure, nextAnchor => { if (editable()) { anchor = nextAnchor; showEditor(panel.hidden === true) } })
    sourcePreview.addEventListener('click', event => { const block = figure.closest<HTMLElement>('[data-md-module]'); if (block && editable() && panel.hidden) { event.preventDefault(); editors.get(figure)?.(block) } })
    // The block itself (a click or Enter opened the panel) is no focus target: back to the editor and its node selection.
    const returnFocus = () => { anchor?.focus(); if (anchor && doc.activeElement !== anchor) context.editor?.view.focus() }
    close.onclick = () => { showEditor(false); returnFocus() }
    const syncContent = () => {
      caption.textContent = context.data.caption; caption.hidden = !context.data.caption
      sourcePreview.textContent = context.data.source
      const image = visual.querySelector('img'); if (image) image.alt = context.data.caption || diagramsStrings.title
    }
    // A draft kept when writing stopped (text the node does not accept) comes back into the fields once the page is writable again.
    const kept = context.editor?.isEditable ? takeMarkdownDraft<MarkdownDiagramData>(context.editor, 'diagram', context.data) : undefined
    textarea.value = kept?.source ?? context.data.source; captionInput.value = kept?.caption ?? context.data.caption; syncContent()
    const paint = (delay = 200) => {
      if (timer !== undefined) { view?.clearTimeout(timer); finishTimer?.(false); finishTimer = undefined }
      const current = ++generation, data = context.data
      const run = async (): Promise<boolean> => {
        if (stopped || signal.aborted || current !== generation) return false
        if (!data.source.trim()) { visual.replaceChildren(); hasVisual = false; status.textContent = diagramsStrings.emptySource; return false }
        try {
          const mermaid = await loadMermaid()
          if (stopped || signal.aborted || current !== generation) return false
          const render = async () => {
            if (stopped || signal.aborted || current !== generation) return false
            const scratch = doc.createElement('div'); scratch.style.cssText = 'position:fixed;left:-100000px;top:0;visibility:hidden;pointer-events:none'; doc.body.append(scratch)
            const removeScratch = () => scratch.remove()
            renderLifetime.signal.addEventListener('abort', removeScratch, { once: true })
            try {
              normalizeDiagram(data)
              const themeVariables = diagramTheme(container); themeSignature = JSON.stringify(themeVariables)
              // layout/look: Mermaid 12 defaults to ELK and the "neo" look; stored diagrams keep the drawing they had.
              mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', maxTextSize: DIAGRAM_SOURCE_LIMIT, maxEdges: 500,
                layout: 'dagre', look: 'classic', theme: 'base', themeVariables, themeCSS: '.node rect, .actor { rx: 8; ry: 8; }', suppressErrorRendering: true, htmlLabels: false,
                flowchart: { htmlLabels: false, curve: 'basis', padding: 16, nodeSpacing: 38, rankSpacing: 48 },
                sequence: { actorMargin: 48, boxMargin: 12, messageMargin: 34 },
                secure: ['securityLevel', 'startOnLoad', 'maxTextSize', 'maxEdges', 'htmlLabels', 'flowchart', 'theme', 'themeVariables', 'themeCSS'],
              })
              let id: string
              do { id = `md-diagram-${++nextDiagram}` } while (doc.getElementById(id))
              const { svg } = await mermaid.render(id, data.source, scratch)
              if (stopped || signal.aborted || current !== generation) return false
              const image = doc.createElement('img'); image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(naturalSizeSvg(svg, view))}`
              await decodedImage(image, renderLifetime.signal)
              if (stopped || signal.aborted || current !== generation) return false
              image.alt = context.data.caption || diagramsStrings.title; visual.replaceChildren(image); hasVisual = true; status.textContent = ''
              return true
            } finally { renderLifetime.signal.removeEventListener('abort', removeScratch); scratch.remove() }
          }
          const painted = rendering.catch(() => undefined).then(render)
          rendering = painted
          return await painted
        } catch (cause) {
          if (stopped || signal.aborted || current !== generation) return false
          status.textContent = diagramsStrings.renderFailed(cause instanceof Error ? cause.message : diagramsStrings.invalidSource, hasVisual)
          if (!hasVisual) { fallback.textContent = data.source; visual.replaceChildren(fallback) }
          return false
        }
      }
      if (delay && view) preparation = new Promise(resolve => {
        finishTimer = resolve
        timer = view.setTimeout(() => { timer = undefined; finishTimer = undefined; void run().then(resolve) }, delay)
      })
      else preparation = run()
    }
    const save = () => {
      if (!editable()) return
      try {
        const next = normalizeDiagram({ version: 1, source: textarea.value, caption: captionInput.value })
        context.update!(next, { history: 'typing' })
      } catch (cause) { status.textContent = cause instanceof Error ? cause.message : diagramsStrings.saveFailed }
    }
    textarea.oninput = save; captionInput.oninput = save
    if (kept) { save(); showEditor(true) }
    textarea.onfocus = captionInput.onfocus = () => context.closeHistory?.()
    textarea.onblur = captionInput.onblur = () => context.closeHistory?.()
    const keyboard = (event: KeyboardEvent) => {
      if (composition.active() || isMarkdownComposition(event, context.editor)) return
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); showEditor(false); returnFocus(); return }
      if ((event.ctrlKey || event.metaKey) && ['z', 'y'].includes(event.key.toLowerCase())) {
        event.preventDefault(); event.stopPropagation()
        if (!editable()) return
        if (event.shiftKey || event.key.toLowerCase() === 'y') context.editor?.commands.redo()
        else context.editor?.commands.undo()
      }
    }
    textarea.onkeydown = keyboard; captionInput.onkeydown = keyboard
    const beforeInput = (event: InputEvent) => {
      if (event.isComposing || composition.active()) return
      if (!editable() || !['historyUndo', 'historyRedo'].includes(event.inputType)) return
      event.preventDefault(); event.stopPropagation()
      if (event.inputType === 'historyRedo') context.editor?.commands.redo()
      else context.editor?.commands.undo()
    }
    textarea.addEventListener('beforeinput', beforeInput); captionInput.addEventListener('beforeinput', beforeInput)
    // A theme change can come in a burst (a class and tokens set one by one): the theme is read once it has settled.
    let themeCheck: number | undefined
    const themeObserver = view?.MutationObserver ? new view.MutationObserver(records => {
      if (stopped || signal.aborted || !records.some(themeMutation)) return
      if (themeCheck !== undefined) view.clearTimeout(themeCheck)
      themeCheck = view.setTimeout(() => {
        themeCheck = undefined
        if (stopped || signal.aborted) return
        const next = JSON.stringify(diagramTheme(container))
        if (next !== themeSignature) { themeSignature = next; paint() }
      }, 150)
    }) : undefined
    for (let ancestor = container.parentElement; ancestor; ancestor = ancestor.parentElement) {
      themeObserver?.observe(ancestor, { attributes: true, attributeOldValue: true, attributeFilter: ['class', 'style', 'data-theme'] })
    }
    const stop = () => {
      if (stopped) return
      stopped = true; generation++; if (timer !== undefined) view?.clearTimeout(timer); if (themeCheck !== undefined) view?.clearTimeout(themeCheck)
      finishTimer?.(false); finishTimer = undefined; renderLifetime.abort()
      // Writing stopped with fields the node does not hold (the text was not accepted): kept for the next edit, announced.
      if (context.editor && (textarea.value !== context.data.source || captionInput.value !== context.data.caption)) {
        retainMarkdownDraft(context.editor, 'diagram', diagramsStrings.title, context.data, { version: 1, source: textarea.value, caption: captionInput.value })
      }
      composition.destroy(); themeObserver?.disconnect(); editors.delete(figure); signal.removeEventListener('abort', stop); showEditor(false)
    }
    signal.addEventListener('abort', stop, { once: true })
    if (signal.aborted) stop(); else paint(0)
    return {
      fallback: 'runtime',
      async prepare() {
        while (!stopped && !signal.aborted) {
          const current = preparation, ready = await current
          if (current === preparation) return ready && !stopped && !signal.aborted
        }
        return false
      },
      update(next) {
        if (stopped || signal.aborted) return
        const sourceChanged = context.data.source !== next.data.source
        if (sourceChanged && textarea.value !== next.data.source) textarea.value = next.data.source
        if (context.data.caption !== next.data.caption && captionInput.value !== next.data.caption) captionInput.value = next.data.caption
        context = next; syncContent()
        if (!editable()) showEditor(false)
        if (sourceChanged) paint()
      },
      // The diagram stays drawn (contract J1.2). Writing stopped: the panel closes, and text the node does not hold stays in the fields,
      // kept and announced as when the render is replaced; writing again drops the kept copy.
      editable(on) {
        if (stopped || signal.aborted || !context.editor) return
        if (on) { takeMarkdownDraft(context.editor, 'diagram', context.data); return }
        showEditor(false)
        if (textarea.value !== context.data.source || captionInput.value !== context.data.caption) {
          retainMarkdownDraft(context.editor, 'diagram', diagramsStrings.title, context.data, { version: 1, source: textarea.value, caption: captionInput.value })
        }
      },
      destroy: stop,
    }
  },
  edit(context) {
    const figure = context.container.querySelector<HTMLElement>('.md-diagram')
    if (figure) editors.get(figure)?.(context.anchor)
  },
}
