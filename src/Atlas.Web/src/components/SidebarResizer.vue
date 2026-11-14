<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue';
import { Tooltip, TooltipContent, TooltipTrigger } from './ui/tooltip';
import t from '../i18n/components-c';
const props = defineProps<{ modelValue: number; max: number }>();
const emit = defineEmits<{ 'update:modelValue': [value: number]; commit: [] }>();
const active = ref(false);
let startX = 0, startWidth = 272, pointerId: number | undefined;
// The handle keeps the pointer for the length of the drag. Past the narrowest and the widest rail the pointer leaves
// the 9 px handle: without the capture it turned into the cursor of whatever lay under it (an arrow over the article,
// a hand over a link of the rail), and that element answered as if it were pointed at. A pointer that cannot be
// captured (a synthetic one) is followed through the window as before.
let holder: HTMLElement | undefined;
function hold(handle: HTMLElement, id: number) {
  try { handle.setPointerCapture?.(id); holder = handle; } catch { holder = undefined; }
}
function release() {
  try { if (pointerId !== undefined) holder?.releasePointerCapture?.(pointerId); } catch { /* The browser has already let go of it. */ }
  holder = undefined;
}
const clamp = (value: number) => Math.round(Math.max(220, Math.min(props.max, value)));
function change(value: number, commit = true) {
  emit('update:modelValue', clamp(value));
  if (commit) emit('commit');
}
function stop() {
  release();
  active.value = false; pointerId = undefined;
  window.removeEventListener('pointermove', move);
  window.removeEventListener('pointerup', finish);
  window.removeEventListener('pointercancel', cancel);
  window.removeEventListener('blur', cancel);
}
function start(event: PointerEvent) {
  if (event.button !== 0 || active.value) return;
  event.preventDefault(); (event.currentTarget as HTMLElement).focus();
  startX = event.clientX; startWidth = props.modelValue; pointerId = event.pointerId; active.value = true;
  hold(event.currentTarget as HTMLElement, event.pointerId);
  window.addEventListener('pointermove', move);
  window.addEventListener('pointerup', finish);
  window.addEventListener('pointercancel', cancel);
  window.addEventListener('blur', cancel);
}
function move(event: PointerEvent) {
  if (event.pointerId === pointerId) change(startWidth + event.clientX - startX, false);
}
function finish(event: PointerEvent) {
  if (event.pointerId !== pointerId) return;
  move(event); stop(); emit('commit');
}
function cancel(event?: Event) {
  if (event?.type === 'pointercancel' && (event as PointerEvent).pointerId !== pointerId) return;
  if (active.value) { change(startWidth, false); stop(); }
}
// A window resize can hide the handle or change its bounds while a pointer is down.
watch(() => props.max, () => { if (active.value) cancel(); });
function keys(event: KeyboardEvent) {
  if (event.isComposing || event.ctrlKey || event.metaKey || event.altKey) return;
  if (event.key === 'Escape' && active.value) { event.preventDefault(); event.stopPropagation(); cancel(); return; }
  const target = event.key === 'Home' ? 220 : event.key === 'End' ? props.max
    : event.key === 'ArrowLeft' ? props.modelValue - 16 : event.key === 'ArrowRight' ? props.modelValue + 16 : undefined;
  if (target !== undefined) { event.preventDefault(); change(target); }
}
onBeforeUnmount(stop);
</script>

<template>
  <!-- The separator is focusable, so unlike a native `title` the hint now also
       reaches keyboard users -- who are the ones the arrow-key part is written for. -->
  <Tooltip><TooltipTrigger as-child><div class="sidebar-resizer" :class="{ 'is-dragging': active }" role="separator" tabindex="0"
    :aria-label="t.sidebarResizer.label" aria-orientation="vertical" aria-controls="wiki-navigation"
    :aria-valuenow="modelValue" :aria-valuemin="220" :aria-valuemax="max" :aria-valuetext="t.sidebarResizer.pixels(modelValue)"
    @pointerdown="start" @keydown="keys" @dblclick="change(272)"><span aria-hidden="true" /></div></TooltipTrigger><TooltipContent>{{ t.sidebarResizer.hint }}</TooltipContent></Tooltip>
</template>

<style scoped>
.sidebar-resizer { position: absolute; inset: 0 -4px 0 auto; width: 9px; z-index: 7; cursor: col-resize; touch-action: none; user-select: none; }
.sidebar-resizer::before { content: ''; position: absolute; inset: 0 3px; border-radius: 4px; background: transparent; transition: background var(--atlas-motion-fast) var(--atlas-ease-out); }
.sidebar-resizer:focus-visible::before,
.sidebar-resizer.is-dragging::before { background: var(--accent); }
@media (hover: hover) and (pointer: fine) { .sidebar-resizer:hover::before { background: var(--accent); } }
.sidebar-resizer > span { position: absolute; top: 45%; left: 1px; width: 7px; height: 32px; border: 1px solid var(--line); border-radius: 5px; background: var(--surface); opacity: 0; transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out); }
.sidebar-resizer:focus-visible > span,
.sidebar-resizer.is-dragging > span { opacity: 1; }
@media (hover: hover) and (pointer: fine) { .sidebar-resizer:hover > span { opacity: 1; } }
@media (max-width: 1024px) { .sidebar-resizer { display: none; } }
</style>
