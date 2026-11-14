<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { Editor } from '@tiptap/core'
import { BubbleMenu } from '@tiptap/vue-3/menus'
import { PluginKey } from '@tiptap/pm/state'
import { CellSelection } from '@tiptap/pm/tables'
import type { MarkdownProfile } from '../createMarkdownKit'
import type { MarkdownCommand } from '../core/types'
import { useToolbarCommands } from './useToolbarCommands'
import { useAnchoredPopover } from './useAnchoredPopover'
import { isMarkdownComposition } from '../features/modules/composition'
import { MarkdownIcon } from '../icons'
import { ChevronDownIcon, TrashIcon,
  RowAddBeforeIcon, RowAddAfterIcon, RowDeleteIcon,
  ColumnAddBeforeIcon, ColumnAddAfterIcon, ColumnDeleteIcon } from './icons'
import { tableStrings } from './strings/table'
import { tableToolbarOffset } from '../features/table/geometry'

const props = defineProps<{ editor: Editor; profile: MarkdownProfile; boundary?: HTMLElement | null }>()
const ownerDocument = props.editor.view.dom.ownerDocument
const emit = defineEmits<{ (event: 'popover-open', open: boolean): void }>()
const { query, run, isActive } = useToolbarCommands(() => props.editor, () => props.profile)
const tableMenuKey = new PluginKey('tableMenu')
const tableBubbleOptions = computed(() => ({
  // Over the column chips, not over the neighbouring blocks' text (tableToolbarOffset); a table that opens the page keeps it off the
  // page header (PG4-8).
  placement: 'top-start' as const,
  offset: ({ rects, placement }: { rects: { floating: { height: number } }; placement: string }) => {
    const wrapper = tableVirtualElement()?.contextElement
    if (!wrapper) return 0
    const table = wrapper.querySelector(':scope > table') ?? wrapper
    const marginBelow = parseFloat(ownerDocument.defaultView?.getComputedStyle(wrapper).marginBottom ?? '') || 0
    return tableToolbarOffset(placement, wrapper.getBoundingClientRect(), table.getBoundingClientRect(), props.editor.view.dom.getBoundingClientRect().top, rects.floating.height, marginBelow)
  },
  flip: { boundary: props.boundary ?? undefined, padding: 8 },
  shift: { boundary: [], padding: 8 },
  onHide: closeTablePopovers,
}))
function tableVirtualElement() {
  const e = props.editor
  if (!e) return null
  const sel = e.state.selection
  for (let depth = sel.$from.depth; depth >= 0; depth--) {
    const node = sel.$from.node(depth)
    if (node.type.name === 'table') {
      const tablePos = sel.$from.before(depth)
      const dom = e.view.nodeDOM(tablePos)
      const Element = e.view.dom.ownerDocument.defaultView?.HTMLElement ?? globalThis.HTMLElement
      if (dom instanceof Element) {
        return {
          getBoundingClientRect: () => dom.getBoundingClientRect(),
          contextElement: dom,
        }
      }
    }
  }
  return null
}


// The tool buttons are drawn the first time the toolbar is wanted, not with every editor that mounts.
const built = ref(false)
function shouldShowTableMenu(args: { editor: Editor }): boolean {
  const show = wantsTableMenu(args)
  if (show) built.value = true
  return show
}
function wantsTableMenu({ editor: e }: { editor: Editor }): boolean {
  // `isFocused` gate: each editor instance owns its own selection state, so
  // without it every editor on the page that has a cursor in a table would
  // show its own bubble-menu in parallel. The active editor's menu would
  // overlap the others' content and intercept clicks.
  const focusedControl = ownerDocument.activeElement?.closest('.tt-table-controls')
  const ownsFocus = e.isFocused || !!focusedControl && e.view.dom.contains(focusedControl)
    || [rowPopup.value, columnPopup.value, rowTrigger.value, columnTrigger.value].some(element => element?.contains(ownerDocument.activeElement))
  if (!e?.isEditable || !e.isActive('table') || !ownsFocus) return false
  const selection = e.state.selection
  // A caret or structural cell selection belongs to the table toolbar. A real text
  // selection inside a cell belongs to the formatting toolbar; showing both creates
  // the stacked menus reported by users.
  return selection.empty || selection instanceof CellSelection
}


