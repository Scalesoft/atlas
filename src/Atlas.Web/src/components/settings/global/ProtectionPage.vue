<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { errorMessage } from "../../../api";
import { networksApi, protectionApi, type ProtectionOverride, type ProtectionState } from "../../../protection";
import { spaceSettingsHash } from "../../../settingsRoutes";
import ChoiceCards from "../ChoiceCards.vue";
import ConflictNote from "../ConflictNote.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import { useSettingsFlash } from "../flash";
import t from "../../../i18n/settings-global";
import { useSettingsRoute } from "../useSettingsRoute";
import ModeMatrix from "../security/ModeMatrix.vue";
import ProtectionChip from "../security/ProtectionChip.vue";
import ProtectionWizard from "../security/ProtectionWizard.vue";
import { modeInfo, modeOptions, modeOrder } from "../security/protection";
import { conflictMessage, isConflict, securityErrorText } from "../security/securityErrors";
import type { ProtectionMode, ProtectionSetting } from "../security/types";

// Ochrana hesel (global security, every wiki administrator): the instance default as three cards,
// the change through ProtectionWizard with a fresh sign-in, and every vault or password with a mode of its own.
const props = defineProps<{ changing: boolean }>();
const router = useSettingsRoute();
const flash = useSettingsFlash();

const state = ref<ProtectionState>();
const overrides = ref<ProtectionOverride[]>([]);
const activeNetworks = ref(0);
const loading = ref(true);
const error = ref("");
const conflict = ref("");
const saving = ref(false);
const saveError = ref("");
let disposed = false;

