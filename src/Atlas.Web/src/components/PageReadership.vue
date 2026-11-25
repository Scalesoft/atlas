<script setup lang="ts">
import t from "../i18n/components-b";
import { computed, onUnmounted, ref, watch } from "vue";
import { api, errorMessage } from "../api";
import { formatActivityTime, formatDay } from "../formatDate";
import { Button } from "./ui/button";
import AppDialog from "./AppDialog.vue";
import AtlasIcon from "./AtlasIcon.vue";
import ActivityHeatmap from "./ActivityHeatmap.vue";
import LoadingRegion from "./LoadingRegion.vue";
import EmptyState from "./settings/EmptyState.vue";
import ErrorState from "./settings/ErrorState.vue";
import FilterMenu from "./settings/FilterMenu.vue";
import LoadingNote from "./settings/LoadingNote.vue";
import SettingsList from "./settings/SettingsList.vue";
import SettingsListRow from "./settings/SettingsListRow.vue";
import StatusBadge from "./settings/StatusBadge.vue";
import SubjectAvatar from "./settings/SubjectAvatar.vue";
import type { ChoiceOption } from "./settings/types";
import "./settings/settings.css";
import { periodQuery, type Readership, type ReadershipItem } from "../activity";
import { pragueNow } from "@atlas/markdown/core";

/**
 * Čtenost stránky, the side panel (AppDialog size="side") of the page's "…" menu like Historie: the year's calendar of views (the ActivityHeatmap of
 * Profil → Aktivita) and the log of who read the page when, for the whole year or the day picked in the calendar. Releases of
 * a password on this page are entries of their own, marked in the calendar and in the log; the server lists them only for
 * managers of the vault.
 */
const props = defineProps<{ pageId: string }>();
const emit = defineEmits<{ close: [] }>();
const dialogOpen = ref(true);
const today = pragueNow().date, currentYear = Number(today.slice(0, 4));
const year = ref(currentYear), selected = ref("");
const summary = ref<Readership>(), loading = ref(false), error = ref("");
const log = ref<Pick<Readership, "items" | "hasMore">>(), logLoading = ref(false), logError = ref("");
let summaryRequest: AbortController | undefined, logRequest: AbortController | undefined;

const yearOptions: ChoiceOption[] = Array.from({ length: 5 }, (_, index) => ({ value: index ? String(currentYear - index) : "", label: index ? String(currentYear - index) : t.readership.thisYear(currentYear), icon: "calendar" }));
const yearFilter = computed({ get: () => (year.value === currentYear ? "" : String(year.value)), set: (value: string) => (year.value = value ? Number(value) : currentYear) });
const readers = (count: number) => t.readership.readers(count);
const viewCount = (count: number) => t.readership.views(count);
const secretCount = (count: number) => t.readership.secretViews(count);
const days = computed(() => (summary.value?.days ?? []).map(day => ({ day: day.day, count: day.views, marked: day.secrets })));
const fieldNames: Record<string, string> = { userName: t.readership.userName, password: t.readership.password, url: t.readership.address, notes: t.readership.notes };
const secretDetail = (item: Extract<ReadershipItem, { kind: "secret" }>) =>
  [item.secretTitle, item.fields.map(field => fieldNames[field] ?? field).join(", "), formatActivityTime(item.at)].join(" · ");

const url = (from: string, to: string, offset = 0) => "/analytics/pages/" + props.pageId + "?" + periodQuery(from, to) + "&offset=" + offset;
async function loadSummary() {
  summaryRequest?.abort(); logRequest?.abort();
  const request = summaryRequest = new AbortController();
  selected.value = ""; loading.value = true; error.value = ""; logError.value = ""; logLoading.value = false;
  try {
    const data = await api<Readership>(url(year.value + "-01-01", year.value + "-12-31"), { signal: request.signal });
    if (!request.signal.aborted) { summary.value = data; log.value = { items: data.items, hasMore: data.hasMore }; }
  } catch (failure) { if (!request.signal.aborted) error.value = errorMessage(failure); }
  finally { if (!request.signal.aborted) loading.value = false; }
}
/** The log of the selected day, or of the whole year again; `append` loads the next page of the same log. */
async function loadLog(append = false) {
  logRequest?.abort();
  const request = logRequest = new AbortController();
  const [from, to] = selected.value ? [selected.value, selected.value] : [year.value + "-01-01", year.value + "-12-31"];
  logLoading.value = true; logError.value = "";
  try {
    const data = await api<Readership>(url(from, to, append ? log.value?.items.length ?? 0 : 0), { signal: request.signal });
    if (!request.signal.aborted) log.value = { items: append ? [...(log.value?.items ?? []), ...data.items] : data.items, hasMore: data.hasMore };
  } catch (failure) { if (!request.signal.aborted) logError.value = errorMessage(failure); }
  finally { if (!request.signal.aborted) logLoading.value = false; }
}
function selectDay(day: string) {
  selected.value = selected.value === day ? "" : day;
  void loadLog();
}
function wholeYear() {
  selected.value = "";
  void loadLog();
}
watch([() => props.pageId, year], loadSummary, { immediate: true });
onUnmounted(() => { summaryRequest?.abort(); logRequest?.abort(); });
</script>

