<script setup lang="ts">
import t from "../i18n/components-b";
import { computed, getCurrentInstance, nextTick, onBeforeUnmount, ref, toRef, useId, watch } from "vue";
import { animateAtlas, motionMs, prefersReducedMotion, revealHeight, scrollBehavior } from "../motion/aiMotion";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { documentHeadings, goToHeading, sectionCount, usePageOutline, type PageOutlineHeading } from "../editor/pageOutline";
import AtlasIcon from "./AtlasIcon.vue";
import { atlasPageSectionDragType, pageSectionPayload } from "../boards/boardDrop";
import { listKeys } from "../keyboard/navigation";
import { type DraggedObject } from "../boards/boardDrop";
import objects from "../i18n/components-objects";
import RowActionsMenu from "./RowActionsMenu.vue";
import AddToTreeDialog from "./objects/AddToTreeDialog.vue";

/**
 * „Na této stránce“ (refresh 2.5): the headings of the open page as in-page navigation. The page dock shows the list
 * (`variant="list"`, board 03); without the dock, and on a phone, one quiet row above the article unfolds it in place
 * (`variant="disclosure"`, board 13). It replaced the editor's floating outline rail, so a page has one outline that
 * moves with the layout. The active heading follows scrolling; choosing one scrolls there and, while reading, focuses it.
 */
const props = withDefaults(defineProps<{
  /** The page's document section; the reading renderer or the live editor inside it is read as it changes. */
  root: HTMLElement | null | undefined;
  variant?: "list" | "disclosure";
  /** Below this many headings the disclosure is not worth a row. */
  minimum?: number;
  /**
   * The disclosure steps aside (the dock beside the article shows the outline). The row leaves and returns at once and
   * the page below travels into its place with a transform: nothing jumps and the article is not laid out on every frame.
   */
  collapsed?: boolean;
  /** The page: each heading with a stable id can then be dragged out as that section of the page (onto a board). */
  pageId?: string;
  /** The page's stored document (the dock's list): its headings stand in, inert, until the page's own are rendered (a page switch). */
  document?: MarkdownRichValue;
}>(), { variant: "list", minimum: 2, collapsed: false, document: undefined });
const emit = defineEmits<{ navigate: [heading: PageOutlineHeading] }>();

const { headings, active, choose: keepActive } = usePageOutline(toRef(props, "root"));
const listId = `page-outline-${useId()}`;
const expanded = ref(false);
const container = ref<HTMLElement>();
const list = ref<HTMLElement>();
// Between pages the rendered headings are none for a moment: the stored document's stand in at the same row height, so the dock below
// the list does not move up and back down (UX open blink, dock CLS).
const standIns = computed(() => props.variant === "list" && !headings.value.length && props.document ? documentHeadings(props.document.document) : []);
const standingIn = computed(() => standIns.value.length > 0);
const entries = computed<readonly { key: string; level: number; title: string }[]>(() => standingIn.value ? standIns.value : headings.value);
const shown = computed(() => !props.collapsed && (props.variant === "list" ? entries.value.length > 0 : headings.value.length >= props.minimum));
/** How many rows the list had when it last went: the same list coming back takes its place as it is, without unfolding. */
let leftWith = 0;
watch(() => entries.value.length, (count, before) => { if (!count && before) leftWith = before; });

const instance = getCurrentInstance();
let pageMotion: Animation[] = [];
/** Set while the row leaves because the dock took over (the watcher below), so only that leave is a fade. */
let steppingAside = false;
watch(() => props.collapsed, async () => {
  steppingAside = props.collapsed;
  // Before the row changes: where is everything that sits below it (its later siblings and its parent's)?
  const start = instance?.proxy?.$el as Node | null | undefined;
  const below: HTMLElement[] = [];
  for (const from of [start, start?.parentElement]) for (let next = (from as Element | null | undefined)?.nextElementSibling; next; next = next.nextElementSibling) if (next instanceof HTMLElement) below.push(next);
  const tops = below.map(element => element.getBoundingClientRect().top);
  for (const animation of pageMotion) animation.cancel();
  pageMotion = [];
  if (props.collapsed) expanded.value = false;
  await nextTick();
  steppingAside = false;
  if (!start || prefersReducedMotion(start.parentElement)) return;
  const moves = below.map((element, index) => [element, element.isConnected ? tops[index]! - element.getBoundingClientRect().top : 0] as const);
  for (const [element, dy] of moves) {
    if (Math.abs(dy) < 1) continue;
    const animation = animateAtlas(element, [{ translate: `0px ${dy}px` }, { translate: "0px 0px" }], { duration: motionMs(element, "--atlas-motion-slow") });
    if (animation) pageMotion.push(animation);
  }
});

