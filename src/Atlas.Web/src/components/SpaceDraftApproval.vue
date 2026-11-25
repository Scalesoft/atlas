<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import { api, ApiError, errorMessage, type Appearance, type Page } from '../api';
import { historyApi, type PageRevision } from '../history';
import EmptyState from './settings/EmptyState.vue';
import ErrorState from './settings/ErrorState.vue';
import FilterMenu from './settings/FilterMenu.vue';
import SearchField from './settings/SearchField.vue';
import type { ChoiceOption } from './settings/types';
import { formatDateTime } from '../formatDate';
import HistoryChanges from './HistoryChanges.vue';
import HistoryRevisionPreview from './HistoryRevisionPreview.vue';
import AppDialog from './AppDialog.vue';
import LoadingRegion from './LoadingRegion.vue';
import LoadingSkeleton from './LoadingSkeleton.vue';
import LoadingNote from './settings/LoadingNote.vue';
import OptionPills from './settings/OptionPills.vue';
import t from '../i18n/components-spaces';
import { Button } from './ui/button';
import ChangeSummaryField from './publishing/ChangeSummaryField.vue';
import { approveRequest, normalizeChangeSummary } from '../publishing/publication';
import { createPresenceRoster } from '../live/objectPresence';
import { viewSessions } from '../live/viewSession';
import { locale } from '../i18n';

