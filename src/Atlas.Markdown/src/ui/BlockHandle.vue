<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import type { Editor } from '@tiptap/core'
import { DragHandle } from '@tiptap/extension-drag-handle-vue-3'
import { NodeRangeSelection } from '@tiptap/extension-node-range'
import { NodeSelection } from '@tiptap/pm/state'
import { isMarkdownComposition } from '../features/modules/composition'
import { observeMarkdownEditability } from '../features/modules/editability'
import { startDragSourceMarker } from '../features/drag/sourceSession'
import { useBlockHandleMotion } from './useBlockHandleMotion'
import { blockRangeForHandle, collectSourceBlocks, selectBlockRange } from '../features/drag/blockSelection'
import { createBlockSelectionUI } from '../features/drag/selectionUI'
import type { MarkdownEditorController } from '../core/types'
import { legacyIconName } from './iconMarkup'
import { moveBlock } from '../features/drag/blockCommands'
import { canMoveBlocks } from '../features/drag/movePolicy'
import { enterMarkdownBlockKeyboardControls } from '../features/drag/blockKeyboard'
import { openMarkdownBlockMenu } from '../features/intentions/intentions'
import { flushDomSelection } from '../core/internal'
import { PlusIcon, GripVerticalIcon } from './icons'
import { insertBlockAt } from '../features/drag/blockInsertion'
import { dragStrings } from './strings/drag'

const props = withDefaults(defineProps<{ editor: Editor; showAddButton?: boolean; controller?: MarkdownEditorController }>(), { showAddButton: true })
const row = ref<HTMLElement | null>(null)
const motion = useBlockHandleMotion(props.editor, row)
const { positionConfig, dragTargetType, rangeLabel, onPointerDown, onFocusOut, onDragNodeChange, updateGeometry, onHandleHover } = motion
let selectionUI: ReturnType<typeof createBlockSelectionUI> | undefined
const keyboardGrip = ref(false)
/** Shift+↑/↓ on the grip moved a block: until the grip loses the focus, the next press moves the same one (see onDragGripKeydown). */
let movedByKeys = false
const editorElement = props.editor.view.dom
const ownerDocument = editorElement.ownerDocument
function releaseKeyboardGrip() {
  if (!row.value?.contains(ownerDocument.activeElement)) {
    movedByKeys = false
    keyboardGrip.value = false
    row.value?.parentElement?.removeAttribute('data-keyboard')
  }
}
function focusCurrentGrip(event: KeyboardEvent) {
  const editor = props.editor
  if (event.defaultPrevented || event.target !== editor.view.dom || !event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || event.key !== 'F10' || !editor.isEditable || isMarkdownComposition(event, editor)) return
  // Native caret navigation can precede ProseMirror's selectionchange handler: read it into the state first.
  flushDomSelection(editor)
  const selection = editor.state.selection
  const position = selection.$from.depth ? selection.$from.before(1) : selection.from
  const node = editor.state.doc.nodeAt(position)
  // In a table, its own handles (J6.4): the chip of the caret's row, Esc back to the cell.
  if (node?.type.name === 'table') { focusTableHandle(event); return }
  if (!node) return
  event.preventDefault()
  motion.releasePointer()
  onDragNodeChange({ pos: position, node }); updateGeometry()
  keyboardGrip.value = true
  const handle = row.value?.parentElement
  // Reveal synchronously: a visibility:hidden/inert grip cannot receive focus.
  if (handle) { handle.dataset.keyboard = 'true'; handle.inert = false }
  const grip = row.value?.querySelector<HTMLElement>('.dh-grip')
  if (handle && grip) focusRevealed(handle, grip)
}
/**
 * Focus on the first Alt+F10 (J6.4): a fading-in control is still `visibility: hidden` at the start of its transition and refuses the
 * focus, so the reveal ends at once. Should the focus still miss, it tries again a moment later, and no key reaches the text meanwhile
 * (an Enter meant for the grip would split the paragraph).
 */
