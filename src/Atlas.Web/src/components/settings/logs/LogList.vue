<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from "vue";
import { errorMessage } from "../../../api";
import type { LogApi, LogEntry, LogFilter, LogSummary } from "../../../logs/logApi";
import { logsTopic, useLiveFeed } from "../../../live/liveFeed";
import LiveUpdateNote from "../LiveUpdateNote.vue";
import AtlasIcon from "../../AtlasIcon.vue";
import LoadingRegion from "../../LoadingRegion.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import t from "../../../i18n/settings-logs";
import LogRow from "./LogRow.vue";
import { entriesText } from "./logFormat";
import { rowKeys } from "../../../keyboard/navigation";
import { TooltipProvider } from "../../ui/tooltip";

/**
 * The entries of the server log for a `filter`, newest first (Správa wiki → Systémový log, and the log of one job run): a sized skeleton while
 * loading, older pages when the end of the list comes into view („Načíst starší“ is the keyboard's way), and, with `live`, the entries after
 * the newest each time the server says it stored lines up to the filter's level (useLiveFeed, logs feed; nothing polls); scrolled away from the top, the new ones are held back and counted in a pill so the row
 * being read does not move. At most 2000 entries stay in the page. The newest summary goes out as `summary` (only a first page carries one);
 * „Jen tento požadavek“ as `trace`.
 */
const props = defineProps<{ api: LogApi; filter: LogFilter; live: boolean }>();
const emit = defineEmits<{ trace: [traceId: string]; summary: [summary: LogSummary] }>();
const maxEntries = 2000;
const levels = ["trace", "debug", "information", "warning", "error", "critical"];

const entries = ref<LogEntry[]>([]);
const pending = ref<LogEntry[]>([]);
// The held-back entries are a whole newer page, not a continuation: showing them replaces the list (a gap lies between).
const pendingGap = ref(false);
const more = ref(false);
const loaded = ref(false), loading = ref(true), loadingOlder = ref(false), error = ref("");
const atTop = ref(true), nearEnd = ref(false);
const top = ref<HTMLElement>(), bottom = ref<HTMLElement>();
const copied = ref(false);
// One Tab stop for the list: the row last focused (the first at first) is the one reached by Tab, the arrows walk the rest (rowKeys).
const current = ref<number>();
const tabbable = (entry: LogEntry) => (current.value ?? entries.value[0]?.id) === entry.id;
function focused(event: FocusEvent) {
  const id = (event.target as HTMLElement).closest<HTMLElement>("li")?.dataset.logRow;
  if (id) current.value = Number(id);
}
async function copyAll() {
  try {
    await navigator.clipboard.writeText(entriesText(entries.value));
    copied.value = true;
    setTimeout(() => { copied.value = false; }, 2000);
  } catch { copied.value = false; }
}
let generation = 0;


async function reload() {
  const mine = ++generation;
  loading.value = true;
  try {
    const page = await props.api.list(props.filter);
    if (mine !== generation) return;
    entries.value = page.entries;
    more.value = page.more;
    pending.value = [];
    pendingGap.value = false;
    if (page.summary) emit("summary", page.summary);
    error.value = "";
    loaded.value = true;
  } catch (failure) {
    if (mine === generation) error.value = errorMessage(failure);
  } finally {
    if (mine === generation) loading.value = false;
  }
}
async function loadOlder() {
  const last = entries.value.at(-1);
  if (!last || loadingOlder.value || !more.value) return;
  const mine = generation;
  loadingOlder.value = true;
  try {
    const page = await props.api.list(props.filter, { before: last.id });
    if (mine !== generation) return;
    entries.value = [...entries.value, ...page.entries];
    more.value = page.more;
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    loadingOlder.value = false;
  }
}

function show(fresh: LogEntry[]) {
  const all = [...fresh, ...entries.value];
  if (all.length > maxEntries) more.value = true;
  entries.value = all.slice(0, maxEntries);
}
function replaceWith(page: LogEntry[]) {
  entries.value = page;
  pending.value = [];
  pendingGap.value = false;
  more.value = true;
}
async function showPending() {
  if (pendingGap.value) replaceWith(pending.value);
  else show(pending.value);
  pending.value = [];
  await nextTick();
  top.value?.scrollIntoView?.({ block: "start" });
}
async function tail() {
  const newest = pending.value[0]?.id ?? entries.value[0]?.id;
  // A reload in flight reads the newest anyway.
  if (loading.value) return;
  if (newest === undefined) return reload();
  const mine = generation;
  try {
    const page = await props.api.list(props.filter, { after: newest });
    if (mine !== generation) return;
    if (page.more) {
      // More than a page arrived: the list has a gap, so it starts again from the newest page, as soon as the reader is at the top (else on the pill).
      if (atTop.value) replaceWith(page.entries);
      else { pending.value = page.entries; pendingGap.value = true; }
    } else if (page.entries.length) {
      if (atTop.value) show(page.entries);
      else pending.value = [...page.entries, ...pending.value];
    }
    error.value = "";
  } catch (failure) {
    error.value = errorMessage(failure);
  }
}