interface Draft {
  id: string;
  spaceId: string;
  title: string;
  version: number;
  revisionId: string;
  publishedRevisionId: string | null;
  publishedVersion: number | null;
  authorId: string;
  authorName: string;
  changedAt: string;
}
type Kind = 'all' | 'new' | 'changes';
const props = defineProps<{ spaceId: string; dark: boolean; accent: Appearance['accent'] }>();
const emit = defineEmits<{ published: [spaceId: string] }>();
const items = ref<Draft[]>([]);
const knownAuthors = ref<Record<string, string>>({});
const total = ref(0);
const query = ref('');
const appliedQuery = ref('');
const authorId = ref('');
const kind = ref<Kind>('all');
const selected = ref<string[]>([]);
const loading = ref(false), loadingMore = ref(false), busy = ref(false), loaded = ref(false);
const requiresReload = ref(false);
const listError = ref(''), failure = ref<Record<string, string>>({});
const confirming = ref(false), completed = ref(0), attempted = ref(0);
// One optional change summary for the whole approval; it is written to every approved revision.
const changeSummary = ref('');
const summaryCheck = computed(() => normalizeChangeSummary(changeSummary.value));
const previewItem = ref<Draft>();
const preview = shallowRef<PageRevision>();
const previous = shallowRef<PageRevision | null>(null);
const previewLoading = ref(false), previewError = ref('');
const previewMode = ref<'version' | 'changes'>('version');
const previewOptions = (): ChoiceOption[] => [{ value: 'version', label: t.approval.wholeVersion }, { value: 'changes', label: t.approval.compareChanges }];
// 3.6 autosave saves a revision every few seconds, so an approval can meet a newer draft (409 draft_changed). The row is read again and
// its newer revision approved only from its preview, compared with the revision the admin saw; never on its own.
const renewed = ref<Record<string, { seen: string; summary: string | null }>>({});
// "právě se upravuje": the draft changed in the last minute; for the row in the preview (where a newer version is approved) also
// whoever edits its page now, from presence in its view `page:{id}`. One view at a time, never one per row of the queue.
const recentMs = 60_000;
const now = ref(Date.now());
const touched = ref<Record<string, number>>({});
const editingNow = ref(false);
const roster = createPresenceRoster(() => { editingNow.value = !!previewItem.value && roster.of(previewItem.value.id).length > 0; });
const clock = setInterval(() => { now.value = Date.now(); }, 15_000);
let closeView: (() => void) | undefined;
function beingEdited(item: Draft) {
  return (editingNow.value && previewItem.value?.id === item.id) || now.value - Math.max(Date.parse(item.changedAt), touched.value[item.id] ?? 0) < recentMs;
}
watch(() => previewItem.value?.id, id => {
  closeView?.(); closeView = undefined; editingNow.value = false;
  if (!id) return;
  const view = viewSessions().open(`page:${id}`);
  view.watch(id, { presence: presence => roster.receive(presence), changed: () => { touched.value = { ...touched.value, [id]: Date.now() }; now.value = Date.now(); } });
  closeView = () => view.close();
});
let disposed = false, listGeneration = 0, previewGeneration = 0;
let listAbort: AbortController | undefined, previewAbort: AbortController | undefined;
const eligible = computed(() => items.value.filter(item => !failure.value[item.id]));
const selection = computed(() => eligible.value.filter(item => selected.value.includes(item.id)));
const authors = computed(() => {
  return Object.entries(knownAuthors.value).sort((a, b) => a[1].localeCompare(b[1], locale));
});
const authorOptions = computed<ChoiceOption[]>(() => [{ value: '', label: t.approval.allAuthors, icon: 'users' }, ...authors.value.map(([id, name]) => ({ value: id, label: name, icon: 'account' }))]);
const kindOptions = (): ChoiceOption[] => [
  { value: '', label: t.approval.allDrafts, icon: 'drafts' },
  { value: 'new', label: t.approval.newPages, icon: 'file-plus' },
  { value: 'changes', label: t.approval.changesOfPublished, icon: 'history' },
];
// FilterMenu treats "" as no filter; the draft kind has "all" for that.
const kindChoice = computed({ get: () => (kind.value === 'all' ? '' : kind.value), set: (value: string) => { kind.value = (value || 'all') as Kind; } });
// A choice applies at once; the title search applies on Enter (or with the other filters).
watch([authorId, kind], () => applyFilters());
const hasMore = computed(() => items.value.length < total.value);
const queryPending = computed(() => query.value.trim() !== appliedQuery.value);
const failedCount = computed(() => Object.keys(failure.value).length);
function listPath(offset: number) {
  const params = new URLSearchParams({ offset: String(offset), limit: '100', kind: kind.value });
  if (appliedQuery.value) params.set('q', appliedQuery.value);
  if (authorId.value) params.set('authorId', authorId.value);
  return `/spaces/${encodeURIComponent(props.spaceId)}/drafts?${params}`;
}
async function load(more = false) {
  if (disposed || busy.value || (more && (loading.value || loadingMore.value || confirming.value || queryPending.value || !hasMore.value))) return;
  const generation = ++listGeneration;
  listAbort?.abort();
  const controller = new AbortController();
  listAbort = controller;
  if (more) loadingMore.value = true;
  else { closePreview(); loading.value = true; selected.value = []; failure.value = {}; renewed.value = {}; confirming.value = false; completed.value = 0; attempted.value = 0; }
  listError.value = '';
  try {
    const response = await api<{ items: Draft[]; total: number; authors?: { id: string; name: string }[] }>(listPath(more ? items.value.length : 0), { signal: controller.signal });
    if (disposed || generation !== listGeneration) return;
    items.value = more ? [...items.value, ...response.items] : response.items;
    total.value = response.total;
    for (const author of response.authors ?? []) knownAuthors.value[author.id] = author.name;
    for (const item of response.items) knownAuthors.value[item.authorId] = item.authorName;
    requiresReload.value = false;
    loaded.value = true;
  } catch (reason) {
    if (!disposed && generation === listGeneration && !controller.signal.aborted) listError.value = errorMessage(reason);
  } finally {
    if (!disposed && generation === listGeneration) { loading.value = false; loadingMore.value = false; }
  }
}
function applyFilters() {
  if (busy.value) return;
  appliedQuery.value = query.value.trim();
  selected.value = [];
  void load();
}
function toggleAll(event: Event) {
  selected.value = (event.target as HTMLInputElement).checked ? eligible.value.map(item => item.id) : [];
}
function closePreview() {
  previewGeneration++; previewAbort?.abort(); previewItem.value = undefined;
  preview.value = undefined; previous.value = null; previewError.value = '';
}
async function showPreview(item: Draft) {
  closePreview();
  previewItem.value = item;
  previewMode.value = 'version';
  previewLoading.value = true;
  const generation = ++previewGeneration;
  const controller = new AbortController();
  previewAbort = controller;
  try {
    // A renewed row compares with the revision the admin saw before, so "Porovnat změny" shows what was typed since.
    const base = renewed.value[item.id]?.seen ?? item.publishedRevisionId;
    if (renewed.value[item.id]) previewMode.value = 'changes';
    const [draft, published] = await Promise.all([
      historyApi.revision(item.id, item.revisionId, controller.signal),
      base ? historyApi.revision(item.id, base, controller.signal) : Promise.resolve(null),
    ]);
    if (disposed || generation !== previewGeneration) return;
    preview.value = draft; previous.value = published;
  } catch (reason) {
    if (!disposed && generation === previewGeneration && !controller.signal.aborted) previewError.value = errorMessage(reason);
  } finally {
    if (!disposed && generation === previewGeneration) previewLoading.value = false;
  }
}
function reviewSelection() {
  if (!busy.value && !loading.value && !loadingMore.value && !requiresReload.value && selection.value.length) confirming.value = true;
}
async function approve() {
  if (disposed || busy.value || loading.value || loadingMore.value || requiresReload.value || !confirming.value || !selection.value.length || summaryCheck.value.error) return;
  const summary = summaryCheck.value.value;
  // Snapshot each selected row so an intervening list update never changes the reviewed revision.
  const batchSpace = props.spaceId;
  const batch = selection.value.map(item => ({ ...item }));
  listGeneration++; listAbort?.abort();
  confirming.value = false; busy.value = true;
  failure.value = {}; completed.value = 0; attempted.value = 0;
  try {
    for (const item of batch) {
      if (disposed || props.spaceId !== batchSpace) break;
      attempted.value++;
      try {
        await api<Page>(`/spaces/${encodeURIComponent(batchSpace)}/drafts/${encodeURIComponent(item.id)}/approve`, {
          method: 'POST', body: JSON.stringify(approveRequest(item.version, item.revisionId, summary)),
        });
        if (disposed || props.spaceId !== batchSpace) break;
        completed.value++;
        items.value = items.value.filter(row => row.id !== item.id);
        total.value = Math.max(0, total.value - 1);
        selected.value = selected.value.filter(id => id !== item.id);
        if (previewItem.value?.id === item.id) closePreview();
      } catch (reason) {
        if (disposed || props.spaceId !== batchSpace) break;
        const stale = reason instanceof ApiError && (reason.status === 409 || reason.status === 404);
        failure.value = { ...failure.value, [item.id]: stale
          ? t.approval.staleFailure(item.title, errorMessage(reason))
          : t.approval.uncertainFailure(item.title, errorMessage(reason)) };
        selected.value = selected.value.filter(id => id !== item.id);
        if (!stale) { requiresReload.value = true; break; }
        if (changedAgain(reason)) await reloadRow(batchSpace, item, summary);
      }
    }
  } finally {
    if (!disposed) {
      busy.value = false;
      if (completed.value) { changeSummary.value = ''; emit('published', batchSpace); }
      if (props.spaceId !== batchSpace) void load();
    }
  }
}
function changedAgain(reason: unknown) {
  return reason instanceof ApiError && reason.code === 'draft_changed';
}
/** Reads the one row again after draft_changed; its newer revision waits for "Schválit novou verzi" from its preview. */
async function reloadRow(space: string, seen: Draft, summary: string | null) {
  try {
    const params = new URLSearchParams({ pageId: seen.id, limit: '1' });
    const response = await api<{ items: Draft[] }>(`/spaces/${encodeURIComponent(space)}/drafts?${params}`);
    const row = response.items[0];
    if (disposed || props.spaceId !== space || !row || !items.value.some(item => item.id === seen.id)) return;
    items.value = items.value.map(item => (item.id === row.id ? row : item));
    renewed.value = { ...renewed.value, [row.id]: { seen: seen.revisionId, summary } };
    failure.value = { ...failure.value, [row.id]: t.approval.changedMeanwhile(row.title, seen.version, row.version) };
  } catch {
    // The row keeps its failure and "Obnovit seznam".
  }
}
function forget(id: string) {
  const { [id]: _failure, ...failures } = failure.value;
  const { [id]: _renewed, ...rest } = renewed.value;
  failure.value = failures; renewed.value = rest;
}
/** Approves the renewed row shown in the preview: exactly the revision the admin has just seen there. */
async function approveRenewed() {
  const item = previewItem.value, state = item && renewed.value[item.id];
  if (disposed || !item || !state || !preview.value || busy.value) return;
  const space = props.spaceId;
  busy.value = true;
  try {
    await api<Page>(`/spaces/${encodeURIComponent(space)}/drafts/${encodeURIComponent(item.id)}/approve`, {
      method: 'POST', body: JSON.stringify(approveRequest(item.version, item.revisionId, state.summary)),
    });
    if (disposed || props.spaceId !== space) return;
    closePreview(); forget(item.id);
    items.value = items.value.filter(row => row.id !== item.id);
    total.value = Math.max(0, total.value - 1);
    emit('published', space);
  } catch (reason) {
    if (disposed || props.spaceId !== space) return;
    closePreview(); forget(item.id);
    failure.value = { ...failure.value, [item.id]: t.approval.staleFailure(item.title, errorMessage(reason)) };
    if (changedAgain(reason)) await reloadRow(space, item, state.summary);
  } finally {
    if (!disposed) busy.value = false;
  }
}
watch(() => props.spaceId, () => {
  listGeneration++; listAbort?.abort(); closePreview(); selected.value = [];
  knownAuthors.value = {}; items.value = []; total.value = 0; loaded.value = false;
  void load();
});
watch(query, () => { selected.value = []; confirming.value = false; });
onBeforeUnmount(() => {
  disposed = true; listGeneration++; previewGeneration++; listAbort?.abort(); previewAbort?.abort();
  clearInterval(clock); closeView?.(); roster.dispose();
});
void load();
</script>

