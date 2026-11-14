<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, useId, watch } from "vue";
import { Button } from "./ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "./ui/tooltip";
import { ApiError, errorMessage } from "../api";
import { personalLibraryApi, type LibraryItem, type LibraryTab } from "../personalLibrary";
import { contentItemLink, isPlainNavigationClick } from "../pageLinks";
import type { TreeNodeType } from "../treeMove";
import AtlasIcon from "./AtlasIcon.vue";
import PageStatusBadge from "./PageStatusBadge.vue";
import LoadingSkeleton from "./LoadingSkeleton.vue";
import { formatDateTime } from "../formatDate";
import ChannelBadge from "./ChannelBadge.vue";
import EmptyState from "./settings/EmptyState.vue";
import ErrorState from "./settings/ErrorState.vue";
import t from "../i18n/components-personal";

// The rows enter one after another with the settings design system's step (--st-stagger).
import "./settings/settings.css";

// `embedded`: inside a page that already has the title and the refresh action (Váš profil → Historie
// návštěv). It drops the own heading and padding; `#empty` and `#error` let that page use its own states.
// `tabbed`: a part of Knihovna under its tabs (refresh 2.5): the page has the title, so the panel's own
// heading is for screen readers only and its description and Aktualizovat form the toolbar line.
const props = defineProps<{ tab: LibraryTab; refreshKey?: number; embedded?: boolean; tabbed?: boolean }>();
/** `type`: a board (module Boards) is listed among favorites and visits like a page, told apart by its icon, and opens as a board. */
const emit = defineEmits<{ select: [id: string, type: TreeNodeType] }>();
const headingId = useId();
// Rows are asked for 20 at a time; the stagger restarts with every batch that Načíst další appends.
const pageSize = 20;
const items = shallowRef<LibraryItem[]>([]);
const loaded = ref(false);
const nextOffset = ref<number | null>(null), loading = ref(false), error = ref("");
// The failed request was the next page of rows that are on the screen: they stay, and the failure is said at the foot.
const moreFailed = ref(false);
// What the part says when it has nothing: the headline and the hint of the shared empty state (EmptyState, the look of
// Prostory, of a space without pages and of Nedávné in Knihovna); the `#empty` slot gets both as one sentence.
const presentation = computed(() => ({ ...t.panel.tabs[props.tab], icon: ({ recent: "recent", favorites: "star", drafts: "drafts", updates: "published" } as const)[props.tab] }));
const emptyText = computed(() => `${presentation.value.emptyTitle}.${presentation.value.emptyHint ? ` ${presentation.value.emptyHint}` : ""}`);
function timestamp(item: LibraryItem) { return props.tab === "recent" ? item.visitedAt : item.publishedAt; }
// The list carries no published version; on the drafts tab a published page is there because it has unpublished changes.
function publishedVersion(item: LibraryItem) { return props.tab === "drafts" || !item.isPublished ? null : item.version; }
let generation = 0, disposed = false;
let abort: AbortController | undefined;

async function load(append = false) {
  if (append && (loading.value || nextOffset.value === null)) return;
  abort?.abort();
  const controller = abort = new AbortController(), current = ++generation;
  const tab = props.tab, offset = append ? nextOffset.value! : 0;
  loading.value = true; error.value = ""; moreFailed.value = false;
  try {
    const result = await personalLibraryApi.list(tab, offset, controller.signal, pageSize);
    if (disposed || current !== generation || props.tab !== tab) return;
    items.value = append ? [...items.value, ...result.items.filter(item => !items.value.some(existing => existing.id === item.id))] : result.items;
    nextOffset.value = result.hasMore ? result.nextOffset : null;
  } catch (reason) {
    if (controller.signal.aborted || disposed || current !== generation) return;
    error.value = errorMessage(reason);
    if (reason instanceof ApiError && [401, 403, 404].includes(reason.status)) {
      items.value = []; nextOffset.value = null;
    }
    // The next page failed (nextOffset is unchanged, so Načíst další stránky asks for that same page again).
    moreFailed.value = append && items.value.length > 0;
  } finally { if (!disposed && current === generation) { loading.value = false; loaded.value = true; } }
}
function select(item: LibraryItem, event: MouseEvent) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  emit("select", item.id, item.type === "board" ? "board" : "page");
}
watch([() => props.tab, () => props.refreshKey], ([tab], [previousTab]) => {
  if (tab !== previousTab) { items.value = []; nextOffset.value = null; loaded.value = false; }
  void load();
});
onMounted(() => { void load(); });
onBeforeUnmount(() => { disposed = true; generation++; abort?.abort(); });
</script>

