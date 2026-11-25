<script setup lang="ts">
import t from "../../../i18n/settings-people";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { disclosureHooks } from "../disclosure";
import { Button } from "../../ui/button";
import { ApiError, errorMessage } from "../../../api";
import { adminApi, adminPageLimit, type DirectoryUser, type Group, type Member } from "../../../administration";
import { reauthRequiredCode } from "../../../protection";
import ConfirmDialog from "../ConfirmDialog.vue";
import ConflictNote from "../ConflictNote.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import InlineReveal from "../InlineReveal.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import ReauthDialog from "../ReauthDialog.vue";
import SaveBar from "../SaveBar.vue";
import SearchableChoiceList from "../SearchableChoiceList.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import SubjectAvatar from "../SubjectAvatar.vue";
import { useDraftGuard } from "../dirtyGuard";
import { useSettingsFlash } from "../flash";
import type { ChoiceOption } from "../types";
import { useSettingsRoute } from "../useSettingsRoute";
import "./people.css";
import { locale } from "../../../i18n";

/**
 * The Členové tab of a group's card in Nastavení wiki → Skupiny: rename with the CAS version (SaveBar), members added
 * from a searchable list of active accounts and removed after a confirmation. A conflict on a member (an immediate
 * action) reloads the members and says so; one on the name keeps the draft and offers Načíst znovu. The group's access
 * and its latest changes are tabs of their own.
 */
const props = defineProps<{ group: Group; reload: () => Promise<void> }>();
/** `members`: the membership changed, so the card's header count („· 2 členové“) is asked again. */
const emit = defineEmits<{ saved: [group: Group]; members: [] }>();
const router = useSettingsRoute();
const flash = useSettingsFlash();
const managed = computed(() => props.group.source === "hub");
let disposed = false;

// Rename.
const name = ref(props.group.name);
watch(
  () => props.group,
  (group) => (name.value = group.name),
);
const dirty = computed(() => !managed.value && name.value.trim() !== props.group.name);
const saving = ref(false);
const saveError = ref("");
const conflict = ref("");
/** The conflict is about the name draft, which stays until Načíst znovu. */
const nameConflict = ref(false);
watch(dirty, (value) => {
  if (value) saveError.value = "";
});
async function save() {
  if (saving.value || !dirty.value) return;
  if (!name.value.trim()) {
    saveError.value = t.groupDetail.nameEmpty;
    return;
  }
  saving.value = true;
  saveError.value = "";
  conflict.value = "";
  nameConflict.value = false;
  try {
    emit("saved", await adminApi.updateGroup(props.group, name.value.trim()));
  } catch (failure) {
    if (failure instanceof ApiError && failure.status === 409) {
      conflict.value = t.groupDetail.nameConflict;
      nameConflict.value = true;
      saveError.value = t.groupDetail.notSaved;
    } else saveError.value = errorMessage(failure);
  } finally {
    saving.value = false;
  }
}
function discard() {
  name.value = props.group.name;
  saveError.value = "";
}
const reloadingConflict = ref(false);
/** The explicit way out of a name conflict: the draft goes, the current group comes. */
async function reloadAfterConflict() {
  reloadingConflict.value = true;
  try {
    // The new group resets the name draft (watch above).
    await props.reload();
    saveError.value = "";
    conflict.value = "";
    nameConflict.value = false;
  } finally {
    reloadingConflict.value = false;
  }
}
useDraftGuard(dirty, { save, discard });