<template>
  <div class="draft-approval" :aria-busy="loading || busy">
    <p class="approval-intro">{{ t.approval.intro }}</p>
    <!-- One filter line (content first); the title search applies on Enter. -->
    <form class="approval-filters" role="search" :aria-label="t.approval.filterLabel" @submit.prevent="applyFilters">
      <SearchField v-model="query" :label="t.approval.search" :placeholder="t.approval.searchPlaceholder" :disabled="busy" class="approval-search" />
      <FilterMenu v-model="authorId" :options="authorOptions" :label="t.approval.author" icon="users" data-draft-author />
      <FilterMenu v-model="kindChoice" :options="kindOptions()" :label="t.approval.type" icon="drafts" data-draft-kind />
    </form>
    <ErrorState v-if="listError" :title="t.approval.loadFailed" :text="listError" :retry-label="t.approval.retry" :busy="busy" @retry="load()" />
    <template v-if="!listError || loaded">
      <div class="approval-summary">
        <span class="approval-count">{{ loaded ? t.approval.count(items.length, total) : '' }}<LoadingNote inline :active="loaded && loading" :label="t.approval.searching" /></span>
        <label v-if="eligible.length"><input type="checkbox" :aria-label="t.approval.selectLoadedAria" :checked="selection.length === eligible.length" :disabled="busy || loading || loadingMore" @change="toggleAll" /> {{ t.approval.selectLoaded }}</label>
      </div>
      <LoadingRegion :loading="loading" :ready="loaded" :rows="4" row-height="64px" :label="t.approval.loading">
      <TransitionGroup v-if="items.length" tag="div" name="approval" class="approval-rows atlas-stagger">
        <article v-for="item in items" :key="item.id" class="approval-row">
          <label class="approval-check"><input v-model="selected" type="checkbox" :value="item.id" :aria-label="t.approval.select(item.title)" :disabled="busy || !!failure[item.id]" /></label>
          <div class="approval-identity"><strong>{{ item.title }}</strong><small>{{ item.authorName }} · {{ formatDateTime(item.changedAt) }} · v{{ item.version }}</small></div>
          <span class="approval-kind">{{ item.publishedRevisionId ? t.approval.kindChanges : t.approval.kindNew }}</span>
          <span v-if="beingEdited(item)" class="approval-live" data-being-edited>{{ t.approval.beingEdited }}</span>
          <Button variant="outline" size="sm" :aria-label="t.approval.previewOf(item.title)" :disabled="busy" @click="showPreview(item)">{{ t.approval.preview }}</Button>
          <p v-if="failure[item.id]" role="alert" class="approval-row-error">{{ failure[item.id] }} <Button v-if="renewed[item.id]" size="sm" data-approve-renewed :disabled="busy" @click="showPreview(item)">{{ t.approval.approveNew }}</Button> <Button variant="outline" size="sm" @click="load()">{{ t.approval.refreshList }}</Button></p>
        </article>
      </TransitionGroup>
      <EmptyState v-else icon="circle-check" :title="t.approval.noDrafts" :text="t.approval.noDraftsText" />
      <Button v-if="hasMore" variant="outline" :aria-busy="loadingMore" :disabled="loading || loadingMore || busy || confirming || queryPending" @click="load(true)">{{ t.approval.loadMore }}</Button>
      </LoadingRegion>
      <div class="approval-actions"><span>{{ t.approval.selected(selection.length) }}</span><Button data-review-selection :disabled="!selection.length || busy || loading || loadingMore || requiresReload" @click="reviewSelection">{{ t.approval.reviewSelection }}</Button></div>
      <AppDialog :title="t.approval.confirmTitle" :open="confirming" @close="confirming = false">
        <div class="approval-confirm" role="group" :aria-label="t.approval.confirmLabel">
        <strong>{{ t.approval.publishQuestion(selection.length) }}</strong>
        <p>{{ t.approval.confirmText }}</p>
        <ul><li v-for="item in selection" :key="item.id">{{ item.title }} · v{{ item.version }}</li></ul>
        <ChangeSummaryField v-model="changeSummary" :error="summaryCheck.error" :disabled="busy" :help="selection.length > 1 ? t.approval.sameSummary : undefined" />
        </div>
        <template #footer>
          <Button variant="outline" @click="confirming = false">{{ t.approval.back }}</Button>
          <Button data-confirm-approval :disabled="busy || !!summaryCheck.error" @click="approve">{{ t.approval.approveAndPublish }}</Button>
        </template>
      </AppDialog>
      <p v-if="attempted" role="status" class="notice">{{ t.approval.progress(busy, completed, failedCount, attempted) }}</p>
    </template>
    <AppDialog :open="!!previewItem" :title="previewItem?.title ?? t.approval.previewTitle" size="wide" @close="closePreview">
      <div class="approval-preview" :aria-label="t.approval.previewTitle">
      <p class="help">{{ t.approval.previewMeta(previewItem?.version) }}<span v-if="previewItem && beingEdited(previewItem)" class="approval-live" data-preview-being-edited>{{ t.approval.beingEditedDot }}</span></p>
      <LoadingSkeleton v-if="previewLoading" :rows="6" row-height="44px" :label="t.approval.loadingPreview" />
      <p v-else-if="previewError" role="alert" class="notice error">{{ previewError }}</p>
      <template v-else-if="preview">
        <OptionPills segmented class="approval-preview-tabs" :model-value="previewMode" :options="previewOptions()" :label="t.approval.previewTitle" hide-label @update:model-value="previewMode = $event as typeof previewMode" />
        <HistoryRevisionPreview v-if="previewMode === 'version'" :key="preview.id" :content-json="preview.contentJson" :markdown="preview.markdown" :page-id="preview.pageId" :dark="dark" :accent="accent" />
        <HistoryChanges v-else :revision="preview" :previous="previous" :dark="dark" :accent="accent" />
      </template>
      </div>
      <template v-if="previewItem && renewed[previewItem.id]" #footer>
        <Button variant="outline" @click="closePreview">{{ t.approval.back }}</Button>
        <Button data-confirm-renewed :disabled="busy || previewLoading || !preview" @click="approveRenewed">{{ t.approval.approveNew }}</Button>
      </template>
    </AppDialog>
  </div>
