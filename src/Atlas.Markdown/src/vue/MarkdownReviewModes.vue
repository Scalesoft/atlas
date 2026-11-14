<script setup lang="ts">
import { computed, ref } from 'vue'
import { reviewStrings } from '../ui/strings/review'
const props = defineProps<{ modelValue: 'document' | 'changes' | 'history'; panelId: string; labels?: Partial<Record<'document' | 'changes' | 'history', string>>; ariaLabel?: string; alwaysExpanded?: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: 'document' | 'changes' | 'history'] }>()
const expanded = ref(false)
const modes = [{ id: 'document', label: reviewStrings.modeDocument }, { id: 'changes', label: reviewStrings.modeChanges }, { id: 'history', label: reviewStrings.modeHistory }] as const
const index = computed(() => modes.findIndex(mode => mode.id === props.modelValue))
function navigate(event: KeyboardEvent) {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
  event.preventDefault()
  const next = event.key === 'Home' ? 0 : event.key === 'End' ? 2 : (index.value + (event.key === 'ArrowRight' ? 1 : 2)) % 3
  emit('update:modelValue', modes[next]!.id)
  ;(event.currentTarget as HTMLElement).querySelectorAll<HTMLButtonElement>('[role=tab]')[next]?.focus()
}
</script>
<template>
  <div class="md-review-modes" :class="{ expanded: alwaysExpanded || expanded || modelValue !== 'document' }" @mouseenter="expanded = true" @mouseleave="expanded = false" @focusout="!($event.currentTarget as HTMLElement).contains($event.relatedTarget as Node) && (expanded = false)" @keydown.esc="expanded = false">
    <button class="md-review-dots" type="button" :aria-label="ariaLabel ?? reviewStrings.viewAriaLabel" :aria-expanded="alwaysExpanded || expanded || modelValue !== 'document'" @focus="expanded = true" @click="expanded = true">•••</button>
    <div class="md-review-tabs" role="tablist" :aria-label="ariaLabel ?? reviewStrings.viewAriaLabel" :style="{ '--mode-index': index }" @keydown="navigate">
      <span class="md-review-thumb" aria-hidden="true" />
      <button v-for="mode in modes" :key="mode.id" :id="`${panelId}-${mode.id}`" type="button" role="tab" :aria-selected="modelValue === mode.id" :aria-controls="panelId" :tabindex="modelValue === mode.id ? 0 : -1" @click="emit('update:modelValue', mode.id)">{{ labels?.[mode.id] ?? mode.label }}</button>
    </div>
  </div>
</template>
<style scoped>
.md-review-modes { position:absolute; z-index:5; top:0; left:50%; transform:translate(-50%,-50%); width:44px; height:14px; border-radius:22px; border:1px solid color-mix(in srgb,var(--review-mode) 40%,var(--review-paper)); background:linear-gradient(105deg,#c6e8d9,#c9e5ec 52%,#f1d8bf); }
.md-review-dots { position:absolute; inset:-9px 0; display:grid; place-items:center; width:100%; border:0; background:transparent; letter-spacing:4px; padding:0 0 0 4px; color:var(--review-mode); font-size:11px; cursor:pointer; }
.md-review-tabs { position:absolute; inset:0; display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); padding:3px; visibility:hidden; opacity:0; }
.md-review-tabs button { position:relative; z-index:1; border:0; border-radius:18px; font:inherit; font-size:11px; color:#355861; background:transparent; cursor:pointer; }
.md-review-tabs button[aria-selected=true] { color:var(--review-mode); }
.md-review-thumb { position:absolute; top:3px; bottom:3px; left:3px; width:calc((100% - 6px)/3); background:var(--review-paper); border-radius:18px; transform:translateX(calc(var(--mode-index)*100%)); box-shadow:0 1px 4px #24484c1c; }
.md-review-modes.expanded,.md-review-modes:focus-within { width:250px; max-width:calc(100% - 36px); height:32px; }
.expanded .md-review-tabs,:focus-within > .md-review-tabs { visibility:visible; opacity:1; }
.expanded .md-review-dots,:focus-within > .md-review-dots { opacity:0; pointer-events:none; }
button:focus-visible { outline:2px solid var(--review-mode); outline-offset:2px; }
/* Reduced motion keeps only the opacity and colour of these transitions (styles/motion.css): the pill and the thumb arrive without an in-between frame. */
.md-review-modes { transition:width var(--md-motion-slow) var(--md-motion-ease),height var(--md-motion-slow) var(--md-motion-ease),border-color var(--md-motion-slow) var(--md-motion-ease); }.md-review-tabs { transition:opacity var(--md-motion-fast) var(--md-motion-ease) calc(var(--md-motion-fast) * .6); }.md-review-thumb { transition:transform var(--md-motion-slow) var(--md-motion-ease); }
@media (pointer:coarse) { .md-review-dots { inset:-16px -4px; width:calc(100% + 8px); }.md-review-tabs button::after { content:''; position:absolute; inset:-9px 0; } }
</style>
