<script setup lang="ts">
import { computed, ref, watch } from "vue";
import t from "../../../i18n/settings-requests";
import { locale } from "../../../i18n";
import AtlasIcon from "../../AtlasIcon.vue";
import { disclosureHooks } from "../disclosure";
import { Button } from "../../ui/button";
import type { AccessRequest } from "../../../secretRelease";
import type { ReauthTarget } from "../../../protection";
import { formatDateTime } from "../../../formatDate";
import ConfirmWithReauth from "../ConfirmWithReauth.vue";
import EmptyState from "../EmptyState.vue";
import GrantValidityFields from "../GrantValidityFields.vue";
import InlineReveal from "../InlineReveal.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import StatusBadge from "../StatusBadge.vue";
import SubjectAvatar from "../SubjectAvatar.vue";
import { validityError, validitySummary } from "../grantValidity";

/**
 * Pending access requests for a manager to decide: who asks for Čte on which password or vault, why and since when. Schválit
 * opens the validity (GrantValidityFields, 30 dní by default) and a fresh sign-in for the request's scope (none inside the session's
 * re-authentication window); Zamítnout an optional note and no sign-in, since a rejection cannot widen access (only if a server
 * ever asks, `reauthFor`). Store-free: the parent calls the API, shows `busy`
 * and a refusal in `error`, and removes decided rows. Use AccessRequestsPanel for a page backed by the live feed.
 */
const props = withDefaults(
  defineProps<{
    requests: AccessRequest[];
    busy?: boolean;
    error?: string;
    /** A rejection the server refused without a proof: that row asks for a fresh sign-in first. */
    reauthFor?: string | null;
    /** Hide the space and vault in the rows (a vault's own list). */
    compact?: boolean;
  }>(),
  { busy: false, error: undefined, reauthFor: null, compact: false },
);
const emit = defineEmits<{
  approve: [request: AccessRequest, expiresAt: string | null, proofId: string | null];
  reject: [request: AccessRequest, note: string | null, proofId: string | null];
}>();

const open = ref<{ id: string; kind: "approve" | "reject" } | null>(null);
const expiresAt = ref("");
const startAt = ref("");
const note = ref("");
function toggle(request: AccessRequest, kind: "approve" | "reject") {
  open.value = open.value?.id === request.id && open.value.kind === kind ? null : { id: request.id, kind };
  expiresAt.value = "";
  note.value = "";
}
const target = (request: AccessRequest): ReauthTarget => (request.secretId ? { secretId: request.secretId } : { vaultId: request.vaultId });
const title = (request: AccessRequest) => (request.secretTitle ? t.list.secret(request.secretTitle) : t.list.vault(request.vaultName));
const iso = computed(() => (expiresAt.value ? new Date(expiresAt.value).toISOString() : null));
// A typed end in the past is said next to the field and blocks Schválit before the server would refuse it.
const validity = computed(() => validityError("", expiresAt.value));
watch(
  () => props.requests.map((request) => request.id).join("|"),
  () => {
    if (open.value && !props.requests.some((request) => request.id === open.value!.id)) open.value = null;
  },
);
</script>