// Members.
const members = ref<Member[]>([]);
const membersLoading = ref(true);
const membersError = ref("");
// The server answers one page (adminPageLimit rows); `moreMembers` = a full page, so another may follow.
const moreMembers = ref(false);
const loadingMore = ref(false);
const byName = (list: Member[]) => [...list].sort((a, b) => a.displayName.localeCompare(b.displayName, locale));
async function loadMembers() {
  membersLoading.value = true;
  membersError.value = "";
  try {
    const result = await adminApi.members(props.group.id);
    if (!disposed) {
      members.value = byName(result);
      moreMembers.value = result.length >= adminPageLimit;
    }
  } catch (failure) {
    if (!disposed) membersError.value = errorMessage(failure);
  } finally {
    if (!disposed) membersLoading.value = false;
  }
}
async function loadMoreMembers() {
  loadingMore.value = true;
  try {
    const result = await adminApi.members(props.group.id, { offset: members.value.length });
    if (!disposed) {
      members.value = byName([...members.value, ...result]);
      moreMembers.value = result.length >= adminPageLimit;
    }
  } catch (failure) {
    if (!disposed) membersError.value = errorMessage(failure);
  } finally {
    loadingMore.value = false;
  }
}
const adding = ref(false);
const picked = ref<string | null>(null);
const query = ref("");
const memberBusy = ref(false);
const memberError = ref("");
const pendingMembership = ref<{ userId: string; member: Member | undefined; isMember: boolean }>();
const memberAttempt = ref(0);
// The candidates are the server's answer to what is typed (name or username, sent when the typing pauses; an older answer is dropped).
const found = ref<DirectoryUser[]>([]);
const searching = ref(false);
let searchVersion = 0;
let searchTimer: ReturnType<typeof setTimeout> | undefined;
watch([adding, query], () => {
  clearTimeout(searchTimer);
  if (!adding.value) return;
  const current = ++searchVersion;
  const text = query.value.trim();
  searching.value = true;
  searchTimer = setTimeout(async () => {
    try {
      const result = await adminApi.users({ q: text || undefined });
      if (current === searchVersion) found.value = result;
    } catch (failure) {
      if (current === searchVersion) memberError.value = errorMessage(failure);
    } finally {
      if (current === searchVersion) searching.value = false;
    }
  }, text ? 250 : 0);
});
const candidates = computed<ChoiceOption[]>(() => {
  const taken = new Set(members.value.map((member) => member.id));
  return found.value
    .filter((user) => user.isActive && !taken.has(user.id))
    .map((user) => ({ value: user.id, label: user.displayName, description: user.username, icon: "account", badge: user.isWikiAdmin ? t.common.wikiAdmin : undefined }));
});
const addPanel = ref<HTMLElement>();
const addButton = ref<InstanceType<typeof Button>>();
/** P7: a removed row (or the closed add panel) took the dialog's opener with it; the keyboard stays in Členové, never on <body>. */
const focusAdd = () => (addButton.value?.$el as HTMLElement | undefined)?.focus();
function keepFocus() {
  if (document.activeElement === document.body) focusAdd();
}
async function toggleAdding() {
  adding.value = !adding.value;
  picked.value = null;
  query.value = "";
  memberError.value = "";
  // P7: the opened search takes the keyboard, so typing finds the person at once.
  if (adding.value) { await nextTick(); addPanel.value?.querySelector<HTMLInputElement>("input[type=search]")?.focus(); }
}

const removing = ref<Member>();
const removeError = ref("");
async function changeMembership(userId: string, member: Member | undefined, isMember: boolean, proof?: string | null) {
  if (managed.value || memberBusy.value) return;
  memberBusy.value = true;
  memberError.value = "";
  removeError.value = "";
  conflict.value = "";
  try {
    await adminApi.setMember(props.group, userId, member, isMember, proof);
    await loadMembers();
    emit("members");
    pendingMembership.value = undefined;
    if (isMember) {
      adding.value = false;
      void nextTick(focusAdd); // the panel with the confirm button closes
      picked.value = null;
      const user = found.value.find((item) => item.id === userId);
      flash.notify(t.groupDetail.added(user?.displayName ?? t.groupDetail.account, props.group.name), { highlight: userId });
    } else {
      removing.value = undefined;
      flash.notify(t.groupDetail.removed(member?.displayName ?? t.groupDetail.account, props.group.name), { icon: "trash" });
    }
  } catch (failure) {
    if (failure instanceof ApiError && failure.status === 403 && failure.code === reauthRequiredCode) {
      pendingMembership.value = { userId, member, isMember };
      memberAttempt.value++;
      return;
    }
    if (failure instanceof ApiError && failure.status === 409) {
      conflict.value = t.groupDetail.membersConflict;
      await loadMembers();
      pendingMembership.value = undefined;
      removing.value = undefined;
    } else {
      memberError.value = errorMessage(failure);
      removeError.value = memberError.value;
    }
  } finally {
    memberBusy.value = false;
  }
}
async function addMember() {
  const userId = picked.value;
  if (!userId || memberBusy.value) return;
  await changeMembership(userId, undefined, true);
}
async function removeMember() {
  const member = removing.value;
  if (!member) return;
  await changeMembership(member.id, member, false);
}
async function retryMembership(proof: string | null) {
  const pending = pendingMembership.value;
  if (pending) await changeMembership(pending.userId, pending.member, pending.isMember, proof);
}

onMounted(() => {
  void loadMembers();
});
onBeforeUnmount(() => {
  disposed = true;
  clearTimeout(searchTimer);
});
</script>