/**
 * The row that steps aside does not vanish at full opacity while the text below slides up: it leaves the flow where it
 * stands (so the page's travel, measured above, starts at once) and fades out over --atlas-motion-surface, and the
 * page moves up under it. Under reduced motion the page does not travel, so the row goes at once, as before.
 */
function leaveRow(el: Element, done: () => void) {
  const row = el as HTMLElement;
  // Only for stepping aside; a row that goes with its page or its headings (another page, editing, the dock's list) goes at once.
  if (!steppingAside || prefersReducedMotion(row)) { done(); return; }
  row.setAttribute("inert", "");
  row.style.cssText += `;position:absolute;left:${row.offsetLeft}px;top:${row.offsetTop}px;width:${row.offsetWidth}px;margin:0;pointer-events:none`;
  const fade = animateAtlas(row, [{ opacity: 1 }, { opacity: 0 }], { duration: motionMs(row, "--atlas-motion-surface"), fill: "forwards" });
  if (!fade) { done(); return; }
  fade.finished.then(done, done);
}

/** The dock's outline arrives after the facts below it: it unfolds, so they glide down instead of jumping (MOT3-2). */
function enterRow(el: Element, done: () => void) {
  const reveal = props.variant === "list" && entries.value.length !== leftWith ? revealHeight(el as HTMLElement) : null;
  if (reveal) reveal.finished.then(done, done); else done();
}

/**
 * An outline entry dragged out is that section of the page: a board makes a
 * page card that targets it; anything else gets the section's link.
 */
function dragSection(heading: PageOutlineHeading, event: DragEvent) {
  if (!props.pageId || !heading.headingId || !event.dataTransfer) return;
  event.dataTransfer.effectAllowed = "copyLink";
  event.dataTransfer.setData(atlasPageSectionDragType, pageSectionPayload(props.pageId, heading.headingId));
  event.dataTransfer.setData("text/uri-list", new URL(`#/pages/${encodeURIComponent(props.pageId)}?heading=${encodeURIComponent(heading.headingId)}`, location.href).href);
}

/** „Přidat do stromu…“: the section as a reference node (the menu path of dragging the entry onto the tree). */
const adding = ref<DraggedObject | null>(null);
const rowMenu = [{ id: "addToTree", label: objects.addToTree.menu, icon: "folder-input" }];
const addable = (heading: { title: string }) => !standingIn.value && !!props.pageId && !!(heading as PageOutlineHeading).headingId;

function choose(heading: PageOutlineHeading, index: number) {
  keepActive(index);
  goToHeading(heading);
  emit("navigate", heading);
  if (props.variant === "disclosure") toggle(false);
}

/**
 * One marker glides to the active entry (the ::before of the list, the way the tab pill of the dock slides to its tab)
 * instead of every entry recolouring itself, so scrolling through the page reads as a movement along the outline. Its
 * place is the entry's box in the list; the first one is set without motion (`ready` a frame later). Under reduced
 * motion no marker is drawn at all and the entries fade their own highlight, so nothing ever snaps.
 */
const marker = ref({ x: 0, y: 0, width: 0, height: 0, ready: false });
function placeMarker() {
  const ol = list.value, entry = ol?.children[active.value] as HTMLElement | undefined;
  if (!ol || !entry || !entry.offsetHeight || prefersReducedMotion(ol)) { marker.value = { x: 0, y: 0, width: 0, height: 0, ready: false }; return; }
  marker.value = { x: entry.offsetLeft, y: entry.offsetTop, width: entry.offsetWidth, height: entry.offsetHeight, ready: marker.value.ready };
  if (!marker.value.ready) requestAnimationFrame(() => { if (marker.value.height) marker.value = { ...marker.value, ready: true }; });
}
let markerSizes: ResizeObserver | undefined;
watch(list, (ol) => {
  markerSizes?.disconnect();
  markerSizes = undefined;
  if (ol && typeof ResizeObserver === "function") { markerSizes = new ResizeObserver(placeMarker); markerSizes.observe(ol); }
  void nextTick(placeMarker);
}, { flush: "post" });
// Not on a renamed heading (typing in it): its entry only changes size, which the list's size observer follows without a forced layout.
watch([active, () => headings.value.length, expanded], () => { void nextTick(placeMarker); }, { flush: "post" });
onBeforeUnmount(() => markerSizes?.disconnect());

/**
 * A long outline follows the reader: the active entry stays in view inside the column that scrolls the list (the
 * dock), with a neighbour above and below. Only that column is scrolled, never the page.
 */