<template>
  <AppDialog class="page-readership-dialog" :title="t.readership.title" size="side" :open="dialogOpen" @close="dialogOpen = false" @closed="emit('close')">
    <div class="page-readership settings-ui">
      <div class="readership-toolbar" role="search" :aria-label="t.readership.filter">
        <FilterMenu v-model="yearFilter" :options="yearOptions" :label="t.readership.year" icon="calendar" data-readership-year />
        <p class="readership-count" aria-live="polite">
          <LoadingNote inline :active="loading && !!summary" :label="t.readership.loading" />
          <template v-if="summary">
            <strong>{{ t.readership.views(summary.views) }}</strong> · {{ readers(summary.readers) }}
            <span v-if="summary.secrets" class="readership-secret-count" data-readership-secrets><AtlasIcon name="key-round" :size="13" />{{ secretCount(summary.secrets) }}</span>
          </template>
        </p>
      </div>
      <ErrorState v-if="error" :title="t.readership.loadFailed" :text="error" :busy="loading" data-readership-error @retry="loadSummary" />
      <template v-else>
        <LoadingRegion :loading="loading" :ready="!!summary" :label="t.readership.loadingReadership">
          <ActivityHeatmap :days="days" :from="year + '-01-01'" :to="year + '-12-31'" :selected="selected" :label="t.readership.calendar"
            :unit="viewCount" :mark-label="secretCount" :mark-legend="t.readership.secretLegend" @select="selectDay" />
        </LoadingRegion>

        <section class="readership-log" aria-labelledby="readership-log-title">
          <header class="readership-log-header">
            <h3 id="readership-log-title">{{ selected ? t.readership.whoRead(formatDay(selected)) : t.readership.whoWhenRead(year) }}</h3>
            <LoadingNote inline :active="logLoading && !!log" :label="t.readership.loading" />
            <Button v-if="selected" variant="ghost" size="xs" type="button" class="readership-whole-year" data-readership-year-log @click="wholeYear">
              <AtlasIcon name="close" :size="13" />{{ t.readership.wholeYear }}
            </Button>
          </header>
          <ErrorState v-if="logError" :title="t.readership.visitsFailed" :text="logError" :busy="logLoading" @retry="loadLog(!!log?.items.length)" />
          <LoadingRegion v-else :loading="(loading && !summary) || (logLoading && !log?.items.length)" :ready="!!log" :rows="4" row-height="52px" :label="t.readership.loadingVisits" class="readership-result">
            <template v-if="log">
              <SettingsList v-if="log.items.length" :key="selected || year" :label="t.readership.pageVisits" class="readership-fade">
                <template v-for="(item, index) in log.items" :key="item.kind + ':' + item.userId + ':' + item.at + ':' + index">
                  <SettingsListRow v-if="item.kind === 'secret'" :title="item.userName" class="readership-secret" data-kind="secret">
                    <template #leading><span class="st-icon-tile readership-secret-icon" data-tone="warning"><AtlasIcon name="key-round" :size="14" /></span></template>
                    <template #description><time :datetime="item.at">{{ secretDetail(item) }}</time></template>
                    <template #meta><StatusBadge tone="warning" size="sm" icon="key-round" :label="item.operation === 'copy' ? t.readership.secretCopied : t.readership.secretShown" /></template>
                  </SettingsListRow>
                  <SettingsListRow v-else :title="item.userName" data-kind="view">
                    <template #leading><SubjectAvatar kind="user" :name="item.userName" :user-id="item.userId" size="sm" /></template>
                    <template #description><time :datetime="item.at">{{ formatActivityTime(item.at) }}</time></template>
                  </SettingsListRow>
                </template>
              </SettingsList>
              <EmptyState v-else compact icon="bar-chart-3" :title="t.readership.noViews" :text="selected ? t.readership.noViewsDay : t.readership.noViewsYear" />
              <div v-if="log.hasMore" class="st-actions"><Button variant="outline" size="sm" type="button" data-more :disabled="logLoading" :aria-busy="logLoading" @click="loadLog(true)"><AtlasIcon name="down" :size="15" />{{ t.readership.moreVisits }}</Button></div>
            </template>
          </LoadingRegion>
        </section>
      </template>
      <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.readership.note }}</p>
    </div>
  </AppDialog>
</template>

<style scoped>
/* A narrower side panel than Historie: the year calendar and a log, no preview beside them. */
.page-readership-dialog { --app-dialog-side-width: min(880px, calc(100vw - 64px)); container: page-readership / inline-size; }
.page-readership { flex: 1; min-height: 0; overflow-y: auto; overscroll-behavior: contain; display: grid; align-content: start; gap: 16px; min-width: 0; padding: 20px 22px 24px; }
/* By the panel's own width (it fills a phone), not the window's. */
@container page-readership (max-width: 760px) { .page-readership { padding: 16px; } }
.readership-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; min-width: 0; }
.readership-count { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 0 0 0 auto; color: var(--muted); font-size: 13px; font-variant-numeric: tabular-nums; }
.readership-count strong { color: var(--accent); font-weight: 650; }
.readership-secret-count { display: inline-flex; align-items: center; gap: 4px; margin-left: 6px; padding: 1px 8px; border-radius: 999px; color: var(--warning); background: var(--warning-bg); font-weight: 600; }
.readership-log { display: grid; gap: 10px; min-width: 0; }
.readership-log-header { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.readership-log-header h3 { margin: 0; font-size: 14px; font-weight: 650; }
.readership-whole-year { margin-left: auto; border-radius: 999px; }
.readership-result > :deep(.loading-region-content) { display: grid; gap: 10px; }
.readership-fade { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
/* A released password stands out in the log by a soft warning tint, a key instead of the avatar and its badge. */
.readership-secret { border-radius: var(--st-radius-sm); background: color-mix(in srgb, var(--warning-bg) 55%, transparent); }
.readership-secret-icon { width: 28px; height: 28px; border-radius: 50%; color: var(--warning); background: var(--warning-bg); }
</style>