<template>
  <SettingsList v-if="requests.length" :label="t.list.label">
    <SettingsListRow
      v-for="request in requests"
      :key="request.id"
      :title="request.requesterName"
      :description="compact ? t.list.describe(title(request), formatDateTime(request.createdAt)) : t.list.describeWithVault(title(request), request.vaultName, formatDateTime(request.createdAt))"
      :data-request="request.id"
    >
      <template #leading><SubjectAvatar kind="user" :name="request.requesterName" /></template>
      <template #meta>
        <StatusBadge tone="info" icon="key-round" :label="t.list.wantsRead" size="sm" />
        <StatusBadge tone="warning" icon="hourglass" :label="t.list.waiting" size="sm" />
      </template>
      <template #actions>
        <Button size="sm" type="button" :disabled="busy" :aria-expanded="open?.id === request.id && open.kind === 'approve'" data-request-approve @click="toggle(request, 'approve')"><AtlasIcon name="circle-check" :size="15" />{{ t.list.approve }}</Button>
        <Button variant="ghost" size="sm" type="button" :disabled="busy" :aria-expanded="open?.id === request.id && open.kind === 'reject'" data-request-reject @click="toggle(request, 'reject')"><AtlasIcon name="circle-x" :size="15" />{{ t.list.reject }}</Button>
      </template>
      <p class="request-reason"><AtlasIcon name="info" :size="14" /><span><strong>{{ t.list.reason }}</strong> {{ request.reason }}</span></p>
      <InlineReveal :open="open?.id === request.id && open.kind === 'approve'">
        <div class="request-decision">
          <GrantValidityFields
            v-model:start-at="startAt"
            v-model:expires-at="expiresAt"
            :label="t.list.validity"
            hide-start
            initial-preset="30"
            :allow-no-end="false"
            :no-end-reason="t.list.noEndReason"
            :error="validity || undefined"
            :disabled="busy"
          />
          <ConfirmWithReauth
            :title="t.list.approveTitle"
            :description="t.list.approveText(request.requesterName, title(request).toLocaleLowerCase(locale), validitySummary('', expiresAt))"
            :confirm-label="t.list.approveConfirm"
            :cancel-label="t.list.cancel"
            :target="target(request)"
            :disabled="!!validity"
            :busy="busy"
            :error="open?.id === request.id ? error : undefined"
            @confirm="emit('approve', request, iso, $event)"
            @cancel="open = null"
          />
        </div>
      </InlineReveal>
      <InlineReveal :open="open?.id === request.id && open.kind === 'reject'">
        <div class="request-decision">
          <label class="request-note">
            <span>{{ t.list.note }} <small>{{ t.list.optional }}</small></span>
            <textarea v-model="note" class="st-input" rows="2" maxlength="500" :disabled="busy" data-request-note />
          </label>
          <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.list.rejectNote }}</p>
          <ConfirmWithReauth
            v-if="reauthFor === request.id"
            :title="t.list.reauthTitle"
            :confirm-label="t.list.rejectConfirm"
            :cancel-label="t.list.cancel"
            :target="target(request)"
            :busy="busy"
            :error="error"
            @confirm="emit('reject', request, note.trim() || null, $event)"
            @cancel="open = null"
          />
          <template v-else>
            <Transition :css="false" v-bind="disclosureHooks">
              <p v-if="open?.id === request.id && error" class="request-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p>
            </Transition>
            <div class="st-actions">
              <Button variant="destructive" size="sm" type="button" :disabled="busy" data-request-reject-confirm @click="emit('reject', request, note.trim() || null, null)"><AtlasIcon :name="busy ? 'loader-circle' : 'circle-x'" :size="15" :class="{ 'atlas-spinner': busy }" />{{ t.list.rejectConfirm }}</Button>
              <Button variant="ghost" size="sm" type="button" :disabled="busy" @click="open = null">{{ t.list.cancel }}</Button>
            </div>
          </template>
        </div>
      </InlineReveal>
    </SettingsListRow>
  </SettingsList>
  <EmptyState v-else icon="circle-check" :title="t.list.emptyTitle" :text="t.list.emptyText" compact />
</template>

<style scoped>
.request-reason { display: flex; align-items: flex-start; gap: 6px; margin: -4px 0 10px 48px; color: var(--muted); font-size: 13px; line-height: 1.5; }
.request-reason > svg { flex-shrink: 0; margin-top: 3px; }
.request-reason strong { color: var(--text); font-weight: 600; }
.request-decision { display: grid; gap: 12px; padding: 2px 0 16px; }
.request-note { display: grid; gap: 5px; max-width: 560px; font-size: 13px; font-weight: 600; }
.request-note small { color: var(--muted); font-weight: 500; }
.request-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
@container (max-width: 560px) { .request-reason { margin-left: 0; } }
</style>
