<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from "vue";
import { Checkbox } from "../../../ui/checkbox";
import { Switch } from "../../../ui/switch";
import { api, put } from "../../../../api";
import ConflictNote from "../../ConflictNote.vue";
import ErrorState from "../../ErrorState.vue";
import LoadingNote from "../../LoadingNote.vue";
import SaveBar from "../../SaveBar.vue";
import SettingsList from "../../SettingsList.vue";
import SettingsListRow from "../../SettingsListRow.vue";
import SettingsSection from "../../SettingsSection.vue";
import StatusBadge from "../../StatusBadge.vue";
import InlineReveal from "../../InlineReveal.vue";
import { useDraftGuard } from "../../dirtyGuard";
import { useSettingsDocument } from "../../useSettingsDocument";
import SettingsRow from "../../SettingsRow.vue";
import t from "../../../../i18n/settings-ai";
import { featureGroups, featureTexts, parseDailyLimit, type AiFeaturesView, type AiProfileView, type AiFeatureItem, type ProfileKind } from "./aiAdmin";

/**
 * Funkce: which AI tasks are on, by what they do for people (Hledání, Čtení, Psaní, Zakládání).
 * One PUT with the CAS version; the server merges, so tasks this version does not list keep their
 * value. A task that works on an author's unsaved text may reach a cloud profile only with the
 * permission here, and the permission can be given only while its profile is a cloud one. A `limitable` task (the
 * answer tasks) also has a daily limit per user: a whole number 1 to 10 000, empty = no limit.
 */
const endpoint = "/admin/ai/features";
const drafts = ref<Record<string, { enabled: boolean; cloud: boolean | null; limit: string }>>({});
/** What one load reads: the task list together with the profiles that its cloud consent depends on. */
interface Loaded {
  features: AiFeaturesView;
  profiles: Partial<Record<ProfileKind, AiProfileView>>;
}
const shownLimitErrors = ref(false);
const limitsOpen = ref(false);
const root = ref<HTMLElement>();
const { saved: loaded, loading, loadError, saving, saveError, conflict, dirty, load, save, reset, reloadAfterConflict } = useSettingsDocument<Loaded>({
  load: async () => {
    const kinds: ProfileKind[] = ["embeddings", "quick", "chat", "rerank"];
    const [features, read] = await Promise.all([api<AiFeaturesView>(endpoint), Promise.all(kinds.map(kind => api<AiProfileView>(`/admin/ai/profiles/${kind}`).catch(() => undefined)))]);
    return { features, profiles: Object.fromEntries(kinds.map((kind,index)=>[kind,read[index]])) };
  },
  save: async (current): Promise<Loaded> => {
    const body = {
      expectedVersion: current.features.version,
      items: current.features.items.map((item) => ({ task: item.task, enabled: drafts.value[item.task]?.enabled ?? item.enabled, cloudWorkingText: item.cloudWorkingText === null ? null : (drafts.value[item.task]?.cloud ?? item.cloudWorkingText),
        // Q3: a limitable task's limit is sent every time (null = no limit); other tasks do not carry the field.
        ...(item.limitable ? { dailyLimit: parseDailyLimit(drafts.value[item.task]?.limit ?? limitText(item.dailyLimit)).value } : {}) })),
    };
    // The profiles are not saved here; the answer keeps the ones that were loaded.
    return { features: await put<AiFeaturesView>(endpoint, body), profiles: current.profiles };
  },
  fill: (current) => {
    drafts.value = Object.fromEntries(current.features.items.map((item) => [item.task, { enabled: item.enabled, cloud: item.cloudWorkingText, limit: limitText(item.dailyLimit) }]));
    shownLimitErrors.value = false;
  },
  dirty: (current): boolean => current.features.items.some((item) => {
    const draft = drafts.value[item.task];
    return !!draft && (draft.enabled !== item.enabled || (item.cloudWorkingText !== null && draft.cloud !== item.cloudWorkingText) || (!!item.limitable && draft.limit.trim() !== limitText(item.dailyLimit)));
  }),
  beforeSave: (): string | undefined => {
    if (!hasLimitErrors.value) return undefined;
    shownLimitErrors.value = true;
    limitsOpen.value = true;
    void focusFirstLimitError();
    return t.features.fixHighlighted;
  },
  clearConflictOnSave: true,
});

const view = computed(() => loaded.value?.features);
const profiles = computed<Loaded["profiles"]>(() => loaded.value?.profiles ?? {});
const items = computed(() => view.value?.items ?? []);
const limitText = (value: number | null | undefined) => (value === null || value === undefined ? "" : String(value));
const limitError = (task: string) => (drafts.value[task] ? parseDailyLimit(drafts.value[task]!.limit).error : undefined);
const hasLimitErrors = computed(() => items.value.some((item) => item.limitable && !!limitError(item.task)));
const groups = computed(() => featureGroups.map((group) => ({ ...group, items: items.value.filter((item) => item.group === group.id) })).filter((group) => group.items.length));
const enabledCount = computed(() => items.value.filter((item) => drafts.value[item.task]?.enabled).length);
const actualKind = (item: AiFeatureItem): ProfileKind => item.profileKind === "quick" && profiles.value.quick?.useChatProfile ? "chat" : item.profileKind;
const cloudProfile = (item: AiFeatureItem) => profiles.value[actualKind(item)]?.location === "cloud";
const textOf = (task: string) => featureTexts[task] ?? { title: task, description: "", icon: "list" };
const cloudItems = (groupItems: AiFeatureItem[]) => groupItems.filter((item) =>
  item.cloudWorkingText !== null && drafts.value[item.task]?.enabled && cloudProfile(item));
