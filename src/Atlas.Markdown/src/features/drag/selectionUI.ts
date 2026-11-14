import type { Editor } from '@tiptap/core'
import { NodeRangeSelection } from '@tiptap/extension-node-range'
import { markdownOverlaysFor, registerMarkdownOverlay } from '../../core/overlay'
import { markdownMotion } from '../../core/motion'
import { copyMarkdownOverlayTheme } from '../../core/overlayTheme'
import { createMarkdownContextToolbar, markdownContextIcon, openMarkdownContextPopover } from '../modules/contextToolbar'
import { HOVER_DECO_PLUGIN_KEY, SOURCE_DECO_PLUGIN_KEY } from './decorations'
import { computeSectionEnd, selectBlockRange } from './blockSelection'
import { moveBlock } from './blockCommands'
import { canMoveBlocks } from './movePolicy'
import { openMarkdownBlockMenu } from '../intentions/intentions'
import { dragStrings } from '../../ui/strings/drag'

/** One frame for the whole range; its controls use the same toolbar as other components. */
/** A plugin action offered for the selected blocks (for example hiding them under invisible ink). */
export interface BlockSelectionAction {
  readonly id: string; readonly label: string; readonly icon?: string
  readonly state: () => { readonly enabled: boolean; readonly active: boolean }
  readonly renderIcon?: (container: HTMLElement) => void | (() => void)
  readonly hideWhenDisabled?: boolean
  readonly run: () => void
}
export function createBlockSelectionUI(editor: Editor, options: { grip: () => HTMLElement | null; target: (position: number) => void; actions?: readonly BlockSelectionAction[] }) {
  const doc = editor.view.dom.ownerDocument, win = doc.defaultView!
  const frame = doc.createElement('div'); frame.className = 'md-block-selection-frame'
  frame.setAttribute('aria-hidden', 'true')
  const svg = doc.createElementNS('http://www.w3.org/2000/svg', 'svg')
  const paths = [0, 1].map(() => {
    const path = doc.createElementNS(svg.namespaceURI, 'path') as SVGPathElement
    path.setAttribute('pathLength', '1'); svg.append(path); return path
  })
  frame.append(svg); doc.body.append(frame)
  const releaseFrame = registerMarkdownOverlay(editor, frame, { consumesEscape: false })
  let destroyed = false, scheduled = 0, key = '', visible = false, themed = false
  let closeMenu: (() => void) | undefined
  const drawing = new Set<Animation>()
  const reduced = win.matchMedia?.('(prefers-reduced-motion: reduce)')
  const stopDrawing = () => { drawing.forEach(animation => animation.cancel()); drawing.clear() }
  const motionChanged = () => { if (reduced?.matches) stopDrawing() }
  reduced?.addEventListener?.('change', motionChanged)
  const ownsFocus = () => {
    const active = doc.activeElement
    return !!active && (editor.view.dom.closest('.md-editor')?.contains(active)
      || markdownOverlaysFor(editor.view.dom).some(element => element.contains(active)))
  }
  const explicit = () => editor.state.selection instanceof NodeRangeSelection && !editor.state.selection.empty
  const toolbar = createMarkdownContextToolbar({ editor, anchor: frame, label: dragStrings.selectedBlocksLabel,
    getPos: () => editor.state.selection.from, selectOnClick: false, markAnchor: false,
    isVisible: () => visible && explicit() && ownsFocus(),
  })
  toolbar.element.dataset.testid = 'block-selection-toolbar'
  toolbar.element.tabIndex = -1
  const countLabel = doc.createElement('span'); countLabel.className = 'md-selection-count'; toolbar.element.append(countLabel)
  const scope = toolbar.addButton({ label: dragStrings.selectionRangeLabel, text: '', onClick(button) {
    const { from, to } = editor.state.selection
    if (!explicit() || editor.state.doc.nodeAt(from)?.type.name !== 'heading') return
    const original = editor.state.doc
    const whole = to === computeSectionEnd(original, from, original.nodeAt(from)!.attrs.level ?? 1)
    button.focus({ preventScroll: true })
    closeMenu = openMarkdownContextPopover({ editor, anchor: button, title: dragStrings.selectionRangeLabel, variant: 'menu', className: 'md-context-menu-compact', render(container, close) {
      for (const [section, label, icon] of [[true, dragStrings.wholeSectionLabel, 'section'], [false, dragStrings.headingOnlyLabel, 'text']] as const) {
        const item = doc.createElement('button'); item.type = 'button'; item.className = 'md-context-menu-item'
        item.setAttribute('role', 'menuitemradio'); item.setAttribute('aria-checked', String(section === whole))
        item.append(markdownContextIcon(doc, icon), label)
        item.onclick = () => {
          close()
          if (editor.state.doc !== original || !editor.isEditable) return
          selectBlockRange(editor, from, false, section); options.target(from); refresh()
          button.focus({ preventScroll: true })
        }
        container.append(item)
      }
      const invalidate = () => { if (editor.state.doc !== original || editor.state.selection.from !== from || editor.state.selection.to !== to) close() }
      editor.on('transaction', invalidate)
      return () => editor.off('transaction', invalidate)
    } })
  } })
  scope.setAttribute('aria-haspopup', 'menu')
  toolbar.addSeparator()
  const move = (direction: -1 | 1) => {
    const keyboardFocus = toolbar.element.contains(doc.activeElement)
    if (keyboardFocus) toolbar.element.focus({ preventScroll: true })
    const position = moveBlock(editor, editor.state.selection.from, direction)
    if (position !== null) options.target(position)
    refresh()
    // Reaching an edge disables the just-used button. Do not strand keyboard
    // focus on body when the browser blurs that now-disabled control.
    if (keyboardFocus) ([scope, up, down].find(button => !button.hidden && !button.disabled) ?? toolbar.element).focus({ preventScroll: true })
  }
  const up = toolbar.addButton({ label: dragStrings.moveUpLabel, icon: 'arrowUp', onClick: () => move(-1) })
  const down = toolbar.addButton({ label: dragStrings.moveDownLabel, icon: 'arrowDown', onClick: () => move(1) })
  // Změnit na… (J4.9): the block's menu for the first selected block, its keys from the editor (the focus goes there).
  const turn = toolbar.addButton({ label: dragStrings.turnIntoLabel, text: dragStrings.turnIntoLabel, onClick() {
    if (explicit()) openMarkdownBlockMenu(editor, editor.state.selection.from)
  } })
  turn.dataset.selectionAction = 'turn-into'
  if (options.actions?.length) toolbar.addSeparator()
  const extras = (options.actions ?? []).map(action => {
    const button = toolbar.addButton({ label: action.label, icon: action.icon ?? 'eye-off', onClick() {
      if (!explicit() || !editor.isEditable || !action.state().enabled) return
      action.run(); refresh()
    } })
    button.dataset.selectionAction = action.id
    let cleanup: void | (() => void) = undefined
    if (action.renderIcon) { button.replaceChildren(); const icon = doc.createElement('span'); button.append(icon); cleanup = action.renderIcon(icon) }
    return { action, button, cleanup }
  })
  // Accessible names remain available, without a native tooltip over the content.
  for (const button of [scope, up, down, turn, ...extras.map(extra => extra.button)]) button.removeAttribute('title')

  function refresh() {
    scheduled = 0
    if (destroyed || editor.isDestroyed) return
    const state = editor.state
    const selected = explicit() && ownsFocus()
    const hovered = HOVER_DECO_PLUGIN_KEY.getState(state)?.find() ?? []
    const from = selected ? state.selection.from : hovered[0]?.from
    const to = selected ? state.selection.to : hovered.at(-1)?.to
    visible = editor.isEditable && from !== undefined && to !== undefined
      && !(SOURCE_DECO_PLUGIN_KEY.getState(state)?.find().length)
    if (!visible) { frame.dataset.visible = 'false'; key = ''; toolbar.refresh(); return }
    const elements: HTMLElement[] = []
    state.doc.nodesBetween(from!, to!, (_node, pos) => {
      const element = editor.view.nodeDOM(pos)
      if (element instanceof win.HTMLElement) elements.push(element)
      return false
    })
    // A block a folded heading or a filter hides has no box (0, 0): measured, it stretched the frame from the window's left edge.
    const rects = elements.map(element => element.getBoundingClientRect()).filter(rect => rect.width || rect.height)
    if (!rects.length) { visible = false; frame.dataset.visible = 'false'; toolbar.refresh(); return }
    // On first show, not as the editor is built: there the read forced a style and layout of the whole page (3.6-G open trace).
    if (!themed) { themed = true; copyMarkdownOverlayTheme(editor.view.dom, frame) }
    const left = Math.min(...rects.map(rect => rect.left)) - 8, top = rects[0].top - 5
    const width = Math.max(...rects.map(rect => rect.right)) - left + 8
    const height = rects.at(-1)!.bottom - top + 5
    Object.assign(frame.style, { left: `${left}px`, top: `${top}px`, width: `${width}px`, height: `${height}px` })
    // Keep the passive frame inside nested scrolling editor surfaces.
    let clipTop = 0, clipBottom = win.innerHeight, clipLeft = 0, clipRight = win.innerWidth
    for (let parent = editor.view.dom.parentElement; parent; parent = parent.parentElement) {
      const style = win.getComputedStyle(parent), rect = parent.getBoundingClientRect()
      if (/(auto|scroll|hidden|clip)/.test(style.overflowY)) { clipTop = Math.max(clipTop, rect.top); clipBottom = Math.min(clipBottom, rect.bottom) }
      if (/(auto|scroll|hidden|clip)/.test(style.overflowX)) { clipLeft = Math.max(clipLeft, rect.left); clipRight = Math.min(clipRight, rect.right) }
    }
    frame.style.clipPath = `inset(${Math.max(0, clipTop - top)}px ${Math.max(0, left + width - clipRight)}px ${Math.max(0, top + height - clipBottom)}px ${Math.max(0, clipLeft - left)}px)`
    const origin = Math.max(8, Math.min(height - 8, (options.grip()?.getBoundingClientRect().top ?? top) - top + 12))
    const w = width - 1, h = height - 1, r = 7
    paths[0].setAttribute('d', `M .5 ${origin} V ${r} Q .5 .5 ${r} .5 H ${w-r} Q ${w} .5 ${w} ${r} V ${origin}`)
    paths[1].setAttribute('d', `M .5 ${origin} V ${h-r} Q .5 ${h} ${r} ${h} H ${w-r} Q ${w} ${h} ${w} ${h-r} V ${origin}`)
    const nextKey = `${from}:${to}`
    if (key !== nextKey) {
      stopDrawing()
      const motion = markdownMotion(editor.view.dom)
      if (!reduced?.matches && !motion.reduced) for (const path of paths) {
        const animation = path.animate?.([{ strokeDashoffset: '1' }, { strokeDashoffset: '0' }], { duration: motion.duration('slow'), easing: motion.ease })
        if (animation) { drawing.add(animation); void animation.finished.then(() => drawing.delete(animation), () => drawing.delete(animation)) }
      }
      key = nextKey
    }
    frame.dataset.visible = 'true'; frame.dataset.selected = String(selected)
    const count = elements.length, first = state.doc.nodeAt(from!)
    const heading = first?.type.name === 'heading'
    const whole = heading && to === computeSectionEnd(state.doc, from!, first.attrs.level ?? 1)
    const label = dragStrings.blockCountLabel(count)
    countLabel.textContent = label; countLabel.hidden = !!heading; scope.hidden = !heading
    scope.replaceChildren(doc.createTextNode(whole && count > 1 ? dragStrings.sectionCountLabel(label) : count === 1 ? dragStrings.headingLabel : label), markdownContextIcon(doc, 'chevronDown'))
    const movable = canMoveBlocks(editor)
    up.disabled = !movable || from === 0; down.disabled = !movable || to === state.doc.content.size
    for (const { action, button } of extras) {
      const current = action.state()
      if (action.hideWhenDisabled && !current.enabled) {
        if (button === doc.activeElement) toolbar.element.focus({ preventScroll: true })
        button.remove()
      } else if (!button.isConnected) toolbar.element.append(button)
      button.disabled = !current.enabled; button.setAttribute('aria-pressed', String(current.active))
    }
    toolbar.refresh()
  }
  function schedule() { if (!destroyed && !scheduled) scheduled = win.requestAnimationFrame(refresh) }
  const observer = typeof win.ResizeObserver === 'function' ? new win.ResizeObserver(schedule) : undefined
  observer?.observe(editor.view.dom)
  editor.on('transaction', schedule); doc.addEventListener('focusin', schedule); doc.addEventListener('focusout', schedule)
  doc.addEventListener('scroll', schedule, true); win.addEventListener('resize', schedule)
  schedule()
  return { focus() { refresh();
    if (!toolbar.element.hidden) ([scope, up, down].find(button => !button.hidden && !button.disabled) ?? toolbar.element).focus({ preventScroll: true })
  }, destroy() {
    destroyed = true; win.cancelAnimationFrame(scheduled); observer?.disconnect(); closeMenu?.()
    for (const { cleanup } of extras) if (typeof cleanup === 'function') cleanup()
    stopDrawing(); reduced?.removeEventListener?.('change', motionChanged); toolbar.destroy(); releaseFrame(); frame.remove()
    editor.off('transaction', schedule); doc.removeEventListener('focusin', schedule); doc.removeEventListener('focusout', schedule)
    doc.removeEventListener('scroll', schedule, true); win.removeEventListener('resize', schedule)
  } }
}
