<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from "vue";
import type { Space } from "../api";
import { animateAtlas, motionMs, prefersReducedMotion, travelTiming } from "../motion/aiMotion";
import { truncatedTitle } from "../lib/truncatedTitle";
import { visibleHeight } from "../lib/visibleHeight";
import { isPlainNavigationClick, spaceLink } from "../pageLinks";
import type { TreeDrop, TreePage } from "../treeMove";
import { filterTree } from "../treeFilter";
import { plainTitle } from "../objects/relationsApi";
import { useSearchKeys } from "../lib/searchKeys";
import type { ReferenceDrop } from "../boards/boardDrop";
import AtlasIcon from "./AtlasIcon.vue";
import PageTree, { type TreeAction } from "./PageTree.vue";
import SearchField from "./settings/SearchField.vue";
import LoadingSkeleton from "./LoadingSkeleton.vue";
import ActionDisclosure from "./ActionDisclosure.vue";
import ActionDisclosureItem from "./ActionDisclosureItem.vue";
import SpaceMenuItems from "./SpaceMenuItems.vue";
import { Tooltip, TooltipContent, TooltipTrigger } from "./ui/tooltip";
import componentsSidebar from "../i18n/components-sidebar";

// One space in the wiki sidebar: a row like a tree row one level up (chevron, icon, name) that links to
// the space overview, and below it, while expanded, the space's page tree. The row is also the drop
// target for the space's root. A space that is not pinned is the open one: it carries its own caption
// with Připnout. App owns navigation, trees, pins and moves; this component only renders and reports.
const props = withDefaults(defineProps<{
  space: Space;
  /** The space's tree; undefined until it has loaded. */
  pages?: TreePage[];
  expanded: boolean;
  /** The space overview is the current page. */
  current?: boolean;
  activeId?: string;
  collapsedIds?: ReadonlySet<string>;
  /** The account may create and move pages here. */
  canWrite?: boolean;
  /** The account administers the space, so it may pin it for everyone in its settings. */
  canManage?: boolean;
  busy?: boolean;
  draggedId?: string;
  /** A dragged page of this space would move to its root. */
  dropTarget?: boolean;
  pinPending?: boolean;
  loadFailed?: boolean;
  /** Module Boards is on: the menu also creates a board (its boards are nodes of the tree itself). */
  boards?: boolean;
  /** „Řadit abecedně“ of the space root (the tree's rootByTitle). */
  rootByTitle?: boolean;
  /** „Přesouvat stránky tažením“, the account's: off, tree rows are not dragged. */
  drag?: boolean;
}>(), { collapsedIds: () => new Set<string>(), drag: true });
const emit = defineEmits<{
  open: [];
  toggle: [];
  /** The reader is about to unfold the space and its tree has not loaded yet. */
  intent: [];
  pin: [pinned: boolean];
  /** Open the space's settings, where its pin policy for everyone is set. */
  pinPolicy: [];
  /** Open the space's settings. */
  settings: [];
  createPage: [];
  createBoard: [];
  /** The rest of the shared space menu (SpaceMenuItems); the shell answers for this space. */
  templates: [];
  access: [];
  trash: [];
  exportSpace: [];
  contentHealth: [];
  setCollapsed: [ids: string[], collapsed: boolean];
  select: [page: TreePage];
  toggleCollapsed: [id: string];
  dragStart: [page: TreePage, event: DragEvent];
  dragEnd: [];
  drop: [destination: TreeDrop];
  rootDragOver: [event: DragEvent];
  rootDragLeave: [];
  rootDrop: [event: DragEvent];
  retry: [];
  /** Dropped from outside the tree at a position (or at the root): a reference node to `target`. */
  insert: [destination: TreeDrop, target: ReferenceDrop];
  action: [action: TreeAction, page: TreePage];
  rename: [page: TreePage, title: string];
  /** The filter found nothing: search the whole wiki for the query. */
  searchWiki: [query: string];
  /** „Řadit abecedně“ of the root ticked or unticked. */
  sortRoot: [byTitle: boolean];
  /** „Přesouvat stránky tažením“ ticked or unticked. */
  dragPages: [on: boolean];
}>();