function focusRevealed(container: HTMLElement, target: HTMLElement) {
  container.getAnimations?.({ subtree: true }).forEach(animation => animation.finish())
  target.focus({ preventScroll: true })
  if (ownerDocument.activeElement === target) return
  const hold = (event: KeyboardEvent) => { event.preventDefault(); event.stopPropagation() }
  ownerDocument.addEventListener('keydown', hold, true)
  setTimeout(() => { ownerDocument.removeEventListener('keydown', hold, true); target.focus({ preventScroll: true }) }, 20)
}
/** Alt+F10 in a table (J6.4): the chip of the caret's row among the table's own handles (features/table/rails.ts); Esc back to the cell. */
function focusTableHandle(event: KeyboardEvent) {
  const editor = props.editor
  let cell: Element | null | undefined
  try { const { node } = editor.view.domAtPos(editor.state.selection.from); cell = (node instanceof Element ? node : node.parentElement)?.closest('td,th') } catch { /* no layout */ }
  const controls = cell?.closest('.tableWrapper')?.querySelector<HTMLElement>(':scope > .tt-table-controls')
  const chip = controls?.querySelector<HTMLElement>(`[data-row-index="${cell?.closest('tr')?.rowIndex ?? 0}"]`) ?? controls?.querySelector<HTMLElement>('button')
  if (!controls || !chip) return
  event.preventDefault()
  // Tab walks the caret cell's row chip and column chip and stays in the table (F24); Esc back to the cell.
  const path = [chip, controls.querySelector<HTMLElement>(`[data-col-index="${(cell as HTMLTableCellElement | null | undefined)?.cellIndex ?? 0}"]`)].filter((item): item is HTMLElement => !!item)
  const back = (key: KeyboardEvent) => {
    if (key.key === 'Escape') { key.preventDefault(); editor.view.focus(); return }
    if (key.key !== 'Tab' || key.ctrlKey || key.altKey || key.metaKey) return
    key.preventDefault(); key.stopPropagation()
    focusRevealed(controls, path[(path.indexOf(key.target as HTMLElement) + (key.shiftKey ? path.length - 1 : 1)) % path.length]!)
  }
  const leave = (focus: FocusEvent) => {
    if (controls.contains(focus.relatedTarget as Node | null)) return
    path.forEach(item => item.removeAttribute('data-active')); controls.removeEventListener('keydown', back); controls.removeEventListener('focusout', leave)
  }
  controls.addEventListener('keydown', back); controls.addEventListener('focusout', leave)
  // A chip is hidden until hovered or active (tables.css), and a hidden one takes no focus: active, as the rails' own keyboard path marks it.
  path.forEach(item => item.setAttribute('data-active', '1'))
  focusRevealed(controls, chip)
}
/**
 * P17, touch: a tap shows no hover, so on a coarse pointer the handle stands at the block that holds the caret (the keyboard's path,
 * without taking the focus); its buttons take the shared touch target (D5, states.css).
 */
const coarse = ownerDocument.defaultView?.matchMedia?.('(pointer: coarse)')
function followCaret() {
  const editor = props.editor
  if (!coarse?.matches || !editor.isEditable || !editor.view.hasFocus() || motion.cancelled) return
  const { $from } = editor.state.selection
  const position = $from.depth ? $from.before(1) : editor.state.selection.from
  const node = editor.state.doc.nodeAt(position)
  if (!node || node.type.name === 'table') return
  onDragNodeChange({ pos: position, node }); updateGeometry()
  const handle = row.value?.parentElement
  if (handle) { handle.dataset.touch = 'true'; handle.inert = false }
}
// Mounted read-only too (contract J1.2): the keyboard and touch reveals (visible !important) end when writing does.
const stopEditable = observeMarkdownEditability(props.editor, editorElement, () => {
  if (props.editor.isEditable) return
  keyboardGrip.value = false
  row.value?.parentElement?.removeAttribute('data-keyboard'); row.value?.parentElement?.removeAttribute('data-touch')
})
onMounted(() => {
  props.editor.on('selectionUpdate', followCaret)
  // Plugin selection actions (the same ones the formatting bubble offers) also apply to selected blocks.
  const actions = (props.controller?.actions.selection ?? []).map(action => ({
    id: action.id, label: action.label, renderIcon: action.renderIcon, hideWhenDisabled: action.hideWhenDisabled, icon: action.icon?.startsWith('svg:') ? legacyIconName(action.icon.slice(4)) : undefined,
    state: () => props.controller!.query(action.command), run: () => { props.controller!.dispatch(action.command) },
  }))
  selectionUI = createBlockSelectionUI(props.editor, { grip: () => row.value?.querySelector('.dh-grip') ?? null, actions,
    target(pos) { motion.releasePointer(); onDragNodeChange({ pos, node: props.editor.state.doc.nodeAt(pos) }); updateGeometry() },
  })
  editorElement.addEventListener('keydown', focusCurrentGrip)
  ownerDocument.addEventListener('focusin', releaseKeyboardGrip)
})
onBeforeUnmount(() => {
  props.editor.off('selectionUpdate', followCaret)
  stopEditable()
  selectionUI?.destroy()
  editorElement.removeEventListener('keydown', focusCurrentGrip)
  ownerDocument.removeEventListener('focusin', releaseKeyboardGrip)
})