<template>
  <section class="personal-library settings-ui" :class="{ embedded, tabbed }" :aria-labelledby="embedded ? undefined : headingId" :aria-label="embedded ? presentation.title : undefined" :aria-busy="loading">
    <header v-if="!embedded" class="library-heading">
      <div><component :is="tabbed ? 'h2' : 'h1'" :id="headingId" :class="{ 'sr-only': tabbed }">{{ presentation.title }}</component><p class="help">{{ presentation.description }}</p></div>
      <Button variant="outline" class="library-refresh" :aria-label="t.panel.refresh" :title="t.panel.refreshTitle" :disabled="loading" :aria-busy="loading" @click="load()"><AtlasIcon name="refresh" :size="16" /><span>{{ t.panel.refresh }}</span></Button>
    </header>
    <div class="library-content">
    <slot v-if="error" name="error" :message="error" :busy="loading" :retry="() => load()"><ErrorState v-if="!moreFailed" :title="t.panel.loadFailed(presentation.title)" :text="error" :retry-label="t.panel.retry" :busy="loading" @retry="load()" /></slot>
    <LoadingSkeleton v-if="loading && !loaded" :label="t.panel.loading" />
    <slot v-else-if="!error && !items.length" name="empty" :text="emptyText"><EmptyState :icon="presentation.icon" :title="presentation.emptyTitle" :text="presentation.emptyHint || undefined" /></slot>
    <ul v-if="items.length" class="library-items" :aria-label="presentation.title">
      <li v-for="(item, index) in items" :key="item.id" :style="{ '--i': Math.min(index % pageSize, 8) }">
        <a class="library-item" :href="contentItemLink(item)" :aria-label="item.type === 'board' ? t.panel.openBoard(item.title) : t.panel.openPage(item.title)" :data-library-type="item.type ?? 'page'" @click="select(item, $event)">
          <span class="library-document-icon"><AtlasIcon :name="item.type === 'board' ? 'layout-dashboard' : 'page'" :size="22" /></span>
          <span class="library-copy"><strong>{{ item.title }}</strong><span class="library-meta">{{ item.spaceName }}<template v-if="item.parentTitle"><span aria-hidden="true"> › </span>{{ item.parentTitle }}</template><template v-if="!item.isPublished || item.version > (publishedVersion(item) ?? 0)"><span aria-hidden="true"> · </span><PageStatusBadge hide-published :is-published="item.isPublished" :version="item.version" :published-version="publishedVersion(item)" /></template></span><ChannelBadge v-if="tab === 'updates'" class="library-channel" :channel="item.publicationChannel" :token="item.publicationApiToken" detailed />
          <span v-if="tab === 'updates' && item.changeSummary" class="library-change-summary" :title="item.changeSummary">{{ item.changeSummary }}</span></span>
          <span v-if="item.isFavorite" class="library-favorite" :aria-label="item.type === 'board' ? t.panel.favoriteBoard : t.panel.favoritePage"><AtlasIcon name="star" :size="16" /></span>
          <Tooltip v-if="timestamp(item)"><TooltipTrigger as-child><time :datetime="timestamp(item)!">{{ formatDateTime(timestamp(item)) }}</time></TooltipTrigger><TooltipContent>{{ tab === 'recent' ? t.panel.lastOpened : t.panel.lastPublished }}</TooltipContent></Tooltip>
          <AtlasIcon name="right" :size="16" />
        </a>
      </li>
    </ul>
    </div>
    <footer class="library-footer" :class="{ 'has-failure': moreFailed && !$slots.error }">
      <p v-if="items.length" class="help" role="status">{{ t.panel.loaded(items.length) }}</p>
      <!-- A failed next page, as in 2.6.0: the rows stay and the retry below asks for the same page; a page that gives its own #error says it itself. -->
      <p v-if="moreFailed && !$slots.error" class="library-more-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p>
      <Button variant="outline" v-if="nextOffset !== null" class="library-more" :disabled="loading" :aria-busy="loading" @click="load(true)"><AtlasIcon name="refresh" :size="16" />{{ t.panel.loadMore }}</Button>
    </footer>
  </section>
