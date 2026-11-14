<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { accessApi } from "../../../accessApi";
import { ApiError, errorMessage } from "../../../api";
import type { DirectoryUser } from "../../../administration";
import { formatDateTime } from "../../../formatDate";
import { reauthRequiredCode } from "../../../protection";
import AuditActivityPanel from "../AuditActivityPanel.vue";
import ConflictNote from "../ConflictNote.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import ImpactPreview from "../ImpactPreview.vue";
import LoadingNote from "../LoadingNote.vue";
import ReauthDialog from "../ReauthDialog.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import SettingsTabs from "../SettingsTabs.vue";
import SubjectAvatar from "../SubjectAvatar.vue";
import PrincipalAccessEditor from "../access/PrincipalAccessEditor.vue";
import PrincipalGrant from "../access/PrincipalGrant.vue";
import { globalRowHref } from "../access/accessLinks";
import type { PrincipalAccess, RevokeAllImpact } from "../access/accessModel";
import { entryKey } from "../access/accessView";
import { useSettingsFlash } from "../flash";
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-people";
import type { ImpactItem, SettingsTab } from "../types";
import { useSettingsRoute } from "../useSettingsRoute";
import AccountDetail from "./AccountDetail.vue";
import ApiTokensPanel from "./ApiTokensPanel.vue";
import { accountStatus, accountStatusLabels } from "./accountStatus";

/**
 * A person's card in Nastavení wiki → Lidé (`people/accounts/{id}[/tab]`): Přístupy (everything the person reaches and
 * from where, GET /api/access/principals/user/{id}), Účet (identity, správce wiki, API permission, password), Skupiny,
 * Tokeny and Činnost (the audit narrowed to the person). Přidat přístup is its own route (`…/new`, PrincipalGrantFlow);
 * Odebrat vše… shows what it would close (revoke-all preview) and confirms with a fresh sign-in.
 */
const props = defineProps<{ account: DirectoryUser; part?: string; currentUserId: string; reload: () => Promise<void> }>();
const emit = defineEmits<{ saved: [account: DirectoryUser] }>();
const router = useSettingsRoute();
const flash = useSettingsFlash();

const path = computed(() => `people/accounts/${props.account.id}`);
const tab = computed(() => props.part ?? "access");
const access = ref<PrincipalAccess>();
const loading = ref(false);
const error = ref("");
let request = 0;
let disposed = false;
async function load() {
  const current = ++request;
  loading.value = true;
  try {
    const next = await accessApi.principal("user", props.account.id);
    if (disposed || current !== request) return;
    access.value = next;
    error.value = "";
  } catch (failure) {
    if (!disposed && current === request) error.value = errorMessage(failure);
  } finally {
    if (!disposed && current === request) loading.value = false;
  }
}
watch(() => props.account.id, () => void load(), { immediate: true });
onBeforeUnmount(() => (disposed = true));

const groups = computed(() => [...(access.value?.principal.groups ?? [])].sort((a, b) => a.name.localeCompare(b.name, locale)));
const tabs = computed<SettingsTab[]>(() => [
  { id: "access", label: t.common.access, icon: "key-round", href: router.href(path.value) },
  { id: "account", label: t.card.tabAccount, icon: "account", href: router.href(`${path.value}/account`) },
  { id: "groups", label: t.common.groups, icon: "users", count: access.value ? groups.value.length : undefined, href: router.href(`${path.value}/groups`) },
  { id: "tokens", label: t.card.tabTokens, icon: "key-round", href: router.href(`${path.value}/tokens`) },
  { id: "activity", label: t.common.activity, icon: "scroll-text", href: router.href(`${path.value}/activity`) },
]);
const description = computed(() => {
  const status = accountStatusLabels[accountStatus(props.account)].label;
  const signIn = access.value?.principal.lastSignInAt ? t.card.lastSignedIn(formatDateTime(access.value.principal.lastSignInAt)) : undefined;
  const member = groups.value.length ? t.card.groups(groups.value.map((group) => group.name).join(", ")) : undefined;
  return [props.account.username, status, props.account.isWikiAdmin ? t.common.wikiAdmin : undefined, signIn, member].filter(Boolean).join(" · ");
});
const spaces = computed(() => new Map((access.value?.entries ?? []).map((entry) => [entryKey(entry), entry.spaceId])));
const rowHref = computed(() => globalRowHref(router, path.value, (row) => spaces.value.get(row.key)));
const groupHref = (id: string) => router.href(`people/groups/${id}`);

function granted(message: string) {
  router.navigate(path.value);
  flash.notify(message);
  void load();
}

// Odebrat vše: what it would close first, then a destructive confirmation with a fresh sign-in.
const revokeOpen = ref(false);
const impact = ref<RevokeAllImpact>();
const revokeBusy = ref(false);
const revokeError = ref("");
const revokeAttempt = ref(0);
/** A refused Odebrat vše (409): the server names what stands in the way, e.g. the last permanent Spravuje of a space. */
const revokeConflict = ref("");
async function askRevokeAll() {
  revokeError.value = "";
  revokeConflict.value = "";
  impact.value = undefined;
  revokeAttempt.value++;
  revokeOpen.value = true;
  try {
    impact.value = await accessApi.revokeAllPreview(props.account.id);
  } catch (failure) {
    revokeError.value = errorMessage(failure);
  }
}

