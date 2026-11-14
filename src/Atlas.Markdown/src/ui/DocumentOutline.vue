<script setup lang="ts">
import { computed, markRaw, nextTick, onBeforeUnmount, onMounted, ref, useId, watch, watchEffect } from 'vue'
import type { Editor } from '@tiptap/core'
import { editorOutlineSource, outlineWave, staticOutlineSource, type MarkdownOutlineOptions, type OutlineHeading, type OutlineSource } from './outline'
import { createLiveOutlinePreview } from '../features/outline/livePreview'
import { registerMarkdownOverlayForElement } from '../core/overlay'
import { markdownMotion, markdownScrollBehavior } from '../core/motion'
import { registerMarkdownPopover } from '../features/modules/popoverInteraction'
import { OutlineIcon, PinIcon, CollapseOutlineIcon } from './icons'
import { outlineStrings } from './strings/outline'
import '../styles/outline.css'

// A live editor, or a statically rendered document (MarkdownRenderer's document variant) whose `revision`
// changes with every render. The sources are built here, so hosts keep the outline code in this lazy chunk.
const props = defineProps<{ editor?: Editor; content?: HTMLElement; revision?: number; options: MarkdownOutlineOptions }>()
const staticSource = props.editor ? undefined : staticOutlineSource(props.content!)
const source: OutlineSource = staticSource ?? editorOutlineSource(props.editor!)
watch(() => props.revision, () => staticSource?.changed())
const emit = defineEmits<{ docked: [value: boolean] }>()
const headings = ref<OutlineHeading[]>([])
const current = ref(0)
const selected = ref(0)
const open = ref(false)
const pinned = ref(false)
const collapsed = ref(false)
const root = ref<HTMLElement>()
const trigger = ref<HTMLButtonElement>()
const rail = ref<HTMLElement>()
const panel = ref<HTMLElement>()
const list = ref<HTMLElement>()
const preview = ref<HTMLElement>()
const panelId = useId()
const railSpace = ref(420)
const railPitch = computed(() => Math.min(12, Math.max(1, railSpace.value - 36) / Math.max(1, headings.value.length)))
function measureRail() {
  if (!rail.value || !hasRail.value) return
  railSpace.value = Math.max(60, Math.min(420, win.innerHeight - rail.value.getBoundingClientRect().top - 20))
  wave(waveTarget)
}
const docked = computed(() => props.options.mode === 'panel' || pinned.value)
const shown = computed(() => docked.value ? !collapsed.value : open.value)
const hasRail = computed(() => props.options.mode === 'floating' && !pinned.value && !collapsed.value)
watch(() => docked.value && shown.value, value => emit('docked', value), { immediate: true })
let closeTimer: number | undefined
let openTimer: number | undefined
let highlightTimer: number | undefined
let highlighted: HTMLElement | null = null
let previewMirror: ReturnType<typeof createLiveOutlinePreview> | undefined
let stopPosition: (() => void) | undefined
let waveFrame = 0
let waveTarget: number | null = null
let previousTime = 0
let explicitOpen = false
let suppressHover = false
let pointerPosition = { x: 0, y: 0 }
let dismissedAt = { x: 0, y: 0 }
function trackPointer(event: PointerEvent) { pointerPosition = { x: event.clientX, y: event.clientY } }
const motion = new Map<HTMLElement, ReturnType<typeof outlineWave>>()
const win = source.root.ownerDocument.defaultView!
const headingHighlight = source.createHighlight()
const reduced = () => win.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false

