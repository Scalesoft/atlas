<script setup lang="ts">
import { onBeforeUnmount, ref, useId, watch } from 'vue'
import type { Editor } from '@tiptap/core'
import { BubbleMenu } from '@tiptap/vue-3/menus'
import { PluginKey } from '@tiptap/pm/state'
import { CellSelection } from '@tiptap/pm/tables'
import type { MarkdownCommand, MarkdownProfile } from '../core/types'
import { CELL_BG_PALETTE } from '../features/table/cellPalette'
import { useToolbarCommands } from './useToolbarCommands'
import { useAnchoredPopover } from './useAnchoredPopover'
import { isMarkdownComposition } from '../features/modules/composition'
import { activeMarkdownPopover, onMarkdownPopoverChange } from '../features/modules/popoverInteraction'
import { AlignLeftIcon, AlignCenterIcon, AlignRightIcon, TrashIcon } from './icons'
import { tableStrings } from './strings/table'

const props = defineProps<{ editor: Editor; profile: MarkdownProfile }>()
const emit = defineEmits<{ 'popover-open': [open: boolean] }>()
const { query, run } = useToolbarCommands(() => props.editor, () => props.profile)
const key = new PluginKey('tableCellMenu')
const id = useId()
const open = ref(false)
const trigger = ref<HTMLButtonElement | null>(null)
const popup = ref<HTMLElement | null>(null)
const doc = props.editor.view.dom.ownerDocument
const alignments = [
  { value: 'left', label: tableStrings.alignLeftLabel, icon: AlignLeftIcon },
  { value: 'center', label: tableStrings.alignCenterLabel, icon: AlignCenterIcon },
  { value: 'right', label: tableStrings.alignRightLabel, icon: AlignRightIcon },
] as const

function cellElement(): HTMLElement | null {
  const selection = props.editor.state.selection
  const position = selection instanceof CellSelection ? selection.$anchorCell.pos : null
  if (position !== null) return props.editor.view.nodeDOM(position) as HTMLElement | null
  const { $from } = selection
  for (let depth = $from.depth; depth > 0; depth--) {
    if (['tableCell', 'tableHeader'].includes($from.node(depth).type.name)) {
      return props.editor.view.nodeDOM($from.before(depth)) as HTMLElement | null
    }
  }
  return null
}
function reference() {
  const cell = cellElement()
  return cell ? { contextElement: cell, getBoundingClientRect: () => cell.getBoundingClientRect() } : null
}
function shouldShow() {
  const editor = props.editor
  const focused = editor.isFocused || !!popup.value?.contains(doc.activeElement) || doc.activeElement === trigger.value
  return editor.isEditable && focused && !!cellElement()
    && (editor.state.selection.empty || editor.state.selection instanceof CellSelection)
}
const options = {
  placement: 'right' as const, offset: -12,
  flip: { boundary: [], padding: 8 }, shift: { boundary: [], padding: 8 },
  onHide: () => { open.value = false },
}
useAnchoredPopover(() => trigger.value, () => popup.value, () => {
  popup.value?.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus({ preventScroll: true })
}, { editor: props.editor, close: () => { open.value = false } })
function apply(command: MarkdownCommand) {
  if (run(command)) close()
}
function close() {
  open.value = false
  props.editor.view.focus()
}
function outside(event: PointerEvent) {
  const target = event.target as Node | null
  if (target && !popup.value?.contains(target) && !trigger.value?.contains(target)) open.value = false
}
function escape(event: KeyboardEvent) {
  if (isMarkdownComposition(event, props.editor)) return
  if (event.key !== 'Escape' || !open.value) return
  event.preventDefault()
  event.stopPropagation()
  close()
}
watch(open, value => emit('popover-open', value))
doc.addEventListener('pointerdown', outside)
doc.addEventListener('keydown', escape)

// Another popover (a row/column menu, the callout picker, …) can open right over this cell.
// The orb sits above the document at a fixed z-index, so without this it visibly poked through
// whatever menu was covering it. Hidden means "some other popover on this editor is open" — our
// own popover keeps the trigger visible (it needs to stay clickable to close itself).
const orbHidden = ref(false)
function refreshOrbVisibility() {
  const other = activeMarkdownPopover(props.editor)
  orbHidden.value = !!other && other.anchor !== trigger.value
}
const stopPopoverWatch = onMarkdownPopoverChange(props.editor, refreshOrbVisibility)
watch(open, refreshOrbVisibility)
refreshOrbVisibility()

onBeforeUnmount(() => {
  doc.removeEventListener('pointerdown', outside)
  doc.removeEventListener('keydown', escape)
  stopPopoverWatch()
  emit('popover-open', false)
})
</script>

