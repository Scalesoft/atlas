<script setup lang="ts">
import type { Appearance } from "../api";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from "vue";
import { lazyComponent } from "../lazyComponent";
import { Button } from "./ui/button";
import AtlasIcon from "./AtlasIcon.vue";
import HistoryChanges from "./HistoryChanges.vue";
import { compareHistoryTextAsync, type HistoryTextDiff } from "../historyDiff";
import { ApiError, errorMessage, type Page, type Space } from "../api";
import { downloadContentExport, rememberedCoverDetails } from "../contentExport";
import { historyApi, type PageHistory, type PageRevision, type PageRevisionSummary } from "../history";
import { bucketKey, describeChanges, matchesFilter, type HistoryFilter } from "./history/versionSummary";
import HistoryVersionBody from "./history/HistoryVersionBody.vue";
import AppDialog from "./AppDialog.vue";
import LoadingRegion from "./LoadingRegion.vue";
import LoadingSkeleton from "./LoadingSkeleton.vue";
import { useSegmentedThumb } from "./library/segmentedThumb";
import InlineReveal from "./settings/InlineReveal.vue";
import EmptyState from "./settings/EmptyState.vue";
import ErrorState from "./settings/ErrorState.vue";
import SearchField from "./settings/SearchField.vue";
import LoadingNote from "./settings/LoadingNote.vue";
import t from "../i18n/components-history";
import { formatDate, formatDateTime } from "../formatDate";
import ChannelBadge from "./ChannelBadge.vue";
import { channelSearchText, channelText } from "../channel";
import { scrollBehavior } from "../motion/aiMotion";