// The tree's filter (spec 2026-10-27 §5.2): a letter typed on a row, Ctrl/⌘+Shift+F or the magnifier opens it; it filters the
// loaded tree, preselects the first match (Enter opens it, ↑ ↓ choose), and Esc closes it, the tree folded as it was.
const filterQuery = ref<string | null>(null);
const filterCursor = ref(0);
const filterField = ref<HTMLElement>();
const filtered = computed(() => filterQuery.value === null || !props.pages ? null : filterTree(props.pages, filterQuery.value, props.collapsedIds));
const filterSelected = computed(() => filtered.value?.matches[Math.min(filterCursor.value, filtered.value.matches.length - 1)]);
watch(filterQuery, () => { filterCursor.value = 0; });
// The preselected match follows ↑ ↓ into view (the focus stays in the field, so the browser will not scroll to it); instant, never smooth.
watch(filterSelected, match => {
  if (match) void nextTick(() => tree.value?.querySelector(`[data-tree-row="${CSS.escape(match.id)}"]`)?.scrollIntoView({ block: "nearest", behavior: "auto" }));
});
let filterReturn: Element | null = null;
function openFilter(typed = "") {
  filterReturn = document.activeElement;
  if (!props.expanded) { intent(); toggle(); }
  filterQuery.value = (filterQuery.value ?? "") + typed;
  void nextTick(() => filterField.value?.querySelector("input")?.focus());
}
function closeFilter() {
  filterQuery.value = null;
  const opener = filterReturn;
  filterReturn = null;
  // A row the filter left out is inert (or its branch folded away) until the tree has re-rendered without the filter; the focus
  // waits for that, finding the row again by its page (it fell to <body> and the shell landed on the article's heading, UX SB4-9).
  const id = opener instanceof HTMLElement ? opener.closest<HTMLElement>("[data-tree-row]")?.dataset.treeRow : undefined;
  void nextTick(() => {
    const row = id ? tree.value?.querySelector<HTMLElement>(`[data-tree-row="${CSS.escape(id)}"] .tree-page`) : undefined;
    (row ?? (opener instanceof HTMLElement && opener.isConnected ? opener : tree.value?.querySelector<HTMLElement>('.tree-page[tabindex="0"]')))?.focus();
  });
}
// The shared search keys (searchKeys.ts): the first Esc empties the typed text (the tree shows whole again), Enter takes the selected match or, with none, searches the wiki.
const searchKeys = useSearchKeys({ text: () => filterQuery.value ?? "", clear: () => { filterQuery.value = ""; }, tab: false,
  take: () => {
    const match = filterSelected.value;
    if (match) { filterQuery.value = null; filterReturn = null; emit("select", match); }
    else emit("searchWiki", filterQuery.value!.trim());
  } });
function filterKeys(event: KeyboardEvent) {
  if (searchKeys.onKeydown(event)) return;
  // The next Esc closes the filter (P3).
  if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); closeFilter(); return; }
  if (event.key === "ArrowDown" || event.key === "ArrowUp") {
    event.preventDefault();
    const matches = filtered.value?.matches ?? [];
    if (matches.length) filterCursor.value = (Math.min(filterCursor.value, matches.length - 1) + (event.key === "ArrowDown" ? 1 : matches.length - 1)) % matches.length;
  }
}
defineExpose({ openFilter });

const treeId = computed(() => `sidebar-space-${props.space.id}`);
const required = computed(() => props.space.pinPolicy === "Required");
const parents = computed(() => {
  const ids = new Set<string>();
  for (const page of props.pages ?? []) if (page.parentId) ids.add(page.parentId);
  return [...ids];
});
const allCollapsed = computed(() => parents.value.every(id => props.collapsedIds.has(id)));
const containsActive = computed(() => !!props.activeId && !!props.pages?.some(page => page.id === props.activeId));
const canCollapse = computed(() => props.expanded && parents.value.length > 0);
const canUnpin = computed(() => props.space.pinned && !required.value);
const hasActions = computed(() => props.canWrite || canCollapse.value || canUnpin.value || props.canManage);

