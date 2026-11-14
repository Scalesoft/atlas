<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-people";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import { apiLevel, apiLevelInfo, type ApiLevel } from "../../../apiTokenRights";
import ApiLevelPicker from "./ApiLevelPicker.vue";
import { ApiError, errorMessage } from "../../../api";
import { adminApi, type AiUserPolicy, type ApiAccess, type DirectoryUser, type Invitation } from "../../../administration";
import { reauthRequiredCode } from "../../../protection";
import ConfirmDialog from "../ConfirmDialog.vue";
import ConflictNote from "../ConflictNote.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import OneTimeSecret from "../OneTimeSecret.vue";
import ReauthDialog from "../ReauthDialog.vue";
import SaveBar from "../SaveBar.vue";
import SettingsRow from "../SettingsRow.vue";
import InlineReveal from "../InlineReveal.vue";
import OptionPills from "../OptionPills.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import { useDirtyGuard } from "../dirtyGuard";
import { useSettingsFlash } from "../flash";
import AiQuotaStatus from "../../ai/AiQuotaStatus.vue";
import AiQuotaFields from "../general/ai/AiQuotaFields.vue";
import { emptyQuotas, quotaSummary, validQuotas } from "../general/ai/aiAccess";
import AccountStatusBadge from "./AccountStatusBadge.vue";
import { accountSourceLabels, codeValidity } from "./accountStatus";
import "./people.css";

const aiModeOptions = (inheritLabel: string) => [{ value: "inherit", label: inheritLabel }, { value: "allow", label: t.accountDetail.allowPersonally }, { value: "deny", label: t.accountDetail.denyPersonally }];

/**
 * The Účet tab of a person's card in Nastavení wiki → Lidé: display name, active and wiki administrator with the CAS
 * version (a conflict keeps the draft and offers Načíst znovu), the separate personal API token permission and a password
 * reset with a new one-time code. Both drafts (account and API permission) share one SaveBar and the leave guard. Turning
 * Správce wiki on or off is a security change: it is confirmed with a fresh sign-in (ReauthDialog) and carries the proof.
 * The groups and the latest changes are tabs of their own on the card.
 */
const props = defineProps<{ account: DirectoryUser; currentUserId: string; reload: () => Promise<void> }>();
const emit = defineEmits<{ saved: [account: DirectoryUser] }>();
const flash = useSettingsFlash();
const guard = useDirtyGuard();
let disposed = false;

const isSelf = computed(() => props.account.id === props.currentUserId);
const source = computed(() => accountSourceLabels[props.account.source]);

// Account draft. A reload after our own reset keeps what is being edited; any other new state resets it.
const draft = ref({ displayName: "", isActive: true, isWikiAdmin: false });
let keepDraft = false;
watch(
  () => props.account,
  (account) => {
    if (!keepDraft) draft.value = { displayName: account.displayName, isActive: account.isActive, isWikiAdmin: account.isWikiAdmin };
    keepDraft = false;
  },
  { immediate: true },
);
const accountDirty = computed(
  () => draft.value.displayName !== props.account.displayName || draft.value.isActive !== props.account.isActive || draft.value.isWikiAdmin !== props.account.isWikiAdmin,
);
const nameError = computed(() => (draft.value.displayName.trim() ? "" : t.accountDetail.nameEmpty));

// API permission draft.
const apiState = ref<ApiAccess>();
const apiDraft = ref<ApiLevel>("none");
/** The stored level; a server that sends only the 1.5 switch counts as the full level it then meant. */
const levelOf = (state: ApiAccess): ApiLevel => (state.level ? apiLevel(state.level) : state.canIssueTokens ? "write" : "none");
const apiLoading = ref(true);
const apiError = ref("");
const apiDirty = computed(() => !!apiState.value && apiDraft.value !== levelOf(apiState.value));
const revoking = computed(() => apiDirty.value && apiDraft.value === "none");

async function loadApi() {
  apiLoading.value = true;
  apiError.value = "";
  try {
    const state = await adminApi.apiAccess(props.account.id);
    if (disposed) return;
    apiState.value = state;
    apiDraft.value = levelOf(state);
  } catch (failure) {
    if (!disposed) apiError.value = errorMessage(failure);
  } finally {
    if (!disposed) apiLoading.value = false;
  }
}

