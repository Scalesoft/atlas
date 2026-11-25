<script setup lang="ts">
import t from "../i18n/components-b";
import { computed, onUnmounted, ref, watch } from "vue";
import { Button } from "./ui/button";
import { Input } from "./ui/input";
import AtlasIcon from "./AtlasIcon.vue";
import ActivityHeatmap from "./ActivityHeatmap.vue";
import LoadingRegion from "./LoadingRegion.vue";
import EmptyState from "./settings/EmptyState.vue";
import ErrorState from "./settings/ErrorState.vue";
import FilterMenu from "./settings/FilterMenu.vue";
import InlineReveal from "./settings/InlineReveal.vue";
import LoadingNote from "./settings/LoadingNote.vue";
import SettingsList from "./settings/SettingsList.vue";
import SettingsListRow from "./settings/SettingsListRow.vue";
import SettingsSection from "./settings/SettingsSection.vue";
import SubjectAvatar from "./settings/SubjectAvatar.vue";
import type { ChoiceOption } from "./settings/types";
import { api, errorMessage } from "../api";
import { disclosureHooks } from "./settings/disclosure";
import { formatActivityTime, formatDate, formatDay } from "../formatDate";
import { isPlainNavigationClick, pageLink } from "../pageLinks";
import { periodQuery, shiftDay, validPeriod, type ActivitySummary, type ActivityChanges, type ActivityChange, type PopularPage } from "../activity";
import { pragueNow } from "@atlas/markdown/core";
import LoadMoreButton from "./LoadMoreButton.vue";

/**
 * Aktivita (profile): the calendar of saved changes for a year, the changes of a chosen day and the
 * most read pages of a period. The page title comes from ProfilePage; each list has its filters in
 * one toolbar line (FilterMenu), and a filter that is set repeats as a removable chip.
 */
const emit = defineEmits<{ select: [id: string] }>();
const today = pragueNow().date, currentYear = Number(today.slice(0, 4));
const year = ref(currentYear), scope = ref("mine");
const summary = ref<ActivitySummary>(), summaryYear = ref(currentYear), loading = ref(false), error = ref("");
const selected = ref(""), changes = ref<ActivityChange[]>([]), more = ref(false), detailLoading = ref(false), detailLoaded = ref(false), detailError = ref("");
const from = ref(shiftDay(today, -29)), to = ref(today), popular = ref<PopularPage[]>([]), popularLoading = ref(false), popularLoaded = ref(false), popularError = ref(""), periodError = ref("");
let summaryRequest: AbortController | undefined, detailRequest: AbortController | undefined, popularRequest: AbortController | undefined;

// Filters: "" is the default of each menu (Moje úpravy, this year, the last 30 days).
const scopeOptions: ChoiceOption[] = [
  { value: "", label: t.activity.myEdits, icon: "account" },
  { value: "wiki", label: t.activity.wikiActivity, icon: "users" },
];
const yearOptions: ChoiceOption[] = Array.from({ length: 10 }, (_, index) => ({ value: index ? String(currentYear - index) : "", label: index ? String(currentYear - index) : `${currentYear} (letos)`, icon: "calendar" }));
const periodOptions: ChoiceOption[] = [
  { value: "7", label: t.activity.days7, icon: "calendar" },
  { value: "", label: t.activity.days30, icon: "calendar" },
  { value: "90", label: t.activity.days90, icon: "calendar" },
  { value: "365", label: t.activity.year, icon: "calendar" },
  { value: "custom", label: t.activity.customPeriod, icon: "pencil" },
];
const scopeFilter = computed({ get: () => (scope.value === "wiki" ? "wiki" : ""), set: (value: string) => (scope.value = value || "mine") });
const yearFilter = computed({ get: () => (year.value === currentYear ? "" : String(year.value)), set: (value: string) => (year.value = value ? Number(value) : currentYear) });
const period = ref("");
watch(period, (value) => {
  if (value === "custom") return;
  from.value = shiftDay(today, -(Number(value || 30) - 1));
  to.value = today;
});
const activityChips = computed(() => [
  ...(scope.value === "wiki" ? [{ key: "scope", icon: "users", label: t.activity.scopeChip, clear: () => (scopeFilter.value = "") }] : []),
  ...(year.value !== currentYear ? [{ key: "year", icon: "calendar", label: t.activity.yearChip(year.value), clear: () => (yearFilter.value = "") }] : []),
]);
const periodChips = computed(() => {
  if (!period.value) return [];
  const label = period.value === "custom" ? `${formatDate(from.value)} – ${formatDate(to.value)}` : periodOptions.find((option) => option.value === period.value)!.label;
  return [{ key: "period", icon: "calendar", label: t.activity.periodChip(label), clear: () => (period.value = "") }];
});

