import { markdownScrollBehavior, openMarkdownRowMenu, retainMarkdownDraft, takeMarkdownDraft, type MarkdownModuleContext, type MarkdownModuleRuntime } from '../../core'
import { tocStrings } from './runtimeStrings'
import { collectTocHeadings, normalizeToc, type MarkdownTocData } from './index'
import styles from './style.css?inline'

const levelRanges: ReadonlyArray<readonly [number, number]> = [[1, 1], [2, 2], [3, 3], [4, 4], [5, 5], [6, 6], [1, 2], [1, 3], [2, 3], [2, 4], [1, 4], [1, 6]]
const rangeText = (min: number, max: number) => min === max ? `H${min}` : `H${min}–H${max}`

/** Title and range chip, edited where they are shown; every change is one undoable update. */
function editableHeader(context: MarkdownModuleContext<MarkdownTocData>, head: HTMLElement, title: HTMLElement) {
  const { data, update, signal } = context
  const doc = head.ownerDocument
  // A click on a button never selects the block: select it, so the keyboard goes on from the block, not from an old caret.
  // view.focus() at once: tiptap's focus() waits a frame, and the re-render left the keyboard on <body> meanwhile (P7).
  const backToBlock = () => { const position = context.getPos?.(); if (position !== undefined) context.editor?.commands.setNodeSelection(position); context.editor?.view.focus() }
  const apply = (next: Partial<MarkdownTocData>) => { if (!signal.aborted) update!(normalizeToc({ ...data, ...next })) }
  const titleText = data.title || tocStrings.defaultTitle
  // The title is the same element a reader sees; editing makes it a button that swaps its text for a field.
  title.tabIndex = 0; title.setAttribute('role', 'button'); title.setAttribute('aria-label', `${tocStrings.titleLabel}: ${titleText}`)
  const edit = () => {
    if (title.querySelector('input')) return
    const input = doc.createElement('input'); input.className = 'md-toc-title-input'; input.value = takeMarkdownDraft<string>(context.editor!, 'toc', data) ?? data.title; input.maxLength = 200
    input.placeholder = tocStrings.defaultTitle; input.setAttribute('aria-label', tocStrings.titleLabel)
    let done = false
    const finish = (save: boolean) => {
      if (done) return
      done = true
      const next = input.value.trim() || tocStrings.defaultTitle
      // An unchanged title (or Esc) only swaps the button back; a change re-renders through the update.
      if (save && next !== titleText) apply({ title: next })
      else if (input.isConnected) { title.textContent = titleText; if (!save) title.focus() }
    }
    // The right went with the field open: the render is replaced, so the text is kept for the next edit and announced.
    signal.addEventListener('abort', () => { if (!done && input.value.trim() !== data.title) retainMarkdownDraft(context.editor!, 'toc', tocStrings.titleLabel, data, input.value) }, { once: true })
    input.onkeydown = key => {
      // The block re-renders on a change: the keyboard goes back to the editor, where the block stays selected.
      if (key.key === 'Enter') { key.preventDefault(); key.stopPropagation(); finish(true); if ((input.value.trim() || tocStrings.defaultTitle) !== titleText) backToBlock() }
      else if (key.key === 'Escape') { key.preventDefault(); key.stopPropagation(); finish(false) }
    }
    input.onblur = () => finish(true)
    title.replaceChildren(input); input.focus(); input.select()
  }
  title.onclick = event => { if (event.target === title) { event.preventDefault(); edit() } }
  title.onkeydown = event => { if (event.target === title && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); event.stopPropagation(); edit() } }
  const chip = doc.createElement('button'); chip.type = 'button'; chip.className = 'md-toc-range'; chip.textContent = rangeText(data.minLevel, data.maxLevel)
  chip.setAttribute('aria-label', `${tocStrings.rangeLabel}: ${chip.textContent}`); chip.setAttribute('aria-haspopup', 'menu'); chip.setAttribute('aria-expanded', 'false')
  const wrap = doc.createElement('span'); wrap.className = 'md-toc-range-wrap'; wrap.append(chip)
  // The editor's row menu, as every other menu: it marks the chip expanded, a second click closes it, Esc gives the chip the keyboard back.
  chip.onclick = event => {
    event.preventDefault()
    if (!context.editor) return
    openMarkdownRowMenu({ editor: context.editor, anchor: chip, title: tocStrings.rangeLabel, className: 'md-context-menu-compact',
      rows: levelRanges.map(([min, max]) => {
        const current = min === data.minLevel && max === data.maxLevel
        return { label: min === 1 && max === 6 ? `${tocStrings.allHeadings} (H1–H6)` : rangeText(min, max), checked: current,
          run: () => { if (current) chip.focus(); else { apply({ minLevel: min, maxLevel: max }); backToBlock() } } }
      }) })
  }
  head.append(wrap)
}

