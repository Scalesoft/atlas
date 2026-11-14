<script setup lang="ts">
import SearchField from "../SearchField.vue";
import { computed, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { Checkbox } from "../../ui/checkbox";
import { Switch } from "../../ui/switch";
import { ApiError, errorMessage } from "../../../api";
import { contentImportApi, type WikiImportFinding, type WikiImportItem, type WikiImportReport } from "../../../contentImport";
import { spaceLink } from "../../../pageLinks";
import LoadingNote from "../LoadingNote.vue";
import OptionPills from "../OptionPills.vue";
import t from "../../../i18n/settings-modules";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatTile from "../StatTile.vue";
import StatusBadge from "../StatusBadge.vue";
import ProgressiveFlow from "../ProgressiveFlow.vue";
import type { ChoiceOption, FlowStep, StatusTone } from "../types";
import { useDraftGuard } from "../dirtyGuard";
import { disclosureHooks } from "../disclosure";
import ZipDropzone from "./ZipDropzone.vue";

/**
 * Import Wiki.js (module `wiki-js-import`) in three phases: 1 Soubor (source, space name, the ZIP and
 * advanced options) · 2 Náhled (what the import creates, per-page conversion findings and two explicit
 * acknowledgements) · 3 Hotovo. Nothing is written before the confirmation; an interrupted
 * confirmation is checked against the same batch instead of importing again.
 */
const emit = defineEmits<{ imported: [spaceId: string] }>();
const maxZip = 64 * 1024 * 1024;
const defaultSpaceName = "Import Wiki.js";
const file = shallowRef<File | null>(null);
const report = shallowRef<WikiImportReport | null>(null);
const sourceKey = ref("");
const spaceName = ref(defaultSpaceName);
const stripPrefix = ref("");
const sourceBaseUrl = ref("");
const updateExisting = ref(false);
const acknowledgeWarnings = ref(false);
const acknowledgeQuarantine = ref(false);
const busy = ref(false);
const error = ref("");
const uncertain = ref(false);
const filter = ref("");
const show = ref("");
const visibleCount = ref(50);
const step = ref("source");
const flowKey = ref(0);
let disposed = false;
let epoch = 0;
let abort: AbortController | undefined;

const dirty = computed(
  () => busy.value || (!report.value?.completedAt && (file.value !== null || sourceKey.value !== "" || stripPrefix.value !== "" || sourceBaseUrl.value !== "" || spaceName.value !== defaultSpaceName || updateExisting.value)),
);
const sourceKeyError = computed(() => (sourceKey.value && !/^[a-zA-Z0-9][a-zA-Z0-9._-]*$/.test(sourceKey.value) ? t.wikiJs.sourceKeyInvalid : undefined));
// Reports stored before the per-page conversion report have no findings.
const findings = (item: { findings?: WikiImportFinding[] | null }) => item.findings ?? [];
// "3×, poprvé na řádku 12": occurrences are aggregated, the location is the first one.
const where = (finding: WikiImportFinding) =>
  [finding.count > 1 ? t.wikiJs.occurrences(finding.count) : "", finding.line ? (finding.count > 1 ? t.wikiJs.firstAtLine(finding.line) : t.wikiJs.atLine(finding.line)) : ""].filter(Boolean).join(", ");
const pagesWithFindings = computed(() => (report.value?.items ?? []).filter((item) => findings(item).length > 0).length);
const filtered = computed(() => {
  const text = filter.value.toLocaleLowerCase("cs");
  return (report.value?.items ?? []).filter((item) => (show.value !== "findings" || findings(item).length > 0) && (!text || `${item.path} ${item.title ?? ""}`.toLocaleLowerCase("cs").includes(text)));
});
const items = computed(() => filtered.value.slice(0, visibleCount.value));
const canConfirm = computed(() => !!report.value?.canCommit && !report.value.completedAt && acknowledgeQuarantine.value && acknowledgeWarnings.value && !busy.value && !uncertain.value);
watch([filter, show], () => (visibleCount.value = 50));
// A reviewed batch only describes the exact source options used to prepare it.
watch([file, sourceKey, spaceName, stripPrefix, sourceBaseUrl, updateExisting], () => {
  if (report.value && !uncertain.value && !busy.value) dropPreview();
}, { flush: "sync" });

const actions: Record<string, { label: string; tone: StatusTone; icon: string }> = {
  create: { label: t.wikiJs.actions.create, tone: "success", icon: "file-plus" },
  update: { label: t.wikiJs.actions.update, tone: "info", icon: "history" },
  skip: { label: t.wikiJs.actions.skip, tone: "neutral", icon: "check" },
  conflict: { label: t.wikiJs.actions.conflict, tone: "danger", icon: "triangle-alert" },
  copy: { label: t.wikiJs.actions.copy, tone: "neutral", icon: "paperclip" },
  preserve: { label: t.wikiJs.actions.preserve, tone: "neutral", icon: "folder-input" },
};
const actionOf = (item: WikiImportItem) => actions[item.action] ?? { label: item.action, tone: "neutral" as const, icon: "info" };
const impacts: Record<WikiImportFinding["impact"], { label: string; tone: StatusTone; icon: string }> = {
  degraded: { label: t.wikiJs.impacts.degraded, tone: "warning", icon: "pencil" },
  unresolved: { label: t.wikiJs.impacts.unresolved, tone: "info", icon: "unlink" },
  lost: { label: t.wikiJs.impacts.lost, tone: "neutral", icon: "folder-input" },
};
const showOptions: ChoiceOption[] = [
  { value: "", label: t.wikiJs.show.all, icon: "list" },
  { value: "findings", label: t.wikiJs.show.findings, icon: "triangle-alert" },
];
const counts = computed(() => {
  const value = report.value?.counts;
  if (!value) return [];
  return [
    { key: "create", label: t.wikiJs.counts.create, value: value.create, icon: "file-plus" },
    { key: "update", label: t.wikiJs.counts.update, value: value.update, icon: "history" },
    { key: "skip", label: t.wikiJs.counts.skip, value: value.skip, icon: "check" },
    { key: "assets", label: t.wikiJs.counts.assets, value: value.assets, icon: "paperclip" },
    { key: "conflict", label: t.wikiJs.counts.conflict, value: value.conflict, icon: "triangle-alert" },
    { key: "unsupported", label: t.wikiJs.counts.unsupported, value: value.unsupported, icon: "folder-input" },
  ];
});
const findingsSummary = computed(() => t.wikiJs.findingsSummary(pagesWithFindings.value));

const steps = computed<FlowStep[]>(() => [
  {
    id: "source",
    label: t.wikiJs.steps.source.label,
    icon: "upload",
    title: t.wikiJs.steps.source.title,
    hint: t.wikiJs.steps.source.hint,
    summary: file.value?.name,
    ready: !!file.value && !!sourceKey.value.trim() && !sourceKeyError.value && !!spaceName.value.trim(),
    manual: true,
    complete: !!report.value,
    disabled: uncertain.value,
    nextLabel: t.wikiJs.steps.source.next,
    nextIcon: "eye",
  },
  {
    id: "review",
    label: t.wikiJs.steps.review.label,
    icon: "list-checks",
    title: t.wikiJs.steps.review.title(report.value?.spaceName ?? spaceName.value),
    hint: t.wikiJs.steps.review.hint,
    ready: canConfirm.value,
    manual: true,
    nextLabel: t.wikiJs.steps.review.next,
    nextIcon: "check",
  },
  { id: "done", label: t.wikiJs.steps.done.label, icon: "circle-check", title: t.wikiJs.steps.done.title, final: true },
]);

function reset() {
  flowKey.value++;
  epoch++;
  abort?.abort();
  file.value = null;
  report.value = null;
  sourceKey.value = "";
  spaceName.value = defaultSpaceName;
  stripPrefix.value = "";
  sourceBaseUrl.value = "";
  updateExisting.value = false;
  acknowledgeQuarantine.value = false;
  acknowledgeWarnings.value = false;
  busy.value = false;
  error.value = "";
  uncertain.value = false;
  filter.value = "";
  show.value = "";
  step.value = "source";
}
function dropPreview() {
  report.value = null;
  acknowledgeQuarantine.value = false;
  acknowledgeWarnings.value = false;
  error.value = "";
}
async function preview() {
  if (busy.value || !file.value) return;
  busy.value = true;
  error.value = "";
  const current = ++epoch;
  const controller = (abort = new AbortController());
  try {
    const result = await contentImportApi.preview(
      file.value,
      { sourceKey: sourceKey.value, spaceName: spaceName.value, stripPrefix: stripPrefix.value || undefined, sourceBaseUrl: sourceBaseUrl.value || undefined, updateExisting: updateExisting.value },
      controller.signal,
    );
    if (disposed || current !== epoch) return;
    report.value = result;
    acknowledgeWarnings.value = false;
    acknowledgeQuarantine.value = false;
    step.value = "review";
  } catch (reason) {
    if (!disposed && current === epoch && !controller.signal.aborted) error.value = errorMessage(reason);
  } finally {
    if (!disposed && current === epoch) busy.value = false;
  }
}
function finished(result: WikiImportReport) {
  report.value = result;
  uncertain.value = false;
  if (result.completedAt) {
    step.value = "done";
    if (result.spaceId) emit("imported", result.spaceId);
  }
}
async function confirm() {
  if (!canConfirm.value || !report.value) return;
  busy.value = true;
  error.value = "";
  const current = ++epoch;
  try {
    const result = await contentImportApi.commit(report.value);
    if (disposed || current !== epoch) return;
    finished(result);
  } catch (reason) {
    if (disposed || current !== epoch) return;
    error.value = errorMessage(reason);
    if (reason instanceof ApiError && [401, 403, 404].includes(reason.status)) {
      // Access was revoked or the batch is gone: the private report must not stay on screen.
      report.value = null;
      file.value = null;
      step.value = "source";
    } else if (!(reason instanceof ApiError) || reason.status >= 500) uncertain.value = true;
    else if ([409, 410].includes(reason.status) && report.value) report.value = { ...report.value, canCommit: false };
  } finally {
    if (!disposed && current === epoch) busy.value = false;
  }
}
async function checkStatus() {
  if (!report.value || busy.value) return;
  busy.value = true;
  error.value = "";
  const current = ++epoch;
  try {
    const result = await contentImportApi.get(report.value.id);
    if (disposed || current !== epoch) return;
    finished(result);
  } catch (reason) {
    if (!disposed && current === epoch) error.value = errorMessage(reason);
  } finally {
    if (!disposed && current === epoch) busy.value = false;
  }
}
function onNext(current: FlowStep) {
  if (current.id === "source") void preview();
  else if (current.id === "review") void confirm();
}
useDraftGuard(dirty, { discard: reset });
onBeforeUnmount(() => {
  disposed = true;
  epoch++;
  abort?.abort();
});
</script>

<template>
  <div class="content-import wiki-import" :aria-busy="busy">
    <SettingsSection :label="t.wikiJs.title">
      <ProgressiveFlow :key="flowKey" v-model="step" :steps="steps" :label="t.wikiJs.title" :busy="busy" :cancel-disabled="uncertain" @next="onNext" @cancel="reset">
        <template #source>
          <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.wikiJs.sourceKey.label" :help="t.wikiJs.sourceKey.help" :error="sourceKeyError">
            <input :id="id" v-model="sourceKey" class="st-input st-mono" maxlength="80" :placeholder="t.wikiJs.sourceKey.placeholder" autocomplete="off" spellcheck="false" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="busy" data-step-focus data-import-source />
          </SettingsRow>
          <SettingsRow v-slot="{ id }" :label="t.wikiJs.spaceName">
            <input :id="id" v-model="spaceName" class="st-input" maxlength="200" :disabled="busy" data-import-space />
          </SettingsRow>
          <SettingsRow :label="t.wikiJs.zip.label" :help="t.wikiJs.zip.help" :label-for="false" stacked>
            <ZipDropzone v-model="file" :label="t.wikiJs.zip.drop" :max-bytes="maxZip" :disabled="busy || uncertain" />
          </SettingsRow>
          <SettingsSection
            :title="t.wikiJs.advanced.title"
            icon="sliders-horizontal"
            advanced
            :summary="[stripPrefix ? t.wikiJs.advanced.withoutFolder(stripPrefix) : t.wikiJs.advanced.autoRoot, sourceBaseUrl ? t.wikiJs.advanced.convertLinks : '', updateExisting ? t.wikiJs.advanced.updateDrafts : t.wikiJs.advanced.noOverwrite].filter(Boolean).join(' · ')"
          >
            <SettingsRow v-slot="{ id, describedBy }" :label="t.wikiJs.advanced.stripLabel" :help="t.wikiJs.advanced.stripHelp">
              <input :id="id" v-model="stripPrefix" class="st-input st-mono" :placeholder="t.wikiJs.advanced.stripPlaceholder" maxlength="1024" :aria-describedby="describedBy" :disabled="busy" />
            </SettingsRow>
            <SettingsRow v-slot="{ id, describedBy }" :label="t.wikiJs.advanced.baseLabel" :help="t.wikiJs.advanced.baseHelp">
              <input :id="id" v-model="sourceBaseUrl" class="st-input" inputmode="url" :placeholder="t.wikiJs.advanced.basePlaceholder" maxlength="2048" :aria-describedby="describedBy" :disabled="busy" />
            </SettingsRow>
            <SettingsRow v-slot="{ id, describedBy }" :label="t.wikiJs.advanced.updateLabel" :help="t.wikiJs.advanced.updateHelp">
              <Switch :id="id" v-model="updateExisting" :aria-describedby="describedBy" :disabled="busy" data-import-update />
            </SettingsRow>
          </SettingsSection>
          <div class="wiki-import-status">
            <Transition :css="false" v-bind="disclosureHooks"><div v-if="busy && !report"><LoadingNote :label="t.wikiJs.preparing" /></div></Transition>
            <Transition :css="false" v-bind="disclosureHooks"><p v-if="error && !report" class="wiki-import-error" role="alert" data-import-error><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p></Transition>
          </div>
        </template>

        <template #review>
          <template v-if="report">
            <section class="wiki-import-counts" :aria-label="t.wikiJs.countsLabel">
              <StatTile v-for="item in counts" :key="item.key" :label="item.label" :value="String(item.value)" :icon="item.icon" :data-count="item.key" />
            </section>
            <ul v-if="report.warnings.length" class="wiki-import-warnings" :aria-label="t.wikiJs.warningsLabel">
              <li v-for="warning in report.warnings" :key="warning" class="st-note"><AtlasIcon name="info" :size="15" />{{ warning }}</li>
            </ul>
            <nav class="st-actions" :aria-label="t.wikiJs.sourceNavLabel">
              <a class="st-link" :href="contentImportApi.sourceUrl(report.id)" download><AtlasIcon name="download" :size="14" />{{ t.wikiJs.originalZip }}</a>
              <a class="st-link" :href="contentImportApi.reportUrl(report.id)" download><AtlasIcon name="download" :size="14" />{{ t.wikiJs.downloadReport }}</a>
            </nav>
            <SearchField v-model="filter" :label="t.wikiJs.filterLabel" :placeholder="t.wikiJs.filterPlaceholder" data-import-filter />
            <p v-if="pagesWithFindings" class="st-note wiki-import-findings-summary"><AtlasIcon name="triangle-alert" :size="15" />{{ findingsSummary }}</p>
            <OptionPills v-if="pagesWithFindings" v-model="show" :options="showOptions" :label="t.wikiJs.showLabel" data-import-show />
            <SettingsList :label="t.wikiJs.itemsLabel" class="wiki-import-items">
              <SettingsListRow v-for="item in items" :key="item.path" :title="item.title || item.path" :icon="actionOf(item).icon" :data-import-item="item.path">
                <template v-if="item.title" #description><code class="st-mono">{{ item.path }}</code></template>
                <template #meta><StatusBadge :tone="actionOf(item).tone" :icon="actionOf(item).icon" :label="actionOf(item).label" size="sm" /></template>
                <div v-if="item.tags?.length || item.warnings.length || findings(item).length" class="wiki-import-item-details">
                  <ul v-if="item.tags?.length" class="wiki-import-tags" :aria-label="t.wikiJs.tagsLabel">
                    <li v-for="tag in item.tags" :key="tag"><StatusBadge :label="tag" icon="hash" size="sm" /></li>
                  </ul>
                  <ul v-if="item.warnings.length" class="wiki-import-item-warnings">
                    <li v-for="warning in item.warnings" :key="warning">{{ warning }}</li>
                  </ul>
                  <ul v-if="findings(item).length" class="wiki-import-findings" :aria-label="t.wikiJs.conversionLabel(item.title || item.path)">
                    <li v-for="finding in findings(item)" :key="`${finding.construct}-${finding.impact}`" :data-impact="finding.impact">
                      <StatusBadge :tone="impacts[finding.impact]?.tone ?? 'neutral'" :icon="impacts[finding.impact]?.icon ?? 'info'" :label="impacts[finding.impact]?.label ?? finding.impact" size="sm" />
                      <span>{{ finding.message }}</span>
                      <span v-if="where(finding)" class="wiki-import-finding-where">{{ where(finding) }}</span>
                      <code v-if="finding.excerpt" class="st-mono">{{ finding.excerpt }}</code>
                    </li>
                  </ul>
                </div>
              </SettingsListRow>
            </SettingsList>
            <p v-if="!filtered.length" class="st-note"><AtlasIcon name="search" :size="15" />{{ t.wikiJs.noMatch }}</p>
            <div v-if="visibleCount < filtered.length" class="st-actions">
              <Button variant="outline" size="sm" type="button" data-import-more @click="visibleCount += 50">{{ t.wikiJs.showMore }}</Button>
            </div>

            <p v-if="!report.canCommit" class="st-note wiki-import-blocked" role="status"><AtlasIcon name="ban" :size="15" />{{ t.wikiJs.blocked }}</p>
            <Button v-if="!report.canCommit && !uncertain" variant="outline" type="button" :disabled="busy" data-import-new-preview @click="dropPreview"><AtlasIcon name="refresh" :size="15" />{{ t.wikiJs.newPreview }}</Button>
            <div v-if="uncertain" class="wiki-import-uncertain" role="status">
              <span><AtlasIcon name="triangle-alert" :size="15" />{{ t.wikiJs.uncertain }}</span>
              <Button variant="outline" size="sm" type="button" :disabled="busy" data-import-check @click="checkStatus"><AtlasIcon name="refresh" :size="15" />{{ t.wikiJs.checkResult }}</Button>
            </div>
            <fieldset class="wiki-import-confirm" :disabled="busy || !report.canCommit || uncertain">
              <legend class="st-sr-only">{{ t.wikiJs.confirmLegend }}</legend>
              <label class="wiki-import-check"><Checkbox :model-value="acknowledgeWarnings" :disabled="busy || !report.canCommit || uncertain" data-import-ack-warnings @update:model-value="acknowledgeWarnings = $event === true" />{{ t.wikiJs.ackReport }}</label>
              <label class="wiki-import-check"><Checkbox :model-value="acknowledgeQuarantine" :disabled="busy || !report.canCommit || uncertain" data-import-ack-quarantine @update:model-value="acknowledgeQuarantine = $event === true" />{{ t.wikiJs.ackQuarantine }}</label>
            </fieldset>
            <div class="wiki-import-status">
              <Transition :css="false" v-bind="disclosureHooks"><div v-if="busy"><LoadingNote :label="t.wikiJs.importing" /></div></Transition>
              <Transition :css="false" v-bind="disclosureHooks"><p v-if="error" class="wiki-import-error" role="alert" data-import-error><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p></Transition>
            </div>
          </template>
        </template>

        <template #done>
          <template v-if="report">
            <p class="wiki-import-success" role="status" data-import-done>
              <AtlasIcon name="circle-check" :size="16" />{{ t.wikiJs.done(report.spaceName) }}
            </p>
            <section class="wiki-import-counts" :aria-label="t.wikiJs.createdLabel">
              <StatTile v-for="item in counts" :key="item.key" :label="item.label" :value="String(item.value)" :icon="item.icon" />
            </section>
            <nav class="st-actions" :aria-label="t.wikiJs.sourceNavLabel">
              <a v-if="report.spaceId" class="st-link" :href="spaceLink(report.spaceId)" data-import-open-space>{{ t.wikiJs.openDrafts }}<AtlasIcon name="arrow-right" :size="14" /></a>
              <a class="st-link" :href="contentImportApi.sourceUrl(report.id)" download><AtlasIcon name="download" :size="14" />{{ t.wikiJs.originalZip }}</a>
              <a class="st-link" :href="contentImportApi.reportUrl(report.id)" download><AtlasIcon name="download" :size="14" />{{ t.wikiJs.downloadReport }}</a>
            </nav>
          </template>
        </template>

        <template #actions="{ step: current }">
          <Button v-if="current.final" variant="outline" type="button" data-import-again @click="reset"><AtlasIcon name="refresh" :size="16" />{{ t.wikiJs.another }}</Button>
        </template>
      </ProgressiveFlow>
    </SettingsSection>
  </div>
</template>

<style scoped>
.wiki-import { min-width: 0; }
.wiki-import-counts { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 130px), 1fr)); gap: 10px; }
.wiki-import-warnings, .wiki-import-tags, .wiki-import-findings { margin: 0; padding: 0; list-style: none; }
.wiki-import-warnings { display: grid; gap: 4px; }
.wiki-import-items { max-height: 480px; overflow: auto; padding-right: 4px; }
.wiki-import-item-details { display: grid; gap: 6px; padding: 0 0 12px 48px; color: var(--muted); font-size: 12.5px; }
.wiki-import-tags { display: flex; flex-wrap: wrap; gap: 4px; }
.wiki-import-item-warnings { margin: 0; padding-left: 16px; line-height: 1.6; }
.wiki-import-findings li { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 8px; padding: 6px 0; border-top: 1px dashed var(--line); color: var(--text); }
.wiki-import-findings li:first-child { border-top: 0; }
.wiki-import-finding-where { color: var(--muted); font-size: 11.5px; }
.wiki-import-findings code { flex-basis: 100%; white-space: pre-wrap; color: var(--muted); font-size: 12px; }
.wiki-import-uncertain { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px 12px; padding: 10px 12px; border: 1px solid color-mix(in srgb, var(--warning) 45%, var(--line)); border-radius: var(--st-radius); background: color-mix(in srgb, var(--warning-bg) 55%, var(--surface)); font-size: 13px; }
.wiki-import-uncertain > span { display: flex; align-items: center; gap: 8px; }
.wiki-import-uncertain svg { color: var(--warning); }
.wiki-import-confirm { display: grid; gap: 8px; margin: 0; padding: 0; border: 0; min-width: 0; }
.wiki-import-check { display: flex; align-items: flex-start; gap: 8px; font-size: 13.5px; line-height: 1.5; cursor: pointer; }
.wiki-import-check > :first-child { margin-top: 3px; }
/* What the step reports (working, failed) is a column of flex items: closed it cancels the step's 12 px grid gap with a negative
   margin, and each message carries that gap as its own top margin, so it unfolds with its space instead of pushing the buttons below. */
.wiki-import-status { display: flex; flex-direction: column; margin-top: -12px; }
.wiki-import-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
.wiki-import-status > * { margin-top: 12px; }
.wiki-import-success { display: flex; align-items: flex-start; gap: 8px; margin: 0; padding: 10px 12px; border: 1px solid color-mix(in srgb, var(--success) 40%, var(--line)); border-radius: var(--st-radius); background: color-mix(in srgb, var(--success-bg) 60%, var(--surface)); font-size: 13.5px; line-height: 1.55; }
.wiki-import-success > svg { flex-shrink: 0; margin-top: 3px; color: var(--success); }
/* The details sit in the default slot of SettingsListRow, whose root is the container: a narrow row drops the indent under the icon. */
@container (max-width: 480px) { .wiki-import-item-details { padding-left: 0; } }
</style>