// Personal exceptions share the account SaveBar; group grants remain the normal entry point.
const aiState = ref<AiUserPolicy>();
const aiDraft = ref({ enabled: null as boolean | null, toolsEnabled: null as boolean | null, ...emptyQuotas() });
const aiLoading = ref(true), aiError = ref("");
function policyDraft(policy: AiUserPolicy) { return { enabled: policy.enabled, toolsEnabled: policy.toolsEnabled, searchAnswerDailyLimit: policy.searchAnswerDailyLimit, chatAnswerDailyLimit: policy.chatAnswerDailyLimit, pageSummaryDailyLimit: policy.pageSummaryDailyLimit, agentDailyLimit: policy.agentDailyLimit }; }
const aiDirty = computed(() => !!aiState.value && JSON.stringify(aiDraft.value) !== JSON.stringify(policyDraft(aiState.value)));
const triState = (key: "enabled" | "toolsEnabled") => computed({ get: () => aiDraft.value[key] === null ? "inherit" : aiDraft.value[key] ? "allow" : "deny", set: (value: string) => { aiDraft.value[key] = value === "inherit" ? null : value === "allow"; } });
// D17: "AI s nástroji" sits under access and shows only while AI is not personally denied (a denial switches tools off too).
const aiMode = triState("enabled"), toolsMode = triState("toolsEnabled");
const toolsSummary = computed(() => aiMode.value === "deny" || toolsMode.value === "inherit" ? "" : toolsMode.value === "allow" ? t.accountDetail.withTools : t.accountDetail.withoutTools);
const aiSummary = computed(() => `${aiMode.value === "inherit" ? t.accountDetail.byGroups : aiMode.value === "allow" ? t.accountDetail.allowedPersonally : t.accountDetail.deniedPersonally} · ${toolsSummary.value}${quotaSummary(aiDraft.value)}`);
async function loadAi() {
  aiLoading.value = true; aiError.value = "";
  try { const value = await adminApi.aiPolicy(props.account.id); if (!disposed) { aiState.value = value; aiDraft.value = policyDraft(value); } }
  catch (failure) { if (!disposed) aiError.value = errorMessage(failure); }
  finally { if (!disposed) aiLoading.value = false; }
}
const dirty = computed(() => accountDirty.value || apiDirty.value || aiDirty.value);
const saving = ref(false);
const saveError = ref("");
const conflict = ref("");
const reloadingConflict = ref(false);
const confirmRevoke = ref(false);
/** The wiki administrator role changes: the save waits for a fresh sign-in (owner decision 3a). */
const confirmAdmin = ref(false);
const adminAttempt = ref(0);
const adminChanging = computed(() => accountDirty.value && draft.value.isWikiAdmin !== props.account.isWikiAdmin);
watch(dirty, (value) => {
  if (guard) guard.dirty.value = value;
  if (value) saveError.value = "";
});

