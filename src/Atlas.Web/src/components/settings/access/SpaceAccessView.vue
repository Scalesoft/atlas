<script setup lang="ts">
import { computed, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { accessApi } from "../../../accessApi";
import { errorMessage } from "../../../api";
import { spaceSettingsHash } from "../../../settingsRoutes";
import AuditActivityPanel from "../AuditActivityPanel.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import SettingsTabs from "../SettingsTabs.vue";
import SpaceSharingPage from "../space/SpaceSharingPage.vue";
import { useSettingsContext } from "../context";
import { useSettingsFlash } from "../flash";
import t from "../../../i18n/settings-access";
import type { SettingsTab } from "../types";
import { useSettingsRoute } from "../useSettingsRoute";
import AccessExceptionsList from "./AccessExceptionsList.vue";
import ResourceAccess from "./ResourceAccess.vue";
import ResourceGrantFlow from "./ResourceGrantFlow.vue";
import ScopedPrincipalPage from "./ScopedPrincipalPage.vue";
import { accessKind } from "./accessKinds";
import { targetName } from "./accessChanges";
import type { AccessException, AccessSubject, ResourceAccess as ResourceAccessState } from "./accessModel";

/**
 * Who has access to one space, the same in the security centre (`access/spaces/{id}[/…]`, surface global: Kdo má
 * přístup · Výjimky · Odkazy · Činnost) and in the space's own settings (`access[/…]`, surface space: Kdo má přístup ·
 * Výjimky). `base` is the path of this view; `part` the segments below it: `new` (Přidat přístup), `exceptions[/kind/id]`
 * (each exception opens the same panel), `links`, `activity`, and in the space settings `people/{id}` / `groups/{id}`,
 * a card limited to the space. In the centre a person opens their full card in Lidé.
 */
const props = defineProps<{ spaceId: string; base: string; part: string[]; surface: "global" | "space" }>();
const router = useSettingsRoute();
const flash = useSettingsFlash();
const context = useSettingsContext();

const space = computed(() => context.spaces.find((item) => item.id === props.spaceId));
const spaceName = computed(() => space.value?.name ?? loaded.value?.resource.title ?? t.space.fallbackName);
const center = computed(() => props.surface === "global");
const section = computed(() => props.part[0] ?? "access");
const loaded = ref<ResourceAccessState>();
const exceptionsError = ref("");
const exceptionsLoading = ref(false);
async function loadExceptions() {
  exceptionsLoading.value = true;
  exceptionsError.value = "";
  try {
    loaded.value = await accessApi.resource("space", props.spaceId, "effective");
  } catch (failure) {
    exceptionsError.value = errorMessage(failure);
  } finally {
    exceptionsLoading.value = false;
  }
}
watch(
  () => [section.value, props.spaceId],
  () => {
    if (section.value === "exceptions" && !loaded.value) void loadExceptions();
  },
  { immediate: true },
);

const tabs = computed<SettingsTab[]>(() => [
  { id: "access", label: t.space.whoHasAccess, icon: "users", href: router.href(props.base) },
  { id: "exceptions", label: t.space.exceptions, icon: "layers", count: loaded.value?.exceptions?.length, href: router.href(`${props.base}/exceptions`) },
  ...(center.value
    ? [
        { id: "links", label: t.space.links, icon: "link", href: router.href(`${props.base}/links`) },
        { id: "activity", label: t.space.activity, icon: "scroll-text", href: router.href(`${props.base}/activity`) },
      ]
    : []),
]);
const subjectHref = (subject: AccessSubject) =>
  center.value
    ? router.href(subject.kind === "group" ? `people/groups/${subject.id}` : `people/accounts/${subject.id}`)
    : router.href(`${props.base}/${subject.kind === "group" ? "groups" : "people"}/${subject.id}`);
const groupHref = (id: string) => (center.value ? router.href(`people/groups/${id}`) : router.href(`${props.base}/groups/${id}`));
const exceptionHref = (item: AccessException) => router.href(`${props.base}/exceptions/${item.kind}/${item.id}`);
// A nested exception: /exceptions/{kind}/{id}.
const exception = computed(() => (section.value === "exceptions" && props.part[1] && props.part[2] ? { kind: props.part[1], id: props.part[2] } : undefined));
const exceptionTitle = computed(() => {
  const found = loaded.value?.exceptions?.find((item) => item.kind === exception.value?.kind && item.id === exception.value?.id);
  return found ? (found.parentTitle ? `${found.parentTitle} › ${found.title}` : found.title) : accessKind(exception.value?.kind ?? "").one;
});
const title = computed(() => (center.value ? spaceName.value : t.space.access));
const description = computed(() =>
  center.value
    ? t.space.description(space.value?.pageCount ?? 0)
    : t.space.descriptionScoped,
);
function added(message: string) {
  router.navigate(props.base);
  flash.notify(message);
  context.accessChanged();
}
function onLoaded(access: ResourceAccessState) {
  loaded.value = access;
}
</script>

<template>
  <ScopedPrincipalPage
    v-if="!center && (section === 'people' || section === 'groups') && part[1]"
    :key="`${section}-${part[1]}`"
    :kind="section === 'groups' ? 'group' : 'user'"
    :id="part[1]"
    :space-id="spaceId"
    :space-name="spaceName"
    :back-href="router.href(base)"
    :group-href="groupHref"
  />

  <SettingsPage v-else-if="section === 'new'" key="new" :title="t.space.add" :description="t.space.newDescription(spaceName)" :back="{ href: router.href(base), label: center ? spaceName : t.space.access }">
    <SettingsSection :label="t.space.newLabel(spaceName)">
      <ResourceGrantFlow kind="space" :id="spaceId" :name="targetName('space', spaceName)" @done="added" @cancel="router.navigate(base)" />
    </SettingsSection>
  </SettingsPage>

  <SettingsPage
    v-else-if="exception"
    :key="`exception-${exception.kind}-${exception.id}`"
    :title="exceptionTitle"
    :icon="accessKind(exception.kind).icon"
    :description="t.space.exceptionDescription(accessKind(exception.kind).one, spaceName)"
    :back="{ href: router.href(`${base}/exceptions`), label: t.space.exceptions }"
  >
    <ResourceAccess :kind="exception.kind" :id="exception.id" :subject-href="subjectHref" :group-href="groupHref" @changed="loaded = undefined" />
  </SettingsPage>

  <SettingsPage v-else key="space" :title="title" :icon="center ? space?.icon || 'book-open' : 'users'" :description="description" :back="center ? { href: router.href('access/spaces'), label: t.space.allSpaces } : undefined" data-space-access>
    <template v-if="center" #actions>
      <Button variant="outline" size="sm" as-child><a :href="spaceSettingsHash(spaceId)" data-space-settings><AtlasIcon name="settings" :size="15" />{{ t.space.settings }}</a></Button>
    </template>
    <SettingsTabs :options="tabs" :model-value="section" :label="center ? t.space.partsCenter : t.space.parts" />

    <!-- The parts of the space change like the parts of the other settings pages: the old one leaves, the new one rises in; the page and its underline stay. -->
    <Transition name="st-step" mode="out-in">
      <div :key="section" class="space-access-part" :data-space-access-part="section">
        <ResourceAccess v-if="section === 'access'" kind="space" :id="spaceId" :add-href="router.href(`${base}/new`)" :subject-href="subjectHref" :group-href="groupHref" @loaded="onLoaded" @changed="context.accessChanged()" />
        <template v-else-if="section === 'exceptions'">
          <ErrorState v-if="exceptionsError" :title="t.space.exceptionsFailed" :text="exceptionsError" :busy="exceptionsLoading" @retry="loadExceptions()" />
          <LoadingSkeleton v-else-if="!loaded" :label="t.space.exceptionsLoading" />
          <AccessExceptionsList v-else :exceptions="loaded.exceptions ?? []" :href="exceptionHref" />
        </template>
        <SpaceSharingPage v-else-if="center && section === 'links'" :key="`links-${spaceId}`" :space-id="spaceId" embedded />
        <AuditActivityPanel
          v-else-if="center && section === 'activity'"
          :key="`activity-${spaceId}`"
          :filter="{ spaceId }"
          :label="t.space.activityLabel"
          :description="t.space.activityDescription"
          :history-href="router.href('security/audit')"
        />
        <EmptyState v-else icon="compass" :title="t.space.missing" compact>
          <Button as-child variant="outline"><a :href="router.href(base)"><AtlasIcon name="back" :size="16" />{{ t.space.whoHasAccess }}</a></Button>
        </EmptyState>
      </div>
    </Transition>
  </SettingsPage>
</template>

<style scoped>
.space-access-part { display: grid; gap: 18px; min-width: 0; }
</style>
