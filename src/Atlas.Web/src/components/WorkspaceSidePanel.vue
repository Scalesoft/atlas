<script setup lang="ts">
import { computed, getCurrentInstance, nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue';
import AtlasIcon from './AtlasIcon.vue';
import CommandIcon from './CommandIcon.vue';
import { readStored, writeStored } from '../lib/browserStorage';
import { animateAtlas, motionMs, prefersReducedMotion } from '../motion/aiMotion';
import t from "../i18n/components-c";

/**
 * Non-modal right column of the workspace (Atlas 2.0, D6). The page beside it stays readable,
 * selectable and clickable, which is the whole point against `AppDialog size="side"`.
 * When the workspace cannot keep 600 px for the page, the panel floats over its right edge.
 * Below 760 px it covers the screen (one task at a time).
 * It is generic: the content (the assistant, the page tools) comes through slots.
 *
 * Opening and closing change the layout once and move the page's blocks to their new place with a transform
 * (2.6): a long article is laid out a single time instead of on every frame of an animated width. The panel always
 * stands at the right edge of the window, however wide it is.
 *
 * With `tabs` (refresh 2.5, board 03) the header becomes the dock's tab row (Obsah · Asistent · Historie · Přístup);
 * the host decides what each tab shows and receives the choice through `update:activeTab`. The `header` slot then
 * sits under the tabs (the assistant's own title and menu). The active tab's pill slides to the new tab and the
 * content only fades, without moving.
 */
/** `icon` is a CommandIcon name: `ai` draws the AI glyph (aiGlyphBoundary.guard.test.ts), anything else an Atlas icon. */
export interface WorkspacePanelTab { id: string; label: string; icon?: string }
const props = withDefaults(defineProps<{
  open: boolean;
  /** Accessible name of the complementary landmark. */
  label: string;
  /** localStorage key of the width (`atlas:reading:<userId>:panel-width`); omitted = not remembered. */
  storageKey?: string;
  closeLabel?: string;
  /** Tabs of the dock; without them the header shows the label (or the `header` slot). */
  tabs?: WorkspacePanelTab[];
  activeTab?: string;
  /** Accessible name of the tab row. */
  tabsLabel?: string;
  /** Width range of the column; the page dock is narrower than the assistant alone. */
  minWidth?: number;
  defaultWidth?: number;
  /**
   * Opening moves focus into the panel; an opening the reader did not ask for (a remembered dock) keeps it, and a
   * panel that is mounted that way arrives with its page: it fades in while the page is where it will stay.
   */
  autoFocus?: boolean;
}>(), { closeLabel: () => t.workspacePanel.close, storageKey: undefined, tabs: undefined, activeTab: undefined, tabsLabel: () => t.workspacePanel.tools, minWidth: 360, defaultWidth: 400, autoFocus: true });
const emit = defineEmits<{ 'update:open': [value: boolean]; close: []; 'update:activeTab': [id: string] }>();
defineSlots<{ default(): unknown; header(): unknown; footer(): unknown }>();

const MAX = 480, OVERLAY_BELOW = 760, PAGE_MIN = 600;
const min = computed(() => Math.min(props.minWidth, MAX));
const fallback = computed(() => Math.max(min.value, Math.min(MAX, props.defaultWidth)));
const clamp = (value: number) => Math.round(Math.max(min.value, Math.min(MAX, value)));
function stored(): number {
  if (!props.storageKey) return fallback.value;
  const raw = readStored(props.storageKey);
  const value = raw === null ? NaN : Number(raw);
  return Number.isFinite(value) && value > 0 ? clamp(value) : fallback.value;
}
function persist() {
  if (!props.storageKey) return;
  writeStored(props.storageKey, String(width.value));
}

const width = ref(stored());
const viewport = ref(typeof window === 'undefined' ? 1280 : window.innerWidth);
const available = ref(viewport.value);
const overlay = computed(() => viewport.value < OVERLAY_BELOW || available.value < PAGE_MIN + min.value);
// A temporary constraint must never overwrite the user's preferred width.
// The remembered width is clamped when it is read or dragged; a minimum that grows afterwards (a page with one more
// tab) still holds, and the reader's own width comes back with the smaller minimum.
const wanted = computed(() => Math.max(min.value, width.value));
const effectiveWidth = computed(() => overlay.value ? Math.min(wanted.value, viewport.value) : Math.min(wanted.value, available.value - PAGE_MIN));
const rendered = ref(props.open);
const host = ref<HTMLElement>();
const panel = ref<HTMLElement>();
const body = ref<HTMLElement>();
const tabList = ref<HTMLElement>();
const uid = useId();
const titleId = `workspace-panel-${uid}`;
const bodyId = `workspace-panel-body-${uid}`;
const tabId = (id: string) => `workspace-panel-tab-${uid}-${id}`;
const selectedTab = computed(() => props.tabs?.find(tab => tab.id === props.activeTab));
let opener: HTMLElement | null = null;
/** The surface's slide and fade. */
let running: Animation | null = null;
/** The page's blocks on their way to where the changed layout puts them. */
let pageMotion: Animation[] = [];
let tabMotion: Animation | null = null;
let restoringDrag = false;
let containerObserver: ResizeObserver | undefined;
/** The workspace row, kept while the panel is not rendered: an opening measures the page in it first. */
let row: HTMLElement | null = null;
/**
 * Tells whether the view beside the panel was replaced in the very update that opens or closes the panel (the reader
 * went to another view): the records of that replacement are still waiting when the watcher of `open` runs. Then the
 * panel comes or goes with its page, nothing travels, and the page is not measured in the middle of its own update.
 */
let pageSwap: MutationObserver | undefined;
function watchPage() {
  if (!row || typeof MutationObserver !== 'function') return;
  pageSwap ??= new MutationObserver(watchPage);
  pageSwap.disconnect();
  pageSwap.observe(row, { childList: true });
  for (const el of row.children) if (el !== host.value) pageSwap.observe(el, { childList: true });
}
function pageSwapped() {
  const swapped = !!pageSwap?.takeRecords().length;
  if (swapped) watchPage();
  return swapped;
}
watch(host, el => {
  containerObserver?.disconnect();
  const parent = el?.parentElement;
  if (!parent) return;
  row = parent;
  let measured = -1;
  const measure = (size = parent.clientWidth) => {
    if (size > 0) available.value = size;
    // The tabs' indicator moves only with the room; read on every resize of the row it forced a layout of the page as editing
    // started (perf 3.5).
    if (size !== measured) { measured = size; placeIndicator(); }
  };
  measure();
  // The observer reports the size it saw, without reading the layout again.
  containerObserver = new ResizeObserver(entries => measure(Math.round(entries.at(-1)?.borderBoxSize?.[0]?.inlineSize ?? parent.clientWidth)));
  containerObserver.observe(parent);
}, { flush: 'post' });

watch(() => props.storageKey, () => { width.value = stored(); });
const onResize = () => { viewport.value = window.innerWidth; available.value = host.value?.parentElement?.clientWidth || viewport.value; placeIndicator(); };
const instance = getCurrentInstance();
onMounted(() => {
  window.addEventListener('resize', onResize);
  // Closed, the component is only a placeholder in the row: that is enough to find the page beside it later.
  row ??= (instance?.proxy?.$el as Node | null | undefined)?.parentElement ?? null;
  watchPage();
  if (props.open) {
    // The room decides between beside and floating before the first frame, not after it.
    const room = host.value?.parentElement?.clientWidth;
    if (room) available.value = room;
    rememberOpener();
    // Asked for by the reader: the page travels from where it was. A panel that comes with its page is simply there.
    enter(undefined, props.autoFocus ? placesWithoutPanel() : undefined);
    if (props.autoFocus) focusPanel();
  }
  void nextTick(placeIndicator);
});
onBeforeUnmount(() => { window.removeEventListener('resize', onResize); containerObserver?.disconnect(); pageSwap?.disconnect(); stopDrag(); cancelMotion(); tabMotion?.cancel(); });

function focusInside() { return !!panel.value && panel.value.contains(document.activeElement); }
function restoreFocus() {
  const target = opener; opener = null;
  if (target?.isConnected && !target.closest('[inert], [hidden]') && !target.matches(':disabled')
    && getComputedStyle(target).visibility !== 'hidden' && getComputedStyle(target).display !== 'none') {
    target.focus({ preventScroll: true });
  } else {
    // A breakpoint or navigation can hide the original trigger while the panel stays open.
    host.value?.closest<HTMLElement>('main[tabindex]')?.focus({ preventScroll: true });
  }
}

function cancelPageMotion() { for (const animation of pageMotion) animation.cancel(); pageMotion = []; }
function cancelMotion() { running?.cancel(); running = null; cancelPageMotion(); }

/**
 * Where the boxes of the page beside the panel are on screen now, running motion included (a reversal continues
 * from there). Outer boxes first, a few levels deep and never into long content (a document's text, a list):
 * which box is the one that moves is only known once the layout has changed.
 */
type PagePlaces = Map<HTMLElement, { left: number; width: number; hollow: boolean }>;
const PAGE_DEPTH = 12, PAGE_FANOUT = 32, PAGE_BOXES = 400;
function pagePlaces(): PagePlaces {
  const places: PagePlaces = new Map();
  const parent = host.value?.parentElement ?? row;
  if (!parent?.isConnected) return places;
  let level = [...parent.children].filter(el => el !== host.value);
  for (let depth = 0; depth < PAGE_DEPTH && level.length && places.size < PAGE_BOXES; depth += 1) {
    const next: Element[] = [];
    for (const el of level) {
      if (!(el instanceof HTMLElement)) continue;
      const rect = el.getBoundingClientRect();
      // `display: contents` has no box of its own, but its children do.
      places.set(el, { left: rect.left, width: rect.width, hollow: !rect.width && !rect.height });
      if (el.childElementCount <= PAGE_FANOUT) next.push(...el.children);
    }
    level = next;
  }
  return places;
}
/** The places the page would have without the panel's column: where a first opening starts from. */
function placesWithoutPanel(): PagePlaces | undefined {
  const el = host.value;
  if (!el || overlay.value) return undefined;
  el.style.marginLeft = `${-effectiveWidth.value}px`;
  const places = pagePlaces();
  el.style.marginLeft = '';
  return places;
}
/**
 * The layout has changed once; what moved travels from where it was to where it now belongs (FLIP). Only the
 * compositor works during the motion, so a long article does not reflow on every frame. A box whose left edge
 * moved travels as a whole (a centred column); a box that only became narrower stays and its content is looked
 * at instead (a full-width header with a centred title). `translate` leaves the boxes' own `transform` (the
 * article's arrival) alone. Reduced motion: the page is in place at once.
 */
function movePage(from: PagePlaces | undefined, ms: number) {
  cancelPageMotion();
  if (!from?.size || !host.value || prefersReducedMotion(host.value)) return;
  const moves: [HTMLElement, number][] = [];
  const settled = new Set<Element>(); // travels as a whole, or did not change: nothing inside needs a look
  for (const [el, was] of from) {
    if (!el.isConnected) continue;
    if (el.parentElement && settled.has(el.parentElement)) { settled.add(el); continue; }
    if (was.hollow) continue;
    const now = el.getBoundingClientRect();
    const dx = was.left - now.left;
    if (Math.abs(dx) >= 1) { moves.push([el, dx]); settled.add(el); }
    else if (Math.abs(was.width - now.width) < 1) settled.add(el);
  }
  for (const [el, dx] of moves) {
    const animation = animateAtlas(el, [{ translate: `${dx}px 0px` }, { translate: '0px 0px' }], { duration: ms });
    if (animation) pageMotion.push(animation);
  }
}
function rememberOpener() {
  const active = document.activeElement;
  if (!focusInside()) opener = active instanceof HTMLElement && active !== document.body ? active : null;
}
/** An owner that switches the tab of the open panel into focus (Alt C into the dock's Diskuse) marks where closing returns, as opening does. */
defineExpose({ rememberOpener });
function focusPanel() {
  if (document.querySelector("dialog[open]")) return; // The modal owner restores focus after its exit.
  const target = panel.value?.querySelector<HTMLElement>('[data-autofocus]')
    ?? (props.tabs ? panel.value?.querySelector<HTMLElement>('[role=tab][aria-selected=true]') : null)
    ?? panel.value;
  target?.focus({ preventScroll: true });
}
watch(() => props.open, async (open) => {
  // Capture the in-flight frame before cancelling: a rapid reversal continues here. Only a running slide has one; at
  // rest the surface is where its styles put it, and asking for them would style the whole page in mid-update.
  const from = running && rendered.value && host.value && panel.value ? {
    opacity: getComputedStyle(panel.value).opacity,
    transform: getComputedStyle(panel.value).transform,
  } : undefined;
  // Measured only when the page will travel: a panel that comes or goes with its page (the view changed in this same
  // update) has nothing to move, and measuring there styled and laid out the new view in the middle of its update.
  const withPage = pageSwapped();
  const places = (open ? props.autoFocus || !!from : !withPage) ? pagePlaces() : undefined;
  cancelMotion();
  if (open) {
    rememberOpener();
    rendered.value = true;
    await nextTick();
    if (!props.open || !panel.value) return;
    panel.value.removeAttribute('inert');
    // A panel the owner opens by itself comes with its page (the article of a new page is already laid out here,
    // but was never shown without the panel): nothing travels. A reversed close always continues.
    enter(from, props.autoFocus || from ? places : undefined);
    placeIndicator();
    if (props.autoFocus) focusPanel();
  } else {
    cancelDrag();
    tabMotion?.cancel(); tabMotion = null;
    if (focusInside()) restoreFocus(); else opener = null;
    leave(from, places);
  }
});

// The panel slides out of the window's right edge (its own box clips it there) and fades in, while the article narrows with it.
const slide = [{ opacity: 0, transform: 'translateX(100%)' }, { opacity: 1, transform: 'none' }];

/**
 * The column takes its room at once and the page moves into the narrower layout over --atlas-motion-slow
 * (movePage) while the panel slides in. Reduced motion keeps only the fade (animateAtlas drops the transform).
 */
type PanelFrame = { opacity: string; transform: string };
function enter(from?: PanelFrame, places?: PagePlaces) {
  const el = host.value, inner = panel.value;
  if (!el || !inner) return;
  el.style.marginLeft = ''; // back in the layout after a reversed close
  const ms = motionMs(el, '--atlas-motion-slow');
  movePage(places, ms);
  running = animateAtlas(inner, from ? [{ opacity: from.opacity, transform: from.transform }, slide[1]!] : slide, { duration: ms });
}
function leave(from?: PanelFrame, places?: PagePlaces) {
  const el = host.value, inner = panel.value;
  const done = () => { if (!props.open) rendered.value = false; };
  if (!el || !inner) { done(); return; }
  inner.setAttribute('inert', ''); // leaving: nothing inside is reachable any more
  const ms = motionMs(el, '--atlas-motion-base');
  // The column gives its room back at once (it stays where it was, over the page's edge, until it has faded).
  if (!overlay.value) el.style.marginLeft = `${-effectiveWidth.value}px`;
  movePage(places, ms);
  const animation = animateAtlas(inner, [{ opacity: from?.opacity ?? 1, transform: from?.transform ?? 'none' }, { opacity: 0, transform: 'translateX(100%)' }], { duration: ms, fill: 'forwards' });
  if (!animation) { done(); return; }
  running = animation;
  animation.finished.then(() => { if (running === animation) { running = null; done(); } }, () => { /* cancelled by a reopen */ });
}

// Resizing may change the containing block while an opening/closing animation runs.
// Release obsolete pixel offsets, but keep the entrance fade rather than flashing fully visible.
watch([overlay, effectiveWidth], () => {
  // Restoring a cancelled drag is part of the pending close/reversal, not a viewport resize.
  if (restoringDrag) return;
  if (overlay.value) cancelDrag();
  cancelPageMotion();
  if (!props.open) { cancelMotion(); rendered.value = false; }
  void nextTick(placeIndicator);
}, { flush: 'sync' });

function close() {
  emit('update:open', false);
  emit('close');
}
function keydown(event: KeyboardEvent) {
  if (event.key !== 'Escape' || event.defaultPrevented || event.isComposing) return;
  event.preventDefault(); event.stopPropagation();
  close();
}

// Tabs: automatic activation, arrows/Home/End (WAI-ARIA tabs); the pill measures the active tab.
const indicator = ref<{ left: number; width: number } | null>(null);
function placeIndicator() {
  const list = tabList.value;
  const active = list?.querySelector<HTMLElement>('[role=tab][aria-selected=true]');
  indicator.value = active ? { left: active.offsetLeft, width: active.offsetWidth } : null;
}
function select(id: string) { if (id !== props.activeTab) emit('update:activeTab', id); }
function tabKeys(event: KeyboardEvent) {
  const tabs = props.tabs ?? [];
  if (!tabs.length || event.isComposing || event.altKey || event.ctrlKey || event.metaKey) return;
  const index = Math.max(0, tabs.findIndex(tab => tab.id === props.activeTab));
  const next = event.key === 'ArrowRight' ? (index + 1) % tabs.length : event.key === 'ArrowLeft' ? (index - 1 + tabs.length) % tabs.length
    : event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : -1;
  if (next < 0) return;
  event.preventDefault();
  select(tabs[next]!.id);
  void nextTick(() => [...(tabList.value?.querySelectorAll<HTMLElement>('[role=tab]') ?? [])].find(tab => tab.dataset.tab === tabs[next]!.id)?.focus());
}
watch(() => [props.activeTab, props.tabs?.map(tab => tab.id).join()], (current, previous) => {
  void nextTick(placeIndicator);
  // Only the content fades on a tab change, it never slides (board 16).
  if (previous && current[0] !== previous[0] && body.value) {
    const opacity = tabMotion?.playState === 'running' ? getComputedStyle(body.value).opacity : '0';
    tabMotion?.cancel();
    tabMotion = animateAtlas(body.value, [{ opacity }, { opacity: 1 }], { duration: motionMs(body.value, '--atlas-motion-base') });
  }
}, { flush: 'post' });
watch(width, () => { void nextTick(placeIndicator); });

// Width: the SidebarResizer drag and keyboard pattern, mirrored (the handle is the left edge).
let startX = 0, startWidth = 0, preferredBeforeDrag = 0, pointerId: number | undefined;
const dragging = ref(false);
function stopDrag() {
  dragging.value = false; pointerId = undefined;
  window.removeEventListener('pointermove', drag);
  window.removeEventListener('pointerup', endDrag);
  window.removeEventListener('pointercancel', cancelDrag);
  window.removeEventListener('blur', cancelDrag);
}
function startDrag(event: PointerEvent) {
  if (event.button !== 0 || dragging.value) return;
  event.preventDefault(); (event.currentTarget as HTMLElement).focus();
  startX = event.clientX; startWidth = effectiveWidth.value; preferredBeforeDrag = width.value; pointerId = event.pointerId; dragging.value = true;
  window.addEventListener('pointermove', drag);
  window.addEventListener('pointerup', endDrag);
  window.addEventListener('pointercancel', cancelDrag);
  window.addEventListener('blur', cancelDrag);
}
function drag(event: PointerEvent) { if (event.pointerId === pointerId) width.value = clamp(Math.min(available.value - PAGE_MIN, startWidth + startX - event.clientX)); }
function endDrag(event: PointerEvent) { if (event.pointerId !== pointerId) return; drag(event); stopDrag(); persist(); }
function cancelDrag(event?: Event) {
  if (event?.type === 'pointercancel' && (event as PointerEvent).pointerId !== pointerId) return;
  if (dragging.value) {
    const preferred = preferredBeforeDrag; stopDrag();
    restoringDrag = true;
    try { width.value = preferred; } finally { restoringDrag = false; }
  }
}
function resizeKeys(event: KeyboardEvent) {
  if (event.isComposing || event.ctrlKey || event.metaKey || event.altKey) return;
  if (event.key === 'Escape' && dragging.value) { event.preventDefault(); event.stopPropagation(); cancelDrag(); return; }
  const target = event.key === 'Home' ? min.value : event.key === 'End' ? Math.min(MAX, available.value - PAGE_MIN)
    : event.key === 'ArrowLeft' ? Math.min(available.value - PAGE_MIN, effectiveWidth.value + 16) : event.key === 'ArrowRight' ? effectiveWidth.value - 16 : undefined;
  if (target !== undefined) { event.preventDefault(); event.stopPropagation(); width.value = clamp(target); persist(); }
}
function resetWidth() { width.value = fallback.value; persist(); }
</script>

<template>
  <aside v-if="rendered" ref="host" class="workspace-panel" :class="{ 'is-overlay': overlay, 'has-tabs': !!tabs }" :data-mode="overlay ? 'overlay' : 'beside'"
    :style="{ width: `${effectiveWidth}px` }">
    <div ref="panel" class="workspace-panel-surface" role="complementary" :aria-label="label" tabindex="-1"
      :style="overlay ? undefined : { width: `${effectiveWidth}px` }" @keydown="keydown">
      <div v-if="!overlay" class="workspace-panel-resizer" :class="{ 'is-dragging': dragging }" role="separator" tabindex="0"
        :aria-label="t.workspacePanel.width(label)" aria-orientation="vertical" :aria-valuenow="effectiveWidth" :aria-valuemin="min" :aria-valuemax="Math.min(MAX, available - PAGE_MIN)"
        :aria-valuetext="t.sidebarResizer.pixels(effectiveWidth)" :title="t.sidebarResizer.hint"
        @pointerdown="startDrag" @keydown="resizeKeys" @dblclick="resetWidth"><span aria-hidden="true" /></div>
      <header class="workspace-panel-header">
        <div v-if="tabs" ref="tabList" class="workspace-panel-tabs" role="tablist" :aria-label="tabsLabel" @keydown="tabKeys">
          <span v-if="indicator" class="workspace-panel-tab-pill" aria-hidden="true" :style="{ transform: `translateX(${indicator.left}px)`, width: `${indicator.width}px` }" />
          <button v-for="tab in tabs" :id="tabId(tab.id)" :key="tab.id" type="button" role="tab" class="workspace-panel-tab"
            :aria-selected="tab.id === activeTab" :aria-controls="bodyId" :tabindex="tab.id === activeTab ? 0 : -1" :data-tab="tab.id"
            @click="select(tab.id)"><CommandIcon v-if="tab.icon" :name="tab.icon" :size="14" /><span class="workspace-panel-tab-label" :data-label="tab.label">{{ tab.label }}</span></button>
        </div>
        <div v-else :id="titleId" class="workspace-panel-title"><slot name="header">{{ label }}</slot></div>
        <button type="button" class="workspace-panel-close" :aria-label="closeLabel" @click="close"><AtlasIcon name="close" :size="16" /></button>
      </header>
      <div v-if="tabs && $slots.header" class="workspace-panel-subheader"><slot name="header" /></div>
      <div :id="bodyId" ref="body" class="workspace-panel-body" :role="tabs ? 'tabpanel' : undefined"
        :aria-labelledby="tabs && selectedTab ? tabId(selectedTab.id) : undefined"><slot /></div>
      <footer v-if="$slots.footer" class="workspace-panel-footer"><slot name="footer" /></footer>
    </div>
  </aside>
</template>

<style scoped>
/* The host sets --workspace-panel-top when the panel sits under a sticky bar (Atlas: under the top bar). */
.workspace-panel { position: sticky; top: var(--workspace-panel-top, 0px); align-self: flex-start; flex: none; height: calc(100dvh - var(--workspace-panel-top, 0px)); overflow: hidden; }
.workspace-panel-surface { position: relative; display: flex; flex-direction: column; height: 100%; min-width: 0; border-left: 1px solid var(--line); background: var(--surface); color: var(--text); outline: none; }
.workspace-panel.has-tabs .workspace-panel-surface { border-left-color: color-mix(in srgb, var(--line) 70%, transparent); background: var(--dock-bg, color-mix(in srgb, var(--surface) 86%, var(--hover))); }
.workspace-panel.is-overlay { position: fixed; inset: var(--workspace-panel-top, 0px) 0 0 auto; z-index: 20; max-width: 100%; box-shadow: var(--shadow-modal); }
.workspace-panel.is-overlay .workspace-panel-surface { width: 100%; border-left: 0; }
@media (max-width: 759px) {
  .workspace-panel.is-overlay { inset: 0; width: 100% !important; height: 100dvh; }
}
.workspace-panel-header { display: flex; flex: none; align-items: center; gap: 8px; padding: 10px 12px; border-bottom: 1px solid var(--line); font-weight: 650; }
.workspace-panel.has-tabs .workspace-panel-header { min-height: 44px; padding: 0 8px; box-sizing: border-box; border-bottom-color: color-mix(in srgb, var(--line) 70%, transparent); font-weight: 400; }
.workspace-panel-title { flex: 1; min-width: 0; display: flex; align-items: center; gap: 8px; }
.workspace-panel-tabs { position: relative; flex: 1; min-width: 0; display: flex; align-items: center; gap: 0; overflow-x: auto; scrollbar-width: none; }
.workspace-panel-tab { position: relative; z-index: 1; display: inline-flex; flex: none; align-items: center; gap: 5px; height: 30px; padding: 0 8px; border: 0; border-radius: 8px; background: none; color: var(--muted); font: inherit; font-size: 13.5px; font-weight: 500; white-space: nowrap; cursor: pointer; transition: color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .workspace-panel-tab:hover { color: var(--text); } }
.workspace-panel-tab[aria-selected="true"] { color: var(--accent); font-weight: 650; }
/* The label keeps the room of its selected (bolder) form, so choosing a tab does not push its neighbours. */
.workspace-panel-tab-label { display: inline-flex; flex-direction: column; }
.workspace-panel-tab-label::after { content: attr(data-label); content: attr(data-label) / ""; height: 0; overflow: hidden; visibility: hidden; font-weight: 650; pointer-events: none; user-select: none; }
.workspace-panel-tab:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.workspace-panel-tab-pill { position: absolute; top: 50%; left: 0; height: 30px; margin-top: -15px; border-radius: 8px; background: var(--accent-soft); transition: transform var(--atlas-motion-base) var(--atlas-ease-inout), width var(--atlas-motion-base) var(--atlas-ease-inout); }
.workspace-panel-subheader { display: flex; flex: none; align-items: center; gap: 8px; min-height: 40px; padding: 4px 12px 4px 16px; border-bottom: 1px solid color-mix(in srgb, var(--line) 70%, transparent); font-weight: 650; }
.workspace-panel-close { display: grid; flex: none; place-items: center; width: var(--control-h, 36px); height: var(--control-h, 36px); border: 0; border-radius: 6px; background: transparent; color: var(--muted); cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .workspace-panel-close:hover { background: var(--accent-soft); color: var(--text); } }
.workspace-panel-close:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
.workspace-panel-body { flex: 1; min-height: 0; overflow: auto; }
.workspace-panel-footer { flex: none; border-top: 1px solid var(--line); padding: 10px 12px; }
/* The accent line is the handle's hover, drag and focus mark; a second outline around the 9 px strip would double it. */
.workspace-panel-resizer { position: absolute; inset: 0 auto 0 -4px; width: 9px; z-index: 2; outline: none; cursor: col-resize; touch-action: none; user-select: none; }
.workspace-panel-resizer:focus-visible { outline: none; }
.workspace-panel-resizer::before { content: ''; position: absolute; inset: 0 3px; border-radius: 4px; background: transparent; transition: background var(--atlas-motion-fast); }
.workspace-panel-resizer:focus-visible::before,
.workspace-panel-resizer.is-dragging::before { background: var(--accent); }
@media (hover: hover) and (pointer: fine) { .workspace-panel-resizer:hover::before { background: var(--accent); } }
/* Reduced motion: the pill moves at once; the content still fades through animateAtlas. */
@media (prefers-reduced-motion: reduce) { .workspace-panel-tab-pill { transition-property: opacity; } }
:global([data-atlas-reduced-motion]) .workspace-panel-tab-pill { transition-property: opacity; }
</style>