<template>
  <BubbleMenu :editor="editor" :plugin-key="key" :update-delay="0" :should-show="shouldShow"
    :options="options" :get-referenced-virtual-element="reference" class="tt-cell-control" :class="{ 'tt-cell-control-hidden': orbHidden }">
    <button ref="trigger" type="button" class="tt-cell-trigger" data-testid="cell-menu-trigger"
      :aria-label="tableStrings.cellSettingsLabel" :aria-expanded="open" :aria-controls="open ? id : undefined"
      @mousedown.prevent @click="open = !open">
      <span class="tt-cell-orb" aria-hidden="true" />
      <span v-for="n in 4" :key="n" :class="`tt-cell-dot dot-${n}`" aria-hidden="true" />
    </button>
    <div v-if="open" :id="id" ref="popup" class="tt-cell-panel" data-testid="cell-menu"
      role="group" :aria-label="tableStrings.cellSettingsLabel" @mousedown.prevent>
      <div class="tt-cell-panel-content">
        <p class="tt-cell-label">{{ profile === 'rich' ? tableStrings.cellAlignLabel : tableStrings.columnAlignLabel }}</p>
        <div class="tt-cell-align" role="group" :aria-label="tableStrings.alignGroupLabel">
          <button v-for="alignment in alignments" :key="alignment.value" type="button"
            :aria-label="alignment.label" :title="alignment.label"
            :aria-pressed="query({ type: 'set-alignment', alignment: alignment.value }).active"
            :disabled="!query({ type: 'set-alignment', alignment: alignment.value }).enabled"
            @click="apply({ type: 'set-alignment', alignment: alignment.value })">
            <component :is="alignment.icon" />
          </button>
        </div>
        <template v-if="profile === 'rich'">
          <p class="tt-cell-label">{{ tableStrings.backgroundColorLabel }}</p>
          <div class="tt-cell-colors" role="group" :aria-label="tableStrings.backgroundColorLabel">
            <button v-for="color in CELL_BG_PALETTE" :key="color.testid" type="button"
              class="bb-bg-swatch" :class="`tone-${color.key ?? 'none'}`" :aria-label="color.label" :title="color.label"
              :aria-pressed="query({ type: 'set-cell-background', color: color.key }).active"
              :disabled="!query({ type: 'set-cell-background', color: color.key }).enabled"
              @click="apply({ type: 'set-cell-background', color: color.key })">
              <span class="bb-bg-dot" aria-hidden="true" />
            </button>
          </div>
        </template>
        <button type="button" class="tt-cell-clear" :disabled="!query({ type: 'clear-cell-content' }).enabled"
          @click="apply({ type: 'clear-cell-content' })"><TrashIcon />{{ tableStrings.clearContentLabel }}</button>
      </div>
    </div>
  </BubbleMenu>
</template>

<style scoped>
.tt-cell-control { z-index: 7; width: 24px; height: 24px; }
/* !important: BubbleMenu/floating-ui writes its own inline `visibility` style on every
   position update (hidden while measuring, visible once positioned) — a plain rule here would
   lose that fight and the orb would flicker back to visible on the next reposition. */
.tt-cell-control-hidden { visibility: hidden !important; pointer-events: none !important; }
.tt-cell-trigger {
  --spread: 0px;
  position: relative; width: 24px; height: 24px; padding: 0; border: 0;
  display: grid; place-items: center; border-radius: 50%; cursor: pointer; background: transparent;
}
.tt-cell-orb {
  width: 7px; height: 7px; border-radius: 50%; background: var(--accent, #58a9b9);
  box-shadow: 0 0 0 2px var(--paper, white);
  transition: transform var(--md-motion-fast) var(--md-motion-ease);
}
.tt-cell-dot {
  position: absolute; width: 3px; height: 3px; border-radius: 50%; background: white; opacity: 0;
  transition: transform var(--md-motion-fast) var(--md-motion-ease), opacity var(--md-motion-fast);
}
.dot-1 { transform: translate(calc(-1 * var(--spread)), calc(-1 * var(--spread))); }
.dot-2 { transform: translate(var(--spread), calc(-1 * var(--spread))); }
.dot-3 { transform: translate(calc(-1 * var(--spread)), var(--spread)); }
.dot-4 { transform: translate(var(--spread), var(--spread)); }
.tt-cell-trigger:is(:hover, :focus-visible, [aria-expanded="true"]) { --spread: 3px; }
.tt-cell-trigger:is(:hover, :focus-visible, [aria-expanded="true"]) .tt-cell-orb { transform: scale(3); }
.tt-cell-trigger:is(:hover, :focus-visible, [aria-expanded="true"]) .tt-cell-dot { opacity: 1; }
.tt-cell-trigger:focus-visible, .tt-cell-panel button:focus-visible { outline: var(--focus-ring); outline-offset: var(--focus-offset); }
.tt-cell-panel { position: absolute; width: 216px; max-width: calc(100vw - 16px); z-index: 10; }
.tt-cell-panel-content { padding: 10px; }
.tt-cell-label { margin: 0 0 8px; font: 500 12px/1.4 var(--font-sans, sans-serif); color: var(--text-dim, #49676e); }
.tt-cell-align, .tt-cell-colors { display: flex; gap: 6px; margin-bottom: 12px; }
.tt-cell-align button, .tt-cell-clear {
  display: flex; align-items: center; justify-content: center; min-height: 32px;
  border: 0; border-radius: 6px; background: transparent; color: inherit; cursor: pointer;
  transition: background-color var(--md-motion-fast);
}
.tt-cell-align button { flex: 1; }
.tt-cell-align button[aria-pressed="true"], .tt-cell-panel button:hover { background: var(--accent-soft, #e3f4f7); }
.tt-cell-panel svg { width: 16px; height: 16px; }
.tt-cell-clear { width: 100%; justify-content: flex-start; gap: 8px; font-size: 12px; }
.tt-cell-panel button:disabled { opacity: .5; cursor: not-allowed; }
.tt-cell-colors .bb-bg-swatch[aria-pressed="true"] {
  border-color: var(--accent, #58a9b9);
  box-shadow: 0 0 0 1px var(--accent, #58a9b9);
  background: var(--accent-soft, #e3f4f7);
}
@media (pointer: coarse) {
  .tt-cell-trigger { width: 32px; height: 32px; }
  .tt-cell-align button, .tt-cell-clear { min-height: 40px; }
  .tt-cell-colors .bb-bg-swatch { width: 32px; height: 32px; }
}
@media (prefers-reduced-motion: reduce) {
  .tt-cell-orb, .tt-cell-dot { transition: none; }
}
</style>