// Lists replaced as a whole (another day, period or scope) fade in once; rows appended by Další změny rise in and glow.
const summaryKey = computed(() => `${summaryYear.value}:${scope.value}`);
const periodKey = computed(() => `${from.value}:${to.value}`);
const shownAuthors = computed(() => (scope.value === "wiki" ? summary.value?.authors ?? [] : []));
// The day and the authors stay rendered while their section folds away.
const shownDay = ref(""), authors = ref<NonNullable<ActivitySummary["authors"]>>([]);
watch(selected, (day) => { if (day) shownDay.value = day; });
watch(shownAuthors, (items) => { if (items.length) authors.value = items; });

async function loadSummary() {
  summaryRequest?.abort(); detailRequest?.abort();
  const request = summaryRequest = new AbortController();
  selected.value = ""; changes.value = []; detailLoaded.value = false; detailError.value = ""; more.value = false;
  loading.value = true; error.value = "";
  const requestedYear = year.value;
  try {
    const result = await api<ActivitySummary>("/analytics/activity?" + periodQuery(requestedYear + "-01-01", requestedYear + "-12-31") + "&scope=" + scope.value, { signal: request.signal });
    if (!request.signal.aborted) { summary.value = result; summaryYear.value = requestedYear; }
  } catch (failure) { if (!request.signal.aborted) error.value = errorMessage(failure); }
  finally { if (!request.signal.aborted) loading.value = false; }
}
async function loadDay(day: string, append = false) {
  detailRequest?.abort();
  const request = detailRequest = new AbortController();
  if (!append) selected.value = day;
  detailLoading.value = true; detailError.value = "";
  try {
    const result = await api<ActivityChanges>("/analytics/changes?" + new URLSearchParams({ day, scope: scope.value, offset: String(append ? changes.value.length : 0) }), { signal: request.signal });
    if (!request.signal.aborted) { changes.value = append ? [...changes.value, ...result.items] : result.items; more.value = result.hasMore; detailLoaded.value = true; }
  } catch (failure) { if (!request.signal.aborted) { if (!append) { changes.value = []; more.value = false; } detailError.value = errorMessage(failure); } }
  finally { if (!request.signal.aborted) detailLoading.value = false; }
}
async function loadPopular() {
  popularRequest?.abort();
  const request = popularRequest = new AbortController();
  popularError.value = ""; periodError.value = ""; popularLoading.value = false;
  if (!validPeriod(from.value, to.value)) { popular.value = []; periodError.value = t.activity.pickPeriod; return; }
  popularLoading.value = true;
  try {
    const result = await api<{ items: PopularPage[] }>("/analytics/popular?" + periodQuery(from.value, to.value), { signal: request.signal });
    if (!request.signal.aborted) { popular.value = result.items; popularLoaded.value = true; }
  } catch (failure) { if (!request.signal.aborted) { popular.value = []; popularError.value = errorMessage(failure); } }
  finally { if (!request.signal.aborted) popularLoading.value = false; }
}
function open(id: string, event: MouseEvent) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  emit("select", id);
}
watch([year, scope], loadSummary, { immediate: true });
watch([from, to], loadPopular, { immediate: true });
onUnmounted(() => { summaryRequest?.abort(); detailRequest?.abort(); popularRequest?.abort(); });
</script>