const KEEP_IN_VIEW = 36;
watch(active, async () => {
  if (props.variant !== "list") return;
  await nextTick();
  const entry = list.value?.querySelector<HTMLElement>("[aria-current=location]");
  let scroller = entry?.parentElement;
  while (scroller && scroller !== document.body && !(scroller.scrollHeight > scroller.clientHeight && /auto|scroll/.test(getComputedStyle(scroller).overflowY))) scroller = scroller.parentElement;
  if (!entry || !scroller || scroller === document.body) return;
  const view = scroller.getBoundingClientRect(), box = entry.getBoundingClientRect();
  const move = box.top < view.top + KEEP_IN_VIEW ? box.top - view.top - KEEP_IN_VIEW : box.bottom > view.bottom - KEEP_IN_VIEW ? box.bottom - view.bottom + KEEP_IN_VIEW : 0;
  if (move) scroller.scrollBy({ top: move, behavior: scrollBehavior(entry) });
}, { flush: "post" });

/** Height and opacity unfold over --atlas-motion-base (board 16); the article below moves smoothly, nothing jumps. */
function toggle(next = !expanded.value) {
  const box = container.value, items = list.value;
  if (!box || !items || next === expanded.value) { expanded.value = next; return; }
  const before = box.offsetHeight;
  items.hidden = !next;
  const after = box.offsetHeight;
  expanded.value = next;
  animateAtlas(box, [{ height: `${before}px`, overflow: "hidden" }, { height: `${after}px`, overflow: "hidden" }], { duration: motionMs(box, "--atlas-motion-base"), fill: "none" });
  if (next) {
    animateAtlas(items, [{ opacity: 0 }, { opacity: 1 }], { duration: motionMs(items, "--atlas-motion-base") });
    void nextTick(() => items.querySelector<HTMLElement>("[aria-current=location], button")?.focus({ preventScroll: true }));
  }
}
function outlineKeys(event: KeyboardEvent) {
  if (event.key === "Escape" && props.variant === "disclosure" && expanded.value) {
    event.preventDefault();
    toggle(false);
    container.value?.querySelector<HTMLElement>(".page-outline-toggle")?.focus();
    return;
  }
  listKeys(event);
}
/* One Tab stop for the whole list (SH2-1): the entry that has the focus, else the active heading, else the first; ↑ ↓ Home End walk the rest. */
const ENTRY = ".page-outline-entry";
function rove(keeper?: HTMLElement) {
  const entries = [...list.value?.querySelectorAll<HTMLElement>(ENTRY) ?? []];
  const stop = keeper ?? entries.find(entry => entry.getAttribute("aria-current") === "location") ?? entries[0];
  for (const entry of entries) entry.tabIndex = entry === stop ? 0 : -1;
}
function roveTo(event: FocusEvent) {
  if (event.target instanceof HTMLElement && event.target.matches(ENTRY)) rove(event.target);
}
watch([headings, active], () => void nextTick(() => { if (!list.value?.contains(document.activeElement)) rove(); }), { immediate: true });
</script>

<template>
  <Transition :css="false" @enter="enterRow" @leave="leaveRow">
    <nav v-if="shown" ref="container" class="page-outline" :class="`page-outline-${variant}`" :aria-label="t.outline.onThisPage" :data-expanded="variant === 'disclosure' ? expanded : undefined" @keydown="outlineKeys" @focusin="roveTo">
      <button v-if="variant === 'disclosure'" type="button" class="page-outline-toggle" :aria-expanded="expanded" :aria-controls="listId" @click="toggle()">
        <AtlasIcon name="list" :size="16" />
        <span class="page-outline-toggle-label">{{ t.outline.onThisPage }}</span>
        <span class="page-outline-count">{{ sectionCount(headings.length) }}</span>
        <AtlasIcon name="chevron-down" :size="14" class="page-outline-chevron" />
      </button>
      <span v-else class="page-outline-heading" aria-hidden="true">{{ t.outline.onThisPage }}</span>
      <ol :id="listId" ref="list" class="page-outline-list" :hidden="variant === 'disclosure' && !expanded" :inert="standingIn || undefined"
        :aria-hidden="standingIn || undefined" :data-outline-stand-in="standingIn || undefined"
        :data-marker="marker.height ? (marker.ready ? 'ready' : 'placed') : undefined"
        :style="marker.height ? { '--outline-marker-x': `${marker.x}px`, '--outline-marker-y': `${marker.y}px`, '--outline-marker-w': `${marker.width}px`, '--outline-marker-h': `${marker.height}px` } : undefined">
        <li v-for="(heading, index) in entries" :key="heading.key" :style="{ '--outline-level': heading.level }">
          <button type="button" class="page-outline-entry" :aria-current="!standingIn && index === active ? 'location' : undefined" :data-level="heading.level"
            :draggable="pageId && (heading as PageOutlineHeading).headingId ? 'true' : undefined" @dragstart="dragSection(heading as PageOutlineHeading, $event)"
            @click="choose(heading as PageOutlineHeading, index)">{{ heading.title }}</button>
          <RowActionsMenu v-if="addable(heading)" :items="rowMenu" :label="objects.addToTree.rowActions(heading.title)"
            @select="adding = { id: pageId!.toLowerCase(), typeId: 'atlas.page', title: heading.title, anchor: (heading as PageOutlineHeading).headingId }" />
        </li>
      </ol>
      <AddToTreeDialog :open="!!adding" :object="adding" @close="adding = null" />
    </nav>
  </Transition>
