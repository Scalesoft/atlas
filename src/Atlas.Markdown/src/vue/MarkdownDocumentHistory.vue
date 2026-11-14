<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, toRef, watch } from 'vue'
import { MarkdownIcon } from '../icons'
import MarkdownRenderer from './MarkdownRenderer.vue'
import type { MarkdownHistorySource } from '../features/history/types'
import type { MarkdownPlugin } from '../core'
import { useDocumentHistory } from '../features/history/useDocumentHistory'
import { DAY, MIN_HISTORY_SPAN, historyEntryAt, historyMarks, historyTicks, historyWindow, historyZoomSpan, historyZoomValue } from '../features/history/timeline'
import { markdownStrings } from '../ui/strings'
import { historyStrings } from '../ui/strings/history'
import '../styles/history.css'

const props = defineProps<{ source: MarkdownHistorySource; locale?: string; plugins?: readonly MarkdownPlugin[] }>()
const { entries, selectedId, selected, snapshot, loading, loadingSnapshot, error, cursor, load, retry } = useDocumentHistory(toRef(props, 'source'))
const track = ref<HTMLElement>(), width = ref(500), span = ref(DAY), center = ref(0), pending = ref(true)
const bounds = computed(() => ({ from: Date.parse(entries.value.at(-1)?.timestamp ?? '') || 0, to: Date.parse(entries.value[0]?.timestamp ?? '') || 0 }))
const maxSpan = computed(() => Math.max(MIN_HISTORY_SPAN, bounds.value.to - bounds.value.from))
const window = computed(() => historyWindow(center.value, span.value, bounds.value))
const marks = computed(() => historyMarks(entries.value, window.value, width.value))
const ticks = computed(() => historyTicks(window.value, width.value))
const selectedTime = computed(() => Date.parse(selected.value?.timestamp ?? '') || bounds.value.to)
const index = computed(() => entries.value.findIndex(entry => entry.id === selectedId.value))
const zoomValue = computed(() => historyZoomValue(window.value.to - window.value.from, maxSpan.value))
const visibleValue = computed(() => pending.value && snapshot.value?.working ? snapshot.value.working : snapshot.value?.approved)
const locale = computed(() => props.locale ?? markdownStrings.locale)
const fullDate = computed(() => new Intl.DateTimeFormat(locale.value, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }))
const tickDate = computed(() => new Intl.DateTimeFormat(locale.value, window.value.to - window.value.from >= 2 * DAY ? { day: 'numeric', month: 'numeric' } : { hour: '2-digit', minute: '2-digit' }))
function x(time: number) { return Math.max(0, Math.min(width.value, (time - window.value.from) / (window.value.to - window.value.from) * width.value)) }
function choose(time: number) { const entry = historyEntryAt(entries.value, time); if (entry) selectedId.value = entry.id }
async function step(direction: number) {
  const previous = selectedId.value, target = index.value + direction
  if (!entries.value[target] && direction > 0 && cursor.value) await load(true)
  if (selectedId.value === previous && entries.value[target]) selectedId.value = entries.value[target]!.id
}
function zoom(size: number, anchor = selectedTime.value) {
  const fraction = Math.max(0, Math.min(1, (anchor - window.value.from) / (window.value.to - window.value.from)))
  span.value = Math.max(MIN_HISTORY_SPAN, Math.min(maxSpan.value, size))
  center.value = anchor + (0.5 - fraction) * span.value
}
function wheel(event: WheelEvent) {
  if (!entries.value.length || !track.value) return
  event.preventDefault()
  const rect = track.value.getBoundingClientRect()
  const anchor = window.value.from + Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width)) * (window.value.to - window.value.from)
  if (event.shiftKey) center.value += Math.sign(event.deltaY || event.deltaX) * span.value * .15
  else zoom((window.value.to - window.value.from) * (event.deltaY > 0 ? 1.25 : .8), anchor)
}
watch(selectedTime, time => { if (time < window.value.from || time > window.value.to || !center.value) center.value = time })
let observer: ResizeObserver | undefined
onMounted(() => { if (track.value) { observer = new ResizeObserver(([entry]) => { if (entry) width.value = entry.contentRect.width }); observer.observe(track.value) } })
onBeforeUnmount(() => observer?.disconnect())
</script>
<template>
  <section class="md-document-history" :aria-label="historyStrings.documentHistoryAriaLabel">
    <div class="md-history-controls">
      <div class="md-history-navigation">
        <button type="button" :aria-label="historyStrings.previousChange" :disabled="index < 0 || (index === entries.length - 1 && !cursor)" @click="step(1)"><MarkdownIcon name="chevron-left" :size="16" /></button>
        <time v-if="selected" :datetime="selected.timestamp">{{ fullDate.format(selectedTime) }}</time><span v-else>{{ historyStrings.historyFallbackLabel }}</span>
        <button type="button" :aria-label="historyStrings.nextChange" :disabled="index <= 0" @click="step(-1)"><MarkdownIcon name="chevron-right" :size="16" /></button>
      </div>
      <div class="md-history-zoom">
        <button type="button" :aria-label="historyStrings.zoomOutAriaLabel" :disabled="!entries.length" @click="zoom((window.to - window.from) * 2)"><MarkdownIcon name="minus" :size="16" /></button>
        <input type="range" :aria-label="historyStrings.zoomSliderAriaLabel" min="0" max="100" step="1" :value="zoomValue" :disabled="entries.length < 2" @input="zoom(historyZoomSpan(Number(($event.target as HTMLInputElement).value), maxSpan))">
        <button type="button" :aria-label="historyStrings.zoomInAriaLabel" :disabled="!entries.length" @click="zoom((window.to - window.from) / 2)"><MarkdownIcon name="plus" :size="16" /></button>
      </div>
    </div>
    <div ref="track" class="md-history-track" @wheel="wheel">
      <svg :viewBox="`0 0 ${width} 56`" aria-hidden="true">
        <rect y="24" :width="width" height="22" rx="4" class="md-history-track-fill" />
        <g v-for="tick in ticks" :key="tick"><line :x1="x(tick)" :x2="x(tick)" y1="19" y2="47" class="md-history-grid" /><text :x="Math.max(3, Math.min(width - 3, x(tick)))" y="12" :text-anchor="x(tick) < 25 ? 'start' : x(tick) > width - 25 ? 'end' : 'middle'">{{ tickDate.format(tick) }}</text></g>
        <g v-for="mark in marks" :key="mark.x" :data-kind="mark.kind" class="md-history-mark"><path v-if="mark.kind === 'approve'" :d="`M${Math.max(4, Math.min(width - 4, mark.x))} 27 l4 6 -4 6 -4 -6 Z`" /><rect v-else :x="Math.min(width - 3, mark.x)" y="28" width="3" :height="Math.min(15, 6 + mark.count * 2)" rx="1" /></g>
        <g v-if="selected" class="md-history-playhead"><line :x1="x(selectedTime)" :x2="x(selectedTime)" y1="17" y2="51" /><circle :cx="x(selectedTime)" cy="51" r="2.5" /></g>
      </svg>
      <input type="range" class="md-history-scrub" :aria-label="historyStrings.scrubAriaLabel" :aria-valuetext="selected ? fullDate.format(selectedTime) : historyStrings.noHistoryValueText" :min="window.from" :max="window.to" step="1" :value="selectedTime" :disabled="!entries.length" @input="choose(Number(($event.target as HTMLInputElement).value))">
    </div>
    <div class="md-history-context">
      <span>{{ selected ? `${selected.author} · ${selected.label}` : loading ? historyStrings.loadingHistory : historyStrings.noHistoryYet }}</span>
      <label><input v-model="pending" type="checkbox">{{ historyStrings.unapprovedChangesLabel }}</label>
    </div>
    <div v-if="error" class="md-history-error" role="alert">{{ error }} <button type="button" @click="retry">{{ markdownStrings.retry }}</button></div>
    <div v-if="cursor" class="md-history-older"><button type="button" :disabled="loading" @click="load(true)">{{ loading ? markdownStrings.loading : historyStrings.loadOlderPeriod }}</button></div>
    <div class="md-history-preview" :aria-busy="loadingSnapshot">
      <p v-if="loadingSnapshot" class="md-history-loading" role="status">{{ historyStrings.loadingSelectedVersion }}</p>
      <template v-else-if="visibleValue">
        <p class="md-history-caption">{{ pending && snapshot?.working ? snapshot.workingLabel || historyStrings.workingVersionFallbackLabel : historyStrings.publishedVersionFallbackLabel }} · {{ historyStrings.readOnlyCaption }}</p>
        <!-- @vue-generic {'rich'} -->
        <MarkdownRenderer :value="visibleValue" :plugins="plugins" profile="rich" :ariaLabel="historyStrings.historicalVersionAriaLabel" />
      </template>
    </div>
  </section>
</template>
