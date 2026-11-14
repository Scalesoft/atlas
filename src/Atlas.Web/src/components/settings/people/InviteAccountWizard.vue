<script setup lang="ts">
import t from "../../../i18n/settings-people";
import { computed, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { disclosureHooks } from "../disclosure";
import type { Invitation } from "../../../administration";
import { codeValidity } from "./accountStatus";
import ChoiceCards from "../ChoiceCards.vue";
import ConfirmWithReauth from "../ConfirmWithReauth.vue";
import OneTimeSecret from "../OneTimeSecret.vue";
import SettingsRow from "../SettingsRow.vue";
import ProgressiveFlow from "../ProgressiveFlow.vue";
import SummaryList from "../SummaryList.vue";
import type { ChoiceOption, FlowStep, SummaryItem } from "../types";
import type { InviteDraft } from "./types";

/**
 * Pozvat člověka: 1 Kdo (username and display name) · 2 Role (regular account or wiki administrator)
 * · 3 Souhrn · 4 the one-time activation code. Store-free: `submit` creates the account and resolves
 * with the invitation (or rejects with the reason, shown on the summary); `takenUsernames` catches
 * a duplicate before the server does. The code is shown once and `done` fires after the admin
 * confirmed handing it over; the parent then leaves the flow. A new wiki administrator is a security change: the summary
 * confirms it with a fresh sign-in (ConfirmWithReauth) and `submit` carries the proof.
 */
const props = withDefaults(defineProps<{ submit: (draft: InviteDraft, proof?: string | null) => Promise<Invitation>; takenUsernames?: string[] }>(), { takenUsernames: () => [] });
const emit = defineEmits<{ done: [invitation: Invitation]; cancel: []; dirty: [value: boolean] }>();

const draft = ref<InviteDraft>({ username: "", displayName: "", isWikiAdmin: false });
const step = ref("who");
const busy = ref(false);
const failure = ref("");
const invitation = ref<Invitation>();

watch(draft, (value) => emit("dirty", !invitation.value && (!!value.username.trim() || !!value.displayName.trim() || value.isWikiAdmin)), { deep: true });
watch(step, () => (failure.value = ""));

const roleOptions = computed<ChoiceOption[]>(() => [
  { value: "regular", label: t.invite.regular, description: t.invite.regularHint, icon: "account", badge: t.invite.usual },
  { value: "admin", label: t.common.wikiAdmin, description: t.invite.adminHint, icon: "shield-check", tone: "warning" },
]);
const role = computed({
  get: () => (draft.value.isWikiAdmin ? "admin" : "regular"),
  set: (value: string) => (draft.value = { ...draft.value, isWikiAdmin: value === "admin" }),
});
const taken = computed(() => new Set(props.takenUsernames.map((name) => name.toLocaleLowerCase("cs"))));
const usernameError = computed(() => taken.value.has(draft.value.username.trim().toLocaleLowerCase("cs")) ? t.invite.usernameTaken : "");
const whoReady = computed(() => !!draft.value.username.trim() && !!draft.value.displayName.trim() && !usernameError.value);

const steps = computed<FlowStep[]>(() => [
  {
    id: "who",
    label: t.invite.whoStep,
    icon: "account",
    title: t.invite.whoTitle,
    hint: t.invite.whoHint,
    summary: draft.value.displayName.trim() || undefined,
    ready: whoReady.value,
  },
  { id: "role", label: t.invite.roleStep, icon: "shield-check", title: t.invite.roleTitle, hint: t.invite.roleHint, summary: roleOptions.value.find((option) => option.value === role.value)?.label, ready: true },
  {
    id: "summary",
    label: t.common.summary,
    icon: "circle-check",
    title: t.invite.summaryTitle,
    hint: draft.value.isWikiAdmin ? t.invite.hintAdmin : t.invite.hintRegular,
    manual: true,
    hideNext: draft.value.isWikiAdmin,
    nextLabel: t.invite.createAndShow,
    nextIcon: "key-round",
  },
  { id: "code", label: t.invite.codeStep, icon: "key-round", title: t.invite.codeTitle, hint: t.invite.codeHint, final: true },
]);
const summary = computed<SummaryItem[]>(() => [
  { key: "username", icon: "account", label: t.invite.username, value: draft.value.username.trim(), code: true },
  { key: "displayName", icon: "users", label: t.invite.displayName, value: draft.value.displayName.trim() },
  { key: "role", icon: "shield-check", label: t.invite.role, value: draft.value.isWikiAdmin ? t.common.wikiAdmin : t.invite.regular },
  { key: "access", icon: "book-open", label: t.invite.contentAccess, value: draft.value.isWikiAdmin ? t.invite.allSpaces : t.invite.noSpaceYet },
]);
const note = computed(() => {
  const validity = invitation.value ? ` ${codeValidity(invitation.value)}` : "";
  return t.invite.note(validity);
});

async function onNext(current: FlowStep) {
  if (current.id !== "summary") return;
  await create();
}
const attempt = ref(0);
async function create(proof?: string | null) {
  if (busy.value) return;
  busy.value = true;
  failure.value = "";
  try {
    invitation.value = await props.submit({ username: draft.value.username.trim(), displayName: draft.value.displayName.trim(), isWikiAdmin: draft.value.isWikiAdmin }, proof);
    emit("dirty", false);
    step.value = "code";
  } catch (reason) {
    failure.value = reason instanceof Error ? reason.message : t.invite.failed;
    // A refused fresh sign-in (reauth_required) asks again in a new confirmation.
    attempt.value++;
  } finally {
    busy.value = false;
  }
}
function finish() {
  // The code leaves the page here: neither this flow nor its parent keeps a copy.
  const result = invitation.value ? { ...invitation.value, token: "" } : undefined;
  invitation.value = result;
  if (result) emit("done", result);
}
</script>

<template>
    <ProgressiveFlow autofocus v-model="step" :steps="steps" :label="t.invite.label" :context="t.invite.context" :busy="busy" @next="onNext" @cancel="emit('cancel')">
    <template #who>
      <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.invite.username" :help="t.invite.usernameHelp" :error="usernameError || undefined">
        <input :id="id" v-model="draft.username" class="st-input" maxlength="100" autocomplete="off" spellcheck="false" :aria-describedby="describedBy" :aria-invalid="invalid" data-step-focus data-invite-username />
      </SettingsRow>
      <SettingsRow v-slot="{ id, describedBy }" :label="t.invite.displayName" :help="t.invite.displayNameHelp">
        <input :id="id" v-model="draft.displayName" class="st-input" maxlength="100" autocomplete="off" :aria-describedby="describedBy" data-invite-display-name />
      </SettingsRow>
    </template>
    <template #role>
      <ChoiceCards v-model="role" :options="roleOptions" :label="t.invite.roleLabel" hide-label :columns="2" size="hero" :reveal-for="['admin']" data-invite-role>
        <template #reveal>
          <p class="invite-warning" role="note"><AtlasIcon name="triangle-alert" :size="15" />{{ t.invite.adminWarning }}</p>
        </template>
      </ChoiceCards>
    </template>
    <template #summary>
      <SummaryList :items="summary" :label="t.invite.summaryLabel" />
      <Transition :css="false" v-bind="disclosureHooks"><p v-if="busy" class="invite-status" role="status"><AtlasIcon name="loader-circle" :size="16" class="atlas-spinner" />{{ t.invite.creating }}</p></Transition>
      <ConfirmWithReauth v-if="draft.isWikiAdmin" :key="attempt" :confirm-label="t.invite.createAndShow" :cancel-label="t.invite.cancel" :busy="busy" :error="failure || undefined" @confirm="create" @cancel="emit('cancel')" />
      <Transition v-else :css="false" v-bind="disclosureHooks"><p v-if="failure" class="invite-failure" role="alert" data-invite-error><AtlasIcon name="triangle-alert" :size="15" />{{ failure }}</p></Transition>
    </template>
    <template #code>
      <OneTimeSecret
        v-if="invitation?.token"
        :value="invitation.token"
        :label="t.invite.codeLabel"
        :note="note"
        :confirm-label="t.invite.codeConfirm"
        :finished-text="t.invite.codeFinished"
        data-invite-code
        @done="finish"
      />
    </template>
    </ProgressiveFlow>
</template>

<style scoped>
.invite-warning { display: flex; align-items: flex-start; gap: 8px; margin: 0; color: var(--text); font-size: 13px; line-height: 1.5; }
.invite-warning > svg { flex-shrink: 0; margin-top: 2px; color: var(--warning); }
.invite-status { display: flex; align-items: center; gap: 8px; margin: 0; color: var(--muted); font-size: 13px; }
.invite-failure { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
</style>