function open(event: MouseEvent) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  emit("open");
}
// The tree unfolds under its space and folds back into it (reduced motion only fades), and while the space holds its
// tree it is a card around both. The row itself never moves: the card's room above and beside it is there all the time
// (styles/refresh.css). What comes and goes with the tree, on one clock, is the card's room under it (the item's
// bottom padding and margin) and its frame (the item's ::before), which fades. The styles alone would switch both in
// the frame the tree is inserted or removed, and every row below would jump by that room before the unfolding and
// after the fold. The motion leaves from rest and takes longer the further the rows below have to go (travelTiming).
const item = ref<HTMLElement>();
const tree = ref<HTMLElement>();
const FRAME = "::before";
const ROOM = ["paddingBottom", "marginBottom"] as const;
interface CardMotion {
  opening: boolean;
  /** The tree's own animation: height, opacity and its margins. Replaced when pages arrive while it unfolds. */
  tree: Animation;
  /** The item's frame and room, started with it. */
  beside: Animation[];
  /** Where it started: how much of a card the item was (0 a plain row … 1 the card) and how tall the tree. */
  from: { card: number; height: number };
  /** Only the part in view moves (lib/visibleHeight.ts). */
  limited: boolean;
  /** Set while it waits for its pages before it moves (`hold`). */
  held?: ReturnType<typeof setTimeout>;
}
let motion: CardMotion | null = null;
const scaled = (length: string, factor: number) => `${(Number.parseFloat(length) || 0) * factor}px`;
/** Where a motion has got to: 0 a plain row … 1 the card with its tree. */
function reached(running: CardMotion) {
  const to = running.opening ? 1 : 0;
  const progress = (running.beside[0] ?? running.tree).effect?.getComputedTiming().progress;
  return typeof progress === "number" ? running.from.card + (to - running.from.card) * progress : to;
}
function stop(running: CardMotion | null) {
  if (!running) return;
  if (running.held !== undefined) clearTimeout(running.held);
  running.held = undefined;
  running.tree.cancel();
  for (const animation of running.beside) animation.cancel();
}
function follow(running: CardMotion, animation: Animation, done?: () => void) {
  const settled = () => {
    // A folded tree is removed first: the item then is a plain row by its own styles, and what the fold held on
    // it until now can go without a trace.
    done?.();
    if (motion !== running || running.tree !== animation) return;
    if (running.held !== undefined) clearTimeout(running.held);
    running.held = undefined;
    for (const beside of running.beside) beside.cancel();
    motion = null;
  };
  animation.finished.then(settled, settled);
}
type Timing = ReturnType<typeof travelTiming>;
/** Starts the card's frame and room beside the tree's `animation`, from `from.card` to open or folded. */
function move(opening: boolean, animation: Animation | null, timing: Timing, from: CardMotion["from"], limited: boolean, done: () => void) {
  const card = item.value;
  if (!animation) { motion = null; done(); return null; }
  const to = opening ? 1 : 0, fill = opening ? "none" : "forwards";
  // The tree is in the item, so these are the card's own values; reduced motion keeps only the fade of the frame.
  const room = card ? getComputedStyle(card) : null;
  const at = (factor: number) => Object.fromEntries(ROOM.map(side => [side, scaled(room?.[side] ?? "", factor)]));
  const beside = card ? [
    animateAtlas(card, [{ opacity: from.card }, { opacity: to }], { ...timing, fill, pseudoElement: FRAME }),
    prefersReducedMotion(card) ? null : animateAtlas(card, [at(from.card), at(to)], { ...timing, fill }),
  ].filter((started): started is Animation => !!started) : [];
  const running: CardMotion = motion = { opening, tree: animation, beside, from, limited };
  follow(running, animation, done);
  return running;
}
// The chevron folds and unfolds the tree where the reader looks, so only the part in view has to move. A space opened
// from elsewhere (going to one of its pages) unfolds whole: the list follows the unfolding down to that page's row.
let asked = false, limitedLast = false;
function toggle() {
  asked = true;
  emit("toggle");
  void nextTick(() => { asked = false; });
}
// Heights are read to the fraction of a pixel: offsetHeight rounds, and a motion that turns around from a rounded
// height begins with a step of up to half a pixel.
const heightOf = (element: HTMLElement) => element.getBoundingClientRect().height;
const shown = (element: HTMLElement, limited: boolean) => limited ? visibleHeight(element, heightOf(element)) : heightOf(element);
/** The tree and its card open from `from` to the height the tree's content has now. */
function unfoldFrom(element: HTMLElement, from: CardMotion["from"], limited: boolean, done: () => void) {
  const margins = getComputedStyle(element), to = shown(element, limited), timing = travelTiming(element, to - from.height);
  return move(true, animateAtlas(element, [
    { height: `${from.height}px`, opacity: from.card, marginTop: scaled(margins.marginTop, from.card), marginBottom: scaled(margins.marginBottom, from.card), overflow: "clip" },
    { height: `${to}px`, opacity: 1, overflow: "clip" },
  ], { ...timing, fill: "none" }), timing, from, limited, done);
}
// A space opened before its tree has loaded would unfold for „Načítání stránek…“ and then again, further, for the
// pages: two motions, the second from wherever the first had got to. So it waits for the pages as long as any loading
// state waits before it shows (--atlas-motion-defer), with everything in its first frame; the arrow already turns.
// An answer within that time unfolds once, to the whole tree. Only a slower one unfolds for the loading line first.
function hold(running: CardMotion, element: HTMLElement) {
  for (const animation of [running.tree, ...running.beside]) animation.pause();
  running.held = setTimeout(() => { if (motion === running) release(running); }, motionMs(element, "--atlas-motion-defer"));
}
function release(running: CardMotion) {
  if (running.held === undefined) return;
  clearTimeout(running.held);
  running.held = undefined;
  for (const animation of [running.tree, ...running.beside]) animation.play();
}
function unfold(el: Element, done: () => void) {
  const element = el as HTMLElement, limited = limitedLast = asked;
  // Reopened while it still folds, the space unfolds on from where the fold had got to, not from zero.
  const turning = motion && !motion.opening ? motion : null;
  const card = turning ? reached(turning) : 0;
  const from = { card, height: turning?.from.card ? turning.from.height * card / turning.from.card : 0 };
  stop(turning);
  const running = unfoldFrom(element, from, limited, done);
  if (running && !turning && !props.pages && !props.loadFailed && !prefersReducedMotion(element)) hold(running, element);
}
function fold(el: Element, done: () => void) {
  const element = el as HTMLElement;
  // Folded while it still unfolds, it folds from the height and opacity it had reached.
  const running = motion?.opening ? motion : null;
  // Folded while it still waits for its pages, it has not moved: there is nothing to fold, and a fold of nothing
  // would make the next unfolding a turn, which does not wait.
  if (running?.held !== undefined) { stop(running); motion = null; done(); return; }
  const card = running ? reached(running) : 1;
  const now = getComputedStyle(element), height = shown(element, true);
  const start = { height: `${height}px`, opacity: now.opacity === "" ? 1 : Number(now.opacity), marginTop: now.marginTop, marginBottom: now.marginBottom, overflow: "clip" };
  stop(running);
  // Out of use at once; it stays visible only to the eye while it folds.
  element.setAttribute("inert", "");
  element.setAttribute("aria-hidden", "true");
  const timing = travelTiming(element, height, true);
  move(false, animateAtlas(element, [
    start,
    { height: "0px", opacity: 0, marginTop: "0px", marginBottom: "0px", overflow: "clip" },
  ], { ...timing, fill: "forwards" }), timing, { card, height }, true, done);
}
// While it unfolds the tree is clipped to an animated height, and Chromium drops a native drag started inside it,
// so pressing on the tree finishes the unfolding at once: a page can be dragged the moment it is visible.
function settleUnfolding() {
  if (!motion?.opening) return;
  release(motion);
  for (const animation of [motion.tree, ...motion.beside]) animation.finish();
}
/** The tree eases on from `from` to the height its content has now; the card around it stays as it is. */
function unfoldOn(element: HTMLElement, running: CardMotion | null, from: Keyframe, limited: boolean) {
  const height = Number.parseFloat(String(from.height)), to = shown(element, limited);
  const animation = running || to !== height ? animateAtlas(element, [{ ...from, overflow: "clip" }, { height: `${to}px`, ...("opacity" in from ? { opacity: 1 } : {}), overflow: "clip" }], { ...travelTiming(element, to - height), fill: "none" }) : null;
  if (!animation) return;
  const moving = running ?? (motion = { opening: true, tree: animation, beside: [], from: { card: 1, height }, limited });
  moving.tree = animation;
  follow(moving, animation);
}
// Pages that arrive while the space still waits for them unfold with it, once (`hold`); so does the message of a load
// that failed. Pages that arrive while it
// unfolds for „Načítání stránek…“ unfold on from where it is, as a motion of its own length: finished „in the time
// it has left“, a tree of hundreds of pixels crossed them in the last few frames. Pages that arrive later unfold from
// that line instead of pushing everything below them down in one frame.
let lineHeight = 0;
watch(() => props.pages, () => { lineHeight = tree.value ? heightOf(tree.value) : 0; }, { flush: "pre" });
watch([() => props.pages, () => props.loadFailed], ([pages], [had]) => {
  const element = tree.value;
  if (!element || prefersReducedMotion(element)) return;
  if (!motion) {
    if (pages && !had && lineHeight) unfoldOn(element, null, { height: `${lineHeight}px` }, limitedLast);
    return;
  }
  const running = motion.opening ? motion : null;
  if (!running) return;
  if (running.held !== undefined) {
    // Nothing has moved yet, and the tree holds what it will show (the pages, or why there are none): one unfolding
    // to that height. Played on as it was, it would end at the height of the loading line.
    stop(running);
    unfoldFrom(element, { card: 0, height: 0 }, running.limited, () => {});
    return;
  }
  if (running.tree.playState !== "running") return;
  const now = getComputedStyle(element);
  const from = { height: `${heightOf(element)}px`, opacity: now.opacity, marginTop: now.marginTop, marginBottom: now.marginBottom };
  running.tree.cancel();
  unfoldOn(element, running, from, running.limited);
}, { flush: "post" });
onBeforeUnmount(() => { if (motion?.held !== undefined) stop(motion); });
// The arrow of a space whose tree has not loaded yet asks for it as soon as it is pressed, a moment before the click:
// the pages are then more often there when the tree unfolds. Not on hover: a pointer that only crosses the arrows
// would load every pinned tree.
function intent() {
  if (!props.expanded && !props.pages && !props.loadFailed) emit("intent");
}
// „Sbalit vše“ and „Rozbalit vše“ change many branches at once, which PageTree does not animate branch by branch
// (that many lists measured and moved together cost frames). The tree as a whole eases from the height it had to the
// height it has now, so the card and the spaces below glide to their place instead of jumping by the difference.
// The menu is used where the row is in view, so only the visible part moves.
let allFrom: number | null = null;
function setAllCollapsed() {
  allFrom = tree.value ? heightOf(tree.value) : null;
  emit("setCollapsed", parents.value, !allCollapsed.value);
  void nextTick(() => { allFrom = null; });
}
watch(() => props.collapsedIds, () => {
  const element = tree.value, from = allFrom;
  allFrom = null;
  if (!element || from === null || motion || prefersReducedMotion(element)) return;
  unfoldOn(element, null, { height: `${visibleHeight(element, from)}px` }, true);
}, { flush: "post" });
// The space row is the tree's level above its pages (keyboard pass): → unfolds the space, then enters its tree; ← folds it, and from
// a top page goes back to the space; ↑ ↓ walk the pinned spaces and their open trees as one list. PageTree moves within a tree;
// this crosses between a space, its tree and the next space.
const link = ref<HTMLAnchorElement>();
const STOPS = ".sidebar-space-link, .sidebar-space-tree:not([inert]) .tree-page";
function keys(event: KeyboardEvent) {
  if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || !(event.target instanceof HTMLElement)) return;
  const target = event.target, onLink = target === link.value, page = target.matches(".tree-page");
  if (onLink && (event.key === "ArrowRight" || event.key === "ArrowLeft")) {
    event.preventDefault();
    if (event.key === "ArrowLeft") { if (props.expanded) toggle(); }
    else if (!props.expanded) { intent(); toggle(); }
    else tree.value?.querySelector<HTMLElement>(".tree-page")?.focus();
    return;
  }
  if (page && event.key === "ArrowLeft" && target.getAttribute("aria-level") === "1" && target.getAttribute("aria-expanded") !== "true") { link.value?.focus(); return; }
  if ((!onLink && !page) || (event.key !== "ArrowDown" && event.key !== "ArrowUp")) return;
  const stops = [...(item.value?.parentElement?.querySelectorAll<HTMLElement>(STOPS) ?? [])];
  const next = stops[stops.indexOf(target) + (event.key === "ArrowDown" ? 1 : -1)];
  // Between two pages of this tree PageTree has already moved.
  if (!next || (page && next.matches(".tree-page") && item.value?.contains(next))) return;
  event.preventDefault();
  next.focus();
}
function dragLeave(event: DragEvent) {
  const row = event.currentTarget as HTMLElement;
  if (!(event.relatedTarget instanceof Node && row.contains(event.relatedTarget))) emit("rootDragLeave");
}
</script>