const tableCommands = {
  addRowBefore: { type: 'add-row', side: 'before' }, addRowAfter: { type: 'add-row', side: 'after' },
  deleteRow: { type: 'delete-row' }, addColumnBefore: { type: 'add-column', side: 'before' },
  addColumnAfter: { type: 'add-column', side: 'after' }, deleteColumn: { type: 'delete-column' },
  sortAscending: { type: 'sort-table', direction: 'ascending' },
  sortDescending: { type: 'sort-table', direction: 'descending' },
  toggleHeaderRow: { type: 'toggle-header-row' }, deleteTable: { type: 'delete-table' },
} as const satisfies Record<string, MarkdownCommand>
type TableCmd = keyof typeof tableCommands | 'toggleHeaderCell' | 'mergeOrSplit'
function tableCmd(command: TableCmd) {
  const editor = props.editor
  if (command === 'toggleHeaderCell') {
    // Arbitrary cell headers cannot roundtrip through GFM.
    if (props.profile === 'rich' && editor.isEditable && editor.can().toggleHeaderCell()) editor.chain().focus().toggleHeaderCell().run()
  } else if (command === 'mergeOrSplit') {
    if (!run({ type: 'merge-cells' })) run({ type: 'split-cell' })
  } else {
    run(tableCommands[command])
  }
  closeTablePopovers()
}
const rowTrigger = ref<HTMLElement | null>(null)
const rowPopup = ref<HTMLElement | null>(null)
const columnTrigger = ref<HTMLElement | null>(null)
const columnPopup = ref<HTMLElement | null>(null)
useAnchoredPopover(() => rowTrigger.value, () => rowPopup.value, undefined, { editor: props.editor, close: () => { tableRowMenuOpen.value = false } })
useAnchoredPopover(() => columnTrigger.value, () => columnPopup.value, undefined, { editor: props.editor, close: () => { tableColumnMenuOpen.value = false } })

const tableRowMenuOpen = ref(false)
const tableColumnMenuOpen = ref(false)

function closeTablePopovers() {
  tableRowMenuOpen.value = false
  tableColumnMenuOpen.value = false
}

function toggleTablePopover(which: 'row' | 'column') {
  const next = which === 'row' ? !tableRowMenuOpen.value : !tableColumnMenuOpen.value
  closeTablePopovers()
  if (which === 'row') tableRowMenuOpen.value = next
  else tableColumnMenuOpen.value = next
}

function onEscape(event: KeyboardEvent) {
  if (isMarkdownComposition(event, props.editor)) return
  if (event.key !== 'Escape' || (!tableRowMenuOpen.value && !tableColumnMenuOpen.value)) return
  event.preventDefault()
  closeTablePopovers()
  // Restore the native selection synchronously; Tiptap delays this on Safari.
  props.editor.view.focus()
  event.stopPropagation()
}
watch([tableRowMenuOpen, tableColumnMenuOpen], values => emit('popover-open', values.some(Boolean)))
function onOutsidePointer(event: PointerEvent) {
  const target = event.target as Node | null
  if (!target) return
  if (![rowTrigger.value, rowPopup.value, columnTrigger.value, columnPopup.value]
    .some(element => element?.contains(target))) closeTablePopovers()
}
onMounted(() => {
  ownerDocument.addEventListener('keydown', onEscape)
  ownerDocument.addEventListener('pointerdown', onOutsidePointer)
})
onBeforeUnmount(() => {
  ownerDocument.removeEventListener('keydown', onEscape)
  ownerDocument.removeEventListener('pointerdown', onOutsidePointer)
  emit('popover-open', false)
})
</script>