async function save(confirmed = false, proof?: string | null) {
  if (saving.value || !dirty.value) return;
  if (accountDirty.value && nameError.value) {
    saveError.value = nameError.value;
    return;
  }
  if (aiDirty.value && !validQuotas(aiDraft.value)) {
    saveError.value = t.accountDetail.quotaInvalid;
    return;
  }
  // Removing the permission revokes every issued token for good: confirm it first.
  if (revoking.value && !confirmed) {
    confirmRevoke.value = true;
    return;
  }
  // Správce wiki on or off: a fresh sign-in first (ReauthDialog), then this save again with its proof.
  if (adminChanging.value && proof === undefined) {
    confirmRevoke.value = false;
    adminAttempt.value++;
    confirmAdmin.value = true;
    return;
  }
  saving.value = true;
  saveError.value = "";
  conflict.value = "";
  let step: "account" | "api" | "ai" = "account";
  try {
    if (accountDirty.value) {
      const saved = await adminApi.updateUser(props.account.id, { displayName: draft.value.displayName.trim(), isActive: draft.value.isActive, isWikiAdmin: draft.value.isWikiAdmin, expectedVersion: props.account.version }, proof);
      emit("saved", saved);
    }
    step = "api";
    if (apiDirty.value && apiState.value) {
      const wasRevoke = revoking.value;
      const state = await adminApi.setApiAccess(props.account.id, apiDraft.value, apiState.value.version);
      apiState.value = state;
      apiDraft.value = levelOf(state);
      flash.notify(wasRevoke ? t.accountDetail.tokensRevoked(props.account.displayName) : t.accountDetail.levelSet(props.account.displayName, apiLevelInfo[apiDraft.value].label.toLocaleLowerCase(locale)), { icon: "key-round" });
    }
    step = "ai";
    if (aiDirty.value && aiState.value) {
      const state = await adminApi.setAiPolicy(props.account.id, { ...aiDraft.value, expectedVersion: aiState.value.version });
      aiState.value = state;
      aiDraft.value = policyDraft(state);
    }
    confirmRevoke.value = false;
    confirmAdmin.value = false;
  } catch (failure) {
    confirmRevoke.value = false;
    if (step === "account" && failure instanceof ApiError && failure.status === 403 && failure.code === reauthRequiredCode) {
      // The server wants a fresh sign-in (the window closed meanwhile): ask again, the draft stays.
      adminAttempt.value++;
      confirmAdmin.value = true;
      saveError.value = "";
      return;
    }
    confirmAdmin.value = false;
    if (failure instanceof ApiError && failure.status === 409) {
      // The draft stays (never replaced silently); Načíst znovu discards it. The API cannot force a save.
      conflict.value = step === "account"
        ? t.accountDetail.conflictAccount
        : step === "ai" ? t.accountDetail.conflictAi
        : t.accountDetail.conflictApi;
      saveError.value = t.accountDetail.notSaved;
    } else saveError.value = errorMessage(failure);
  } finally {
    saving.value = false;
  }
}
/** The explicit way out of a conflict: the drafts go, the current account and API permission come. */
async function reloadAfterConflict() {
  reloadingConflict.value = true;
  try {
    // The new account resets the draft (watch above); loadApi resets the API draft.
    await Promise.all([props.reload(), loadApi(), loadAi()]);
    if (disposed) return;
    saveError.value = "";
    conflict.value = "";
  } finally {
    reloadingConflict.value = false;
  }
}
function discard() {
  draft.value = { displayName: props.account.displayName, isActive: props.account.isActive, isWikiAdmin: props.account.isWikiAdmin };
  if (apiState.value) apiDraft.value = levelOf(apiState.value);
  if (aiState.value) aiDraft.value = policyDraft(aiState.value);
  saveError.value = "";
}
guard?.register({ save: () => save(), discard });

// Password reset: a fresh sign-in confirmation, then the new one-time code shown once.
const resetOpen = ref(false);
const resetAttempt = ref(0);
const resetBusy = ref(false);
const resetError = ref("");
const resetCode = ref<Invitation>();
async function resetPassword(proof: string | null) {
  resetBusy.value = true;
  resetError.value = "";
  try {
    resetCode.value = await adminApi.resetPassword(props.account, proof);
    resetOpen.value = false;
    // The reset changes the account's version; a draft being edited survives the reload.
    keepDraft = accountDirty.value;
    await props.reload();
    keepDraft = false;
  } catch (failure) {
    if (failure instanceof ApiError && failure.status === 403 && failure.code === reauthRequiredCode) {
      resetAttempt.value++;
      return;
    }
    if (failure instanceof ApiError && failure.status === 409) {
      resetError.value = t.accountDetail.resetConflict;
      // The reset has no draft of its own; an account draft being edited survives the reload.
      keepDraft = accountDirty.value;
      await props.reload();
      keepDraft = false;
    } else resetError.value = errorMessage(failure);
  } finally {
    resetBusy.value = false;
  }
}
// A reset of an account that never activated is a new invitation (7 days); any other reset lasts 30 minutes.
const resetNote = computed(() => t.accountDetail.resetNote(resetCode.value ? ` ${codeValidity(resetCode.value)}` : ""));

