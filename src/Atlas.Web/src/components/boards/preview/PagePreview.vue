<script lang="ts">
import t from "../../../i18n/boards";
import { atlasContentPlugins } from "../../../documentWidgets";
import { createOverviewReading } from "../../../overview/overviewReading";
import { lazyComponent } from "../../../lazyComponent";

// The page's own plugins in their static policy (as a historical version reads): stored snapshots, password references always
// hidden, no OpenAPI requests, in a read-only editor (one editor, contract J1.7). One list for every preview on screen.
const previewPlugins = atlasContentPlugins({ kind: "history" });
/** Changes of a page come in bursts (a save, then its projection; an import): one reload after they settle. */
export const previewRefreshDelayMs = 750;
/** A card scrolled out of view keeps listening this long, so panning back and forth neither reloads nor misses a change. */
export const previewLingerMs = 30_000;
/** A read of a page serves the other previews of the same page this long (a board shows a few pages many times over); a change heard or missed reads anew. */
const recentMs = 5000;
const recent = new Map<string, { at: number; page: unknown }>();
// Once for every preview, not in its setup: a chunk's first load keeps its loader, which would keep that preview and its board alive.
const BoardPageEditor = lazyComponent(() => import("./BoardPageEditor.vue"));
</script>

<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onErrorCaptured, ref, shallowRef, watch } from "vue";
import { MarkdownEditor } from "@atlas/markdown/vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/ocean.css";
import "@atlas/markdown/themes/forest.css";
import "@atlas/markdown/themes/midnight.css";
import { api, ApiError, type Page } from "../../../api";
import { dedupedGet } from "../../../lib/dedupedGet";
import { sliceSection } from "../../../boards/boardSections";
import { isCommentsChange } from "../../../comments/commentThread";
import { readDocument } from "../../../document";
import { editorChrome, editorOutline, useEditorTheme } from "../../../editor/editorSetup";
import { liveConnection, type ChannelListener, type LiveConnection } from "../../../live/liveConnection";
import type { ViewHandle } from "../../../live/viewSession";
import type { EditPoint } from "../../../boards/boardCaret";
import { boardCanvasKey } from "../canvas/boardCanvasContext";
import { animateAtlas } from "../../../motion/aiMotion";
import AtlasIcon from "../../AtlasIcon.vue";
import CardSwap from "../cards/CardSwap.vue";
import { Button } from "../../ui/button";
import ErrorState from "../../settings/ErrorState.vue";
import "../text/boardTextSurface.css";
import { hostViewKey } from "./hostView";

/**
 * The live preview of a page card in its „Náhled“ display (spec §3.6): the rendered page, or one section of it (`anchor`), at the
 * card's size, scrollable inside, its rest fading out at the bottom.
 *
 * - It reads the page through the page API (`GET /api/pages/{id}`), which applies the reader's rights, hides password
 *   references and answers the published version to a Reader and the working one to a Writer; previews of one page share the read in flight or younger than `recentMs`, a change reads anew.
 * - It loads and listens only while `visible` (the canvas passes whether the card is on screen; it keeps listening for
 *   `previewLingerMs` after the card leaves the screen, and reloads on its return only when a change came or it stopped): `page:{pageId}` on
 *   `/hubs/live` tells it the page changed (`PageChanged`, no content), and it reloads after the burst settles, cross-fading
 *   to the new content. A reconnect or a revoked subscription reloads too; access is for the API to decide. On a board it
 *   listens to the page object in the board's view session instead (`view`, or the board around it): the same reload on a
 *   change, on a change missed while offline and on a revoked page.
 * - A slow first load shows its placeholder only after `--atlas-motion-defer` (CardSwap); a page the reader may not see shows
 *   the locked card; a section the page no longer has says so and offers the whole page (`wholePage`: the canvas drops the
 *   anchor).
 * - Who may edit the page edits it in place, with no „Upravit“ (3.6, as a page): the board says when (`editing`: a click into the text of
 *   the selected card, a double-click, Enter, F2) and where (`at`). The page's live editor (BoardPageEditor `inline`, loaded only then)
 *   builds unseen over the preview and takes its place once it can take the caret, a section's preview scrolled so the section stays
 *   where it stood; Enter and F2 aim at the start of what the preview shows. Esc or a press outside ends it (`done`); a page this person
 *   may only read, or a board read only, is never entered (`done` at once).
 * - A card of its own over the page (the deployment's product or service, DeploymentBoardCard) passes `select`, what of the page
 *   it shows (null: gone, `missing` says so), and draws that in the default slot; the loading, rights and live updates stay here.
 * Why not PageCard or HistoryRevisionPreview: the card shows a title and an excerpt without content, the history preview
 * renders a stored revision once, without rights, sections or live updates.
 */
