<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import AccessExplanation from "../settings/AccessExplanation.vue";
import ChoiceCards from "../settings/ChoiceCards.vue";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import ConflictNote from "../settings/ConflictNote.vue";
import { disclosureHooks } from "../settings/disclosure";
import ErrorState from "../settings/ErrorState.vue";
import ImpactPreview from "../settings/ImpactPreview.vue";
import ResultBanner from "../settings/ResultBanner.vue";
import SearchableChoiceList from "../settings/SearchableChoiceList.vue";
import SettingsList from "../settings/SettingsList.vue";
import SettingsListRow from "../settings/SettingsListRow.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import ResourceAccess from "../settings/access/ResourceAccess.vue";
import type { ChoiceOption, ImpactItem } from "../settings/types";
import { ApiError, errorMessage } from "../../api";
import { sharingApi, type AccessCandidate, type PageAccess, type PageAccessExplanation } from "../../sharing";
import { formatShortDate } from "../../formatDate";
import { accessPaths, explanationLine, pageRoleOptions, peopleCount } from "./pageAccessText";
import t from "../../i18n/components-sharing";

/**
 * The „Přístup“ part of the page access view for people who may manage it. The mode (space defaults or own access)
 * changes after an impact confirmation; who reaches the page is ResourceAccess (kind page): the same rows, levels,
 * in-place change, confirmed removal and Přidat přístup as every other resource. „Zobrazit jako“ explains the decision
 * for a person. Readers never get here: the server sends them only their own row. The page state (mode, counts, links)
 * is loaded in PageSharingView; this panel asks it to reload after every change (`changed`).
 */
const props = withDefaults(defineProps<{ pageId: string; access: PageAccess; linksHref?: string }>(), { linksHref: undefined });
const emit = defineEmits<{ changed: [] }>();
let disposed = false;

const title = computed(() => props.access.pageTitle ?? "");
const where = computed(() => ({ spaceName: props.access.spaceName, pageTitle: title.value }));
const counts = computed(() => props.access.counts);
const mode = computed(() => props.access.mode ?? "inherit");
const custom = computed(() => mode.value === "custom");
const viewers = computed(() => (counts.value?.manage ?? 0) + (counts.value?.edit ?? 0) + (counts.value?.read ?? 0));
const summary = computed(() => {
  const current = counts.value;
  const seen = t.panel.seen(peopleCount(viewers.value), props.access.isPublished);
  return current ? t.panel.summary(seen, current.manage, current.edit, current.read) : seen;
});
const resource = ref<InstanceType<typeof ResourceAccess>>();

// The result line and conflicts of a mode change; grant changes report inside ResourceAccess.
const notice = ref<{ key: number; text: string; icon: string }>(), conflict = ref("");
let noticeKey = 0;
function notify(text: string, icon = "circle-check") { notice.value = { key: ++noticeKey, text, icon }; }
/** A mode change changes who is in the list too: reload it here and the page state in PageSharingView. */
function refresh() {
  void resource.value?.reload();
  emit("changed");
}