</template>

<style scoped>
.page-outline { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
.page-outline-heading { padding: 0 8px 6px; color: var(--muted); font-size: 11px; font-weight: 700; letter-spacing: 0.06em; text-transform: uppercase; }
.page-outline-list { display: flex; flex-direction: column; gap: 2px; margin: 0; padding: 0; list-style: none; }
.page-outline-list[hidden] { display: none; }
.page-outline-list li { min-width: 0; }
/* The marker of the active heading: one box that glides to the active entry (placeMarker). While it is drawn, the
   active entry shows only its colour and weight; under reduced motion no marker exists and the entry fades its own.
   (`ol`: the dock's nav carries the class page-outline-list too.) */
ol.page-outline-list { position: relative; isolation: isolate; }
ol.page-outline-list > li { position: relative; z-index: 1; }
ol.page-outline-list::before {
  content: ""; position: absolute; z-index: 0; top: 0; left: var(--outline-marker-x, 0px); box-sizing: border-box; width: var(--outline-marker-w, 0px); height: var(--outline-marker-h, 0px);
  border-left: 2px solid var(--accent); border-radius: 0 8px 8px 0; background: color-mix(in srgb, var(--accent-soft) 60%, transparent);
  transform: translateY(var(--outline-marker-y, 0px)); opacity: 0; pointer-events: none;
}
ol.page-outline-list[data-marker]::before { opacity: 1; }
ol.page-outline-list[data-marker="ready"]::before { transition: transform var(--atlas-motion-base) var(--atlas-ease-inout), height var(--atlas-motion-base) var(--atlas-ease-inout), opacity var(--atlas-motion-fast) var(--atlas-ease-out); }
.page-outline-entry {
  display: block; width: 100%; min-height: 30px; padding: 4px 8px 4px calc(8px + var(--outline-level, 0) * 14px);
  /* The rail is a straight line: a radius on the rail's side bent it into a "(" at both ends of every entry. */
  border: 0; border-left: 2px solid color-mix(in srgb, var(--line) 80%, transparent); border-radius: 0 8px 8px 0;
  background: none; color: var(--text); font: inherit; font-size: 13.5px; line-height: 1.4; text-align: left; overflow-wrap: anywhere; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out), border-color var(--atlas-motion-fast) var(--atlas-ease-out);
}
.page-outline-entry[data-level]:not([data-level="0"]) { color: var(--muted); }
@media (hover: hover) and (pointer: fine) { .page-outline-entry:hover { background: var(--hover); color: var(--text); } }
.page-outline-entry[aria-current="location"] { border-left-color: var(--accent); background: color-mix(in srgb, var(--accent-soft) 60%, transparent); color: var(--accent); font-weight: 600; }
ol.page-outline-list[data-marker] .page-outline-entry[aria-current="location"] { border-left-color: transparent; background: transparent; }
.page-outline-entry:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }

/* Disclosure (board 13): one row in the article, the list unfolds under it. */
/* The row fades in where it stays (when the headings are known, and when the dock that replaced it closes). */
.page-outline-disclosure { gap: 0; border: 1px solid color-mix(in srgb, var(--line) 70%, transparent); border-radius: 10px; background: color-mix(in srgb, var(--hover) 30%, var(--surface)); animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
.page-outline-toggle { display: flex; align-items: center; gap: 8px; width: 100%; min-height: 44px; padding: 0 12px; border: 0; border-radius: 10px; background: none; color: var(--text); font: inherit; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .page-outline-toggle:hover { background: color-mix(in srgb, var(--hover) 55%, transparent); } }
.page-outline-toggle > svg:first-child { color: var(--muted); }
.page-outline-toggle-label { flex: 1; font-weight: 500; text-align: left; }
.page-outline-count { color: var(--muted); font-size: 13px; }
.page-outline-chevron { rotate: -90deg; transition: rotate var(--atlas-motion-base) var(--atlas-ease-out); }
.page-outline-disclosure[data-expanded="true"] .page-outline-chevron { rotate: 0deg; }
.page-outline-toggle:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.page-outline-disclosure .page-outline-list { padding: 2px 8px 10px; }
</style>
