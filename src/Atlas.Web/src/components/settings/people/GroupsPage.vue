<script setup lang="ts">
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-people";
import SearchField from "../SearchField.vue";
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { errorMessage } from "../../../api";
import { adminApi, type Group } from "../../../administration";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import SubjectAvatar from "../SubjectAvatar.vue";
import { useSettingsContext } from "../context";
import { useDirtyGuard } from "../dirtyGuard";
import { useSettingsFlash } from "../flash";
import { useSettingsRoute } from "../useSettingsRoute";
import CreateGroupWizard from "./CreateGroupWizard.vue";
import GroupCard from "./GroupCard.vue";
import "./people.css";

/**
 * Nastavení wiki → Kdo → Skupiny (wiki administrator only). `sub` is the third path segment:
 * none = the list with a search (`?hledat=`), `new` = the create wizard, anything else = the group's card with
 * that id (`part`: Přístupy, Členové, Činnost, or `new` for Přidat přístup).
 */
const props = defineProps<{ sub?: string; part?: string }>();
const context = useSettingsContext();
const router = useSettingsRoute();
const flash = useSettingsFlash();
const guard = useDirtyGuard();

const groups = ref<Group[]>([]);
const loading = ref(false);
const loaded = ref(false);
const error = ref("");
let disposed = false;

async function load() {
  if (!context.isWikiAdmin) return;
  loading.value = true;
  error.value = "";
  try {
    const nextGroups = await adminApi.groups();
    if (!disposed) {
      groups.value = nextGroups;
      loaded.value = true;
    }
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
onMounted(load);
onBeforeUnmount(() => (disposed = true));

const mode = computed(() => (!props.sub ? "list" : props.sub === "new" ? "new" : "detail"));
const group = computed(() => (mode.value === "detail" ? groups.value.find((item) => item.id === props.sub) : undefined));
const listHref = computed(() => router.href("people/groups"));
const title = computed(() => (mode.value === "new" ? t.groups.newTitle : group.value?.name ?? t.groups.groups));
const description = computed(() =>
  mode.value === "new"
    ? t.groups.newDescription
    : group.value
      ? group.value.source === "hub" ? t.common.managedInHub : t.common.localGroup
      : t.groups.listDescription,
);

const search = computed({
  get: () => router.route.value.query.hledat ?? "",
  set: (value: string) => router.setQuery({ hledat: value || undefined }),
});
const shown = computed(() => {
  const text = search.value.trim().toLocaleLowerCase(locale);
  return [...groups.value].sort((a, b) => a.name.localeCompare(b.name, locale)).filter((item) => !text || item.name.toLocaleLowerCase(locale).includes(text));
});

const takenNames = computed(() => groups.value.map((item) => item.name));
const submit = (name: string) => adminApi.createGroup(name);
function created(next: Group, open: boolean) {
  if (guard) guard.dirty.value = false;
  groups.value = [...groups.value.filter((item) => item.id !== next.id), next];
  router.navigate(open ? `people/groups/${next.id}/members` : "people/groups");
  flash.notify(t.groups.created(next.name, open), { highlight: next.id });
}
function cancel() {
  if (guard) guard.dirty.value = false;
  router.navigate("people/groups");
}
function setDirty(value: boolean) {
  if (guard) guard.dirty.value = value;
}
function replace(saved: Group) {
  groups.value = groups.value.map((item) => (item.id === saved.id ? saved : item));
}
</script>

<template>
  <GroupCard v-if="mode === 'detail' && context.isWikiAdmin && loaded && group" :key="group.id" :group="group" :part="part" :reload="load" @saved="replace" />
  <SettingsPage v-else :title="title" :description="description" :back="mode === 'list' ? undefined : { href: listHref, label: t.groups.allGroups }">
    <template v-if="mode === 'list' && context.isWikiAdmin && loaded" #actions>
      <Button as-child><a :href="router.href('people/groups/new')" data-new-group><AtlasIcon name="plus" :size="16" />{{ t.groups.newTitle }}</a></Button>
    </template>

    <EmptyState v-if="!context.isWikiAdmin" icon="lock" :title="t.common.wikiAdminsOnly" :text="t.groups.adminsOnlyText" />
    <ErrorState v-else-if="error && !loaded" :title="t.groups.loadFailed" :text="error" :busy="loading" @retry="load()" />
    <LoadingNote v-else-if="!loaded" :label="t.groups.loading" />

    <SettingsSection v-else-if="mode === 'new'" :label="t.groups.newTitle">
      <CreateGroupWizard :submit="submit" :taken-names="takenNames" @done="created($event, false)" @open="created($event, true)" @cancel="cancel" @dirty="setDirty" />
    </SettingsSection>

    <EmptyState v-else-if="mode === 'detail'" icon="users" :title="t.groups.notFound" :text="t.groups.notFoundText">
      <Button as-child variant="outline"><a :href="listHref"><AtlasIcon name="back" :size="16" />{{ t.groups.allGroups }}</a></Button>
    </EmptyState>

    <template v-else>
      <ErrorState v-if="error" :title="t.groups.refreshFailed" :text="error" :busy="loading" @retry="load()" />
      <template v-if="groups.length">
        <div v-if="groups.length > 6" class="people-filters" role="search" :aria-label="t.groups.filter">
          <SearchField v-model="search" :label="t.groups.search" data-group-search />
        </div>
        <SettingsSection :label="t.groups.countLabel(shown.length)">
          <SettingsList v-if="shown.length" :label="t.groups.groups">
            <SettingsListRow v-for="item in shown" :key="item.id" :title="item.name" :description="item.source === 'hub' ? t.common.managedInHub : t.common.localGroup" :highlighted="flash.highlight.value === item.id" :data-group="item.id" :menu="[{ id: 'open', label: t.groups.open, icon: 'arrow-right' }]" :menu-label="t.groups.openFor(item.name)" @select="router.navigate(`people/groups/${item.id}`)">
              <template #leading><SubjectAvatar kind="group" :name="item.name" /></template>
              <template #actions>
                <Button variant="ghost" size="sm" as-child><a :href="router.href(`people/groups/${item.id}`)" :data-open-group="item.id" :aria-label="t.groups.openFor(item.name)"><AtlasIcon name="arrow-right" :size="15" />{{ t.groups.open }}</a></Button>
              </template>
            </SettingsListRow>
          </SettingsList>
          <EmptyState v-else compact icon="search" :title="t.groups.noMatch" :text="t.groups.tryOther" />
        </SettingsSection>
      </template>
      <EmptyState v-else icon="users" :title="t.groups.noneTitle" :text="t.groups.noneText">
        <Button as-child><a :href="router.href('people/groups/new')"><AtlasIcon name="plus" :size="16" />{{ t.groups.newTitle }}</a></Button>
      </EmptyState>
    </template>
  </SettingsPage>
</template>