// Space defaults or own access: the tiles only ask; the change happens after the impact confirmation.
const modeOptions = computed<ChoiceOption[]>(() => [
  { value: "inherit", label: t.panel.inherit, description: t.panel.inheritText(props.access.spaceName), icon: "layers", badge: mode.value === "inherit" ? t.panel.now : undefined },
  { value: "custom", label: t.panel.custom, description: t.panel.customText, icon: "lock", badge: mode.value === "custom" ? t.panel.now : undefined },
]);
const pendingMode = ref<"inherit" | "custom">(), modeBusy = ref(false), modeError = ref("");
function chooseMode(value: string | string[] | null) {
  const next = value === "custom" ? "custom" : "inherit";
  if (next === mode.value) return;
  modeError.value = "";
  pendingMode.value = next;
}
const modeImpact = computed<ImpactItem[]>(() => {
  const current = counts.value;
  if (pendingMode.value === "custom") return [
    { icon: "copy", label: t.panel.copied(current?.defaultGrants ?? 0), detail: t.panel.nobodyLoses, tone: "success" },
    { icon: "eye-off", label: t.panel.newMembers, tone: "warning" },
  ];
  const losing = current?.losing.length ? `${current.losing.join(", ")}${current.loseOnReset > current.losing.length ? t.panel.andMore : ""}` : undefined;
  return [
    { icon: "user-check", label: t.panel.gain(peopleCount(current?.hidden ?? 0)), detail: t.panel.otherMembers, tone: "success" },
    current?.loseOnReset
      ? { icon: "ban", label: t.panel.lose(peopleCount(current.loseOnReset)), detail: losing, tone: "danger" }
      : { icon: "circle-check", label: t.panel.nobodyLosesAccess, tone: "success" },
    ...(current?.downgradeOnReset ? [{ icon: "arrow-down", label: t.panel.partial(peopleCount(current.downgradeOnReset)), tone: "warning" as const }] : []),
    { icon: "trash", label: t.panel.directRemoved, tone: "warning" },
  ];
});
async function confirmMode() {
  const next = pendingMode.value;
  if (!next || modeBusy.value) return;
  modeBusy.value = true; modeError.value = ""; conflict.value = "";
  try {
    const ownAccess = next === "custom";
    await sharingApi.setAccessMode(props.pageId, { ownAccess, expectedVersion: props.access.modeVersion ?? null });
    if (disposed) return;
    pendingMode.value = undefined;
    notify(ownAccess ? t.panel.ownSet : t.panel.defaultSet, ownAccess ? "lock" : "layers");
    refresh();
  } catch (failure) {
    if (disposed) return;
    if (failure instanceof ApiError && failure.status === 409) {
      pendingMode.value = undefined;
      conflict.value = t.panel.conflict;
      refresh();
    } else modeError.value = errorMessage(failure);
  } finally {
    if (!disposed) modeBusy.value = false;
  }
}

// „Zobrazit jako“: the same policy decision for another person.
const asQuery = ref(""), asCandidates = ref<AccessCandidate[]>([]), asSelected = ref<string | null>(null);
const asResult = ref<PageAccessExplanation>(), asError = ref("");
let asTimer: ReturnType<typeof setTimeout> | undefined, asRequest = 0, asRetry: (() => void) | undefined;
const asOptions = computed<ChoiceOption[]>(() => asCandidates.value.map(item => ({ value: item.id, label: item.name, description: item.detail ? `@${item.detail}` : undefined, icon: "account", tone: "neutral" })));
async function findPeople(value: string) {
  const current = ++asRequest;
  try {
    const items = await sharingApi.accessCandidates(props.pageId, "user", value);
    if (!disposed && current === asRequest) asCandidates.value = items;
  } catch (failure) {
    if (!disposed && current === asRequest) { asError.value = errorMessage(failure); asRetry = () => void findPeople(value); }
  }
}
watch(asQuery, value => {
  clearTimeout(asTimer);
  asResult.value = undefined; asSelected.value = null; asError.value = "";
  if (!value.trim()) { asRequest++; asCandidates.value = []; return; }
  asTimer = setTimeout(() => void findPeople(value.trim()), 250);
});
async function viewAs(userId: string) {
  const current = ++asRequest;
  asError.value = "";
  try {
    const result = await sharingApi.explainAccess(props.pageId, userId);
    if (!disposed && current === asRequest) asResult.value = result;
  } catch (failure) {
    if (!disposed && current === asRequest) { asError.value = errorMessage(failure); asRetry = () => void viewAs(userId); }
  }
}

// A reloaded page state may decide differently for the person shown: ask again.
watch(() => props.access, () => { asResult.value = undefined; asSelected.value = null; });
onUnmounted(() => { disposed = true; clearTimeout(asTimer); });
</script>

