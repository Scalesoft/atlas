<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import { ApiError, errorMessage } from "../../../api";
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-space";
import { searchGapsApi, type SearchGap, type SearchGapDays, type SearchGapsApi } from "../../../searchGaps";
import { collapseHeight } from "../../../motion/aiMotion";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import OptionPills from "../OptionPills.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import StatusBadge from "../StatusBadge.vue";
import SettingsSection from "../SettingsSection.vue";
import { useSettingsContext } from "../context";
import { disclosureHooks } from "../disclosure";
import { useSettingsFlash } from "../flash";
import type { ChoiceOption } from "../types";

/**
 * Nenalezené dotazy (G2): what people searched for in this space and did not find, as seen by the
 * space administrator. A statistic of queries searched by at least `minimumUsers` different people,
 * without names. Rule-based, not a model: no AI glyph. The server answers 403 or 404 to everyone who is not
 * an administrator of the space, and then the section does not exist.
 */
const props = withDefaults(defineProps<{ spaceId: string; api?: SearchGapsApi }>(), { api: () => searchGapsApi });
const context = useSettingsContext();
const flash = useSettingsFlash();

const periods: ChoiceOption[] = [
  { value: "7", label: t.gaps.days(7), icon: "clock" },
  { value: "30", label: t.gaps.days(30), icon: "clock" },
  { value: "90", label: t.gaps.days(90), icon: "clock" },
];
const days = ref("30");
const items = ref<SearchGap[]>([]);
const minimumUsers = ref(3);
const retentionDays = ref(90);
const loaded = ref(false);
const loading = ref(false);
const unavailable = ref(false);
const loadError = ref("");
const busy = ref<string | null>(null);
const resolveError = ref<{ gap: SearchGap; text: string } | null>(null);
const root = ref<HTMLElement>();
let request: AbortController | undefined;

async function load() {
  request?.abort();
  const controller = (request = new AbortController());
  loading.value = true;
  loadError.value = "";
  try {
    const result = await props.api.list(props.spaceId, Number(days.value) as SearchGapDays, controller.signal);
    if (controller.signal.aborted) return;
    items.value = result.items;
    minimumUsers.value = result.minimumUsers;
    retentionDays.value = result.retentionDays;
    loaded.value = true;
  } catch (failure) {
    if (controller.signal.aborted) return;
    if (failure instanceof ApiError && [403, 404].includes(failure.status)) unavailable.value = true;
    else loadError.value = errorMessage(failure);
  } finally {
    if (request === controller) loading.value = false;
  }
}
watch(days, () => void load());
void load();
onBeforeUnmount(() => request?.abort());

/** The gap leaves the list; its row folds away first (A0 `collapseHeight`, a fade with reduced motion). */
function remove(gap: SearchGap) {
  const drop = () => { items.value = items.value.filter((item) => item.normalizedQuery !== gap.normalizedQuery); };
  const row = root.value?.querySelector<HTMLElement>(`[data-search-gap="${CSS.escape(gap.normalizedQuery)}"]`);
  const animation = row ? collapseHeight(row) : null;
  if (animation) void animation.finished.then(drop, drop);
  else drop();
}
async function resolve(gap: SearchGap) {
  if (busy.value) return;
  busy.value = gap.normalizedQuery;
  resolveError.value = null;
  try {
    await props.api.resolve(props.spaceId, gap.normalizedQuery);
    flash.notify(t.gaps.resolved(gap.query));
    remove(gap);
  } catch (failure) {
    resolveError.value = { gap, text: errorMessage(failure) };
  } finally {
    busy.value = null;
  }
}

const day = (value: string) => {
  const [year, month, date] = value.split("-").map(Number);
  return year && month && date ? new Date(year, month - 1, date).toLocaleDateString(locale, { dateStyle: "medium" }) : value;
};
const seen = (gap: SearchGap) => t.gaps.seen(gap.users, day(gap.lastSearchedOn));
const emptyTitle = computed(() => t.gaps.emptyTitle(minimumUsers.value));
const description = computed(() => t.gaps.description(minimumUsers.value, retentionDays.value));
</script>

<template>
  <div ref="root">
    <EmptyState v-if="unavailable" icon="compass" :title="t.missing.title" :text="t.missing.text" />
    <SettingsPage v-else :title="t.gaps.title" icon="search" :description="description">
      <SettingsSection :title="t.gaps.section" icon="search" :count="loaded ? items.length : undefined">
        <OptionPills v-model="days" :options="periods" :label="t.gaps.period" data-period />
        <ErrorState v-if="loadError" :title="t.gaps.loadFailed" :text="loadError" :busy="loading" @retry="load" />
        <LoadingSkeleton v-else-if="!loaded" :label="t.gaps.loading" />
        <template v-else>
          <ErrorState v-if="resolveError" :title="t.gaps.resolveFailed" :text="resolveError.text" :busy="busy !== null" @retry="resolve(resolveError.gap)" />
          <div :aria-busy="loading" class="gap-rows">
            <SettingsList v-if="items.length" :label="t.gaps.list">
              <SettingsListRow v-for="gap in items" :key="gap.normalizedQuery" :title="t.gaps.quoted(gap.query)" :description="seen(gap)" icon="search" :data-search-gap="gap.normalizedQuery">
                <template v-if="gap.unanswered || gap.reported" #meta>
                  <StatusBadge v-if="gap.unanswered" tone="neutral" icon="circle-help" :label="t.gaps.unanswered(gap.unanswered)" size="sm" data-gap-unanswered />
                  <StatusBadge v-if="gap.reported" tone="neutral" icon="megaphone" :label="t.gaps.reported(gap.reported)" size="sm" data-gap-reported />
                </template>
                <template #actions>
                  <span class="gap-actions">
                    <Button variant="outline" size="sm" type="button" data-create-page @click="context.newPage(spaceId, { title: gap.query })"><AtlasIcon name="file-plus" :size="15" />{{ t.gaps.createPage }}</Button>
                    <Button variant="ghost" size="sm" type="button" data-resolve-gap :disabled="busy !== null" @click="resolve(gap)"><AtlasIcon name="circle-check" :size="15" />{{ t.gaps.markResolved }}</Button>
                  </span>
                </template>
              </SettingsListRow>
            </SettingsList>
            <!-- The last row has folded away: the empty state unfolds in its place instead of popping in (+134 px in one frame). -->
            <Transition :css="false" v-bind="disclosureHooks"><EmptyState v-if="!items.length" compact icon="search" :title="emptyTitle" /></Transition>
          </div>
        </template>
      </SettingsSection>
    </SettingsPage>
  </div>
</template>

<style scoped>
/* The list reaches the card edges and its bottom like a list placed in the card directly (settings.css), and the empty
   state is frameless there, as one directly in a card is (it drew a dashed box inside the card). */
.gap-rows > .settings-list { margin: 0 calc(var(--st-pad-x, 18px) * -1) calc(var(--st-body-pad-y, 16px) * -1); --st-row-pad-x: var(--st-pad-x, 18px); }
.gap-rows > .st-empty.is-compact { border-style: none; padding-block: 20px; }
/* Two actions on one row: side by side while there is room, stacked on a phone instead of overflowing. */
.gap-actions { display: flex; flex-wrap: wrap; justify-content: inherit; gap: 4px; min-width: 0; }
</style>
