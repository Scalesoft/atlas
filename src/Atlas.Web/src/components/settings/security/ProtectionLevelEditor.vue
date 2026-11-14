<script setup lang="ts">
import t from "../../../i18n/settings-security";
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { errorMessage } from "../../../api";
import { globalSettingsBase } from "../../../settingsRoutes";
import { networksApi, protectionApi, type ProtectionState } from "../../../protection";
import ConflictNote from "../ConflictNote.vue";
import EffectiveValue from "../EffectiveValue.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import StatusBadge from "../StatusBadge.vue";
import { useSettingsFlash } from "../flash";
import { modeInfo, originChain } from "./protection";
import ProtectionWizard from "./ProtectionWizard.vue";
import { conflictMessage, isConflict, securityErrorText } from "./securityErrors";
import { useSecurityState } from "./securityState";
import type { ProtectionSetting } from "./types";

/**
 * The protection of one vault or secret on the V4 API: the effective mode with its origin chain (EffectiveValue), what a release
 * by you would need right now, and for a Spravuje holder the change: own mode or back to inheriting ("Převzít (nyní: …)")
 * through ProtectionWizard and a fresh sign-in for this vault or secret. A 409 reloads and says so.
 */
const props = defineProps<{ level: "vault" | "secret"; id: string; name: string }>();
const flash = useSettingsFlash();
const security = useSecurityState();

const state = ref<ProtectionState>();
const activeNetworks = ref<number | null>(null);
const loading = ref(true);
const error = ref("");
const conflict = ref("");
const changing = ref(false);
const initial = ref<ProtectionSetting>();
const saving = ref(false);
const saveError = ref("");
let disposed = false;

async function load() {
  loading.value = true;
  try {
    const next = await protectionApi.get(props.level, props.id);
    if (disposed) return;
    state.value = next;
    error.value = "";
    // Only wiki administrators read the networks; for everyone else the wizard leaves their count out.
    if (security?.globalAdmin.value && activeNetworks.value === null)
      activeNetworks.value = (await networksApi.list().catch(() => null))?.items.filter((item) => item.isActive).length ?? null;
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
onMounted(load);
onBeforeUnmount(() => (disposed = true));

const chain = computed(() => (state.value ? originChain(state.value.chain, security?.globalAdmin.value ? { instance: `${globalSettingsBase}/security/protection` } : {}) : []));
const label = computed(() => (props.level === "vault" ? t.editor.vaultLabel : t.editor.secretLabel));
const preview = computed(() => {
  const value = state.value?.preview;
  if (!value) return undefined;
  if (value.blocked) return { tone: "danger" as const, icon: "shield-alert", label: t.editor.blocked };
  if (value.networkException) return { tone: "success" as const, icon: "building-2", label: t.editor.trustedNetwork };
  if (value.reauthRequired) return { tone: "info" as const, icon: "fingerprint", label: t.editor.reauth };
  return { tone: "warning" as const, icon: "lock-open", label: t.editor.noReauth };
});
function open(setting?: ProtectionSetting) {
  initial.value = setting;
  saveError.value = "";
  changing.value = true;
}

async function save(setting: ProtectionSetting, proof: string | null) {
  if (!state.value) return;
  saving.value = true;
  saveError.value = "";
  try {
    state.value = await protectionApi.set(props.level, props.id, setting === "inherit" ? null : setting, state.value.version, proof);
    changing.value = false;
    const text = setting === "inherit" ? t.editor.inheritNow(modeInfo[state.value.effective].label) : modeInfo[setting].label;
    flash.notify(t.editor.saved(props.level === "vault", props.name, text), { icon: modeInfo[state.value.effective].icon });
  } catch (failure) {
    if (isConflict(failure)) {
      conflict.value = conflictMessage();
      changing.value = false;
      await load();
    } else saveError.value = securityErrorText(failure);
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <div class="protection-level" :data-protection-level="level">
    <ConflictNote v-if="conflict" :text="conflict" data-protection-conflict @dismiss="conflict = ''" />
    <ErrorState v-if="error" :title="t.editor.loadFailed" :text="error" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading && !state" :label="t.editor.loading" />
    <template v-else-if="state">
      <ProtectionWizard
        v-if="changing"
        :target="`${level === 'vault' ? t.editor.targetVault : t.editor.targetSecret} ${name}`"
        :state="state"
        :initial="initial"
        :active-networks="activeNetworks"
        :networks-href="security?.globalAdmin.value ? `${globalSettingsBase}/security/networks` : undefined"
        :busy="saving"
        :error="saveError"
        @save="save"
        @cancel="changing = false"
      />
      <template v-else>
        <EffectiveValue :chain="chain" :label="label" :readonly="!state.canManage" @customize="open()" @inherit="open('inherit')" />
        <div v-if="preview" class="protection-level-meta">
          <StatusBadge :tone="preview.tone" :icon="preview.icon" :label="preview.label" size="sm" />
        </div>
        <p v-if="!state.canManage" class="st-note"><AtlasIcon name="info" :size="15" />{{ t.editor.managerOnly(level === "vault") }}</p>
      </template>
    </template>
  </div>
</template>

<style scoped>
.protection-level { display: grid; gap: 12px; min-width: 0; }
.protection-level-meta { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 12px; }
</style>