<template>
  <div class="page-access" data-page-access>
    <ResultBanner v-if="notice" :key="notice.key" :message="notice.text" :icon="notice.icon" @dismiss="notice = undefined" />
    <ConflictNote v-if="conflict" :text="conflict" data-access-conflict @dismiss="conflict = ''" />

    <SettingsSection :title="t.panel.whoSees" icon="eye" :description="summary">
      <template #title-extra><span :key="viewers" class="st-count page-access-count" data-access-summary>{{ viewers }}</span></template>
      <ChoiceCards :model-value="mode" :options="modeOptions" :label="t.panel.whoSees" hide-label :columns="2" :disabled="modeBusy" name="accessMode" data-access-mode @update:model-value="chooseMode" />
      <p v-if="!access.isPublished" class="st-note"><AtlasIcon name="info" :size="15" />{{ t.panel.notPublished }}</p>
    </SettingsSection>

    <ResourceAccess ref="resource" kind="page" :id="pageId" :note="t.panel.childNote" @changed="emit('changed')">
      <template v-if="(custom && counts?.hidden) || access.shareLinks?.active" #after>
        <SettingsList v-if="custom && counts?.hidden" :label="t.panel.whoDoesntSee">
          <SettingsListRow title="" icon="eye-off" icon-tone="neutral" data-hidden-members>
            <template #title><s>{{ t.panel.otherMembersCount(counts.hidden) }}</s></template>
            <template #description>{{ t.panel.doesntSee }}</template>
          </SettingsListRow>
        </SettingsList>
        <p v-if="access.shareLinks?.active" class="st-note" data-share-summary>
          <AtlasIcon name="link" :size="15" />
          <span>{{ t.panel.anonymousLinks(access.shareLinks.active, formatShortDate(access.shareLinks.nextExpiry)) }}
            <a v-if="linksHref" class="st-link" :href="linksHref">{{ t.panel.manageLinks }}<AtlasIcon name="arrow-right" :size="13" /></a></span>
        </p>
      </template>
    </ResourceAccess>

    <SettingsSection :title="t.panel.viewAs" icon="user-check" advanced :summary="t.panel.viewAsSummary" data-view-as>
      <SearchableChoiceList
        v-model="asSelected"
        v-model:query="asQuery"
        remote
        :options="asOptions"
        :label="t.panel.person"
        :placeholder="t.panel.personPlaceholder"
        :empty-text="asQuery.trim() ? t.panel.personNone : t.panel.personStart"
        :max-height="220"
        @pick="viewAs"
      />
      <ErrorState v-if="asError" :title="t.panel.verifyFailed" :text="asError" @retry="asRetry?.()" />
      <Transition :css="false" v-bind="disclosureHooks" mode="out-in">
        <AccessExplanation v-if="asResult" :key="asResult.userId" :subject="{ name: asResult.displayName, kind: 'user' }" :target="{ name: t.panel.pageTarget(title), icon: 'file-text' }" :paths="accessPaths(asResult.sources, where)" :options="pageRoleOptions()">
          <p class="page-view-as-decision" :data-allowed="asResult.canView || undefined" role="status">
            <AtlasIcon :name="asResult.canView ? 'circle-check' : 'eye-off'" :size="15" /><span data-view-as-result>{{ asResult.displayName }} {{ explanationLine(asResult) }}</span>
          </p>
        </AccessExplanation>
      </Transition>
    </SettingsSection>

    <ConfirmDialog
      :open="!!pendingMode"
      :title="pendingMode === 'custom' ? t.panel.enableTitle : t.panel.revertTitle"
      :confirm-label="pendingMode === 'custom' ? t.panel.enableTitle : t.panel.revertConfirm"
      :confirm-icon="pendingMode === 'custom' ? 'lock' : 'layers'"
      :destructive="pendingMode !== 'custom'"
      :busy="modeBusy"
      :error="modeError"
      @confirm="confirmMode"
      @cancel="pendingMode = undefined"
    >
      <ImpactPreview :items="modeImpact" :before="pendingMode === 'custom' ? t.panel.inherit : t.panel.custom" :after="pendingMode === 'custom' ? t.panel.custom : t.panel.inherit" :unchanged="[t.panel.unchanged]" data-mode-confirm />
    </ConfirmDialog>
  </div>
</template>

<style scoped>
.page-access { display: grid; gap: 18px; min-width: 0; }
.page-access-count { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.page-view-as-decision { display: flex; align-items: flex-start; gap: 8px; margin: 0; padding: 10px 12px; border-radius: var(--st-radius-sm); background: var(--hover); font-size: 13px; font-weight: 550; }
.page-view-as-decision > svg { flex-shrink: 0; margin-top: 2px; color: var(--muted); }
.page-view-as-decision[data-allowed] { background: var(--success-bg); }
.page-view-as-decision[data-allowed] > svg { color: var(--success); }
</style>