// The two ends of the list: the top says whether the reader is reading the newest, the bottom asks for older entries.
function watchVisible(element: typeof top, seen: (visible: boolean) => void) {
  watch(element, (target, _, cleanup) => {
    if (!target || typeof IntersectionObserver === "undefined") return;
    const observer = new IntersectionObserver((items) => seen(items.at(-1)!.isIntersecting));
    observer.observe(target);
    cleanup(() => observer.disconnect());
  }, { flush: "post" });
}
watchVisible(top, (visible) => { atTop.value = visible; });
watchVisible(bottom, (visible) => { nearEnd.value = visible; });
watch(atTop, (visible) => { if (visible && pending.value.length) void showPending(); });
watch([nearEnd, more, loadingOlder, () => entries.value.length], () => { if (nearEnd.value) void loadOlder(); });

watch(() => props.filter, () => void reload());
// Live: a signal of stored lines below the filter's level is skipped; each (re)subscription reads what came meanwhile.
const { unavailable: liveOff, refresh } = useLiveFeed<{ level?: string }>(() => (props.live ? logsTopic : undefined), tail,
  (written) => levels.indexOf(written.level ?? "critical") >= levels.indexOf(props.filter.level ?? "trace"));

onMounted(() => void reload());
</script>

<template>
  <div class="log-list-wrap" data-log-list>
    <ErrorState v-if="error" :title="t.list.loadFailed" :text="error" @retry="reload" />
    <LiveUpdateNote v-if="live && liveOff" @refresh="refresh" />
    <LoadingRegion :loading="loading" :ready="loaded || !!error" :rows="8" row-height="40px" :label="t.list.loading">
      <EmptyState v-if="loaded && !entries.length" icon="file-text" :title="t.list.empty" />
      <div v-else-if="loaded" class="log-body">
        <button type="button" class="st-link log-copy-all" data-log-copy-all @click="copyAll"><AtlasIcon name="copy" :size="13" />{{ copied ? t.list.copied : t.list.copy(entries.length) }}</button>
        <span ref="top" class="log-sentinel" data-log-top aria-hidden="true" />
        <div class="log-pill-slot">
          <Transition name="log-pill">
            <button v-if="pending.length" type="button" class="log-pill" data-log-pill @click="showPending"><AtlasIcon name="arrow-up" :size="14" />{{ t.list.fresh(pending.length) }}</button>
          </Transition>
        </div>
        <TooltipProvider :delay-duration="400">
          <ul class="log-list" :aria-label="t.list.label" @keydown="rowKeys($event, 'li')" @focusin="focused">
            <LogRow v-for="entry in entries" :key="entry.id" :entry="entry" :tabbable="tabbable(entry)" @trace="emit('trace', $event)" />
          </ul>
        </TooltipProvider>
        <span ref="bottom" class="log-sentinel" data-log-bottom aria-hidden="true" />
        <button v-if="more" type="button" class="st-link log-older" :disabled="loadingOlder" data-log-older @click="loadOlder">{{ loadingOlder ? t.list.loadingOlder : t.list.older }}</button>
      </div>
    </LoadingRegion>
  </div>
</template>

<style scoped>
.log-list-wrap { display: grid; gap: 14px; min-width: 0; }
.log-body { position: relative; display: grid; gap: 8px; min-width: 0; }
.log-list { container: log-list / inline-size; margin: 0; padding: 6px 0; list-style: none; min-width: 0; border: 1px solid var(--line); border-radius: var(--st-radius, 12px); background: var(--surface); overflow: hidden; }
.log-sentinel { display: block; height: 1px; }
.log-older { justify-self: center; padding: 6px 12px; }
.log-copy-all { justify-self: end; display: inline-flex; align-items: center; gap: 4px; font-size: 12.5px; background: none; cursor: pointer; }
.log-pill-slot { position: sticky; top: 8px; z-index: 2; height: 0; display: grid; justify-items: center; }
.log-pill { display: inline-flex; align-items: center; gap: 6px; padding: 6px 14px; border: 0; border-radius: 999px; background: var(--accent); color: var(--on-accent); font: inherit; font-size: 13px; font-weight: 600; cursor: pointer; box-shadow: var(--shadow-menu, 0 4px 14px rgb(0 0 0 / .2)); }
.log-pill:focus-visible { outline: var(--state-focus-ring); outline-offset: 2px; }
.log-pill-enter-active, .log-pill-leave-active { transition: opacity var(--atlas-motion-base) var(--atlas-ease-out), translate var(--atlas-motion-base) var(--atlas-ease-out); }
.log-pill-enter-from, .log-pill-leave-to { opacity: 0; translate: 0 -8px; }
</style>