const props = withDefaults(defineProps<{
  pageId: string;
  anchor?: string | null;
  visible?: boolean;
  /** The page API; the default asks the server. */
  load?: (pageId: string, signal: AbortSignal) => Promise<Pick<Page, "contentJson"> & Partial<Pick<Page, "canEdit" | "title">>>;
  /** The tab's live connection; the default is the one of the app. */
  live?: Pick<LiveConnection, "route"> & { subscribe(name: string, listener: ChannelListener): { close(): void } };
  /** The view session of the board hosting the card; the default is the one BoardView provides, if any. */
  view?: () => ViewHandle | undefined;
  /** What of the page the default slot draws instead of the rendered page; null when the page no longer has it. */
  select?: (value: MarkdownRichValue) => unknown;
  /** What a missing `select` says. */
  missing?: string;
  /** The board edits the page here (BoardCanvas `editing`), aimed `at` a point (a click, a double-click) or not (Enter, F2). */
  editing?: boolean;
  at?: EditPoint;
}>(), { anchor: null, visible: true, view: undefined, select: undefined, missing: undefined, at: undefined });
const emit = defineEmits<{ wholePage: []; done: [] }>();
// On a board read only (a reader, the lock) the page is not edited from its card either (D10, K13); off a board, the page's own right.
const board = inject(boardCanvasKey, undefined);
const offersEdit = computed(() => editable.value && (board?.canEdit.value ?? true));
// The page's overviews (contacts, issues) at its reader's level.
const plugins = [...previewPlugins, createOverviewReading(() => props.pageId)];

type State = "pending" | "ready" | "missing" | "locked" | "failed";
const theme = useEditorTheme();
const state = ref<State>("pending");
const page = shallowRef<MarkdownRichValue>();
const revision = ref(0);
const editable = ref(false);
const shown = computed(() => (!page.value ? null : props.select ? props.select(page.value) ?? null : props.anchor ? sliceSection(page.value, props.anchor) : page.value));
watch(shown, value => { if (page.value) state.value = value ? "ready" : "missing"; });
const swapState = computed(() => (state.value === "pending" ? "pending" : state.value === "locked" ? "locked" : "ready"));
onErrorCaptured(() => { state.value = "failed"; return false; });

let request: AbortController | undefined;
let stale = true;
// A change was heard, or may have been missed, since the last read: the next one skips the recent reads.
let changedSince = false;
async function fetchPage(fresh = false) {
  request?.abort();
  const controller = request = new AbortController();
  stale = false;
  changedSince = false;
  try {
    const loaded = await (props.load ?? ((pageId, signal) => readPage(pageId, signal, fresh)))(props.pageId, controller.signal);
    if (controller.signal.aborted) return;
    page.value = readDocument(loaded.contentJson);
    editable.value = loaded.canEdit === true;
    revision.value++;
    state.value = shown.value ? "ready" : "missing";
  } catch (error) {
    if (controller.signal.aborted) return;
    page.value = undefined;
    editable.value = false;
    state.value = error instanceof ApiError && (error.status === 403 || error.status === 404) ? "locked" : "failed";
  }
}

async function readPage(pageId: string, signal: AbortSignal, fresh: boolean) {
  const key = pageId.toLowerCase(), known = recent.get(key);
  if (!fresh && known && Date.now() - known.at < recentMs) return known.page as Page;
  const read = await dedupedGet(`page:${key}`, shared => api<Page>(`/pages/${encodeURIComponent(pageId)}`, { signal: shared }), signal);
  recent.set(key, { at: Date.now(), page: read });
  return read;
}