function clearTimer(kind: 'open' | 'close' | 'highlight') {
  const value = kind === 'open' ? openTimer : kind === 'close' ? closeTimer : highlightTimer
  if (value !== undefined) win.clearTimeout(value)
}
function highlight(element: HTMLElement | null) {
  clearTimer('highlight')
  if (highlighted === element) return
  highlighted = element
  headingHighlight.set(element ? headings.value.find(entry => entry.element === element) ?? null : null)
}
function refresh(map?: (pos: number) => number) {
  const previous = headings.value[selected.value]
  const position = previous && map ? map(previous.pos) : previous?.pos
  headings.value = source.collect().map(entry => ({ ...entry, element: markRaw(entry.element) }))
  selected.value = Math.max(0, headings.value.findIndex(entry => previous?.headingId ? entry.headingId === previous.headingId : entry.pos === position))
  current.value = Math.min(current.value, Math.max(0, headings.value.length - 1))
  if (highlighted) highlight(headings.value[selected.value]?.element ?? null)
  if (!headings.value.length) { highlight(null); if (!docked.value) open.value = false }
  motion.clear()
  void nextTick(() => { previewMirror?.refresh(); measureRail(); wave(waveTarget); locateCurrent() })
}
function redrawn() {
  if (shown.value) previewMirror?.refresh()
}
let stopSource: (() => void) | undefined
function locateCurrent() {
  if (!root.value || !headings.value.length) return
  const threshold = Math.max(24, root.value.getBoundingClientRect().top + 20)
  let index = 0
  for (let i = 0; i < headings.value.length; i++) {
    if (headings.value[i].element.getBoundingClientRect().top <= threshold) index = i
    else break
  }
  if (index !== current.value) { current.value = index; wave(waveTarget) }
}
function keepRowVisible(index: number) {
  const row = list.value?.children[index] as HTMLElement | undefined
  const parent = list.value
  if (!row || !parent) return
  const y = row.offsetTop - parent.offsetTop
  if (y < parent.scrollTop) parent.scrollTop = y
  else if (y + row.offsetHeight > parent.scrollTop + parent.clientHeight) parent.scrollTop = y + row.offsetHeight - parent.clientHeight
}
function select(index: number, animateWave = true) {
  if (!headings.value[index]) return
  selected.value = index
  highlight(headings.value[index].element)
  if (animateWave) wave(index)
  keepRowVisible(index)
  previewMirror?.refresh()
}
function show(persist = false) {
  explicitOpen ||= persist
  clearTimer('open'); clearTimer('close')
  collapsed.value = false; open.value = true
  void nextTick(() => select(selected.value))
}
function close(restoreFocus = false) {
  suppressHover = true
  dismissedAt = { ...pointerPosition }
  explicitOpen = false
  clearTimer('open'); clearTimer('close')
  if (restoreFocus) trigger.value?.focus({ preventScroll: true })
  if (docked.value) collapsed.value = true
  open.value = false; highlight(null); wave(null)
}
function toggle() {
  if (docked.value) { if (collapsed.value) show(true); else close(true) }
  else if (collapsed.value) { collapsed.value = false; show(true) }
  else if (open.value) close(true)
  else show(true)
}
function pin() {
  clearTimer('open'); clearTimer('close'); explicitOpen = true
  pinned.value = !pinned.value
  collapsed.value = false; open.value = true
  wave(null)
  void nextTick(() => panel.value?.querySelector<HTMLButtonElement>('.md-outline-pin')?.focus({ preventScroll: true }))
}
function enter(event: PointerEvent) {
  clearTimer('close')
  if (event.pointerType === 'touch' || shown.value || suppressHover) return
  clearTimer('open'); openTimer = win.setTimeout(() => show(), 90)
}
function leave() {
  clearTimer('open'); clearTimer('close')
  if (docked.value) { highlight(null); wave(null); return }
  if (explicitOpen) return
  closeTimer = win.setTimeout(() => {
    if (explicitOpen || docked.value) return
    if (!panel.value?.contains(win.document.activeElement) && !rail.value?.contains(win.document.activeElement)) close()
  }, 240)
}
function focusOut(event: FocusEvent) {
  const next = event.relatedTarget as Node | null
  if (next && !root.value?.contains(next) && !docked.value) close()
}
function jump(index: number) {
  const entry = headings.value[index]
  if (!entry || !source.root.contains(entry.element)) return
  entry.element.scrollIntoView?.({ block: 'start', behavior: markdownScrollBehavior(entry.element) })
  // The writer's caret goes into the heading (G2); a reader's navigation is view-only. Neither writes the document or its history.
  const editor = props.editor?.isEditable && !props.editor.isDestroyed ? props.editor : undefined
  if (!docked.value) close(!editor)
  if (editor) { editor.commands.setTextSelection(editor.view.posAtDOM(entry.element, 0)); editor.view.focus() }
  highlight(entry.element)
  current.value = index
  // The afterglow of the target heading is the glow token (styles/outline.css) plus the reveal's 140 ms.
  highlightTimer = win.setTimeout(() => highlight(null), markdownMotion(entry.element).duration('glow') + 140)
}
function keyboard(event: KeyboardEvent, index: number, source: 'rail' | 'list') {
  if (event.key === 'Enter' || event.key === ' ') {
    // Prevent the keyup click from hitting the trigger after focus is restored
    // (notably WebKit), which would reopen the outline immediately after a jump.
    event.preventDefault(); jump(index); return
  }
  let target = index
  if (event.key === 'ArrowDown') target++
  else if (event.key === 'ArrowUp') target--
  else if (event.key === 'Home') target = 0
  else if (event.key === 'End') target = headings.value.length - 1
  else return
  event.preventDefault()
  target = Math.max(0, Math.min(headings.value.length - 1, target))
  const parent = source === 'rail' ? rail.value : list.value
  ;(parent?.children[target] as HTMLElement | undefined)?.focus({ preventScroll: true })
  select(target)
}
function wave(index: number | null) {
  waveTarget = index
  if (!waveFrame && rail.value) { previousTime = 0; waveFrame = win.requestAnimationFrame(drawWave) }
}
function drawWave(time: number) {
  waveFrame = 0
  const dt = previousTime ? Math.min(40, time - previousTime) : 16
  previousTime = time
  const blend = reduced() ? 1 : 1 - Math.exp(-dt / 56)
  let moving = false
  for (const [index, element] of [...(rail.value?.children ?? [])].entries()) {
    const button = element as HTMLElement
    const target = outlineWave(headings.value[index]?.level ?? 1, waveTarget === null ? null : index - waveTarget, index === current.value, railPitch.value)
    const value = motion.get(button) ?? outlineWave(headings.value[index]?.level ?? 1, null, index === current.value, railPitch.value)
    for (const key of Object.keys(target) as Array<keyof typeof target>) {
      value[key] += (target[key] - value[key]) * blend
      if (Math.abs(target[key] - value[key]) < .002) value[key] = target[key]
      else moving = true
    }
    motion.set(button, value)
    for (const key of ['width', 'height', 'x', 'y'] as const) button.style.setProperty(`--outline-${key}`, `${value[key]}px`)
    button.style.setProperty('--outline-label', String(value.opacity))
  }
  if (moving) waveFrame = win.requestAnimationFrame(drawWave)
}
function pointerMove(event: PointerEvent) {
  if (event.pointerType === 'touch') return
  if (suppressHover) {
    if (Math.hypot(event.clientX - dismissedAt.x, event.clientY - dismissedAt.y) < 4) return
    suppressHover = false; enter(event)
  }
  // Keep painted targets still while crossing their enlarged or displaced edges.
  if ((event.target as Element).closest('.md-outline-mark')) return
  const first = rail.value?.firstElementChild?.getBoundingClientRect()
  if (first?.height) wave((event.clientY - first.top) / first.height - .5)
}
function outside(event: PointerEvent) {
  if (!shown.value || docked.value) return
  if (!(event.composedPath().some(node => node === panel.value || node === rail.value || node === trigger.value))) close()
}
// Watch each primitive separately: a getter returning a fresh array would fire for every new
// `options` object (e.g. an inline `:outline="{…}"` literal on parent re-render) and reset the
// user's pin/collapse state even when nothing changed. Multi-source watchers compare per value.
watch([() => props.options.mode, () => props.options.pinned ?? false, () => props.options.collapsed ?? false], () => {
  pinned.value = props.options.pinned ?? false; collapsed.value = props.options.collapsed ?? false
  open.value = false; explicitOpen = false; highlight(null)
}, { immediate: true })
watchEffect(onCleanup => {
  if (!shown.value || !panel.value) return
  const release = registerMarkdownOverlayForElement(source.root, panel.value)
  const releasePopover = !docked.value && trigger.value ? registerMarkdownPopover(source.popoverOwner, {
    anchor: trigger.value, key: 'document-outline', close: () => close(),
  }) : undefined
  onCleanup(() => { release(); releasePopover?.() })
}, { flush: 'post' })
watchEffect(onCleanup => {
  if (!shown.value || props.options.preview === false || !preview.value) return
  const mirror = createLiveOutlinePreview(source.root, preview.value, () => headings.value[selected.value]?.element ?? null)
  previewMirror = mirror
  onCleanup(() => { mirror.destroy(); if (previewMirror === mirror) previewMirror = undefined })
}, { flush: 'post' })
onMounted(() => {
  refresh()
  stopSource = source.subscribe(refresh, redrawn)
  win.document.addEventListener('pointerdown', outside, true)
  win.document.addEventListener('pointermove', trackPointer, true)
  let positionFrame = 0
  const updatePosition = () => {
    if (!positionFrame) positionFrame = win.requestAnimationFrame(() => { positionFrame = 0; locateCurrent(); measureRail() })
  }
  win.document.addEventListener('scroll', updatePosition, true)
  win.addEventListener('resize', updatePosition)
  const resize = win.ResizeObserver ? new win.ResizeObserver(() => {
    if (panel.value && root.value?.parentElement) root.value.parentElement.style.setProperty('--outline-panel-height', `${panel.value.offsetHeight + 52}px`)
    updatePosition()
  }) : null
  if (panel.value) resize?.observe(panel.value)
  stopPosition = () => {
    win.document.removeEventListener('scroll', updatePosition, true); win.removeEventListener('resize', updatePosition)
    if (positionFrame) win.cancelAnimationFrame(positionFrame)
    resize?.disconnect()
  }
})
onBeforeUnmount(() => {
  stopSource?.()
  win.document.removeEventListener('pointerdown', outside, true)
  win.document.removeEventListener('pointermove', trackPointer, true)
  stopPosition?.(); previewMirror?.destroy(); highlight(null)
  headingHighlight.destroy()
  clearTimer('open'); clearTimer('close'); clearTimer('highlight')
  if (waveFrame) win.cancelAnimationFrame(waveFrame)
  motion.clear()
})
</script>