const HistoryRevisionPreview = lazyComponent(() => import("./HistoryRevisionPreview.vue"));
const HistoryActivityChart = lazyComponent(() => import("./history/HistoryActivityChart.vue"));
const props = defineProps<{
  page: Page;
  /** A specific version chosen in the article dock; the regular History entry leaves this empty. */
  initialRevision?: PageRevisionSummary;
  spaceRole: Space["role"];
  isWikiAdmin: boolean;
  dark: boolean;
  accent: Appearance["accent"];
  beforeRestore: () => boolean | Promise<boolean>;
}>();
const emit = defineEmits<{ close: []; restored: [page: Page] }>();
const dialogOpen = ref(true);
const items = shallowRef<PageRevisionSummary[]>([]);
// What is shown: the selected row (a version, or one of its steps) and its summary, which the revision itself may not carry.
const selectedId = ref<string>();
const selectedStep = ref(false);
const selectedItem = shallowRef<PageRevisionSummary>();
let selectedPredecessor: string | null = null;
const preview = shallowRef<PageRevision>();
const previous = shallowRef<PageRevision | null>(null);
const previewDiff = shallowRef<HistoryTextDiff>();
const mode = ref<"changes" | "version">("changes");
const restoreOpen = ref(false);
const author = ref("");
const onlyDeletions = ref(false);
const kind = ref<HistoryFilter>("all");
const kinds: { id: HistoryFilter; label: string }[] = [{ id: "all", label: t.panel.all }, { id: "published", label: t.panel.published }, { id: "named", label: t.panel.named }, { id: "ai", label: t.panel.ai }];
const filtering = ref(false);
const filterError = ref("");
const versionsElement = ref<HTMLElement>();
const changesView = ref<{ scrollToHeading: (text: string) => boolean }>();
// The raised segment of Změny | Celá verze (and of the version filter) is one thumb that glides to the chosen option (segmentedThumb.ts).
const viewTrack = ref<HTMLElement>();
const viewThumb = useSegmentedThumb(viewTrack, '[aria-pressed="true"]', mode);
const kindTrack = ref<HTMLElement>();
const kindThumb = useSegmentedThumb(kindTrack, '[aria-pressed="true"]', kind);
// Steps (the saved checkpoints) of expanded versions, loaded lazily by `group`.
type StepState = { items: PageRevisionSummary[]; loading: boolean; error: string };
const expanded = ref<string[]>([]);
const stepState = shallowRef<Record<string, StepState>>({});
// Removed lines come from the version's stored summary, so this filter downloads no revisions. Old rows have no summary and do not match.
const filteredItems = computed(() => items.value.filter(item => (!author.value || `${item.authorName} ${channelSearchText(item.channel)}`.toLocaleLowerCase("cs").includes(author.value.toLocaleLowerCase("cs"))) && matchesFilter(item, kind.value) && (!onlyDeletions.value || (item.changes?.removed ?? 0) > 0)));
const groups = computed(() => {
  const result: { day: string; items: PageRevisionSummary[] }[] = [];
  for (const item of filteredItems.value) {
    const day = formatDate(item.activeFrom) || t.panel.unknownDate;
    if (result.at(-1)?.day !== day) result.push({ day, items: [] });
    result.at(-1)!.items.push(item);
  }
  return result;
});
const nextBeforeVersion = ref<number | null>(null);
const headVersion = ref(props.page.version);
const loading = ref(false), previewLoading = ref(false), busy = ref(false);
const listError = ref(""), previewError = ref(""), restoreError = ref("");
const exporting = ref(false), exportError = ref("");
const conflict = ref(false), notice = ref("");
// The page's own right (PageHistoryAccess asks EditContent): a Writer grant on the one page needs no space role.
const allowed = computed(() => !!props.page.canEdit);
const filterActive = computed(() => !!author.value || onlyDeletions.value || kind.value !== "all");
const selectedChanges = computed(() => selectedItem.value?.changes ?? null);
const selectedLines = computed(() => selectedChanges.value ? describeChanges(selectedChanges.value) : []);
let disposed = false, listGeneration = 0, previewGeneration = 0, contextGeneration = 0;
let listAbort: AbortController | undefined, previewAbort: AbortController | undefined;
let lifetime = new AbortController();
function localDate(value: string) { return formatDateTime(value) || t.panel.unknownTime; }
const snapshot = (id: string) => historyApi.revision(props.page.id, id, lifetime.signal);
// The diff starts at `predecessorId` (the list item's previousRevisionId, or the next older step), so no list is searched or fetched.
async function comparison(item: PageRevisionSummary, predecessorId: string | null) {
  const signal = lifetime.signal;
  const [value, predecessor] = await Promise.all([snapshot(item.id), predecessorId ? snapshot(predecessorId) : Promise.resolve(null)]);
  if (signal.aborted) throw new DOMException("Aborted", "AbortError");
  const diff = await compareHistoryTextAsync(predecessor?.markdown ?? "", value.markdown);
  if (signal.aborted) throw new DOMException("Aborted", "AbortError");
  return { value, predecessor, diff };
}
// A dock item that predates the list fields has no previousRevisionId: the next older loaded version stands in.
const versionPredecessor = (item: PageRevisionSummary) => item.previousRevisionId !== undefined ? item.previousRevisionId : items.value.find(candidate => candidate.version < item.version)?.id ?? null;
function stepPredecessor(version: PageRevisionSummary, step: PageRevisionSummary) {
  if (step.previousRevisionId !== undefined) return step.previousRevisionId;
  const list = stepState.value[version.groupId ?? ""]?.items ?? [];
  const index = list.findIndex(candidate => candidate.id === step.id);
  return list[index + 1]?.id ?? versionPredecessor(version);
}
async function filterHistory() {
  if (filtering.value || loading.value || !allowed.value || busy.value) return;
  const generation = contextGeneration;
  filtering.value = true; filterError.value = "";
  try {
    while (nextBeforeVersion.value !== null && filterActive.value) {
      await load(true);
      if (disposed || generation !== contextGeneration) return;
      if (listError.value) throw new Error(listError.value);
    }
  } catch (error) {
    if (!disposed && generation === contextGeneration) filterError.value = errorMessage(error);
  } finally {
    if (generation === contextGeneration) filtering.value = false;
  }
}
watch([author, onlyDeletions, kind], () => { void filterHistory(); });
// The shown version, its neighbours and what each is compared with are fetched ahead: the arrows and hovering then find
// them in the cache. Hover waits a moment so that a pointer crossing the list does not fetch every row.
let hoverTimer: ReturnType<typeof setTimeout> | undefined;
function warm(item: PageRevisionSummary) {
  const index = items.value.findIndex(candidate => candidate.id === item.id);
  for (const target of index < 0 ? [item] : items.value.slice(Math.max(0, index - 1), index + 2)) {
    historyApi.prefetch(props.page.id, target.id);
    if (target.previousRevisionId) historyApi.prefetch(props.page.id, target.previousRevisionId);
  }
}
// The version under the pointer or keyboard focus lights its bar in the activity chart; leaving the list goes back to the selected one.
const pointed = ref<string>();
function hover(item: PageRevisionSummary) { pointed.value = item.activeFrom; clearTimeout(hoverTimer); hoverTimer = setTimeout(() => { if (!disposed && allowed.value) warm(item); }, 120); }
// ↑/↓/Home/End walk every visible row button in list order: versions and, when a version is expanded, its steps right under it.
function moveVersion(event: KeyboardEvent) {
  if (!["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) return;
  const buttons = [...(versionsElement.value?.querySelectorAll<HTMLButtonElement>(".history-version") ?? [])];
  const index = buttons.indexOf(event.target as HTMLButtonElement);
  if (index < 0) return;
  event.preventDefault();
  const target = event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1 : Math.max(0, Math.min(buttons.length - 1, index + (event.key === "ArrowDown" ? 1 : -1)));
  buttons[target]?.focus(); buttons[target]?.click();
}

async function selectRevision(item: PageRevisionSummary, predecessorId: string | null = versionPredecessor(item), step = false) {
  if (!allowed.value || busy.value) return;
  selectedId.value = item.id; selectedStep.value = step; selectedItem.value = item; selectedPredecessor = predecessorId; exportError.value = "";
  // The shown version stays in place, dimmed, until the next one arrives.
  restoreOpen.value = false;
  previewError.value = "";
  previewLoading.value = true;
  previewAbort?.abort();
  const abort = previewAbort = new AbortController(), generation = ++previewGeneration;
  const pageId = props.page.id;
  try {
    const { value, predecessor, diff } = await comparison(item, predecessorId);
    if (disposed || generation !== previewGeneration || !allowed.value || props.page.id !== pageId) return;
    preview.value = value;
    previous.value = predecessor;
    previewDiff.value = diff;
    warm(item);
  } catch (error) {
    if (!abort.signal.aborted && generation === previewGeneration && !disposed) { preview.value = undefined; previous.value = null; previewDiff.value = undefined; previewError.value = errorMessage(error); }
  } finally {
    if (generation === previewGeneration && !disposed) previewLoading.value = false;
  }
}
const retryPreview = () => selectedItem.value && selectRevision(selectedItem.value, selectedPredecessor, selectedStep.value);
const selectStep = (version: PageRevisionSummary, step: PageRevisionSummary) => selectRevision(step, stepPredecessor(version, step), true);
async function toggleSteps(item: PageRevisionSummary) {
  const id = item.groupId;
  if (!id) return;
  if (expanded.value.includes(id)) { expanded.value = expanded.value.filter(value => value !== id); return; }
  expanded.value = [...expanded.value, id];
  if (stepState.value[id] && !stepState.value[id]!.error) return;
  const generation = contextGeneration, pageId = props.page.id;
  const set = (value: StepState) => { if (generation === contextGeneration && !disposed) stepState.value = { ...stepState.value, [id]: value }; };
  set({ items: [], loading: true, error: "" });
  try {
    // A version has few steps (an autosave pause or 4 hours ends it); the pages are read to the end so every step knows its predecessor.
    const all: PageRevisionSummary[] = [];
    for (let before: number | null = null, more = true; more;) {
      const result: PageHistory = await historyApi.steps(pageId, id, before);
      all.push(...result.items); before = result.nextBeforeVersion; more = before !== null;
    }
    set({ items: all, loading: false, error: "" });
  } catch (error) {
    set({ items: [], loading: false, error: errorMessage(error) });
  }
}
// A changed section chip: show the diff and scroll it to that heading (the lazy renderer may need a moment to draw it).
async function showSection(heading: string) {
  mode.value = "changes";
  await nextTick();
  for (let attempt = 0; attempt < 10 && !disposed; attempt++) {
    if (changesView.value?.scrollToHeading(heading)) return;
    await new Promise(done => setTimeout(done, 100));
  }
}
// A bar of the activity chart: the newest listed version of that day (or hour) takes the focus.
async function pickBucket(key: string, hourly: boolean) {
  const target = filteredItems.value.find(item => bucketKey(item.activeFrom, hourly) === key);
  if (!target) return;
  await nextTick();
  const button = versionsElement.value?.querySelector<HTMLElement>(`[data-revision="${target.id}"]`);
  button?.scrollIntoView({ behavior: scrollBehavior(button), block: "nearest" });
  button?.focus({ preventScroll: true });
}
async function load(append = false) {
  if (!allowed.value || busy.value || append && (loading.value || nextBeforeVersion.value === null)) return;
  listAbort?.abort();
  const abort = listAbort = new AbortController(), generation = ++listGeneration;
  const pageId = props.page.id;
  loading.value = true; listError.value = "";
  try {
    const result = await historyApi.list(pageId, append ? nextBeforeVersion.value : null, abort.signal);
    if (disposed || generation !== listGeneration || !allowed.value || props.page.id !== pageId) return;
    items.value = append ? [...items.value, ...result.items.filter(item => !items.value.some(existing => existing.id === item.id))] : result.items;
    nextBeforeVersion.value = result.nextBeforeVersion;
    headVersion.value = result.currentVersion;
    if (conflict.value) {
      conflict.value = false; restoreError.value = "";
      notice.value = t.panel.upToDate(result.currentVersion);
    }
    if (!selectedId.value) {
      const requested = props.initialRevision;
      const initial = requested ? items.value.find(item => item.id === requested.id) ?? requested : result.items[0];
      if (initial) {
        // The dock may predate new revisions: keep its explicitly chosen version reachable in the list.
        if (!items.value.some(item => item.id === initial.id)) items.value = [...items.value, initial].sort((a, b) => b.version - a.version);
        void selectRevision(initial);
        // The dialog opened on the author filter before there was a list: the shown version is the logical place, the arrows
        // step through the history from there (keyboard pass). Not when the reader has already moved or typed.
        void nextTick(() => {
          const active = document.activeElement;
          if (active instanceof HTMLInputElement && active.type === "search" && !active.value && versionsElement.value?.parentElement?.contains(active))
            versionsElement.value.querySelector<HTMLElement>('.history-version[aria-current="true"]')?.focus({ preventScroll: true });
        });
      }
    }
  } catch (error) {
    if (!abort.signal.aborted && generation === listGeneration && !disposed) listError.value = errorMessage(error);
  } finally {
    if (generation === listGeneration && !disposed) {
      loading.value = false;
      if (!listError.value && filterActive.value) void filterHistory();
    }
  }
}
async function exportPdf() {
  const revision = preview.value;
  if (!allowed.value || !revision || exporting.value || previewLoading.value || busy.value) return;
  const pageId = props.page.id, generation = contextGeneration;
  exporting.value = true; exportError.value = "";
  // The cover states what this browser chose last for a manual PDF export.
  try { await downloadContentExport("pages", pageId, "pdf", { name: t.panel.pdfName(revision.title, revision.version), revisionId: revision.id, coverDetails: rememberedCoverDetails() }); }
  catch (failure) {
    if (!disposed && generation === contextGeneration && selectedId.value === revision.id) exportError.value = errorMessage(failure);
  } finally { if (!disposed && generation === contextGeneration) exporting.value = false; }
}
async function restore() {
  const revision = preview.value;
  if (!allowed.value || busy.value || !revision || conflict.value) return;
  busy.value = true; restoreError.value = "";
  const generation = contextGeneration, pageId = props.page.id;
  try {
    if (!await props.beforeRestore() || disposed || generation !== contextGeneration || !allowed.value) return;
    // The host has just saved the writer's own last edits as a checkpoint (beforeRestore): the version they moved is no one else's change.
    const page = await historyApi.restore(pageId, revision.id, Math.max(headVersion.value, props.page.version));
    if (!disposed && generation === contextGeneration && allowed.value) emit("restored", page);
  } catch (error) {
    if (disposed || generation !== contextGeneration) return;
    // Others' unsaved live edits are no CAS conflict: refreshing history cannot help, retrying once they saved does.
    conflict.value = error instanceof ApiError && error.status === 409 && error.code !== "collaboration_unsaved";
    restoreError.value = conflict.value
      ? t.panel.conflict
      : errorMessage(error);
  } finally { if (!disposed && generation === contextGeneration) busy.value = false; }
}
function close() {
  if (busy.value) return;
  dialogOpen.value = false;
}
function reset() {
  contextGeneration++; listGeneration++; previewGeneration++;
  listAbort?.abort(); previewAbort?.abort();
  items.value = []; preview.value = undefined; selectedId.value = undefined; pointed.value = undefined;
  lifetime.abort(); lifetime = new AbortController(); historyApi.forget();
  expanded.value = []; stepState.value = {}; selectedItem.value = undefined; selectedStep.value = false; kind.value = "all";
  previous.value = null; previewDiff.value = undefined; filtering.value = false;
  filterError.value = ""; author.value = ""; onlyDeletions.value = false; mode.value = "changes"; restoreOpen.value = false;
  nextBeforeVersion.value = null; headVersion.value = props.page.version;
  loading.value = false; previewLoading.value = false; busy.value = false; exporting.value = false; exportError.value = "";
  listError.value = ""; previewError.value = ""; restoreError.value = ""; conflict.value = false; notice.value = "";
  if (allowed.value) void load();
}
watch([() => props.page.id, allowed], reset);
// The version the dock showed is fetched beside the list (one round trip fewer before the first diff).
onMounted(() => { if (props.initialRevision && allowed.value) warm(props.initialRevision); void load(); });
onBeforeUnmount(() => {
  disposed = true; contextGeneration++; listGeneration++; previewGeneration++;
  listAbort?.abort(); previewAbort?.abort();
  lifetime.abort(); clearTimeout(hoverTimer); historyApi.forget();
});
</script>

<template>
  <AppDialog class="page-history-dialog" :title="t.panel.title" size="side" :open="dialogOpen" @close="close" @closed="emit('close')">
    <p v-if="!allowed" class="notice error" role="alert">{{ t.panel.notAllowed }}</p>
    <div v-else class="history-layout">
      <nav ref="versionsElement" class="history-versions" :aria-label="t.panel.savedVersions" :aria-busy="loading" @keydown="moveVersion" @mouseleave="pointed = undefined">
        <div class="history-list-heading"><span>{{ t.panel.versions(items.length) }}{{ nextBeforeVersion !== null ? '+' : '' }}</span><Button variant="ghost" size="icon-sm" :aria-label="t.panel.refresh" :disabled="loading || busy || filtering" @click="load()"><AtlasIcon name="refresh-cw" :size="14" /></Button></div>
        <HistoryActivityChart class="history-activity-slot" :items="items" :focus="pointed ?? selectedItem?.activeFrom" @pick="pickBucket" />
        <div class="history-filters">
          <div ref="kindTrack" v-bind="kindThumb" class="history-view-switch history-kind-switch" role="group" :aria-label="t.panel.kindLabel"><button v-for="option in kinds" :key="option.id" :aria-pressed="kind === option.id" @click="kind = option.id">{{ option.label }}</button></div>
          <SearchField v-model="author" :label="t.panel.searchAuthorLabel" :placeholder="t.panel.searchAuthor" wide :disabled="busy" />
          <label class="history-only-deletions"><input v-model="onlyDeletions" type="checkbox" :disabled="busy" /> {{ t.panel.onlyDeletions }}</label>
        </div>
        <p v-if="filtering" class="help" role="status">{{ t.panel.searching }}</p>
        <ErrorState v-if="filterError" :title="t.panel.filterFailed" :text="filterError" :retry-label="t.panel.retryFilter" @retry="filterHistory" />
        <ErrorState v-if="listError" :title="t.panel.listFailed" :text="listError" :retry-label="t.panel.retry" :busy="loading || busy" @retry="load()" />
        <LoadingSkeleton v-if="loading && !items.length" :rows="5" row-height="52px" :label="t.panel.loading" />
        <EmptyState v-if="!loading && !filtering && !listError && !filterError && !filteredItems.length" compact icon="history" :title="items.length ? t.panel.noMatch : t.panel.noVersions" />
        <section v-for="group in groups" :key="group.day" class="history-day">
          <h3>{{ group.day }}</h3>
          <ol class="atlas-stagger">
            <li v-for="item in group.items" :key="item.id">
              <div class="history-row">
                <button class="history-version" :data-revision="item.id" :aria-current="selectedId === item.id && !selectedStep ? 'true' : undefined" :disabled="busy" :title="t.panel.versionTitle(localDate(item.activeFrom), [item.authorName, channelText(item.channel)].filter(Boolean).join(' · '), item.version)" @click="selectRevision(item)" @mouseenter="hover(item)" @focus="hover(item)">
                  <HistoryVersionBody :item="item" />
                </button>
                <button v-if="item.groupId && (item.checkpoints ?? 0) > 1" class="history-steps-toggle" :aria-expanded="expanded.includes(item.groupId)" :aria-controls="`history-steps-${item.groupId}`" :aria-label="t.panel.stepsLabel(item.version, item.checkpoints ?? 0)" :disabled="busy" @click="toggleSteps(item)"><AtlasIcon name="chevron-down" :size="14" /></button>
              </div>
              <InlineReveal v-if="item.groupId" :open="expanded.includes(item.groupId)" :id="`history-steps-${item.groupId}`">
                <div class="history-steps">
                  <LoadingSkeleton v-if="stepState[item.groupId]?.loading" :rows="Math.min(item.checkpoints ?? 2, 4)" row-height="34px" :label="t.panel.stepsLoading" />
                  <p v-if="stepState[item.groupId]?.error" class="notice error" role="alert">{{ stepState[item.groupId]!.error }}</p>
                  <ol v-if="stepState[item.groupId]?.items.length">
                    <li v-for="(step, index) in stepState[item.groupId]!.items" :key="step.id" :style="{ '--step': index }">
                      <button class="history-version history-step" :data-revision="step.id" :data-step="item.groupId" :aria-current="selectedId === step.id && selectedStep ? 'true' : undefined" :disabled="busy" :title="t.panel.stepTitle(localDate(step.activeFrom), step.authorName, item.version)" @click="selectStep(item, step)" @mouseenter="hover(step)" @focus="hover(step)">
                        <HistoryVersionBody :item="step" step />
                      </button>
                    </li>
                  </ol>
                </div>
              </InlineReveal>
            </li>
          </ol>
        </section>
        <Button variant="ghost" v-if="nextBeforeVersion !== null" class="history-load-more" :disabled="loading || busy || filtering" :aria-busy="loading" @click="load(true)">{{ t.panel.older }}</Button>
      </nav>
      <section class="history-preview" :aria-label="t.panel.selected" :aria-busy="previewLoading || busy">
        <div class="history-context"><AtlasIcon name="updates" :size="14" /><span>{{ t.panel.historic }}</span><span class="history-readonly">{{ t.panel.readOnly }}</span><LoadingNote inline class="history-context-note" :active="previewLoading && !!preview" :label="t.panel.loadingVersion" /></div>
        <div v-if="preview" class="history-preview-heading" :data-refreshing="previewLoading || undefined">
          <div class="history-preview-identity"><h3>{{ preview.title }}</h3><p>{{ preview.authorName }} · <time :datetime="preview.activeFrom">{{ localDate(preview.activeFrom) }}</time> · v{{ preview.version }}</p><p v-if="selectedItem?.label" class="history-preview-label" data-preview-label>{{ selectedItem.label }}</p><p v-if="selectedItem?.isAi" class="history-preview-channel history-preview-ai" data-preview-ai><strong>AI</strong> {{ t.panel.aiChange(preview.authorName) }}</p><p v-if="channelText(preview.channel, null, true)" class="history-preview-channel" data-preview-channel>{{ t.panel.change }} <ChannelBadge :channel="preview.channel" :token="preview.apiToken" detailed /></p><p v-if="channelText(preview.publicationChannel, null, true)" class="history-preview-channel" data-preview-publication-channel>{{ t.panel.publication }} <ChannelBadge :channel="preview.publicationChannel" :token="preview.publicationApiToken" detailed /></p><p v-if="preview.changeSummary" class="history-preview-summary">{{ preview.changeSummary }}</p>
            <ul v-if="selectedLines.length" class="history-preview-changes" :aria-label="t.panel.componentChanges"><li v-for="line in selectedLines" :key="line">{{ line }}</li></ul>
            <div v-if="selectedChanges?.sections.length" class="history-sections" role="group" :aria-label="t.panel.changedSections"><button v-for="section in selectedChanges.sections" :key="section" class="history-section-chip" :title="t.panel.showSection(section)" @click="showSection(section)">{{ section }}</button></div></div>
          <Button variant="outline" size="sm" :disabled="previewLoading || busy || exporting" :aria-busy="exporting" data-history-pdf @click="exportPdf"><AtlasIcon name="download" :size="14" />{{ exporting ? t.panel.pdfBusy : t.panel.pdf }}</Button>
          <div ref="viewTrack" v-bind="viewThumb" class="history-view-switch" role="group" :aria-label="t.panel.viewLabel"><button :aria-pressed="mode === 'changes'" @click="mode = 'changes'">{{ t.panel.modeChanges }}</button><button :aria-pressed="mode === 'version'" @click="mode = 'version'">{{ t.panel.modeVersion }}</button></div>
        </div>
        <p v-if="exportError" class="notice error" role="alert">{{ exportError }}</p>
        <div class="history-preview-body">
          <ErrorState v-if="previewError && !previewLoading" :title="t.panel.previewFailed" :text="previewError" :retry-label="t.panel.retryPreview" @retry="retryPreview" />
          <LoadingRegion v-else-if="preview || previewLoading" :loading="previewLoading" :ready="!!preview" :rows="6" row-height="44px" :label="t.panel.loadingPreview">
            <template v-if="preview">
              <HistoryChanges v-if="mode === 'changes'" :key="preview.id" ref="changesView" :class="{ 'history-ai-diff': selectedItem?.isAi }" :revision="preview" :previous="previous" :diff="previewDiff" :dark="dark" :accent="accent" />
              <HistoryRevisionPreview v-else :key="preview.id" :content-json="preview.contentJson" :markdown="preview.markdown" :page-id="preview.pageId" :dark="dark" :accent="accent" />
            </template>
          </LoadingRegion>
          <p v-else class="help">{{ t.panel.selectPrompt }}</p>
        </div>
        <footer class="history-restore">
          <button class="history-restore-toggle" :aria-expanded="restoreOpen" :disabled="!preview || previewLoading || busy" @click="restoreOpen = !restoreOpen">{{ t.panel.restore }}</button>
          <InlineReveal :open="restoreOpen">
            <div class="history-restore-confirm">
              <p class="help">{{ preview && preview.version === page.publishedVersion ? t.panel.restoreHelpPublished : t.panel.restoreHelp }}</p>
              <p v-if="restoreError" class="notice error" role="alert">{{ restoreError }}</p>
              <p v-if="notice" class="notice success" role="status">{{ notice }}</p>
              <Button variant="outline" v-if="conflict" :disabled="loading || busy" @click="load()">{{ t.panel.reload }}</Button>
              <Button variant="default" size="sm" :disabled="!preview || previewLoading || busy || conflict || loading" @click="restore">{{ busy ? t.panel.restoring : t.panel.restoreAsDraft }}</Button>
            </div>
          </InlineReveal>
        </footer>
      </section>
    </div>
  </AppDialog>
</template>

<style scoped>
.history-layout { display: grid; grid-template-columns: 300px minmax(0, 1fr); flex: 1; min-height: 0; }
.history-versions { overflow-y: auto; overscroll-behavior: contain; background: var(--sidebar); border-right: 1px solid var(--line); padding: 0 0 16px; }
.history-list-heading { display: flex; align-items: center; justify-content: space-between; color: var(--muted); padding: 8px 16px 0; font-size: 11px; }
.history-filters { padding: 4px 16px 14px; display: grid; gap: 10px; font-size: 12px; color: var(--muted); }
.history-activity-slot { display: block; }
.history-kind-switch { display: flex; }
.history-kind-switch button { flex: 1; padding: 3px 4px; font-size: 11px; white-space: nowrap; }
.history-row { position: relative; }
.history-steps-toggle { position: absolute; right: 10px; bottom: 6px; display: grid; place-items: center; width: 24px; height: 24px; border: 0; border-radius: 6px; background: transparent; color: var(--muted); cursor: pointer; transition: color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.history-steps-toggle :deep(svg) { rotate: -90deg; transition: rotate var(--atlas-motion-base) var(--atlas-ease-out); }
.history-steps-toggle[aria-expanded=true] :deep(svg) { rotate: 0deg; }
.history-steps-toggle:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
@media (hover: hover) and (pointer: fine) { .history-steps-toggle:hover { color: var(--text); background: var(--hover); } }
.history-steps { margin-left: 18px; border-left: 2px solid var(--line); }
.history-steps ol { list-style: none; margin: 0; padding: 0; }
/* Steps come in one after another under their version (motion.css: no distance under reduced motion). */
.history-steps li { animation: st-rise-in var(--atlas-motion-base) var(--atlas-ease-out) backwards; animation-delay: calc(min(var(--step), 8) * var(--atlas-motion-fast) / 4); }
.history-step { min-height: 40px; padding: 6px 14px; }
.history-preview-label { margin: 0 0 4px; font-size: 14px; font-weight: 600; color: var(--text); }
.history-preview-ai strong { padding: 0 5px; margin-right: 4px; border-radius: 4px; font-size: 10px; color: var(--ai-ink, var(--accent)); background: var(--ai-wash-strong, var(--hover)); border: 1px solid var(--ai-line, var(--line)); }
.history-preview-changes { margin: 8px 0 0; padding-left: 18px; font-size: 12px; color: var(--text); }
.history-sections { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
.history-section-chip { max-width: 100%; padding: 2px 8px; border: 1px solid var(--line); border-radius: 999px; background: var(--sidebar); color: var(--text); font-size: 11px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), border-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.history-section-chip:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
@media (hover: hover) and (pointer: fine) { .history-section-chip:hover { background: var(--hover); border-color: var(--accent); } }
/* A change with AI is drawn in its own colour: every addition of the diff (text and rendered) takes the AI ink. */
.history-ai-diff { --success: var(--ai-ink, var(--accent)); }
.history-only-deletions { display: flex; align-items: center; gap: 7px; cursor: pointer; }
.history-filters input[type=checkbox] { accent-color: var(--accent); }
.history-versions > .help, .history-versions > .notice { padding: 8px 16px; font-size: 12px; }
.history-day h3 { margin: 0; padding: 8px 18px; background: var(--sidebar); font-size: 11px; font-weight: 500; color: var(--muted); border-top: 1px solid var(--line); }
.history-day ol { list-style: none; margin: 0; padding: 0; }
/* Long histories: rows outside the viewport skip layout and paint; the buttons stay in the DOM for the arrows and the stats observer. */
.history-day li { content-visibility: auto; contain-intrinsic-size: auto 53px; }
.history-version { display: grid; grid-template-columns: minmax(5ch, max-content) minmax(0, 1fr) auto; column-gap: 9px; row-gap: 3px; width: 100%; min-height: 53px; padding: 9px 18px; border: 0; background: transparent; text-align: left; color: var(--text); border-radius: 0; cursor: pointer; }
@media (hover: hover) and (pointer: fine) { .history-version:hover { background: var(--hover); } }
.history-version[aria-current] { background: var(--navigation-selected-bg); }
.history-version:focus-visible, .history-view-switch button:focus-visible, .history-restore-toggle:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.history-preview-channel { margin-top: 6px; font-size: 12px; color: var(--muted); }
.history-preview-heading .history-preview-summary { margin-top: 8px; font-size: 13px; color: var(--text); overflow-wrap: anywhere; }
.addition { color: var(--success, #23794b); }
.deletion { color: var(--error); }
.history-load-more { width: calc(100% - 32px); margin: 12px 16px 0; font-size: 12px; }
.history-preview { min-height: 0; min-width: 0; display: flex; flex-direction: column; background: var(--surface); }
/* The idle note keeps its room; drawn before „Pouze pro čtení“, so that ends on the bar's right edge like the controls
   under it instead of 110 px inside it. */
.history-context-note { order: 1; margin-left: auto; }
.history-context { display: flex; align-items: center; gap: 7px; padding: 10px 24px; background: var(--sidebar); border-bottom: 1px solid var(--line); font-size: 11px; color: var(--muted); flex-shrink: 0; }
.history-readonly { order: 2; margin-left: 8px; }
.history-preview-heading[data-refreshing] { opacity: .55; transition-delay: var(--atlas-motion-defer); }
.history-preview-heading { transition: opacity var(--atlas-motion-base) var(--atlas-ease-out); display: flex; flex-wrap: wrap; align-items: center; gap: 14px; padding: 20px 24px 16px; border-bottom: 1px solid var(--line); flex-shrink: 0; }
/* The title keeps a readable width; in a narrow panel the PDF button and the switch wrap under it instead of
   squeezing it into a column of single syllables. */
.history-preview-identity { flex: 1 1 240px; min-width: min(240px, 100%); }
.history-preview-heading h3 { margin: 0 0 5px; font-size: 18px; font-weight: 600; overflow-wrap: anywhere; }
.history-preview-heading p { margin: 0; font-size: 11px; color: var(--muted); }
.history-view-switch { --thumb-inset: 3px; --thumb-radius: calc(6px - 1px - 3px); --thumb-shadow: var(--shadow); display: flex; gap: 2px; padding: 3px; border: 1px solid var(--line); border-radius: 6px; background: var(--sidebar); }
.history-view-switch button { border: 0; border-radius: calc(6px - 1px - 3px); background: transparent; color: var(--muted); padding: 3px 9px; font-size: 12px; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
.history-view-switch button[aria-pressed=true] { color: var(--text); background: var(--surface); box-shadow: var(--shadow); }
/* While the thumb is drawn it carries the raised segment; without it (reduced motion) the chosen option fades its own. */
.history-view-switch[data-thumb] button[aria-pressed=true] { background: transparent; box-shadow: none; }
@media (hover: hover) and (pointer: fine) { .history-view-switch button:hover:not([aria-pressed=true]) { color: var(--text); } }
.history-preview-body { flex: 1; min-height: 0; overflow-y: auto; overscroll-behavior: contain; padding: 0 24px; }
.history-preview-body > .md-renderer { padding-top: 20px; }
.history-restore { border-top: 1px solid var(--line); padding: 10px 24px; flex-shrink: 0; }
.history-restore-toggle { border: 0; background: none; color: var(--muted); font-size: 12px; padding: 3px 0; cursor: pointer; }
@media (hover: hover) and (pointer: fine) { .history-restore-toggle:hover { color: var(--text); text-decoration: underline; } }
.history-restore-confirm { padding: 4px 0; }
.history-restore-confirm .help { font-size: 12px; margin: 4px 0 10px; }
.history-restore-confirm button + button { margin-left: 8px; }
/* The list goes above the preview by the panel's own width, not the window's: the side dialog is its container. */
.page-history-dialog { container: page-history / inline-size; }
@container page-history (max-width: 760px) {
  .history-layout { grid-template-columns: 1fr; grid-template-rows: minmax(150px, 32%) minmax(0, 1fr); }
  .history-versions { border-right: 0; border-bottom: 1px solid var(--line); }
  .history-version { min-height: 48px; }
  .history-activity-slot { display: none; }
  .history-context, .history-preview-heading, .history-restore { padding-left: 16px; padding-right: 16px; }
  .history-preview-heading { padding-top: 12px; padding-bottom: 12px; }
  .history-preview-body { padding: 0 16px; }
}
</style>
