<script setup lang="ts">
import t from "../../../i18n/settings-people";
import { computed, onBeforeUnmount, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { accessApi } from "../../../accessApi";
import { errorMessage } from "../../../api";
import type { Group } from "../../../administration";
import AuditActivityPanel from "../AuditActivityPanel.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import SettingsTabs from "../SettingsTabs.vue";
import PrincipalAccessEditor from "../access/PrincipalAccessEditor.vue";
import PrincipalGrant from "../access/PrincipalGrant.vue";
import { globalRowHref } from "../access/accessLinks";
import type { PrincipalAccess } from "../access/accessModel";
import { entryKey } from "../access/accessView";
import { useSettingsFlash } from "../flash";
import type { SettingsTab } from "../types";
import { useSettingsRoute } from "../useSettingsRoute";
import GroupDetail from "./GroupDetail.vue";

/**
 * A group's card in Nastavení wiki → Skupiny (`people/groups/{id}[/tab]`): Přístupy (the group's own grants, GET
 * /api/access/principals/group/{id}; its members get them all), Členové (rename and members) and Činnost (the audit
 * narrowed to the group). Přidat přístup is its own route (`…/new`).
 */
const props = defineProps<{ group: Group; part?: string; reload: () => Promise<void> }>();
const emit = defineEmits<{ saved: [group: Group] }>();
const router = useSettingsRoute();
const flash = useSettingsFlash();

const path = computed(() => `people/groups/${props.group.id}`);
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
    const next = await accessApi.principal("group", props.group.id);
    if (disposed || current !== request) return;
    access.value = next;
    error.value = "";
  } catch (failure) {
    if (!disposed && current === request) error.value = errorMessage(failure);
  } finally {
    if (!disposed && current === request) loading.value = false;
  }
}
watch(() => props.group.id, () => void load(), { immediate: true });
onBeforeUnmount(() => (disposed = true));

const tabs = computed<SettingsTab[]>(() => [
  { id: "access", label: t.common.access, icon: "key-round", href: router.href(path.value) },
  { id: "members", label: t.common.members, icon: "account", href: router.href(`${path.value}/members`) },
  { id: "activity", label: t.common.activity, icon: "scroll-text", href: router.href(`${path.value}/activity`) },
]);
const description = computed(() => [props.group.source === "hub" ? t.common.managedInHub : t.common.localGroup, access.value?.principal.detail].filter(Boolean).join(" · "));
const spaces = computed(() => new Map((access.value?.entries ?? []).map((entry) => [entryKey(entry), entry.spaceId])));
const rowHref = computed(() => globalRowHref(router, path.value, (row) => spaces.value.get(row.key)));
function granted(message: string) {
  router.navigate(path.value);
  flash.notify(message);
  void load();
}
</script>

<template>
  <SettingsPage v-if="tab === 'new'" key="new" :title="t.common.addAccess" :description="t.groupCard.newDescription(group.name)" :back="{ href: router.href(path), label: group.name }">
    <ErrorState v-if="error && !access" :title="t.common.loadAccessFailed" :text="error" :busy="loading" @retry="load()" />
    <LoadingNote v-else-if="!access" :label="t.common.loadingAccess" />
    <SettingsSection v-else :label="t.common.addAccess">
      <PrincipalGrant :access="access" @done="granted" @cancel="router.navigate(path)" />
    </SettingsSection>
  </SettingsPage>

  <SettingsPage v-else key="card" :title="group.name" icon="users" :description="description" :back="{ href: router.href('people/groups'), label: t.groupCard.allGroups }" data-group-card>
    <template #actions>
      <Button size="sm" as-child><a :href="router.href(`${path}/new`)" data-group-grant><AtlasIcon name="plus" :size="15" />{{ t.common.addAccess }}</a></Button>
    </template>
    <SettingsTabs :options="tabs" :model-value="tab" :label="t.groupCard.parts" />

    <!-- The part under the tabs changes like the parts of the other settings pages: the old one leaves, the new one rises in. -->
    <Transition name="st-step" mode="out-in">
      <div :key="tab" class="group-part" :data-group-part="tab">
        <template v-if="tab === 'access'">
          <ErrorState v-if="error" :title="t.common.loadAccessFailed" :text="error" :busy="loading" @retry="load()" />
          <LoadingNote v-if="loading && !access" :label="t.common.loadingAccess" />
          <PrincipalAccessEditor
            v-if="access"
            :access="access"
            :reload="load"
            :row-href="rowHref"
            :scope-note="t.groupCard.scopeNote"
            :empty-text="t.groupCard.empty"
          />
        </template>
        <GroupDetail v-else-if="tab === 'members'" :key="group.id" :group="group" :reload="reload" @saved="emit('saved', $event)" @members="load" />
        <AuditActivityPanel
          v-else-if="tab === 'activity'"
          :key="`activity-${group.id}`"
          :filter="{ groupId: group.id }"
          :label="t.groupCard.activityLabel"
          :description="t.groupCard.activityDescription"
          :history-href="router.href('security/audit', { co: 'group' })"
        />
        <EmptyState v-else icon="compass" :title="t.groupCard.missing" compact>
          <Button as-child variant="outline"><a :href="router.href(path)"><AtlasIcon name="back" :size="16" />{{ t.common.access }}</a></Button>
        </EmptyState>
      </div>
    </Transition>
  </SettingsPage>
</template>

<style scoped>
.group-part { display: grid; gap: 18px; min-width: 0; }
</style>