function selectGrip(event: MouseEvent | KeyboardEvent) {
  if (!props.editor.isEditable || motion.cancelled) return
  const position = motion.resolveTarget()
  if (position < 0) return
  motion.releasePointer()
  // A plain click (Space) opens the block's menu (J4.9); Shift extends the selected blocks, whose toolbar then serves the range.
  if (!event.shiftKey) { openMarkdownBlockMenu(props.editor, position); return }
  selectBlockRange(props.editor, position, event.shiftKey)
  onDragNodeChange({ pos: position, node: props.editor.state.doc.nodeAt(position) })
  if (props.editor.state.selection instanceof NodeSelection) props.editor.view.focus()
}

/** The right button, the menu key and Shift+F10 on the grip: the block's menu, as a click on it. */
function openBlockMenu(event: MouseEvent | KeyboardEvent) {
  event.preventDefault()
  if (!props.editor.isEditable || motion.cancelled) return
  const position = motion.resolveTarget()
  if (position < 0) return
  motion.releasePointer()
  openMarkdownBlockMenu(props.editor, position)
}

function openMoveMenu(event: MouseEvent | KeyboardEvent) {
  event.preventDefault()
  if (!props.editor.isEditable || motion.cancelled) return
  const selection = props.editor.state.selection, position = motion.resolveTarget()
  if (position < 0) return
  if (!(selection instanceof NodeRangeSelection) || position < selection.from || position >= selection.to) {
    // This action explicitly requests the block movement toolbar, even for atoms.
    selectBlockRange(props.editor, position, false, true, true)
    motion.releasePointer()
  }
  else motion.releasePointer()
  selectionUI?.focus()
}

/** The block the grip addresses: the hovered one, else the one with the caret. Unlike `resolveTarget` it changes nothing. */
function currentBlockPosition(): number {
  if (motion.targetPos >= 0) return motion.targetPos
  const selection = props.editor.state.selection
  return selection.$from.depth ? selection.$from.before(1) : selection.from
}

/**
 * Keyboard alternative to mouse-only block reorder. Shift+ArrowUp / Down on
 * the focused drag-grip moves the hovered top-level block one slot up / down,
 * picking up the heading-section + multi-block selection rules from the
 * mouse drag-start logic.
 */