const rootIds = new WeakMap<HTMLElement, string>()
const headingIds = new WeakMap<HTMLElement, string>()
let nextRoot = 0
let nextHeading = 0
function anchorFor(root: HTMLElement, heading: HTMLElement): string {
  if (heading.id && root.ownerDocument.getElementById(heading.id) === heading) return heading.id
  let prefix = rootIds.get(root)
  if (!prefix) { prefix = `md-toc-${++nextRoot}`; rootIds.set(root, prefix) }
  let id = headingIds.get(heading)
  if (!id) {
    do { id = `${prefix}-heading-${++nextHeading}` } while (root.ownerDocument.getElementById(id))
    headingIds.set(heading, id)
  }
  // IDs belong to the mounted view, not the rich document or another editor.
  heading.id = id
  return id
}
export const tocRuntime: MarkdownModuleRuntime<MarkdownTocData> = {
  render(context) {
    const { container, data, signal } = context
    if (signal.aborted) return
    const doc = container.ownerDocument
    const root = context.rootContainer ?? context.editor?.view.dom
    const nav = doc.createElement('nav'); nav.className = 'md-toc'; nav.setAttribute('aria-label', data.title || tocStrings.defaultTitle)
    const head = doc.createElement('div'); head.className = 'md-toc-head'
    const title = doc.createElement('strong'); title.className = 'md-toc-title'; title.textContent = data.title || tocStrings.defaultTitle; head.append(title)
    if (context.editor?.isEditable && context.update) editableHeader(context, head, title)
    const style = doc.createElement('style'); style.textContent = styles
    nav.append(style, head)
    const headings = collectTocHeadings(context.document, data)
    const headingElements = () => root ? [...root.querySelectorAll<HTMLElement>('h1,h2,h3,h4,h5,h6')].filter(element => !element.closest('[data-md-module]')) : []
    const elements = headingElements()
    const list = doc.createElement('ul')
    for (const entry of headings) {
      const item = doc.createElement('li'); item.style.marginInlineStart = `${(entry.level - data.minLevel) * 1.25}em`
      const resolveHeading = (items: HTMLElement[]) => entry.headingId ? items.find(element => element.dataset.headingId === entry.headingId) : items[entry.index]
      const heading = resolveHeading(elements)
      if (!heading || !root) { item.textContent = entry.title; list.append(item); continue }
      const link = doc.createElement('a'); link.textContent = entry.title
      if (!context.editor) link.href = `#${anchorFor(root, heading)}`
      else { link.tabIndex = 0; link.setAttribute('role', 'button'); link.onkeydown = key => { if (key.key === 'Enter' || key.key === ' ') { key.preventDefault(); link.click() } } }
      link.onclick = event => {
        event.preventDefault()
        // ProseMirror may replace heading DOM after an edit; resolve the current view on activation.
        const currentHeading = resolveHeading(headingElements())
        if (signal.aborted || !currentHeading) return
        currentHeading.scrollIntoView?.({ block: 'start', behavior: markdownScrollBehavior(currentHeading) })
        if (context.editor && !context.editor.isDestroyed && context.editor.isEditable) {
          let index = 0; let target: number | undefined
          context.editor.state.doc.descendants((node, pos) => {
            if (node.type.name !== 'heading') return
            const headingIndex = index++
            if (entry.headingId ? node.attrs.headingId === entry.headingId : headingIndex === entry.index) target = pos + 1
          })
          if (target !== undefined) context.editor.chain().setTextSelection(target).focus().run()
        } else {
          const old = currentHeading.getAttribute('tabindex'); currentHeading.tabIndex = -1; currentHeading.focus({ preventScroll: true })
          if (old === null) currentHeading.removeAttribute('tabindex'); else currentHeading.setAttribute('tabindex', old)
        }
      }
      item.append(link); list.append(item)
    }
    if (!headings.length) { const empty = doc.createElement('p'); empty.textContent = tocStrings.emptyDocument; nav.append(empty) }
    else nav.append(list)
    container.replaceChildren(nav)
  },
  edit(context) {
    // Click or Enter on the block lands on the title; there is no form any more.
    const find = (root?: HTMLElement) => root?.querySelector<HTMLElement>('.md-toc-title[role="button"]')
    ;(find(context.anchor) ?? find(context.container))?.click()
  },
}
