<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { errorMessage } from "../../../api";
import { formatDateTime } from "../../../formatDate";
import { vaultApi, type Vault, type VaultVerification } from "../../../vaults";
import ConfirmDialog from "../ConfirmDialog.vue";
import ErrorState from "../ErrorState.vue";
import InlineReveal from "../InlineReveal.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import SummaryList from "../SummaryList.vue";
import type { SummaryItem } from "../types";
import { useSettingsFlash } from "../flash";
import t from "../../../i18n/settings-space";
import tv from "../../../i18n/settings-vaults";
import { useSettingsRoute } from "../useSettingsRoute";
import { useVaultRegistry } from "../vaultRegistry";
import { disclosureHooks } from "../disclosure";

// Stav a soubor: the file facts, verification against the file, a new master password and archiving.
const props = defineProps<{ vault: Vault }>();
const router = useSettingsRoute();
const registry = useVaultRegistry();
const flash = useSettingsFlash();
const busy = ref<"" | "verify" | "password" | "archive">("");
const error = ref("");
const verification = ref<VaultVerification>();

const facts = computed<SummaryItem[]>(() => [
  { key: "space", icon: "book-open", label: t.status.space, value: props.vault.spaceName },
  { key: "location", icon: "folder", label: t.status.location, value: props.vault.locationName },
  { key: "file", icon: "file-text", label: t.status.file, value: props.vault.fileName, code: true },
  { key: "entries", icon: "key-round", label: t.status.entries, value: tv.secretCount(props.vault.entryCount) },
  { key: "refresh", icon: "refresh", label: t.status.lastLoaded, value: props.vault.lastRefreshAt ? formatDateTime(props.vault.lastRefreshAt) : t.status.notLoaded },
  { key: "fingerprint", icon: "fingerprint", label: t.status.fingerprint, value: props.vault.snapshotSha256 ? `${props.vault.snapshotSha256.slice(0, 16)}…` : "—", code: true },
  { key: "created", icon: "calendar", label: t.status.attached, value: formatDateTime(props.vault.createdAt) },
]);

async function run<T>(kind: typeof busy.value, action: () => Promise<T>) {
  busy.value = kind;
  error.value = "";
  try {
    return await action();
  } catch (failure) {
    error.value = errorMessage(failure);
    return undefined;
  } finally {
    busy.value = "";
  }
}
async function verify() {
  const result = await run("verify", () => vaultApi.verify(props.vault.id));
  if (!result) return;
  registry.replace(result.vault);
  verification.value = result;
  flash.notify(result.entryCount === null ? t.status.unreadable : result.missing.length ? t.status.someMissing(result.missing.length) : t.status.fileOk, {
    icon: result.entryCount === null || result.missing.length ? "triangle-alert" : "circle-check",
  });
}

const changingPassword = ref(props.vault.status === "needsPassword");
const password = ref("");
async function replacePassword() {
  if (!password.value) return;
  const vault = await run("password", () => vaultApi.replacePassword(props.vault.id, password.value));
  password.value = "";
  if (!vault) return;
  registry.replace(vault);
  changingPassword.value = false;
  flash.notify(vault.status === "unlocked" ? t.status.passwordSavedUnlocked : t.status.passwordSavedLocked, { icon: vault.status === "unlocked" ? "circle-check" : "triangle-alert" });
}
onBeforeUnmount(() => (password.value = ""));

const archiving = ref(false);
async function archive() {
  const vault = props.vault;
  const done = await run("archive", async () => {
    await vaultApi.archive(vault.id);
    return true;
  });
  if (!done) return;
  archiving.value = false;
  router.navigate("vaults");
  registry.remove(vault.id);
  flash.notify(t.status.archived(vault.name, vault.fileName), { icon: "folder-input" });
}
</script>