<template>
  <div class="group-detail">
    <ConflictNote v-if="conflict" :text="conflict" :reload-label="nameConflict ? t.common.reload : undefined" :busy="reloadingConflict || saving" data-group-conflict @reload="reloadAfterConflict" @dismiss="conflict = ''" />

    <SettingsSection :title="t.groupDetail.title" icon="users" :description="managed ? t.groupDetail.managedDescription : t.groupDetail.localDescription">
      <SettingsRow v-if="managed" :label="t.common.name" :label-for="false">
        <strong>{{ group.name }}</strong>
        <StatusBadge :label="t.common.managedInHub" tone="info" />
        <Button v-if="group.hubUrl" as-child variant="outline" size="sm"><a :href="group.hubUrl" target="_blank" rel="noopener noreferrer" data-hub-manage>{{ t.groupDetail.manageInHub }}</a></Button>
      </SettingsRow>
      <SettingsRow v-else v-slot="{ id, describedBy, invalid }" :label="t.common.name" :error="dirty && !name.trim() ? t.groupDetail.nameEmpty : undefined">
        <input :id="id" v-model="name" class="st-input" maxlength="100" autocomplete="off" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="saving" data-group-rename />
      </SettingsRow>
    </SettingsSection>

    <SettingsSection :title="t.common.members" icon="account" :count="membersLoading || moreMembers ? undefined : members.length" :description="t.groupDetail.membersDescription">
      <template #actions>
        <Button v-if="!managed" ref="addButton" :variant="adding ? 'outline' : 'default'" size="sm" type="button" :aria-expanded="adding" :disabled="membersLoading || memberBusy" data-add-member @click="toggleAdding">
          <AtlasIcon :name="adding ? 'close' : 'plus'" :size="15" />{{ adding ? t.common.cancel : t.groupDetail.addMember }}
        </Button>
      </template>
      <InlineReveal :open="adding && !managed">
        <div ref="addPanel" class="group-add" data-member-candidates>
          <SearchableChoiceList v-model="picked" v-model:query="query" :options="candidates" :loading="searching" remote :label="t.groupDetail.candidates" :placeholder="t.groupDetail.candidatesPlaceholder" :empty-text="t.groupDetail.candidatesEmpty" />
          <Transition :css="false" v-bind="disclosureHooks">
            <p v-if="memberError && adding" class="people-warning" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ memberError }}</p>
          </Transition>
          <div class="st-actions">
            <Button type="button" :disabled="!picked || memberBusy" data-add-member-confirm @click="addMember">
              <AtlasIcon :name="memberBusy ? 'loader-circle' : 'check'" :size="16" :class="{ 'atlas-spinner': memberBusy }" />{{ t.groupDetail.addToGroup }}
            </Button>
          </div>
        </div>
      </InlineReveal>
      <LoadingSkeleton v-if="membersLoading && !members.length" :rows="3" row-height="56px" :label="t.groupDetail.loadingMembers" />
      <ErrorState v-else-if="membersError" :title="t.groupDetail.membersFailed" :text="membersError" :busy="membersLoading" @retry="loadMembers()" />
      <SettingsList v-else-if="members.length" :label="t.groupDetail.membersList">
        <SettingsListRow v-for="member in members" :key="member.id" :title="member.displayName" :description="member.username" :highlighted="flash.highlight.value === member.id" :data-member="member.id">
          <template #leading><SubjectAvatar kind="user" :name="member.displayName" :user-id="member.id" /></template>
          <template #meta>
            <StatusBadge v-if="!member.isActive" tone="neutral" icon="ban" :label="t.common.deactivated" size="sm" />
          </template>
          <template #actions>
            <Button variant="ghost" size="sm" as-child><a :href="router.href(`people/accounts/${member.id}`)"><AtlasIcon name="account" :size="15" />{{ t.groupDetail.card }}</a></Button>
            <Button v-if="!managed" variant="ghost" size="sm" type="button" :disabled="memberBusy" :data-remove-member="member.id" @click="removing = member; removeError = ''"><AtlasIcon name="trash" :size="15" />{{ t.common.remove }}</Button>
          </template>
        </SettingsListRow>
      </SettingsList>
      <EmptyState v-else compact icon="account" :title="t.groupDetail.emptyTitle" :text="managed ? t.groupDetail.emptyManaged : t.groupDetail.emptyLocal" />
      <div v-if="moreMembers && !membersError" class="st-actions">
        <Button variant="outline" size="sm" type="button" :disabled="loadingMore" data-more-members @click="loadMoreMembers"><AtlasIcon :name="loadingMore ? 'loader-circle' : 'plus'" :size="15" :class="{ 'atlas-spinner': loadingMore }" />{{ t.common.loadMore }}</Button>
      </div>
    </SettingsSection>

    <SaveBar v-if="!managed" :dirty="dirty" :saving="saving" :error="saveError || undefined" :save-label="t.groupDetail.saveName" @save="save" @cancel="discard" />

    <ConfirmDialog :open="!!removing && !pendingMembership" :title="t.groupDetail.removeFromGroup" :confirm-label="t.groupDetail.removeFromGroup" confirm-icon="trash" :busy="memberBusy" :error="removeError || undefined" @confirm="removeMember" @cancel="removing = undefined" @closed="keepFocus">
      <p><strong>{{ removing?.displayName }}</strong> {{ t.groupDetail.removeMid }} <strong>{{ group.name }}</strong> {{ t.groupDetail.removeTail }}</p>
      <p>{{ t.groupDetail.removeNote }}</p>
    </ConfirmDialog>
    <ReauthDialog :open="!!pendingMembership" :title="pendingMembership?.isMember ? t.groupDetail.addToGroup : t.groupDetail.removeFromGroup" :confirm-label="pendingMembership?.isMember ? t.groupDetail.addToGroup : t.groupDetail.removeFromGroup" :busy="memberBusy" :error="memberError || undefined" :attempt="memberAttempt" @confirm="retryMembership" @cancel="pendingMembership = undefined; removing = undefined" @closed="keepFocus">
      <p>{{ t.groupDetail.reauthLead }} <strong>{{ group.name }}</strong> {{ t.groupDetail.reauthTail }}</p>
    </ReauthDialog>
  </div>
</template>

<style scoped>
.group-detail { display: grid; gap: 16px; min-width: 0; }
.group-add { display: grid; gap: 10px; padding-bottom: 12px; }
</style>