<template>
  <li ref="item" class="sidebar-space" :data-sidebar-space="space.id" :data-expanded="expanded || undefined" @keydown="keys">
    <div v-if="!space.pinned" class="sidebar-space-caption">
      <span>{{ componentsSidebar.sidebar.openSpace }}</span>
      <button type="button" class="sidebar-space-pin" :aria-label="componentsSidebar.sidebar.pinSpace(space.name)" :disabled="pinPending" :aria-busy="pinPending" @click="emit('pin', true)">
        <AtlasIcon name="pin" :size="13" />{{ componentsSidebar.sidebar.pin }}
      </button>
    </div>
    <div
      class="sidebar-space-row"
      :class="{ active: current, 'contains-active': containsActive && !current, 'drop-inside': dropTarget }"
      @dragover="emit('rootDragOver', $event)"
      @dragleave="dragLeave"
      @drop.prevent="emit('rootDrop', $event)"
    >
      <span class="tree-toggle-col">
        <button
          type="button"
          class="tree-toggle"
          :aria-expanded="expanded"
          :aria-controls="expanded ? treeId : undefined"
          :aria-label="expanded ? componentsSidebar.sidebar.collapseSpace(space.name) : componentsSidebar.sidebar.expandSpace(space.name)"
          :title="expanded ? componentsSidebar.sidebar.collapseTitle : componentsSidebar.sidebar.expandTitle"
          @pointerdown="intent"
          @click="toggle"
        ><AtlasIcon name="right" :size="14" /></button>
      </span>
      <a ref="link" class="sidebar-space-link" :href="spaceLink(space.id)" :aria-current="current ? 'page' : undefined" @click="open" @pointerenter="truncatedTitle($event, '.sidebar-space-name')">
        <span class="space-icon" :data-color="space.color"><AtlasIcon :name="space.icon" :size="14" /></span>
        <span class="sidebar-space-name">{{ space.name }}</span>
        <template v-if="rootByTitle && canWrite">
          <Tooltip><TooltipTrigger as-child><span class="sidebar-space-sorted" aria-hidden="true" data-sidebar-space-sorted><AtlasIcon name="arrow-down-a-z" :size="12" /></span></TooltipTrigger><TooltipContent>{{ componentsSidebar.sidebar.sortedTip }}</TooltipContent></Tooltip>
          <span class="sr-only">{{ componentsSidebar.sidebar.sortedSr }}</span>
        </template>
        <template v-if="required">
          <Tooltip><TooltipTrigger as-child><span class="sidebar-space-lock" aria-hidden="true"><AtlasIcon name="lock" :size="12" /></span></TooltipTrigger><TooltipContent>{{ componentsSidebar.sidebar.pinnedByAdminTip }}</TooltipContent></Tooltip>
          <span class="sr-only">{{ componentsSidebar.sidebar.pinnedByAdminSr }}</span>
        </template>
      </a>
      <!-- On touch there are no letters to type on a row: the magnifier opens the tree's filter. -->
      <button v-if="pages?.length" type="button" class="sidebar-space-find" :aria-label="componentsSidebar.sidebar.findIn(space.name)" data-sidebar-space-find @click="openFilter()">
        <AtlasIcon name="search" :size="14" /></button>
      <span v-if="hasActions || pages?.length" class="sidebar-space-actions">
        <ActionDisclosure :label="componentsSidebar.sidebar.moreActions(space.name)" :disabled="busy">
          <SpaceMenuItems :can-write="canWrite" :can-manage="canManage" :boards="boards" :empty="pages !== undefined && !pages.length"
            @create-page="emit('createPage')" @create-board="emit('createBoard')" @settings="emit('settings')" @pin-policy="emit('pinPolicy')"
            @templates="emit('templates')" @access="emit('access')" @trash="emit('trash')" @export-space="emit('exportSpace')" @content-health="emit('contentHealth')">
            <template #before>
              <ActionDisclosureItem v-if="pages?.length" data-sidebar-space-filter @click="openFilter()"><AtlasIcon name="search" />{{ componentsSidebar.sidebar.find }}</ActionDisclosureItem>
              <ActionDisclosureItem v-if="canCollapse" @click="setAllCollapsed"><AtlasIcon :name="allCollapsed ? 'chevrons-up-down' : 'chevrons-down-up'" />{{ allCollapsed ? componentsSidebar.sidebar.expandAll : componentsSidebar.sidebar.collapseAll }}</ActionDisclosureItem>
              <ActionDisclosureItem v-if="canUnpin" :disabled="pinPending" @click="emit('pin', false)"><AtlasIcon name="pin" />{{ componentsSidebar.sidebar.unpin }}</ActionDisclosureItem>
              <template v-if="canWrite && pages?.length">
                <ActionDisclosureItem :checked="!!rootByTitle" data-sidebar-space-sort @click="emit('sortRoot', !rootByTitle)"><AtlasIcon name="arrow-down-a-z" />{{ componentsSidebar.sidebar.sortAlpha }}</ActionDisclosureItem>
                <ActionDisclosureItem :checked="drag" data-sidebar-space-drag @click="emit('dragPages', !drag)"><AtlasIcon name="grip-vertical" />{{ componentsSidebar.sidebar.dragPages }}</ActionDisclosureItem>
              </template>
            </template>
          </SpaceMenuItems>
        </ActionDisclosure>
      </span>
    </div>
    <Transition :css="false" @enter="unfold" @leave="fold">
    <div v-if="expanded" :id="treeId" ref="tree" class="sidebar-space-tree" @pointerdown.capture="settleUnfolding" @dragstart.capture="settleUnfolding">
      <div v-if="filterQuery !== null && pages?.length" ref="filterField" class="sidebar-tree-filter" data-sidebar-tree-filter @keydown.capture="filterKeys">
        <SearchField v-model="filterQuery" :label="componentsSidebar.sidebar.findIn(space.name)" :placeholder="componentsSidebar.sidebar.find" wide />
        <button v-if="filterQuery.trim() && !filtered?.matches.length" type="button" class="sidebar-tree-filter-wiki" data-tree-filter-wiki
          @click="emit('searchWiki', filterQuery.trim())"><AtlasIcon name="search" :size="14" />{{ componentsSidebar.sidebar.searchWiki(filterQuery.trim()) }}</button>
        <span class="sr-only" role="status">{{ filterQuery.trim() ? (filtered?.matches.length ? componentsSidebar.sidebar.matches(filtered.matches.length, plainTitle(filterSelected?.title)) : componentsSidebar.sidebar.noMatch) : "" }}</span>
      </div>
      <PageTree
        v-if="pages?.length"
        :pages="pages"
        :aria-label="componentsSidebar.sidebar.pagesOf(space.name)"
        :active-id="activeId"
        :can-reorder="canWrite"
        :dragged-id="draggedId"
        :busy="busy"
        :collapsed-ids="filtered?.collapsed ?? collapsedIds"
        :hidden-ids="filtered?.hidden"
        :highlight="filterQuery ?? ''"
        :selected-id="filterSelected?.id"
        :boards="boards"
        :drag="drag"
        menu
        filterable
        @drag-start="(page, event) => emit('dragStart', page, event)"
        @drag-end="emit('dragEnd')"
        @drop="emit('drop', $event)"
        @insert="(destination, target) => emit('insert', destination, target)"
        @action="(action, page) => emit('action', action, page)"
        @rename="(page, title) => emit('rename', page, title)"
        @filter="openFilter"
        @select="emit('select', $event)"
        @toggle-collapsed="emit('toggleCollapsed', $event)"
      />
      <p v-else-if="pages" class="sidebar-empty">{{ componentsSidebar.sidebar.empty }}</p>
      <p v-else-if="loadFailed" class="sidebar-empty" role="alert">{{ componentsSidebar.sidebar.loadFailed }} <button type="button" class="sidebar-space-retry" @click="emit('retry')">{{ componentsSidebar.sidebar.retry }}</button></p>
      <LoadingSkeleton v-else class="sidebar-space-loading" :rows="3" row-height="30px" :label="componentsSidebar.sidebar.loading" />
    </div>
    </Transition>
  </li>
