<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import type { Editor } from '@tiptap/core'
import type { Transaction } from '@tiptap/pm/state'
import { insertBlockAt } from '../features/drag/blockInsertion'
import { isMarkdownComposition } from '../features/modules/composition'
import { observeMarkdownEditability } from '../features/modules/editability'
import { PlusIcon } from './icons'
import { dragStrings } from './strings/drag'

const props = defineProps<{ editor: Editor; boundary: HTMLElement | null }>()
const content = props.editor.view.dom
const ownerDocument = content.ownerDocument
const ownerWindow = ownerDocument.defaultView!
const button = ref<HTMLButtonElement | null>(null)
type Target = { position: number; y: number; left: number; width: number; tolerance: number }
const target = ref<Target | null>(null)
let boundaries: Target[] | null = null
let observer: ResizeObserver | undefined
// Mounted read-only too (contract J1.2): a flip to read-only hides a shown button without a transaction.
const stopEditable = observeMarkdownEditability(props.editor, content, () => { if (!available()) hide() })

function hide() { target.value = null }
function invalidate() { boundaries = null; hide() }
function available() {
  return !props.editor.isDestroyed && props.editor.isEditable && !props.editor.view.composing
    && !props.editor.view.dragging && content.dataset.blockDragging !== 'true'
}
function geometry(): Target[] {
  if (boundaries) return boundaries
  const rect = content.getBoundingClientRect()
  const blocks: Array<{ position: number; top: number; bottom: number }> = []
  props.editor.state.doc.forEach((_node, position) => {
    const element = props.editor.view.nodeDOM(position)
    if (!(element instanceof ownerWindow.HTMLElement)) return
    const box = element.getBoundingClientRect()
    if (box.height) blocks.push({ position, top: box.top, bottom: box.bottom })
  })
  boundaries = blocks.map((block, index) => ({ position: block.position,
    y: index ? (blocks[index - 1]!.bottom + block.top) / 2 : block.top - 12,
    tolerance: index ? Math.max(0, Math.min(12, (block.top - blocks[index - 1]!.bottom) / 2)) : 12,
    left: rect.left, width: rect.width }))
  const last = blocks.at(-1)
  if (last) boundaries.push({ position: props.editor.state.doc.content.size, y: last.bottom + 12, left: rect.left, width: rect.width, tolerance: 12 })
  return boundaries
}
function onPointerMove(event: PointerEvent) {
  if (!available() || event.buttons) { hide(); return }
  if (button.value?.contains(event.target as Node)) return
  if (!props.boundary?.contains(event.target as Node)) { hide(); return }
  if (button.value === ownerDocument.activeElement) return
  target.value = geometry().find(point => event.clientX >= point.left && event.clientX <= point.left + point.width
    && point.tolerance > 0 && Math.abs(event.clientY - point.y) <= point.tolerance) ?? null
}
function onTransaction({ transaction }: { transaction: Transaction }) {
  if (transaction.docChanged || !available()) invalidate()
}
function onTouchEnd(event: PointerEvent) {
  if (event.pointerType !== 'touch' || !available()) return
  let element = event.target as HTMLElement | null
  while (element && element.parentElement !== content) element = element.parentElement
  if (!element) return
  const position = props.editor.view.posAtDOM(element, 0)
  const resolved = props.editor.state.doc.resolve(position)
  const end = resolved.depth ? resolved.after(1) : position + (props.editor.state.doc.nodeAt(position)?.nodeSize ?? 0)
  target.value = geometry().find(point => point.position === end) ?? null
}
async function onKeydown(event: KeyboardEvent) {
  if (event.defaultPrevented || event.target !== content || !event.altKey || event.ctrlKey || event.metaKey
    || event.shiftKey || event.key !== 'Insert' || !available() || isMarkdownComposition(event, props.editor)) return
  event.preventDefault()
  const selection = props.editor.state.selection
  const position = selection.$to.depth ? selection.$to.after(1) : selection.to
  target.value = geometry().find(point => point.position === position) ?? null
  await nextTick()
  button.value?.focus({ preventScroll: true })
}
function cancel() { hide(); props.editor.view.focus() }
function insert() {
  const position = target.value?.position
  if (position !== undefined && available()) insertBlockAt(props.editor, position)
  hide()
}
function positionStyle() {
  const point = target.value, boundary = props.boundary
  if (!point || !boundary) return {}
  const box = boundary.getBoundingClientRect()
  return { left: `${point.left - box.left - boundary.clientLeft + boundary.scrollLeft}px`,
    top: `${point.y - box.top - boundary.clientTop + boundary.scrollTop}px`, width: `${point.width}px` }
}
onMounted(() => {
  ownerDocument.addEventListener('pointermove', onPointerMove)
  ownerDocument.addEventListener('dragstart', hide, true)
  ownerDocument.addEventListener('scroll', invalidate, true)
  ownerWindow.addEventListener('resize', invalidate)
  content.addEventListener('keydown', onKeydown)
  content.addEventListener('pointerup', onTouchEnd)
  props.editor.on('transaction', onTransaction)
  observer = new ResizeObserver(invalidate)
  observer.observe(content)
})
onBeforeUnmount(() => {
  ownerDocument.removeEventListener('pointermove', onPointerMove)
  ownerDocument.removeEventListener('dragstart', hide, true)
  ownerDocument.removeEventListener('scroll', invalidate, true)
  ownerWindow.removeEventListener('resize', invalidate)
  content.removeEventListener('keydown', onKeydown)
  content.removeEventListener('pointerup', onTouchEnd)
  props.editor.off('transaction', onTransaction)
  stopEditable()
  observer?.disconnect()
})
</script>

<template>
  <Transition name="md-block-inserter-fade" @before-leave="element => { (element as HTMLElement).inert = true }">
  <div v-if="target && boundary" :key="target.position" class="md-block-inserter" :style="positionStyle()">
    <button ref="button" type="button" data-testid="block-inserter" :aria-label="dragStrings.insertBlockHereLabel"
      aria-keyshortcuts="Alt+Insert" :title="dragStrings.insertBlockHereTitle"
      @pointerdown.prevent.stop @click.stop="insert" @keydown.esc.prevent.stop="cancel" @blur="hide">
      <PlusIcon />
    </button>
  </div>
  </Transition>
</template>