<template>
  <div class="vault-status">
    <ErrorState
      v-if="vault.status !== 'unlocked'"
      :title="vault.status === 'needsPassword' ? t.status.needsPassword : vault.status === 'unavailable' ? t.status.unavailable : t.status.unreadableFile"
      :text="`${vault.lastFailure ?? ''} ${t.status.notIssued}`.trim()"
      :busy="!!busy"
      :retry-label="t.status.verifyAgain"
      @retry="verify"
    />
    <Transition :css="false" v-bind="disclosureHooks">
      <p v-if="error" class="vault-status-error" role="alert" data-vault-error><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p>
    </Transition>

    <SettingsSection :title="t.status.fileSection" icon="database">
      <template #actions>
        <Button variant="outline" size="sm" type="button" :disabled="!!busy" data-vault-verify @click="verify">
          <AtlasIcon :name="busy === 'verify' ? 'loader-circle' : 'refresh'" :size="15" :class="{ 'atlas-spinner': busy === 'verify' }" />{{ t.status.verify }}
        </Button>
      </template>
      <SummaryList :items="facts" :label="t.status.fileLabel" />
      <InlineReveal :open="!!verification" class="vault-reveal">
        <div v-if="verification" class="vault-verification" data-vault-verification>
          <StatusBadge v-if="verification.entryCount === null" tone="danger" icon="triangle-alert" :label="verification.vault.lastFailure ?? t.status.couldNotRead" />
          <template v-else>
            <StatusBadge :tone="verification.missing.length ? 'warning' : 'success'" :label="verification.missing.length ? t.status.missing(verification.missing.length) : t.status.readable(verification.entryCount)" />
            <ul v-if="verification.missing.length" class="vault-missing">
              <li v-for="secret in verification.missing" :key="secret.secretId"><AtlasIcon name="key-round" :size="14" />{{ secret.title || t.secrets.untitled }}<small v-if="secret.groupPath"> · {{ secret.groupPath }}</small></li>
            </ul>
            <p v-if="verification.missing.length" class="st-note">{{ t.status.missingNote }}</p>
          </template>
        </div>
      </InlineReveal>
      <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.status.readOnly }}</p>
    </SettingsSection>

    <SettingsSection :title="t.status.masterPassword" icon="key-round" class="vault-password-section" :data-open="changingPassword || undefined" :description="t.status.masterPasswordDescription">
      <template #actions>
        <Transition name="st-fade"><Button v-if="!changingPassword" variant="outline" size="sm" type="button" :disabled="!!busy" data-vault-change-password @click="changingPassword = true"><AtlasIcon name="key-round" :size="15" />{{ t.status.changePassword }}</Button></Transition>
      </template>
      <InlineReveal :open="changingPassword">
        <form class="vault-password" @submit.prevent="replacePassword">
          <SettingsRow v-slot="{ id, describedBy }" :label="t.status.newMasterPassword" :help="t.status.newMasterPasswordHelp" stacked>
            <input :id="id" v-model="password" class="st-input" type="password" autocomplete="off" :aria-describedby="describedBy" :disabled="!!busy" data-vault-password />
          </SettingsRow>
          <div class="st-actions">
            <Button type="submit" :disabled="!password || !!busy" data-vault-save-password><AtlasIcon :name="busy === 'password' ? 'loader-circle' : 'check'" :size="16" :class="{ 'atlas-spinner': busy === 'password' }" />{{ t.status.savePassword }}</Button>
            <Button variant="ghost" type="button" :disabled="!!busy" @click="changingPassword = false; password = ''">{{ t.status.cancel }}</Button>
          </div>
        </form>
      </InlineReveal>
    </SettingsSection>

    <SettingsSection :title="t.status.archiving" icon="folder-input" class="vault-header-only" :description="t.status.archivingDescription">
      <template #actions>
        <Button variant="destructive" size="sm" type="button" :disabled="!!busy" data-vault-archive @click="archiving = true"><AtlasIcon name="folder-input" :size="15" />{{ t.status.archive }}</Button>
      </template>
    </SettingsSection>

    <ConfirmDialog :open="archiving" :title="t.status.archive" :confirm-label="t.status.archiveConfirm" confirm-icon="folder-input" :busy="busy === 'archive'" :error="archiving ? error : undefined" @confirm="archive" @cancel="archiving = false">
      <p>{{ t.status.archiveLead }} <strong>{{ vault.name }}</strong> {{ t.status.archiveTail }}</p>
      <p>{{ t.status.fileLead }} <code class="st-mono">{{ vault.fileName }}</code> {{ t.status.fileMid }} {{ vault.locationName }} {{ t.status.fileTail }}</p>
    </ConfirmDialog>
  </div>
</template>

<style scoped>
/* A column of flex items, not a grid: the error note unfolds and gives the gap above it back (settings/disclosure.ts). */
.vault-status { display: flex; flex-direction: column; gap: 18px; min-width: 0; }
.vault-status-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
/* A folded reveal still takes its gap in the card's column (24 px between the facts and the note); the panel's own lead
   takes the gap's place instead, so a folded one takes nothing and opening grows the gap with the height. */
.vault-reveal { margin-top: calc(var(--st-body-gap, 12px) * -1); }
.vault-reveal > :deep(.inline-reveal > .inline-reveal-inner) { padding-top: var(--st-body-gap, 12px); }
/* The badge keeps its own width instead of stretching into a bar across the card. */
.vault-verification { display: grid; justify-items: start; gap: 8px; padding: 6px 0 4px; }
.vault-missing { display: grid; gap: 4px; margin: 0; padding: 0; list-style: none; font-size: 13px; }
.vault-missing li { display: flex; align-items: center; gap: 6px; }
.vault-missing small { color: var(--muted); }
/* A card that is only its header (Archivace, Hlavní heslo until it is changed) drew an empty 32 px band of body padding
   under a line. The body gives its padding to the form inside the reveal instead, and the line under the header shows
   only while there is something under it. */
.vault-header-only > :deep(.settings-section-body:empty) { display: none; }
.vault-header-only > :deep(.settings-section-header), .vault-password-section > :deep(.settings-section-header) { border-bottom-color: transparent; transition: border-color var(--atlas-motion-base) var(--atlas-ease-out); }
.vault-password-section[data-open] > :deep(.settings-section-header) { border-bottom-color: var(--st-line-soft, var(--line)); }
.vault-password-section > :deep(.settings-section-body) { padding-block: 0; }
/* The row brings its own 16 px above the label, as a row reaching the card edges does. */
.vault-password { display: grid; gap: 8px; padding: 0 0 var(--st-body-pad-y, 16px); max-width: 520px; }
</style>