async function load() {
  loading.value = true;
  try {
    const [instance, own, networks] = await Promise.all([protectionApi.get("instance"), protectionApi.overrides(), networksApi.list()]);
    if (disposed) return;
    state.value = instance;
    overrides.value = own;
    activeNetworks.value = networks.items.filter((item) => item.isActive).length;
    error.value = "";
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
onMounted(load);
onBeforeUnmount(() => (disposed = true));

const current = computed<ProtectionMode>(() => state.value?.effective ?? "always");
const ownVaults = computed(() => overrides.value.filter((item) => !item.secretId));
const ownSecrets = computed(() => overrides.value.filter((item) => item.secretId));
const noNetworks = computed(() => current.value === "outsideTrustedNetworks" && activeNetworks.value === 0);
const cards = computed(() => modeOptions(current.value));
// A card picks the mode; the change itself is the wizard with its impact and the fresh sign-in.
const picked = computed({
  get: () => current.value as string,
  set: (value: string | string[] | null) => {
    if (typeof value === "string" && value !== current.value) router.navigate("security/protection/change", { rezim: value });
  },
});
const initial = computed(() => {
  const value = router.route.value.query.rezim as ProtectionMode | undefined;
  return value && modeOrder.includes(value) ? value : undefined;
});
const overrideHref = (item: ProtectionOverride) => spaceSettingsHash(item.spaceId, `vaults/${item.vaultId}/${item.secretId ? "secrets" : "protection"}`);

async function save(setting: ProtectionSetting, proof: string | null) {
  if (!state.value || setting === "inherit") return;
  saving.value = true;
  saveError.value = "";
  try {
    state.value = await protectionApi.set("instance", undefined, setting, state.value.version, proof);
    flash.notify(t.protection.saved(modeInfo[setting].label), { icon: modeInfo[setting].icon });
    router.navigate("security/protection");
  } catch (failure) {
    if (isConflict(failure)) {
      conflict.value = conflictMessage();
      router.navigate("security/protection");
      await load();
    } else saveError.value = securityErrorText(failure);
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <SettingsPage :title="t.protection.title" :description="t.protection.description">
    <ConflictNote v-if="conflict" :text="conflict" data-protection-conflict @dismiss="conflict = ''" />
    <ErrorState v-if="error" :title="t.protection.loadFailed" :text="error" :busy="loading" @retry="load" />
    <LoadingSkeleton v-else-if="loading && !state" :label="t.protection.loading" />
    <template v-else-if="state">
      <SettingsSection v-if="props.changing" :label="t.protection.changeSection">
        <ProtectionWizard
          :target="t.protection.wizardTarget"
          :state="state"
          :initial="initial"
          :active-networks="activeNetworks"
          :overrides="{ vaults: ownVaults.length, secrets: ownSecrets.length }"
          :networks-href="router.href('security/networks/new')"
          :busy="saving"
          :error="saveError"
          @save="save"
          @cancel="router.navigate('security/protection')"
        />
      </SettingsSection>
      <template v-else>
        <SettingsSection :title="t.protection.defaultTitle" icon="shield-check" :description="t.protection.defaultDescription">
          <ChoiceCards v-model="picked" :options="cards" :label="t.protection.modesLabel" hide-label :columns="1" size="hero" data-instance-modes>
            <template #option="{ option }"><ModeMatrix :mode="option.value as ProtectionMode" /></template>
          </ChoiceCards>
          <p v-if="noNetworks" class="st-note protection-warning" role="status" data-no-networks-warning>
            <AtlasIcon name="triangle-alert" :size="15" />{{ t.protection.noNetworks }}
            <a class="st-link" :href="router.href('security/networks/new')">{{ t.protection.addNetwork }}<AtlasIcon name="arrow-right" :size="13" /></a>
          </p>
          <p class="st-note"><AtlasIcon name="arrow-right-left" :size="15" />{{ t.protection.inherits(t.counts.vaults(state.impact?.vaults ?? 0), t.counts.secrets(state.impact?.secrets ?? 0)) }}</p>
        </SettingsSection>

        <SettingsSection :title="t.protection.ownTitle" icon="pencil" :count="overrides.length" :description="t.protection.ownDescription">
          <SettingsList v-if="overrides.length" :label="t.protection.ownLabel">
            <SettingsListRow
              v-for="item in overrides"
              :key="item.secretId ?? item.vaultId"
              :title="item.secretTitle ?? item.vaultName"
              :description="item.secretId ? t.protection.secret(item.vaultName, item.spaceName) : t.protection.vault(item.spaceName)"
              :icon="item.secretId ? 'key-round' : 'lock'"
              :icon-tone="item.secretId ? 'neutral' : undefined"
              :data-override="item.secretId ?? item.vaultId"
            >
              <template #meta><ProtectionChip :mode="item.mode" :setting="item.mode" /></template>
              <template #actions><a class="st-link" :href="overrideHref(item)">{{ t.protection.edit }}<AtlasIcon name="arrow-right" :size="13" /></a></template>
            </SettingsListRow>
          </SettingsList>
          <EmptyState v-else icon="circle-check" :title="t.protection.allInheritTitle" :text="t.protection.allInheritText" compact />
        </SettingsSection>

        <SettingsSection :title="t.protection.howTitle" icon="info" advanced :summary="t.protection.howSummary">
          <div class="protection-modes">
            <div v-for="mode in modeOrder" :key="mode" class="protection-mode">
              <strong><AtlasIcon :name="modeInfo[mode].icon" :size="16" />{{ modeInfo[mode].label }}</strong>
              <ModeMatrix :mode="mode" />
            </div>
          </div>
          <p class="st-note"><AtlasIcon name="shield-check" :size="15" />{{ t.protection.howNote }}</p>
        </SettingsSection>
      </template>
    </template>
  </SettingsPage>
</template>

<style scoped>
.protection-modes { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr)); gap: 12px; margin-bottom: 10px; }
.protection-mode { display: grid; gap: 6px; }
.protection-mode strong { display: flex; align-items: center; gap: 6px; font-size: 13.5px; }
.protection-warning { color: var(--warning); font-weight: 550; }
.protection-warning > svg { color: var(--warning); }
</style>