</template>

<style scoped>
.sidebar-space-sorted { display: inline-flex; flex: 0 0 auto; color: var(--muted); opacity: .75; }
/* Only on touch, beside the always shown menu of the row; elsewhere letters, Ctrl/⌘+Shift+F and the menu open the filter. */
.sidebar-space-find { display: none; }
@media (hover: none) {
  .sidebar-space-find {
    position: absolute; top: 0; right: 32px; bottom: 0; display: grid; place-items: center; width: 32px; padding: 0;
    border: 0; background: none; color: var(--muted);
  }
  .sidebar-space-find:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
}
/* The tree's rows to come, in the tree's own size (a row of 30 px and a small mark), so the space does not jump when its pages arrive. */
.sidebar-space-loading :deep(.loading-skeleton-row) { padding: 0 10px 0 30px; }
.sidebar-space-loading :deep(.loading-skeleton-row + .loading-skeleton-row) { border-top: 0; }
.sidebar-space-loading :deep(.loading-skeleton-icon) { flex-basis: 14px; height: 14px; border-radius: 4px; }
.sidebar-space-loading :deep(.loading-skeleton-copy) { gap: 0; }
/* `:last-child` (of its two lines), not `span + span`: a sibling combinator over every span restyled the editor's blocks after each inserted block (J12.5). */
.sidebar-space-loading :deep(.loading-skeleton-copy > span:last-child) { display: none; }
.sidebar-tree-filter { display: grid; gap: 4px; margin: 2px 4px 6px; animation: st-rise-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
@media (prefers-reduced-motion: reduce) { .sidebar-tree-filter { animation: st-fade-in var(--atlas-motion-surface) linear both; } }
.sidebar-tree-filter :deep(.search-field-input) { min-height: 32px; font-size: 13px; }
.sidebar-tree-filter-wiki {
  display: flex; align-items: center; gap: 8px; min-height: 30px; padding: 0 8px; border: 0; border-radius: var(--radius-sm, 6px);
  background: var(--hover); color: var(--text); font: inherit; font-size: 13px; text-align: left; cursor: pointer;
}
.sidebar-tree-filter-wiki:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
</style>