<template>
    <BubbleMenu
      :editor="props.editor"
      :plugin-key="tableMenuKey"
      :update-delay="80"
      :should-show="shouldShowTableMenu"
      :options="tableBubbleOptions"
      :get-referenced-virtual-element="tableVirtualElement"
      @mousedown.prevent
      class="bubble-menu table-menu"
      data-testid="table-menu"
      role="group"
      :aria-label="tableStrings.toolbarLabel">
    <template v-if="built">
      <button type="button" class="bb" :class="{ on: isActive('tableHeader') }" :title="tableStrings.cellAsHeaderLabel" :aria-label="tableStrings.cellAsHeaderLabel" v-if="props.profile === 'rich'" data-testid="cell-toggle-header" @click="tableCmd('toggleHeaderCell')">
        <!-- Distinct icon vs row-toggle below: single-cell highlight (top-left) -->
        <MarkdownIcon name="table-header-cell" :size="16" />
      </button>
      <button v-if="props.profile === 'rich'" type="button" class="bb" :title="tableStrings.mergeSplitCellsLabel" :aria-label="tableStrings.mergeSplitCellsLabel" data-testid="cell-merge-split" @click="tableCmd('mergeOrSplit')">
        <MarkdownIcon name="table-merge" :size="16" />
      </button>
      <button type="button" class="bb" :class="{ on: isActive('tableHeader') }" :title="tableStrings.toggleHeaderRowLabel" :aria-label="tableStrings.toggleHeaderRowLabel" v-if="props.profile === 'rich'" data-testid="table-toggle-header" :disabled="!query(tableCommands.toggleHeaderRow).enabled" @click="tableCmd('toggleHeaderRow')">
        <MarkdownIcon name="table-header-row" :size="16" />
      </button>
      <div class="bb-group">
        <button type="button" class="bb bb-menu-trigger" :class="{ on: tableRowMenuOpen }" :aria-label="tableStrings.rowOperationsLabel" aria-haspopup="menu" :aria-expanded="tableRowMenuOpen" ref="rowTrigger" data-testid="table-row-menu-trigger" @click.stop="toggleTablePopover('row')">
          <RowAddAfterIcon/><ChevronDownIcon class="bb-trigger-chevron"/>
        </button>
        <div v-if="tableRowMenuOpen" class="bb-popover bb-table-actions" role="menu" :aria-label="tableStrings.rowOperationsLabel" ref="rowPopup" data-testid="table-row-menu" @click.stop @keydown.escape="onEscape">
          <button type="button" role="menuitem" :disabled="!query(tableCommands.addRowBefore).enabled" @click="tableCmd('addRowBefore')"><RowAddBeforeIcon/><span>{{ tableStrings.insertRowAboveLabel }}</span></button>
          <button type="button" role="menuitem" :disabled="!query(tableCommands.addRowAfter).enabled" @click="tableCmd('addRowAfter')"><RowAddAfterIcon/><span>{{ tableStrings.insertRowBelowLabel }}</span></button>
          <button type="button" role="menuitem" class="danger" :disabled="!query(tableCommands.deleteRow).enabled" @click="tableCmd('deleteRow')"><RowDeleteIcon/><span>{{ tableStrings.deleteRowLabel }}</span></button>
        </div>
      </div>
      <span class="sep" aria-hidden="true"/>
      <div class="bb-group">
        <button type="button" class="bb bb-menu-trigger" :class="{ on: tableColumnMenuOpen }" :aria-label="tableStrings.columnOperationsLabel" aria-haspopup="menu" :aria-expanded="tableColumnMenuOpen" ref="columnTrigger" data-testid="table-column-menu-trigger" @click.stop="toggleTablePopover('column')">
          <ColumnAddAfterIcon/><ChevronDownIcon class="bb-trigger-chevron"/>
        </button>
        <div v-if="tableColumnMenuOpen" class="bb-popover bb-table-actions" role="menu" :aria-label="tableStrings.columnOperationsLabel" ref="columnPopup" data-testid="table-column-menu" @click.stop @keydown.escape="onEscape">
          <button type="button" role="menuitem" :disabled="!query(tableCommands.addColumnBefore).enabled" @click="tableCmd('addColumnBefore')"><ColumnAddBeforeIcon/><span>{{ tableStrings.insertColumnLeftLabel }}</span></button>
          <button type="button" role="menuitem" :disabled="!query(tableCommands.addColumnAfter).enabled" @click="tableCmd('addColumnAfter')"><ColumnAddAfterIcon/><span>{{ tableStrings.insertColumnRightLabel }}</span></button>
          <hr role="separator" />
          <button type="button" role="menuitem" data-testid="table-sort-ascending" :disabled="!query(tableCommands.sortAscending).enabled" :title="tableStrings.sortHint" @click="tableCmd('sortAscending')"><MarkdownIcon name="arrow-down-a-z" :size="16" /><span>{{ tableStrings.sortAscendingLabel }}</span></button>
          <button type="button" role="menuitem" data-testid="table-sort-descending" :disabled="!query(tableCommands.sortDescending).enabled" :title="tableStrings.sortHint" @click="tableCmd('sortDescending')"><MarkdownIcon name="arrow-down-z-a" :size="16" /><span>{{ tableStrings.sortDescendingLabel }}</span></button>
          <hr role="separator" />
          <button type="button" role="menuitem" class="danger" :disabled="!query(tableCommands.deleteColumn).enabled" @click="tableCmd('deleteColumn')"><ColumnDeleteIcon/><span>{{ tableStrings.deleteColumnLabel }}</span></button>
        </div>
      </div>
      <span class="sep" aria-hidden="true"/>
      <!-- Danger stays neutral at rest — a permanently red trash can in the toolbar was the only
           coloured icon outside a menu. It turns red only on hover/focus, same as a menu's danger
           item, via .bb-danger-hover. -->
      <button type="button" class="bb bb-danger-hover" :title="tableStrings.deleteTableLabel" :aria-label="tableStrings.deleteTableLabel" data-testid="table-delete" :disabled="!query(tableCommands.deleteTable).enabled" @click="tableCmd('deleteTable')">
        <TrashIcon/>
      </button>
    </template>
    </BubbleMenu>
</template>