let refresh: ReturnType<typeof setTimeout> | undefined;
function changed(change?: unknown) {
  // A comment on the page does not change what the preview shows.
  if (isCommentsChange(change)) return;
  changedSince = true;
  if (!props.visible) { stale = true; return; }
  clearTimeout(refresh);
  refresh = setTimeout(() => { refresh = undefined; void fetchPage(true); }, previewRefreshDelayMs);
}
let subscription: { close(): void } | undefined;
const channel = (pageId: string) => `page:${pageId.toLowerCase()}`;
const hostView = inject(hostViewKey, undefined);
function listen() {
  const host = (props.view ?? hostView)?.();
  if (host) {
    subscription = { close: host.watch(props.pageId, { changed, missed: changed, revoked: changed }) };
    return;
  }
  const live = props.live ?? liveConnection();
  live.route("PageChanged", payload => {
    const pageId = payload && typeof payload === "object" ? (payload as { pageId?: unknown }).pageId : undefined;
    return typeof pageId === "string" ? channel(pageId) : undefined;
  });
  let subscribed = 0;
  subscription = live.subscribe(channel(props.pageId), {
    message: event => { if (event === "PageChanged") changed(); },
    // After a reconnect changes may have been missed while the line was down.
    subscribed: () => { if (subscribed++) changed(); },
    revoked: changed,
  });
}
let linger: ReturnType<typeof setTimeout> | undefined;
function stop() {
  clearTimeout(refresh);
  clearTimeout(linger);
  subscription?.close();
  subscription = undefined;
}
watch(() => [props.visible, props.pageId] as const, ([visible], previous) => {
  if (previous && previous[1] !== props.pageId) { stop(); stale = true; page.value = undefined; state.value = "pending"; }
  if (!visible) {
    // A change announced meanwhile marks the preview stale (changed()); once no longer listening, nothing would tell it.
    if (refresh !== undefined) { clearTimeout(refresh); refresh = undefined; stale = true; }
    clearTimeout(linger);
    if (subscription) linger = setTimeout(() => { stop(); stale = true; changedSince = true; }, previewLingerMs);
    return;
  }
  clearTimeout(linger);
  if (!subscription) listen();
  if (stale) void fetchPage(changedSince);
}, { immediate: true });
onBeforeUnmount(() => { stop(); request?.abort(); });

const scroller = ref<HTMLElement>();
const entering = computed(() => !!props.editing && offersEdit.value && state.value === "ready" && !props.select);
// Nothing to enter (a page this person may only read, a board read only, a preview that failed): the board's editing ends at once.
watch(() => !!props.editing && state.value !== "pending" && !entering.value, refused => { if (refused) emit("done"); }, { immediate: true });
/** The editor took the preview's place. */
const entered = ref(false);
/** Where Enter and F2 aim: the start of what the preview shows. */
const startPoint = ref<EditPoint>();
/** How far a section's preview scrolled into the whole page when its editor came, undone when the preview comes back. */
let shift = 0;
function enter() {
  const element = scroller.value;
  if (element) {
    const box = element.getBoundingClientRect(), style = getComputedStyle(element);
    const top = box.top + parseFloat(style.paddingTop);
    // The section's heading stays where it stood (the preview began with it), the page above it scrolled away.
    const heading = props.anchor ? element.querySelector(`.board-page-editor [data-heading-id="${CSS.escape(props.anchor)}"]`) : null;
    shift = heading ? heading.getBoundingClientRect().top - top + element.scrollTop : 0;
    element.scrollTop += shift;
    startPoint.value = { x: box.left + parseFloat(style.paddingLeft) + 1, y: top + 1 };
  }
  entered.value = true;
}
watch(entering, on => {
  if (on) return;
  entered.value = false;
  startPoint.value = undefined;
  if (scroller.value && shift) scroller.value.scrollTop = Math.max(0, scroller.value.scrollTop - shift);
  shift = 0;
}, { flush: "post" });
const atEnd = ref(false);
function scrolled() {
  const element = scroller.value;
  if (element) atEnd.value = element.scrollTop + element.clientHeight >= element.scrollHeight - 2;
}
// New content cross-fades with the old one: both lie in one grid cell while one leaves and the other comes.
function fadeIn(el: Element, done: () => void) {
  const animation = revision.value > 1 ? animateAtlas(el, [{ opacity: 0 }, { opacity: 1 }]) : null;
  if (!animation) { done(); return; }
  animation.finished.then(done, done);
}
function fadeOut(el: Element, done: () => void) {
  // The editor that took its place stands where it was: no fade between the two.
  if (entered.value) { done(); return; }
  el.setAttribute("inert", "");
  const animation = animateAtlas(el, [{ opacity: 1 }, { opacity: 0 }], { fill: "forwards" });
  if (!animation) { done(); return; }
  animation.finished.then(done, done);
}
</script>