const companyTextNote = (groupItems: AiFeatureItem[]) => {
  const textItems = groupItems.filter((item) => item.cloudWorkingText !== null);
  return textItems.length > 0 && textItems.every((item) => profiles.value[actualKind(item)]?.location === "company");
};
const limitableItems = computed(() => items.value.filter((item) => item.limitable));
const limitSummary = computed(() => limitableItems.value.map((item) =>
  textOf(item.task).title + ": " + (drafts.value[item.task]?.limit.trim() || t.features.noLimit)).join(" · "));
const groupDescriptions = (): Record<string, string> => t.features.groupDescriptions;

async function focusFirstLimitError() {
  await nextTick();
  await nextTick();
  root.value?.querySelector<HTMLInputElement>('[data-ai-limit-input][aria-invalid="true"]')?.focus();
}

useDraftGuard(dirty, { save, discard: reset });
onMounted(load);
</script>

<template>
  <div ref="root" class="ai-features" role="region" :aria-label="t.features.region" data-ai-features>
    <ConflictNote v-if="conflict" :text="t.features.conflict" :reload-label="t.features.reload" :busy="loading || saving" data-ai-conflict @reload="reloadAfterConflict" @dismiss="conflict = false" />
    <ErrorState v-if="loadError" :title="t.features.loadFailed" :text="loadError" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading && !view" :label="t.features.loading" />
    <template v-else-if="view">
      <p class="ai-features-intro">{{ t.features.intro }}</p>
      <SettingsSection v-for="group in groups" :key="group.id" :title="group.label" :description="groupDescriptions()[group.id]" :icon="group.icon" :data-ai-group="group.id">
        <template v-if="group === groups[0]" #actions><StatusBadge tone="neutral" icon="list-checks" :label="t.features.enabledCount(enabledCount, items.length)" size="sm" /></template>
        <SettingsList :label="t.features.listLabel(group.label)" :class="{ 'reveal-lead': !companyTextNote(group.items) }">
          <SettingsListRow v-for="item in group.items" :key="item.task" :title="textOf(item.task).title" :description="textOf(item.task).description" :icon="textOf(item.task).icon" :data-ai-feature="item.task">
            <template #actions><Switch v-if="drafts[item.task]" v-model="drafts[item.task]!.enabled" :aria-label="t.features.turnOn(textOf(item.task).title)" :disabled="saving" data-ai-feature-switch /></template>
          </SettingsListRow>
        </SettingsList>
        <p v-if="companyTextNote(group.items)" class="ai-company-note">{{ t.features.companyNote }}</p>
        <InlineReveal :open="cloudItems(group.items).length > 0" class="reveal-tail" :class="{ 'reveal-tail-bleed': !companyTextNote(group.items) }">
          <div v-if="cloudItems(group.items).length" class="ai-cloud-permissions" :data-ai-cloud-permissions="group.id">
            <h4>{{ t.features.cloudTitle }}</h4>
            <p>{{ t.features.cloudText }}</p>
            <label v-for="item in cloudItems(group.items)" :key="item.task" class="ai-cloud-check" :data-ai-cloud-consent="item.task">
              <Checkbox :model-value="drafts[item.task]!.cloud === true" :disabled="saving" @update:model-value="drafts[item.task]!.cloud = $event === true" />
              {{ textOf(item.task).title }}
            </label>
          </div>
        </InlineReveal>
      </SettingsSection>
      <SettingsSection v-if="limitableItems.length" v-model:open="limitsOpen" :title="t.features.limitsTitle" icon="list-checks" advanced :summary="limitSummary" data-ai-limit-section>
        <div v-for="item in limitableItems" :key="item.task" :data-ai-limit="item.task">
          <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.features.limitLabel(textOf(item.task).title)" :help="t.features.limitHelp" :error="shownLimitErrors ? limitError(item.task) : undefined">
            <input :id="id" v-model="drafts[item.task]!.limit" class="st-input ai-limit-input" type="text" inputmode="numeric" autocomplete="off" :placeholder="t.features.noLimit" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="saving" data-ai-limit-input />
          </SettingsRow>
        </div>
      </SettingsSection>
      <SaveBar :dirty="dirty" :saving="saving" :error="saveError" :save-label="t.features.save" @save="save" @cancel="reset" />
    </template>
  </div>
</template>

<style scoped>
.ai-features { display: grid; gap: 16px; min-width: 0; }
.ai-features-intro { margin: 0; color: var(--muted); font-size: 13px; line-height: 1.5; }
.ai-limit-input { max-width: 140px; }
.ai-company-note { margin: 0; color: var(--muted); font-size: 12.5px; }
.ai-cloud-permissions { display: grid; gap: 8px; padding: 12px 0 2px; border-top: 1px solid var(--line); }
.ai-cloud-permissions h4 { margin: 0; font-size: 13px; font-weight: 650; }
.ai-cloud-permissions p { margin: 0 0 2px; color: var(--muted); font-size: 12.5px; line-height: 1.5; }
.ai-cloud-check { display: flex; align-items: center; gap: 8px; font-size: 13px; cursor: pointer; }
</style>