onMounted(() => {
  void loadApi();
  void loadAi();
});
onBeforeUnmount(() => {
  disposed = true;
  resetCode.value = undefined;
  guard?.release();
});
</script>

<template>
  <div class="account-detail" :aria-busy="saving || undefined">
    <ConflictNote v-if="conflict" :text="conflict" :reload-label="t.common.reload" :busy="reloadingConflict || saving" data-account-conflict @reload="reloadAfterConflict" @dismiss="conflict = ''" />

    <SettingsSection :title="t.card.tabAccount" icon="account" :description="t.accountDetail.description">
      <SettingsRow :label="t.accountDetail.username" :label-for="false">
        <code class="st-mono" data-account-username>{{ account.username }}</code>
      </SettingsRow>
      <SettingsRow :label="t.accountDetail.signIn" :label-for="false">
        <span class="st-actions">
          <StatusBadge :icon="source.icon" :label="source.label" size="sm" />
          <AccountStatusBadge :account="account" />
          <StatusBadge v-if="isSelf" tone="info" icon="account" :label="t.accountDetail.yourAccount" size="sm" />
        </span>
      </SettingsRow>
      <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.accountDetail.displayName" :error="accountDirty && nameError ? nameError : undefined">
        <input :id="id" v-model="draft.displayName" class="st-input" maxlength="100" name="displayName" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="saving" data-account-display-name />
      </SettingsRow>
      <SettingsRow v-slot="{ id, describedBy }" :label="t.accountDetail.active" :help="t.accountDetail.activeHelp">
        <Switch :id="id" :model-value="draft.isActive" :aria-describedby="describedBy" :disabled="saving" data-account-active @update:model-value="draft.isActive = $event" />
      </SettingsRow>
      <SettingsRow v-slot="{ id, describedBy }" :label="t.common.wikiAdmin" :help="t.accountDetail.adminHelp">
        <Switch :id="id" :model-value="draft.isWikiAdmin" :aria-describedby="describedBy" :disabled="saving" data-account-admin @update:model-value="draft.isWikiAdmin = $event" />
      </SettingsRow>
      <p v-if="isSelf && ((account.isActive && !draft.isActive) || (account.isWikiAdmin && !draft.isWikiAdmin))" class="people-warning" role="note">
        <AtlasIcon name="triangle-alert" :size="15" />{{ t.accountDetail.selfWarning }}
      </p>
    </SettingsSection>

    <SettingsSection :title="t.accountDetail.apiTitle" icon="key-round" :description="t.accountDetail.apiDescription">
      <LoadingNote v-if="apiLoading && !apiState" :label="t.accountDetail.apiLoading" />
      <ErrorState v-else-if="apiError && !apiState" :title="t.accountDetail.apiFailed" :text="apiError" :busy="apiLoading" @retry="loadApi()" />
      <template v-else-if="apiState">
        <ApiLevelPicker v-model="apiDraft" :saved="levelOf(apiState)" :disabled="saving" />
      </template>
    </SettingsSection>

    <SettingsSection :title="t.accountDetail.aiTitle" icon="sliders-horizontal" advanced :summary="aiSummary" data-account-ai :description="t.accountDetail.aiDescription">
      <LoadingNote v-if="aiLoading && !aiState" :label="t.accountDetail.aiLoading" />
      <ErrorState v-else-if="aiError" :title="t.accountDetail.aiFailed" :text="aiError" :busy="aiLoading" @retry="loadAi" />
      <template v-else-if="aiState">
        <SettingsRow :label="t.accountDetail.aiAccess" :label-for="false">
          <OptionPills v-model="aiMode" :options="aiModeOptions(t.accountDetail.byGroups)" :label="t.accountDetail.aiAccess" hide-label :disabled="saving" data-ai-personal-enabled />
        </SettingsRow>
        <InlineReveal :open="aiMode !== 'deny'">
          <SettingsRow :label="t.accountDetail.aiTools" :label-for="false" :help="t.accountDetail.aiToolsHelp">
            <OptionPills v-model="toolsMode" :options="aiModeOptions(t.accountDetail.toolsDefault)" :label="t.accountDetail.aiTools" hide-label :disabled="saving" data-ai-personal-tools />
          </SettingsRow>
        </InlineReveal>
        <p class="st-note" data-ai-personal-resolved>{{ t.accountDetail.resolved(aiState.resolved.enabled, aiState.resolved.toolsEnabled) }}</p>
        <AiQuotaStatus :value="aiState.resolved" />
        <AiQuotaFields :draft="aiDraft" :disabled="saving" :inherit-label="t.accountDetail.inheritLabel" />
        <p class="st-note">{{ t.accountDetail.quotaNote }}</p>
      </template>
    </SettingsSection>

    <SettingsSection v-if="account.hasBuiltinCredential" :title="t.accountDetail.passwordTitle" icon="lock" :description="t.accountDetail.passwordDescription">
      <OneTimeSecret
        v-if="resetCode?.token"
        :value="resetCode.token"
        :label="t.accountDetail.resetCodeLabel"
        :note="resetNote"
        :confirm-label="t.accountDetail.codeConfirm"
        :finished-text="t.accountDetail.codeFinished"
        data-reset-code
        @done="resetCode = undefined"
      />
      <SettingsRow v-else :label="t.accountDetail.resetPassword" :label-for="false" :help="isSelf ? t.accountDetail.resetSelfHelp : t.accountDetail.resetHelp">
        <Button v-if="!isSelf" variant="outline" type="button" :disabled="saving || resetBusy" data-reset-password @click="resetOpen = true; resetError = ''"><AtlasIcon name="refresh" :size="15" />{{ t.accountDetail.resetPassword }}</Button>
      </SettingsRow>
    </SettingsSection>

    <SaveBar :dirty="dirty" :saving="saving" :error="saveError || undefined" :save-label="revoking ? t.accountDetail.saveRevoking : t.accountDetail.saveAccount" @save="save()" @cancel="discard" />

    <ConfirmDialog :open="confirmRevoke" :title="t.accountDetail.revokeTokensTitle" :confirm-label="t.accountDetail.revokeTokensConfirm" confirm-icon="key-round" :busy="saving" @confirm="save(true)" @cancel="confirmRevoke = false">
      <p><strong>{{ account.displayName }}</strong> {{ t.accountDetail.revokeLoseTail }}</p>
      <p>{{ t.accountDetail.revokeLater }}</p>
    </ConfirmDialog>
    <ReauthDialog
      :open="confirmAdmin"
      :title="draft.isWikiAdmin ? t.accountDetail.grantAdminTitle : t.accountDetail.removeAdminTitle"
      :confirm-label="draft.isWikiAdmin ? t.accountDetail.grantAdminConfirm : t.accountDetail.removeAdminConfirm"
      :busy="saving"
      :error="saveError || undefined"
      :attempt="adminAttempt"
      @confirm="save(true, $event)"
      @cancel="confirmAdmin = false"
    >
      <p class="st-note" data-admin-change>
        <AtlasIcon name="shield-check" :size="15" />
        <span v-if="draft.isWikiAdmin"><strong>{{ account.displayName }}</strong> {{ t.accountDetail.adminGrantTail }}</span>
        <span v-else><strong>{{ account.displayName }}</strong> {{ t.accountDetail.adminRemoveTail }}</span>
      </p>
    </ReauthDialog>
    <ReauthDialog :open="resetOpen" :title="t.accountDetail.resetTitle" :confirm-label="t.accountDetail.resetConfirm" :busy="resetBusy" :error="resetError || undefined" :attempt="resetAttempt" @confirm="resetPassword" @cancel="resetOpen = false">
      <p>{{ t.accountDetail.resetLead }} <strong>{{ account.displayName }}</strong> {{ t.accountDetail.resetTail }}</p>
      <p>{{ t.accountDetail.resetNew }}</p>
    </ReauthDialog>
  </div>
</template>

<style scoped>
.account-detail { display: grid; gap: 16px; min-width: 0; }
</style>
