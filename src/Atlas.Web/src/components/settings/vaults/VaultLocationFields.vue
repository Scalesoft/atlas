<script setup lang="ts">
import t from "../../../i18n/settings-vaults";
import { computed, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import ChoiceCards from "../ChoiceCards.vue";
import InlineReveal from "../InlineReveal.vue";
import SettingsRow from "../SettingsRow.vue";
import StatusBadge from "../StatusBadge.vue";
import type { ChoiceOption } from "../types";
import { vaultLocationKinds, vaultLocationRootReady, withVaultLocationKind, type VaultLocationDraft, type VaultLocationKind } from "../../../vaults";

/**
 * The fields of one vault location (Umístění souborů): kind as tiles, name, the folder (not for a
 * managed location, whose folder Atlas derives) and, for SMB, the service account. With `test` the fields offer "Otestovat připojení" and show the server's
 * answer. Used by the vault wizard's "Nové umístění" step and by the location wizard; the parent
 * owns the draft (v-model) and saving. `showKind: false` leaves the kind to a previous step.
 */
const props = withDefaults(
  defineProps<{
    /** Editing a location that already has a stored SMB password: empty keeps it. */
    hasStoredPassword?: boolean;
    showKind?: boolean;
    /** Tests the current draft; resolves with the server's message, rejects with the reason. */
    test?: (draft: VaultLocationDraft) => Promise<string>;
    disabled?: boolean;
  }>(),
  { hasStoredPassword: false, showKind: true, test: undefined, disabled: false },
);
const draft = defineModel<VaultLocationDraft>({ required: true });
const result = ref<{ state: "idle" | "testing" | "ok" | "failed"; message: string }>({ state: "idle", message: "" });

const kindOptions: ChoiceOption[] = (Object.keys(vaultLocationKinds) as VaultLocationKind[])
  .map((value) => ({ value, label: vaultLocationKinds[value].label, description: vaultLocationKinds[value].description, icon: vaultLocationKinds[value].icon }));
const kind = computed({
  get: () => draft.value.kind,
  set: (value: VaultLocationKind) => (draft.value = withVaultLocationKind(draft.value, value)),
});
const smb = computed(() => draft.value.kind === "smb");
const managed = computed(() => draft.value.kind === "managed");
// A changed draft makes an earlier test result stale.
watch(() => [draft.value.kind, draft.value.root, draft.value.username, draft.value.domain, draft.value.password], () => (result.value = { state: "idle", message: "" }));

async function runTest() {
  if (!props.test) return;
  result.value = { state: "testing", message: "" };
  try {
    result.value = { state: "ok", message: await props.test({ ...draft.value }) };
  } catch (failure) {
    result.value = { state: "failed", message: failure instanceof Error ? failure.message : t.fields.testFailed };
  }
}
</script>

<template>
  <div class="vault-location-fields">
    <ChoiceCards v-if="showKind" :model-value="kind" :options="kindOptions" :label="t.fields.kind" :columns="3" @update:model-value="kind = $event as VaultLocationKind" />
    <SettingsRow v-slot="{ id, describedBy }" :label="t.fields.name" :help="t.fields.nameHelp">
      <input :id="id" v-model="draft.name" class="st-input" :aria-describedby="describedBy" :disabled="disabled" maxlength="200" data-step-focus data-location-name />
    </SettingsRow>
    <InlineReveal :open="!managed">
      <SettingsRow v-slot="{ id, describedBy }" :label="smb ? t.fields.sharedFolder : t.fields.serverFolder" :help="smb ? t.fields.smbHelp : t.fields.serverHelp">
        <input :id="id" v-model="draft.root" class="st-input st-mono" :placeholder="smb ? 'smb://fs02/it/keepass' : t.fields.serverPlaceholder" :aria-describedby="describedBy" :disabled="disabled" spellcheck="false" data-location-root />
      </SettingsRow>
    </InlineReveal>
    <InlineReveal :open="smb">
      <SettingsRow v-slot="{ id }" :label="t.fields.serviceAccount"><input :id="id" v-model="draft.username" class="st-input" placeholder="svc-atlas" autocomplete="off" :disabled="disabled" data-location-username /></SettingsRow>
      <SettingsRow v-slot="{ id }" :label="t.fields.domain"><input :id="id" v-model="draft.domain" class="st-input" :placeholder="t.fields.domainPlaceholder" autocomplete="off" :disabled="disabled" /></SettingsRow>
      <SettingsRow v-slot="{ id, describedBy }" :label="t.fields.accountPassword" :help="hasStoredPassword ? t.fields.keepPassword : t.fields.storedEncrypted">
        <input :id="id" v-model="draft.password" class="st-input" type="password" autocomplete="new-password" :aria-describedby="describedBy" :disabled="disabled" />
      </SettingsRow>
    </InlineReveal>
    <div v-if="test" class="st-actions vault-location-test">
      <Button variant="outline" type="button" :disabled="disabled || !vaultLocationRootReady(draft) || result.state === 'testing'" data-location-test @click="runTest">
        <AtlasIcon :name="result.state === 'testing' ? 'loader-circle' : 'plug'" :size="16" :class="{ 'atlas-spinner': result.state === 'testing' }" />{{ t.fields.test }}
      </Button>
      <Transition name="st-fade">
        <StatusBadge v-if="result.state === 'ok' || result.state === 'failed'" :tone="result.state === 'ok' ? 'success' : 'danger'" :label="result.message" data-location-test-result />
      </Transition>
    </div>
  </div>
</template>

<style scoped>
.vault-location-fields { display: grid; gap: 4px; }
.vault-location-test { margin-top: 6px; }
</style>