function onDragGripKeydown(event: KeyboardEvent) {
  if (isMarkdownComposition(event, props.editor)) return
  if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); props.editor.view.focus(); return }
  // Enter and Space open the block's menu (J4.9) as a click does, Shift+Enter / Shift+Space extend the selected blocks.
  if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); selectGrip(event); return }
  if (event.key === 'Tab' && !event.shiftKey) {
    // Plugin controls registered for this block come first; after the last one Tab continues to the selection toolbar.
    const selected = () => props.editor.state.selection instanceof NodeRangeSelection
    const position = event.altKey || event.ctrlKey || event.metaKey ? -1 : currentBlockPosition()
    const node = position >= 0 ? props.editor.state.doc.nodeAt(position) : null
    if (node && enterMarkdownBlockKeyboardControls(props.editor, { pos: position, node }, { grip: event.currentTarget as HTMLElement, after: () => { if (!selected()) return false; selectionUI?.focus(); return true } })) { event.preventDefault(); return }
    if (selected()) { event.preventDefault(); selectionUI?.focus(); return }
  }
  // The menu key and Shift+F10 open the block's menu as the right button does; ArrowDown goes on to the move panel.
  if (event.key === 'ContextMenu' || event.key === 'F10' && event.shiftKey) { openBlockMenu(event); return }
  if (event.key === 'ArrowDown' && !event.shiftKey) { openMoveMenu(event); return }
  // Ctrl+Shift+Arrow is the editor's key for moving a block (KeyboardFirst): the grip takes it beside Shift+Arrow.
  if (!event.shiftKey) return
  const dir = event.key === 'ArrowUp' ? -1 : event.key === 'ArrowDown' ? 1 : 0
  if (dir === 0) return
  event.preventDefault()
  const e = props.editor
  if (!e) return

  // Wave 8 fix — when the user reached the grip via Tab without a prior
  // mouse hover, motion.targetPos is -1. Fall back to the current selection's
  // top-level block so keyboard reorder still works. Round-3 audit flagged
  // this as a regression in the "we made it accessible" claim.
  // After a move, the block range moveBlock selected is the target: a colleague's edit retargets the hovered position (the binding
  // replaces the whole document, so no position maps through it), while y-tiptap keeps the selection through it.
  const { selection } = e.state
  let activePos = movedByKeys && selection instanceof NodeRangeSelection ? selection.from : motion.targetPos
  if (activePos < 0) {
    try {
      activePos = e.state.selection.$from.depth ? e.state.selection.$from.before(1) : e.state.selection.from
    } catch {
      return
    }
  }

  const adjustedTarget = moveBlock(e, activePos, dir)
  if (adjustedTarget === null) return
  movedByKeys = true
  onDragNodeChange({ pos: adjustedTarget, node: e.state.doc.nodeAt(adjustedTarget) })
  updateGeometry()
}
function guardDragElementStart(event: DragEvent) {
  if (canMoveBlocks(props.editor) && !motion.cancelled) return
  // The upstream callback continues into dragHandler even after preventDefault.
  // Stop before that listener changes selection or arms a drag with no dragend.
  event.preventDefault()
  event.stopImmediatePropagation()
  motion.releasePointer()
}
function onDragElementStart(event: DragEvent) {
  if (!canMoveBlocks(props.editor)) { event.preventDefault(); return }
  motion.cancelHover()
  if (motion.cancelled) { event.preventDefault(); return }
  const element = row.value?.parentElement
  if (element) element.dataset.motion = 'instant'
  const e = props.editor
  if (!e) return

  // Tiptap's mousemove tracker may not have fired by the time a synthetic
  // dragstart hits the handle (e.g. a keyboard-driven drag-grip activation
  // or a Playwright dispatchEvent). Fall back to the top-level block that
  // owns the current cursor position so we always have a usable target.
  if (motion.resolveTarget() < 0) return

  const dragRange = blockRangeForHandle(e.state, motion.targetPos)
  if (!dragRange) return

  // Tags the source blocks and installs the custom drag image. Here, not in the DragPreview plugin, because the drag-handle root
  // sits outside view.dom (its dragstart never bubbles through it), and synchronously: Safari and Firefox snapshot the drag image
  // when the handler returns. Its document-bubble override selects the range and sets `view.dragging` (move) afterwards.
  startDragSourceMarker(e.view, collectSourceBlocks(e, motion.targetPos, dragRange), event, dragRange)
  // Tiptap's dragHandler, next, would copy every computed style of every element of the blocks into a drag image ours replaces,
  // and select and set `view.dragging` as the override does anyway: hundreds of ms per block at 4× CPU. Without a dataTransfer it
  // returns at once (its first check); the override keeps the real one.
  Object.defineProperty(event, 'dataTransfer', { value: null })
}

function onAddBlockBelow(ev: MouseEvent) {
  ev.stopPropagation()
  if (motion.cancelled) return
  motion.releasePointer()
  const e = props.editor
  if (!e) return

  let blockEnd: number
  if (motion.targetPos >= 0 && motion.targetSize > 0) {
    blockEnd = motion.targetPos + motion.targetSize
  } else {
    const $from = e.state.selection.$from
    const depth = $from.depth || 1
    blockEnd = $from.before(depth) + $from.node(depth).nodeSize
  }

  insertBlockAt(e, blockEnd)
}
</script>