</template>

<style scoped>
.personal-library { width: min(1020px, 100%); margin: 0 auto; padding: clamp(20px, 3vw, 36px); box-sizing: border-box; color: var(--text); }
.library-heading { display: flex; align-items: flex-start; justify-content: space-between; gap: 20px; margin-bottom: 26px; }
.library-heading h1 { font-size: clamp(25px, 3vw, 35px); letter-spacing: -.035em; margin: 4px 0 10px; }
.library-heading .eyebrow { margin: 0; }
.library-heading .help { max-width: 560px; line-height: 1.6; }
.library-heading button { flex: 0 0 auto; margin-top: 9px; }
.library-content { min-height: 304px; }
.library-items { --library-items-radius: var(--radius-lg); list-style: none; padding: 0; margin: 0; border: 1px solid var(--line); border-radius: var(--library-items-radius); background: var(--surface); overflow: hidden; }
/* The first and the last row take the inner radius of the frame (its radius less its 1 px border), so their focus ring and
   hover follow the frame instead of being cut by it with a square inner corner. */
.library-items li:first-child .library-item { border-top-left-radius: calc(var(--library-items-radius) - 1px); border-top-right-radius: calc(var(--library-items-radius) - 1px); }
.library-items li:last-child .library-item { border-bottom-left-radius: calc(var(--library-items-radius) - 1px); border-bottom-right-radius: calc(var(--library-items-radius) - 1px); }
.library-items li + li { border-top: 1px solid var(--line); }
.library-item { display: flex; align-items: center; gap: 12px; width: 100%; padding: 13px 16px; background: transparent; border: 0; color: var(--text); text-align: left; text-decoration: none; cursor: pointer; box-sizing: border-box; }
@media (hover: hover) and (pointer: fine) {
  .library-item:hover { background: var(--accent-soft); }
}
/* The rows fill a clipped, rounded list: draw the focus ring inside them, as the sidebar does. */
.library-items .library-item:focus-visible:not(.md-editor):not(.md-editor *) { outline-offset: -2px; }
.library-document-icon { display: grid; place-items: center; flex: 0 0 auto; width: 28px; height: 32px; color: var(--muted); }
.library-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 6px; }
.library-copy strong { font-size: 14px; font-weight: 600; overflow-wrap: anywhere; }
.library-meta { font-size: 12px; color: var(--muted); }
.library-change-summary { font-size: 12px; color: var(--muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.library-favorite { color: var(--accent); font-size: 18px; }
.library-item time { color: var(--muted); font-size: 11px; flex-shrink: 0; }
.library-item > svg { color: var(--muted); flex: 0 0 auto; }
.library-footer { display: flex; justify-content: space-between; align-items: center; gap: 18px; margin-top: 18px; }
.library-footer.has-failure { flex-wrap: wrap; gap: 8px 18px; }
/* The quiet failure line of the app (as in the page list of a space): the reason, and the retry (Načíst další stránky) beside it. */
.library-more-error { display: flex; flex: 1 1 240px; align-items: center; gap: 8px; margin: 0; color: var(--error); font-size: 13px; line-height: 1.55; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.library-more-error > svg { flex: none; }
.personal-library.embedded { width: 100%; max-width: none; padding: 0; }
.embedded .library-content { min-height: 0; display: grid; gap: 12px; }
.embedded .library-items { --library-items-radius: var(--st-radius, var(--radius-lg)); box-shadow: var(--st-card-shadow, none); }
.embedded .library-footer:not(:has(> *)) { display: none; }
/* Knihovna (refresh 2.5): no frame of its own, a toolbar line, and the rows of an aligned list (board 02); the rows are the
   same in the history of visits of the profile (embedded), which lists the same pages. */
.personal-library.tabbed { width: 100%; max-width: none; padding: 0; }
.tabbed .library-heading { align-items: center; margin-bottom: 16px; }
.tabbed .library-heading .help { margin: 0; color: var(--muted); font-size: 14px; line-height: 1.45; }
.tabbed .library-heading button { margin-top: 0; min-height: 36px; border-radius: 10px; }
/* The room a part keeps while it loads or is empty belongs to the part, not to the list inside it: the footer ("Načtené
   stránky: 1.") then sits right under a short list instead of 200 px below it. 306 px = heading 36 + 16, list 240, footer gap 14. */
.personal-library.tabbed { min-height: 306px; }
.tabbed .library-content { min-height: 0; }
.tabbed .library-items { --library-items-radius: 14px; }
.tabbed .library-items li { animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards; animation-delay: calc(var(--i, 0) * var(--st-stagger)); }
:is(.tabbed, .embedded) .library-items li + li { border-top-color: color-mix(in srgb, var(--line) 50%, var(--surface)); }
:is(.tabbed, .embedded) .library-item { min-height: 56px; padding: 10px 16px; gap: 12px; }
@media (hover: hover) and (pointer: fine) {
  :is(.tabbed, .embedded) .library-item:hover { background: color-mix(in srgb, var(--hover) 45%, transparent); }
}
:is(.tabbed, .embedded) .library-document-icon { width: 30px; height: 30px; border-radius: 9px; background: color-mix(in srgb, var(--line) 30%, var(--surface)); }
:is(.tabbed, .embedded) .library-document-icon > svg { width: 16px; height: 16px; }
:is(.tabbed, .embedded) .library-copy { gap: 2px; }
:is(.tabbed, .embedded) .library-copy strong { font-size: 14px; font-weight: 600; }
:is(.tabbed, .embedded) .library-meta { font-size: 12.5px; }
/* The status chip is taller than the line it stands in and would grow its row by 2.6 px (a row of Koncept 66.7 px, one
   without 64.1 px): it is centred on the line with the surplus taken from its margin, so every row has one height. */
:is(.tabbed, .embedded) .library-meta :deep([data-status]) { margin-block: -3px; vertical-align: middle; }
:is(.tabbed, .embedded) .library-item time { font-size: 12.5px; font-variant-numeric: tabular-nums; }
.tabbed .library-footer { margin-top: 14px; }
@media (max-width: 680px) { :is(.tabbed, .embedded) .library-item { padding: 12px; } :is(.tabbed, .embedded) .library-item time { padding-left: 42px; } }
@media (max-width: 680px) { .personal-library { padding: 20px 16px; } .library-heading { gap: 12px; margin-bottom: 18px; } .library-heading h1 { margin-top: 0; } .library-refresh { min-width: 44px; min-height: 44px; margin-top: 0 !important; padding: 8px; } .library-refresh span { display: none; } .library-item { flex-wrap: wrap; padding: 12px; gap: 8px; } .library-item time { width: 100%; padding-left: 36px; } .library-item > svg { display: none; } }
</style>