</template>

<style scoped>
/* The space's tab stage already keeps 20 px under the tabs (Stránky and O prostoru start there); the panel's own 20 px
   put its first line twice as far down as the other two tabs. */
.draft-approval { min-width: 0; }
.approval-intro { margin-top: 0; color: var(--muted); font-size: 13px; }
.approval-filters { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin: 14px 0 10px; }
.approval-search { flex: 1 1 260px; }
.approval-summary, .approval-actions { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 12px; padding: 12px 4px; color: var(--muted); font-size: 12px; }
.approval-summary { border-bottom: 1px solid var(--line); }
.approval-count { display: inline-flex; align-items: center; gap: 12px; }
.approval-summary label { display: flex; align-items: center; gap: 8px; cursor: pointer; }
input[type=checkbox] { width: 16px; height: 16px; accent-color: var(--accent); }
/* Rows arrive one after another; an approved row fades away in place and the rest close the gap. */
.approval-rows { position: relative; }
.approval-rows > .approval-leave-active { position: absolute; inset-inline: 0; animation: st-fade-out var(--atlas-motion-base) var(--atlas-ease-out) both; pointer-events: none; }
.approval-rows > .approval-move { transition: transform var(--atlas-motion-base) var(--atlas-ease-inout); }
.approval-row { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; padding: 12px 4px; border-bottom: 1px solid var(--line); }
.approval-check { display: grid; place-items: center; width: 28px; }
.approval-identity { flex: 1; min-width: 180px; display: grid; gap: 4px; overflow-wrap: anywhere; font-size: 14px; }
.approval-identity small { color: var(--muted); font-size: 11px; }
.approval-kind, .approval-live { color: var(--muted); font-size: 11px; }
.approval-live { font-style: italic; }
.approval-row-error { flex-basis: 100%; margin: 0 0 0 40px; color: var(--error); font-size: 12px; }
/* The dialog pads its own content (24 px) and holds its buttons in the footer, as every other dialog does. */
.approval-confirm p { color: var(--muted); font-size: 12px; }
.approval-confirm ul { max-height: min(40vh, 320px); overflow: auto; padding-left: 20px; font-size: 13px; }
.approval-actions { position: sticky; bottom: 0; z-index: 2; padding: 12px; background: var(--surface); border: 1px solid var(--line); border-radius: 10px; box-shadow: var(--shadow); }
.approval-preview { min-width: 0; max-height: min(75vh, 760px); overflow: auto; }
.approval-preview-tabs { margin-bottom: 12px; }
@media (max-width: 640px) { .approval-search { flex-basis: 100%; } .approval-row { gap: 8px; } }
</style>