<template>
  <div class="profile-activity">
    <SettingsSection :label="t.activity.savedChanges">
      <div class="activity-toolbar" role="search" :aria-label="t.activity.activityFilter">
        <FilterMenu v-model="scopeFilter" :options="scopeOptions" :label="t.activity.scope" icon="account" data-activity-scope />
        <FilterMenu v-model="yearFilter" :options="yearOptions" :label="t.activity.year" icon="calendar" data-activity-year />
        <p class="activity-count" aria-live="polite">
          <LoadingNote inline :active="loading" :label="summary ? t.activity.loading : t.activity.loadingActivity" />
          <template v-if="summary?.revisions"><strong>{{ t.activity.changes(summary.revisions) }}</strong> · {{ t.activity.pages(summary.pages) }}</template>
          <template v-else-if="summary">{{ t.activity.noChangesIn(summaryYear) }}</template>
        </p>
      </div>
      <Transition name="st-fade">
        <TransitionGroup v-if="activityChips.length" name="st-fade" tag="ul" class="activity-chips" :aria-label="t.activity.usedFilters">
          <li v-for="chip in activityChips" :key="chip.key"><Button variant="secondary" size="xs" type="button" class="activity-chip" :aria-label="t.activity.clearFilter(chip.label)" @click="chip.clear()"><AtlasIcon :name="chip.icon" :size="13" />{{ chip.label }}<AtlasIcon name="close" :size="13" /></Button></li>
        </TransitionGroup>
      </Transition>
      <ErrorState v-if="error" :title="t.activity.loadFailed" :text="error" :busy="loading" @retry="loadSummary" />
      <LoadingRegion v-else :loading="loading" :ready="true" :label="t.activity.loadingActivity" class="activity-summary">
        <ActivityHeatmap :days="summary?.days ?? []" :from="summaryYear + '-01-01'" :to="summaryYear + '-12-31'" :selected="selected" :label="t.activity.calendar" @select="loadDay" />
        <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.activity.note }}</p>
      </LoadingRegion>
    </SettingsSection>

    <InlineReveal :open="!!selected">
      <SettingsSection v-if="shownDay" :title="t.activity.changesDay(formatDay(shownDay))" icon="history" :count="detailLoaded ? changes.length : undefined">
        <template #title-extra><LoadingNote inline :active="detailLoading && detailLoaded" :label="t.activity.loading" /></template>
        <ErrorState v-if="detailError" :title="t.activity.dayFailed" :text="detailError" :busy="detailLoading" @retry="loadDay(selected, changes.length > 0)" />
        <LoadingRegion :loading="detailLoading" :ready="detailLoaded" :rows="3" row-height="56px" :label="t.activity.loadingChanges" class="activity-rows">
          <SettingsList v-if="changes.length" :key="shownDay" :label="t.activity.dayChanges" class="activity-fade">
            <SettingsListRow v-for="change in changes" :key="change.pageId + ':' + change.version" title="" icon="page" :description="t.activity.changeLine(change.authorName, formatActivityTime(change.at), change.version)">
              <template #title><a class="activity-page-link" :href="pageLink(change.pageId)" data-change-page @click="open(change.pageId, $event)">{{ change.title }}</a></template>
            </SettingsListRow>
          </SettingsList>
          <EmptyState v-else-if="!detailError" compact icon="calendar" :title="t.activity.noChanges" :text="t.activity.noChangesDay" />
          <div v-if="more" class="st-actions"><LoadMoreButton :busy="detailLoading" @click="loadDay(selected, true)" /></div>
        </LoadingRegion>
      </SettingsSection>
    </InlineReveal>

    <InlineReveal :open="shownAuthors.length > 0">
      <SettingsSection v-if="authors.length" :title="t.activity.topAuthors" icon="users" :count="authors.length" :description="t.activity.topAuthorsNote(summaryYear)">
        <SettingsList :key="summaryKey" :label="t.activity.topAuthors" class="activity-fade">
          <SettingsListRow v-for="author in authors" :key="author.userId" :title="author.userName" :description="`${t.activity.changes(author.revisions)} · ${t.activity.pages(author.pages)}`">
            <template #leading><SubjectAvatar kind="user" :name="author.userName" :user-id="author.userId" /></template>
          </SettingsListRow>
        </SettingsList>
      </SettingsSection>
    </InlineReveal>

    <SettingsSection :title="t.activity.popularPages" icon="bar-chart-3" :description="t.activity.viewsNote">
      <div class="activity-toolbar" role="search" :aria-label="t.activity.readershipFilter">
        <FilterMenu v-model="period" :options="periodOptions" :label="t.activity.period" icon="calendar" data-popular-period />
        <InlineReveal :open="period === 'custom'" class="activity-dates">
          <div class="activity-dates-fields">
            <Input v-model="from" type="date" :aria-label="t.activity.readFrom" :aria-invalid="periodError ? true : undefined" />
            <span aria-hidden="true">–</span>
            <Input v-model="to" type="date" :aria-label="t.activity.readTo" :aria-invalid="periodError ? true : undefined" />
          </div>
        </InlineReveal>
        <p class="activity-count"><LoadingNote inline :active="popularLoading && popularLoaded" :label="t.activity.loading" /></p>
      </div>
      <Transition name="st-fade">
        <TransitionGroup v-if="periodChips.length" name="st-fade" tag="ul" class="activity-chips" :aria-label="t.activity.usedFilters">
          <li v-for="chip in periodChips" :key="chip.key"><Button variant="secondary" size="xs" type="button" class="activity-chip" :aria-label="t.activity.clearFilter(chip.label)" @click="chip.clear()"><AtlasIcon :name="chip.icon" :size="13" />{{ chip.label }}<AtlasIcon name="close" :size="13" /></Button></li>
        </TransitionGroup>
      </Transition>
      <Transition :css="false" v-bind="disclosureHooks">
        <p v-if="periodError" class="activity-period-error" role="alert"><AtlasIcon name="triangle-alert" :size="14" />{{ periodError }}</p>
      </Transition>
      <ErrorState v-if="!periodError && popularError" :title="t.activity.popularFailed" :text="popularError" :busy="popularLoading" @retry="loadPopular" />
      <LoadingRegion v-else-if="!periodError" :loading="popularLoading" :ready="popularLoaded" :rows="5" row-height="56px" :label="t.activity.loadingReadership" class="activity-rows">
        <SettingsList v-if="popular.length" :key="periodKey" :label="t.activity.popularPages" class="activity-fade">
          <SettingsListRow v-for="item in popular" :key="item.pageId" title="" icon="page" :description="`${t.activity.views(item.views)} · ${t.activity.readers(item.readers)}`">
            <template #title><a class="activity-page-link" :href="pageLink(item.pageId)" data-popular-page @click="open(item.pageId, $event)">{{ item.title }}</a></template>
          </SettingsListRow>
        </SettingsList>
        <EmptyState v-else compact icon="bar-chart-3" :title="t.activity.noViews" :text="t.activity.noViewsPeriod" />
      </LoadingRegion>
    </SettingsSection>
  </div>