<template>
  <div ref="root" class="md-outline-navigation" :data-outline-mode="options.mode">
    <div class="md-outline-sticky">
      <div class="md-outline-controls">
        <button ref="trigger" type="button" class="bb md-outline-trigger" :aria-controls="panelId" :aria-expanded="shown"
          :aria-label="shown ? outlineStrings.toggleHide : outlineStrings.toggleShow" @click="toggle"><OutlineIcon /></button>
      </div>
      <Transition name="md-outline-wave">
      <div v-show="hasRail" ref="rail" class="md-outline-rail" :style="{ '--outline-pitch': `${railPitch}px` }" :inert="!hasRail" :aria-hidden="!hasRail || undefined" role="navigation" :aria-label="outlineStrings.railLabel"
        @pointerenter="enter" @pointerleave="leave" @pointermove="pointerMove" @keydown.esc.stop.prevent="close(true)">
        <button v-for="(entry, index) in headings" :key="entry.headingId ?? entry.pos" type="button" :data-level="entry.level"
          :aria-label="outlineStrings.railEntryLabel(entry.title, entry.level)" :aria-current="index === current ? 'location' : undefined"
          :aria-expanded="shown" :aria-controls="panelId"
          @pointerenter="select(index, false)" @focus="show(true); select(index)"
          @click="jump(index)" @keydown="keyboard($event, index, 'rail')">
          <span class="md-outline-mark" aria-hidden="true"><span>H{{ entry.level }}</span></span>
        </button>
      </div>
      </Transition>
      <Transition name="md-outline-pop">
      <div v-show="shown" :id="panelId" ref="panel" class="md-context-popover md-outline-panel" :class="{ 'is-docked': docked }"
        data-positioned="true"
        role="navigation" :aria-label="outlineStrings.panelLabel" @pointerenter="clearTimer('close')" @pointerleave="leave"
        @keydown.esc.stop.prevent="close(true)" @focusout="focusOut">
        <div class="md-outline-controls md-outline-panel-controls">
          <button v-if="options.mode === 'floating'" type="button" class="bb md-outline-pin" :aria-label="pinned ? outlineStrings.unpinLabel : outlineStrings.pinLabel"
            :aria-pressed="pinned" @click="pin"><PinIcon /></button>
          <button v-else type="button" class="bb" :aria-label="outlineStrings.collapseLabel" @click="close(true)"><CollapseOutlineIcon /></button>
        </div>
        <div ref="list" class="md-outline-list">
          <button v-for="(entry, index) in headings" :key="entry.headingId ?? entry.pos" type="button" class="bb-item md-outline-entry"
            :class="{ 'is-previewed': index === selected }"
            :data-level="entry.level"
            :aria-current="index === current ? 'location' : undefined" @pointerenter="select(index)" @focus="select(index)"
            @click="jump(index)" @keydown="keyboard($event, index, 'list')"><span>{{ entry.title }}</span></button>
        </div>
        <p v-if="!headings.length" class="md-outline-empty">{{ outlineStrings.emptyHint }}</p>
        <div v-if="options.preview !== false && headings.length" class="md-outline-preview" inert aria-hidden="true">
          <div class="md-outline-preview-caption">{{ outlineStrings.livePreviewCaption }}</div>
          <div class="editor-body"><div ref="preview" class="ProseMirror md-outline-preview-content" /></div>
        </div>
      </div>
      </Transition>
    </div>
  </div>
</template>