<template>
  <CardSwap :state="swapState" class="page-preview" data-page-preview :data-state="state">
    <template #pending>
      <div class="page-preview-placeholder" aria-hidden="true"><span /><span /><span /></div>
    </template>
    <template #locked>
      <div class="page-preview-note" data-page-preview-locked>
        <AtlasIcon name="lock" :size="18" />
        <strong>{{ t.preview.unavailable }}</strong>
      </div>
    </template>
    <div v-if="state === 'missing'" class="page-preview-note" data-page-preview-missing>
      <strong>{{ missing ?? t.preview.sectionGone }}</strong>
      <Button v-if="!select" variant="outline" size="sm" class="nodrag" type="button" @click="emit('wholePage')"><AtlasIcon name="file-text" :size="15" />{{ t.preview.wholePage }}</Button>
    </div>
    <div v-else-if="state === 'failed'" class="page-preview-note nodrag">
      <ErrorState :title="t.preview.failed" :retry-label="t.preview.retry" @retry="fetchPage(true)" />
    </div>
    <!-- A card of its own (`select`) has no text to select or scroll through by dragging: a drag over it moves the card, as any other. -->
    <div v-else ref="scroller" class="page-preview-scroll nowheel" :class="{ nodrag: !select }" :data-at-end="atEnd || undefined" tabindex="0"
      :aria-label="t.preview.label" @scroll.passive="scrolled">
      <slot v-if="select && shown" :shown="shown" />
      <Transition v-else :css="false" @enter="fadeIn" @leave="fadeOut">
        <!-- @vue-generic {'rich'} -->
        <MarkdownEditor v-if="shown && !entered" :key="revision" :model-value="(shown as MarkdownRichValue)" profile="rich" :ariaLabel="t.preview.content" :plugins="plugins" readonly
          v-bind="editorChrome" :outline="editorOutline" :show-block-add-button="false" :fullscreenable="false" :class="[theme, 'atlas-document-editor', 'board-text', 'page-preview-content']" />
      </Transition>
      <BoardPageEditor v-if="entering" inline :page-id="pageId" :at="at ?? startPoint" :class="{ 'is-entering': !entered }" @entered="enter" @close="emit('done')" />
    </div>
  </CardSwap>
</template>

<style scoped>
/* The card gives the preview its size; the content scrolls inside it instead of growing the card. */
.page-preview { grid-template-rows: minmax(0, 1fr); font-size: 13px; }
.page-preview :deep(.bcard-layer) { position: relative; grid-template-rows: minmax(0, 1fr); }
.page-preview-scroll { display: grid; min-height: 0; padding: 12px 14px 24px; overflow: auto; overscroll-behavior: contain;
  mask-image: linear-gradient(to bottom, #000 calc(100% - 40px), transparent); }
.page-preview-scroll[data-at-end] { mask-image: none; }
.page-preview-scroll:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.page-preview-scroll > :deep(*) { grid-area: 1 / 1; min-width: 0; }
/* The preview is read: no selected block ring on whatever the editor selected first (contract J2.4). */
.page-preview :deep(.ProseMirror:not([contenteditable="true"]) .ProseMirror-selectednode) { outline: none; box-shadow: none; }
/* The page's editor builds unseen over the preview until it takes its place (BoardPageEditor `entered`). */
.page-preview-scroll > .board-page-editor.is-entering { opacity: 0; pointer-events: none; }
.page-preview-note { display: grid; place-content: center; justify-items: center; gap: 10px; padding: 16px; color: var(--muted); text-align: center; }
.page-preview-note strong { color: var(--text); font-weight: 600; }
.page-preview-placeholder { display: grid; align-content: start; gap: 10px; padding: 16px 14px; }
.page-preview-placeholder span { height: 10px; border-radius: 5px; background: var(--hover); }
.page-preview-placeholder span:first-child { width: 60%; height: 14px; }
.page-preview-placeholder span:last-child { width: 80%; }
</style>