const impactItems = computed<ImpactItem[]>(() =>
  impact.value
    ? [
        { icon: "key-round", label: t.card.impactGrants(impact.value.grants), detail: t.card.impactGrantsDetail, tone: impact.value.grants ? "danger" : "neutral" },
        { icon: "users", label: t.card.impactGroups(impact.value.groups), tone: impact.value.groups ? "danger" : "neutral" },
        { icon: "ban", label: t.card.impactTokens(impact.value.tokens), tone: impact.value.tokens ? "danger" : "neutral" },
      ]
    : [],
);
async function revokeAll(proof: string | null) {
  revokeBusy.value = true;
  revokeError.value = "";
  try {
    await accessApi.revokeAll(props.account.id, proof);
    revokeOpen.value = false;
    flash.notify(t.card.revokedAll(props.account.displayName), { icon: "trash" });
    await load();
  } catch (failure) {
    if (failure instanceof ApiError && failure.code === reauthRequiredCode) {
      revokeAttempt.value++;
      revokeError.value = t.common.expired;
    } else if (failure instanceof ApiError && failure.status === 409) {
      revokeOpen.value = false;
      revokeConflict.value = failure.message;
      await load();
    } else revokeError.value = errorMessage(failure);
  } finally {
    revokeBusy.value = false;
  }
}
</script>

<template>
  <SettingsPage v-if="tab === 'new'" key="new" :title="t.common.addAccess" :description="t.card.newDescription(account.displayName)" :back="{ href: router.href(path), label: account.displayName }">
    <ErrorState v-if="error && !access" :title="t.common.loadAccessFailed" :text="error" :busy="loading" @retry="load()" />
    <LoadingNote v-else-if="!access" :label="t.common.loadingAccess" />
    <SettingsSection v-else :label="t.common.addAccess">
      <PrincipalGrant :access="access" @done="granted" @cancel="router.navigate(path)" />
    </SettingsSection>
  </SettingsPage>

  <SettingsPage v-else key="card" :title="account.displayName" icon="account" :description="description" :back="{ href: router.href('people/accounts'), label: t.card.allPeople }" data-person-card>
    <template #actions>
      <Button size="sm" as-child><a :href="router.href(`${path}/new`)" data-person-grant><AtlasIcon name="plus" :size="15" />{{ t.common.addAccess }}</a></Button>
      <Button v-if="account.id !== currentUserId" variant="outline" size="sm" type="button" class="person-danger" data-revoke-all @click="askRevokeAll"><AtlasIcon name="trash" :size="15" />{{ t.card.revokeAllButton }}</Button>
    </template>
    <ConflictNote v-if="revokeConflict" :text="revokeConflict" data-revoke-all-conflict @dismiss="revokeConflict = ''" />
    <SettingsTabs :options="tabs" :model-value="tab" :label="t.card.parts" />

    <!-- The part under the tabs changes like the parts of the other settings pages: the old one leaves, the new one rises in. -->
    <Transition name="st-step" mode="out-in">
      <div :key="tab" class="person-part" :data-person-part="tab">
        <template v-if="tab === 'access'">
          <ErrorState v-if="error" :title="t.common.loadAccessFailed" :text="error" :busy="loading" @retry="load()" />
          <LoadingNote v-if="loading && !access" :label="t.common.loadingAccess" />
          <PrincipalAccessEditor v-if="access" :access="access" :reload="load" :row-href="rowHref" :group-href="groupHref" :empty-text="t.card.emptyAccess" />
        </template>

        <AccountDetail v-else-if="tab === 'account'" :key="account.id" :account="account" :current-user-id="currentUserId" :reload="reload" @saved="emit('saved', $event)" />

        <SettingsSection v-else-if="tab === 'groups'" :title="t.common.groups" icon="users" :count="access ? groups.length : undefined" :description="t.card.groupsDescription">
          <LoadingNote v-if="!access && !error" :label="t.card.loadingGroups" />
          <ErrorState v-else-if="error" :title="t.card.groupsFailed" :text="error" :busy="loading" @retry="load()" />
          <SettingsList v-else-if="groups.length" :label="t.card.groupsList">
            <SettingsListRow v-for="group in groups" :key="group.id" title="" :data-account-group="group.id">
              <template #leading><SubjectAvatar kind="group" :name="group.name" /></template>
              <template #title><a class="st-link" :href="groupHref(group.id)">{{ group.name }}</a></template>
              <template #description>{{ t.card.groupMembership }}</template>
            </SettingsListRow>
          </SettingsList>
          <EmptyState v-else compact icon="users" :title="t.card.notMember" :text="t.card.editMembership" />
        </SettingsSection>

        <ApiTokensPanel v-else-if="tab === 'tokens'" :key="`tokens-${account.id}`" :user-id="account.id" />

        <AuditActivityPanel
          v-else-if="tab === 'activity'"
          :key="`activity-${account.id}`"
          :filter="{ userId: account.id }"
          :label="t.card.activityLabel"
          :description="t.card.activityDescription"
          :history-href="router.href('security/audit', { kdo: account.id })"
        />

        <EmptyState v-else icon="compass" :title="t.card.missing" compact>
          <Button as-child variant="outline"><a :href="router.href(path)"><AtlasIcon name="back" :size="16" />{{ t.common.access }}</a></Button>
        </EmptyState>
      </div>
    </Transition>

    <ReauthDialog
      :open="revokeOpen"
      :title="t.card.revokeAll"
      :confirm-label="t.card.revokeAllFor(account.displayName)"
      :busy="revokeBusy"
      :error="revokeError || undefined"
      :attempt="revokeAttempt"
      @confirm="revokeAll"
      @cancel="revokeOpen = false"
    >
      <LoadingNote v-if="!impact && !revokeError" :label="t.card.finding" />
      <ImpactPreview v-else-if="impact" :items="impactItems" :unchanged="t.card.unchanged" data-revoke-all-impact />
    </ReauthDialog>
  </SettingsPage>
</template>

<style scoped>
.person-danger { color: var(--error); }
.person-part { display: grid; gap: 18px; min-width: 0; }
</style>