<template>
    <DragHandle
      :editor="props.editor"
      :compute-position-config="positionConfig"
      :on-node-change="onDragNodeChange"
      :on-element-drag-start="onDragElementStart"
      :class="dragTargetType === 'table' ? 'dh-wrap dh-suppressed' : 'dh-wrap'"
      :data-keyboard="keyboardGrip ? 'true' : undefined"
      data-testid="block-handle"
      @dragstart.capture="guardDragElementStart"
      @pointerdown.capture="onPointerDown"
      @focusout="onFocusOut">

      <!-- One column in the editor's left gutter: the grip beside the block's first line, the "+" (insert below)
           directly under it. Narrower than the gutter, so neither control reaches past the editor surface. -->
      <div ref="row" class="dh-row" @pointerenter="onHandleHover" @pointerleave="onHandleHover" @focusin="onHandleHover">
        <div
          class="dh-grip"
          data-testid="block-drag-btn"
          tabindex="0"
          role="button"
          aria-keyshortcuts="Alt+F10"
          :aria-label="dragStrings.gripAriaLabel(rangeLabel)"
          @click="selectGrip"
          @contextmenu="openBlockMenu"
          @keydown="onDragGripKeydown">
          <GripVerticalIcon/>
        </div>
        <button
          v-if="showAddButton"
          type="button"
          class="dh-add"
          data-testid="block-add-btn"
          :aria-label="dragStrings.addBlockBelowLabel"
          draggable="false"
          @mousedown.stop
          @click="onAddBlockBelow">
          <PlusIcon/>
        </button>
      </div>
    </DragHandle>
</template>

<style scoped>
.dh-wrap {
  user-select: none;
  -webkit-user-drag: element;
}
.dh-wrap:focus-within:not(.dh-suppressed) {
  visibility: visible !important;
  pointer-events: auto !important;
}
/* Hide the block drag handle for table blocks — the table's own row/col
   rails + insert/delete chips occupy the same gutter and would otherwise
   visually collide with (and intercept clicks meant for) the drag-grip.
   Set with !important to defeat the plugin's inline `visibility: visible`
   it writes when entering a table cell. */
.dh-wrap.dh-suppressed {
  visibility: hidden !important;
  pointer-events: none !important;
}
/* Grip on top, "+" under it: the 24px column and its 4px gap to the text fit the editor gutter (drag.css). */
.dh-row {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 2px;
  margin-right: 4px;
}
.dh-grip {
  width: 24px;
  height: 24px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 5px;
  color: var(--text-dimmer);
  font-size: 14px;
  cursor: grab;
  transition: background var(--md-motion-fast), color var(--md-motion-fast);
}
.dh-wrap:is([data-keyboard=true], [data-touch=true]):not(.dh-suppressed) { visibility: visible !important; pointer-events: auto !important; opacity: 1; transition: none; }
.dh-grip:hover,
.dh-grip:focus-visible {
  background: var(--bg-3);
  background: color-mix(in oklab, var(--text) 8%, transparent);
  color: var(--text);
}
.dh-grip:focus-visible,
.dh-add:focus-visible {
  outline: var(--focus-ring);
  outline-offset: var(--focus-offset);
}
.dh-wrap[data-dragging="true"] .dh-grip {
  cursor: grabbing;
  background: var(--accent-soft);
  color: var(--chip-accent-fg);
}
.dh-grip:active { cursor: grabbing; }
.dh-grip[aria-disabled="true"] { opacity: .5; cursor: not-allowed; }

.dh-add {
  width: 24px; height: 24px;
  border: none; padding: 0;
  background: transparent;
  color: var(--text-dimmer);
  display: inline-flex; align-items: center; justify-content: center;
  border-radius: 5px;
  cursor: pointer;
  transition: background var(--md-motion-fast), color var(--md-motion-fast);
}
.dh-add:hover,
.dh-add:focus-visible {
  background: var(--bg-3);
  background: color-mix(in oklab, var(--text) 8%, transparent);
  color: var(--text);
}
.dh-add :deep(svg), .dh-grip :deep(svg) { width: 16px; height: 16px; }
</style>