</template>

<style scoped>
/* A column of flex items, not a grid: a grid track cannot give a negative margin back (the folded reveals below). */
.profile-activity { display: flex; flex-direction: column; gap: 18px; min-width: 0; }
/* A folded InlineReveal still takes its gap in the column (its wrapper is never :empty), which stacked 54 px between the
   two cards. Its section's own lead takes the gap's place instead, so a folded one takes nothing and opening grows the
   gap with the height (as the appearance picker's panels do). */
.profile-activity > .inline-reveal-root { margin-top: -18px; }
.profile-activity > .inline-reveal-root > :deep(.inline-reveal > .inline-reveal-inner) { padding-top: 18px; }
.activity-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; min-width: 0; }
.activity-count { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 0 0 0 auto; color: var(--muted); font-size: 13px; font-variant-numeric: tabular-nums; }
.activity-count strong { color: var(--accent); font-weight: 650; }
.activity-chips { display: flex; flex-wrap: wrap; gap: 6px; margin: 0; padding: 0; list-style: none; }
.activity-chip { border-radius: 999px; }
.activity-summary > :deep(.loading-region-content) { display: grid; gap: 12px; }
.activity-dates-fields { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; color: var(--muted); }
.activity-dates-fields input { width: auto; min-width: 150px; }
.activity-period-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 12.5px; font-weight: 550; }
.activity-fade { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
/* A list inside a LoadingRegion reaches the card edges like a list placed in the card directly (settings.css): its lines
   run across the card and its first and last rows are not inset twice (33 px of air above the first title). An empty
   state there is frameless, as one directly in a card is. */
.activity-rows :deep(.loading-region-content > .settings-list) { margin-inline: calc(var(--st-pad-x, 18px) * -1); --st-row-pad-x: var(--st-pad-x, 18px); }
.activity-rows:first-child :deep(.loading-region-content > .settings-list:first-child) { margin-top: calc(var(--st-body-pad-y, 16px) * -1); }
.activity-rows:last-child :deep(.loading-region-content > .settings-list:last-child) { margin-bottom: calc(var(--st-body-pad-y, 16px) * -1); }
.activity-rows :deep(.loading-region-content > .st-empty) { border-style: none; padding-block: 20px; }
.activity-page-link { color: var(--text); text-decoration: none; border-radius: 4px; overflow-wrap: anywhere; transition: color var(--atlas-motion-fast); }
@media (hover: hover) and (pointer: fine) {
  .activity-page-link:hover { color: var(--accent); text-decoration: underline; text-underline-offset: 3px; }
}
.activity-page-link:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
</style>
