<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { ApiError, errorMessage } from "../../../api";
import { hubApi, hubMigrationApi, type HubGroups, type HubMigrationDraft, type HubMigrationGrant, type HubMigrationInventory, type HubMigrationPage, type HubMigrationPreview, type HubMigrationResult, type HubMigrationRoute } from "../../../hub";
import { roleLabel } from "../../../roles";
import t from "../../../i18n/settings-modules";
import { locale } from "../../../i18n";
import { reauthRequiredCode } from "../../../protection";
import { Button } from "../../ui/button";
import ConfirmWithReauth from "../ConfirmWithReauth.vue";
import ErrorState from "../ErrorState.vue";
import ImpactPreview from "../ImpactPreview.vue";
import InlineReveal from "../InlineReveal.vue";
import LoadingNote from "../LoadingNote.vue";
import OptionPills from "../OptionPills.vue";
import SearchableChoiceList from "../SearchableChoiceList.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StepFlow from "../StepFlow.vue";
import type { FlowStep } from "../types";
import { useDraftGuard } from "../dirtyGuard";
import { disclosureHooks } from "../disclosure";

const props = defineProps<{ version: string }>();
const emit = defineEmits<{ cancel: []; applied: [result: HubMigrationResult] }>();
const inventory = ref<HubMigrationInventory>();
const version = ref(props.version);
const step = ref("selection");
const busy = ref(false), error = ref(""), conflict = ref(false);
const review = ref<HubMigrationPreview>();
const groups = ref<HubMigrationDraft["groups"]>([]);
const removeGrants = ref<string[]>([]), removeAdmins = ref<string[]>([]);
const boundaries = ref<HubMigrationPage[]>([]);
const local = ref<string | null>(null), external = ref<string | null>(null), query = ref("");
const matches = ref<HubGroups>();
const page = ref<string | null>(null), pageGroup = ref<string | null>(null);
const pageRole = ref<"Reader" | "Writer" | "Admin">("Reader");
const pageGrants = ref<HubMigrationPage["grants"]>([]);
const now = ref(Date.now());
let disposed = false;
let timer: ReturnType<typeof setInterval> | undefined;
const expired = computed(() => !!review.value && Date.parse(review.value.expiresAt) <= now.value);
const dirty = computed(() => groups.value.length + removeGrants.value.length + removeAdmins.value.length + boundaries.value.length > 0);
const steps = computed<FlowStep[]>(() => [
  { id: "selection", label: t.hubMigration.steps.selection.label, title: t.hubMigration.steps.selection.title, icon: "list", manual: true, ready: dirty.value && !conflict.value, nextLabel: t.hubMigration.steps.selection.next },
  { id: "impact", label: t.hubMigration.steps.impact.label, title: t.hubMigration.steps.impact.title, icon: "activity", ready: !!review.value && !expired.value, nextLabel: t.hubMigration.steps.impact.next },
  { id: "confirmation", label: t.hubMigration.steps.confirmation.label, title: t.hubMigration.steps.confirmation.title, icon: "shield-check", hideNext: true },
]);
const localOptions = computed(() => (inventory.value?.groups ?? []).filter(group => !inventory.value?.bindings.some(binding => binding.groupId === group.id) && !groups.value.some(mapping => mapping.localGroupId === group.id)).map(group => ({ value: group.id, label: group.name, description: group.id, icon: "users", disabled: busy.value })));
const externalOptions = computed(() => (matches.value?.groups ?? []).map(group => ({ value: group.id, label: group.name, description: group.id, icon: "users", disabled: busy.value || groups.value.some(mapping => mapping.externalGroupId === group.id) || inventory.value?.bindings.some(binding => binding.externalGroupId === group.id), disabledReason: t.hubMigration.groupTaken })));
const currentGrants = computed(() => inventory.value?.grants.filter(grant => grant.activeTo === null) ?? []);
const administrators = computed(() => inventory.value?.users.filter(user => user.isWikiAdmin) ?? []);
const pageOptions = computed(() => (inventory.value?.pages ?? []).filter(item => !item.isTrashed && !item.isTemplate && !boundaries.value.some(boundary => boundary.pageId === item.id)).map(item => ({ value: item.id, label: item.title, description: `${spaceName(item.spaceId)} · ${item.id}`, icon: "page", disabled: busy.value })));
const pageGroupOptions = computed(() => (inventory.value?.groups ?? []).filter(group => !pageGrants.value.some(grant => grant.groupId === group.id)).map(group => ({ value: group.id, label: group.name, description: group.id, icon: "users", disabled: busy.value })));
const roleOptions = [{ value: "Reader", label: t.hubMigration.roles.reader }, { value: "Writer", label: t.hubMigration.roles.writer }, { value: "Admin", label: t.hubMigration.roles.admin }];
const userName = (id: string) => inventory.value?.users.find(user => user.id === id)?.displayName ?? id;
const groupName = (id: string) => inventory.value?.groups.find(group => group.id === id)?.name ?? id;
const spaceName = (id: string) => inventory.value?.spaces.find(space => space.id === id)?.name ?? id;
const pageName = (id: string) => inventory.value?.pages.find(item => item.id === id)?.title ?? id;
const role = (value: number) => ({ 1: t.hubMigration.roles.reader, 2: t.hubMigration.roles.writer, 3: t.hubMigration.roles.admin })[value] ?? String(value);
const subject = (grant: { userId: string | null; groupId: string | null }) => grant.groupId ? t.hubMigration.subjectGroup(groupName(grant.groupId)) : grant.userId ? userName(grant.userId) : t.hubMigration.subjectUnknown;
const grantLabel = (grant: HubMigrationGrant) => `${subject(grant)} · ${role(grant.role)} · ${grant.pageId ? pageName(grant.pageId) : spaceName(grant.spaceId!)}`;
const names = (ids: string[]) => ids.map(userName).join(", ") || t.hubMigration.nobody;
const date = (value: string) => new Date(value).toLocaleString(locale);
function routeLabel(route: HubMigrationRoute) {
  const destination = route.resourceId ? (route.resourceKind === "space" ? spaceName(route.resourceId) : pageName(route.resourceId)) : t.hubMigration.wholeWiki;
  const [origin, inherited] = route.source.split(":");
  const source = t.hubMigration.sources[origin!] ?? route.source;
  const inheritance = inherited === "space-admin" ? t.hubMigration.spaceAdmin : inherited === "space-default" ? t.hubMigration.spaceDefault : "";
  return `${userName(route.userId)} · ${role(route.role)} · ${destination} · ${source}${route.groupId ? ` ${groupName(route.groupId)}` : ""}${inheritance}${route.grantId ? t.hubMigration.grantSuffix(route.grantId) : ""}`;
}
const impact = computed(() => review.value ? [
  ...review.value.groupDiffs.map(diff => ({ icon: "users", label: diff.convertsLocalGroup ? t.hubMigration.impact.convert(groupName(diff.localGroupId)) : t.hubMigration.impact.restore(groupName(diff.localGroupId)), detail: t.hubMigration.impact.groupDetail(diff.externalGroupId, names(diff.beforeUserIds), names(diff.afterUserIds)) })),
  ...review.value.removedGrants.map(grant => ({ icon: "close", label: t.hubMigration.impact.removeGrant(grantLabel(grant)), detail: t.hubMigration.impact.grantDetail(grant.id) })),
  ...review.value.removedWikiAdminUserIds.map(id => ({ icon: "shield-check", label: t.hubMigration.impact.removeAdmin(userName(id)) })),
  ...review.value.pageBoundaries.map(boundary => ({ icon: "page", label: t.hubMigration.impact.pageOwn(pageName(boundary.pageId)), detail: boundary.grants.map(grant => `${groupName(grant.groupId)} · ${roleLabel(grant.role)}`).join(", ") || t.hubMigration.impact.noGroupGrants })),
  ...review.value.copiedGrantsRemoved.map(grant => ({ icon: "close", label: t.hubMigration.impact.removeCopied(pageName(grant.pageId)), detail: t.hubMigration.impact.copiedDetail(subject(grant), role(grant.role), grant.sourceSpaceGrantId) })),
] : []);

async function load(refreshVersion = false) {
  if (busy.value) return;
  busy.value = true; error.value = "";
  try {
    const next = await hubMigrationApi.inventory();
    const settings = refreshVersion ? await hubApi.settings() : undefined;
    if (disposed) return;
    inventory.value = next;
    if (settings?.version) version.value = settings.version;
    conflict.value = false;
  } catch (failure) { if (!disposed) error.value = errorMessage(failure); }
  finally { if (!disposed) busy.value = false; }
}
async function search() {
  if (busy.value) return;
  busy.value = true; error.value = ""; matches.value = undefined; external.value = null;
  const searched = query.value;
  try { const next = await hubApi.search(searched); if (!disposed && searched === query.value) matches.value = next; }
  catch (failure) { if (!disposed) error.value = errorMessage(failure); }
  finally { if (!disposed) busy.value = false; }
}
function addGroup() {
  if (!local.value || !external.value || busy.value) return;
  groups.value.push({ localGroupId: local.value, externalGroupId: external.value });
  local.value = external.value = null;
}
function addPageGrant() {
  if (!pageGroup.value || busy.value) return;
  pageGrants.value.push({ groupId: pageGroup.value, role: pageRole.value });
  pageGroup.value = null;
}
function addBoundary() {
  if (!page.value || busy.value) return;
  boundaries.value.push({ pageId: page.value, grants: [...pageGrants.value] });
  page.value = pageGroup.value = null; pageGrants.value = [];
}
async function preview() {
  if (busy.value || !dirty.value || conflict.value) return;
  if (local.value || external.value || page.value) { error.value = t.hubMigration.selectionPending; return; }
  busy.value = true; error.value = ""; review.value = undefined;
  try {
    const next = await hubMigrationApi.preview({ expectedVersion: version.value, groups: groups.value,
      removeSpaceGrantIds: inventory.value!.grants.filter(grant => grant.spaceId && removeGrants.value.includes(grant.id)).map(grant => grant.id),
      removePageGrantIds: inventory.value!.grants.filter(grant => grant.pageId && removeGrants.value.includes(grant.id)).map(grant => grant.id),
      removeWikiAdminUserIds: removeAdmins.value, pageBoundaries: boundaries.value });
    if (!disposed) { review.value = next; inventory.value = next.inventory; now.value = Date.now(); step.value = "impact"; }
  } catch (failure) {
    if (!disposed) { error.value = errorMessage(failure); conflict.value = failure instanceof ApiError && failure.status === 409; }
  } finally { if (!disposed) busy.value = false; }
}
async function apply(proof: string | null) {
  if (busy.value || !review.value) return;
  now.value = Date.now();
  if (expired.value) { error.value = t.hubMigration.previewExpired; return; }
  busy.value = true; error.value = "";
  try {
    const result = await hubMigrationApi.apply(review.value.ticket, proof);
    if (!disposed) { groups.value = []; removeGrants.value = []; removeAdmins.value = []; boundaries.value = []; review.value = undefined; emit("applied", result); }
  } catch (failure) {
    if (!disposed) {
      error.value = errorMessage(failure);
      if (failure instanceof ApiError && failure.status === 409) { conflict.value = true; step.value = "selection"; review.value = undefined; }
      else if (failure instanceof ApiError && failure.code === reauthRequiredCode) error.value = t.hubMigration.reauthAgain;
    }
  } finally { if (!disposed) busy.value = false; }
}
watch(query, () => { matches.value = undefined; external.value = null; });
watch(step, value => { if (value === "selection") review.value = undefined; });
useDraftGuard(dirty, { discard: () => emit("cancel") });
onMounted(() => { void load(); timer = setInterval(() => { now.value = Date.now(); }, 1000); });
onBeforeUnmount(() => { disposed = true; clearInterval(timer); });
</script>

<template>
  <div class="hub-migration">
    <LoadingNote v-if="busy && !inventory" :label="t.hubMigration.loading" />
    <ErrorState v-if="error && !inventory" :title="t.hubMigration.loadFailed" :text="error" :busy="busy" @retry="load()" />
    <template v-if="inventory">
      <Transition :css="false" v-bind="disclosureHooks"><p v-if="error" class="migration-alert" role="alert">{{ error }}</p></Transition>
      <p v-if="conflict" class="st-note">{{ t.hubMigration.conflictNote }} <Button variant="outline" size="sm" :disabled="busy" @click="load(true)">{{ t.hubMigration.loadCurrent }}</Button></p>
      <Transition :css="false" v-bind="disclosureHooks"><p v-if="expired" class="migration-alert" role="alert">{{ t.hubMigration.previewExpired }}</p></Transition>
      <StepFlow v-model="step" :steps="steps" :label="t.hubMigration.flowLabel" :busy="busy" @next="active => active.id === 'selection' && preview()" @cancel="emit('cancel')">
        <template #selection>
          <SettingsSection :title="t.hubMigration.local.title" icon="users" :description="t.hubMigration.local.description">
            <SearchableChoiceList v-model="local" :options="localOptions" :label="t.hubMigration.local.localGroup" :hide-label="false" data-migration-local />
            <InlineReveal :open="!!local">
              <SearchableChoiceList v-model="external" v-model:query="query" :options="externalOptions" :label="t.hubMigration.local.hubGroup" :hide-label="false" remote :empty-text="matches ? t.hubMigration.local.noMatch : t.hubMigration.local.searchHub" data-migration-hub />
              <div class="st-actions"><Button variant="outline" :disabled="busy" data-migration-search @click="search">{{ t.hubMigration.local.search }}</Button><Button :disabled="busy || !external" data-migration-add-group @click="addGroup">{{ t.hubMigration.local.add }}</Button><Button variant="ghost" :disabled="busy" @click="local = external = null">{{ t.hubMigration.local.cancelSelection }}</Button></div>
              <p v-if="matches?.hasMore" class="st-note">{{ t.hubMigration.local.hasMore }}</p>
            </InlineReveal>
            <SettingsList v-if="groups.length" :label="t.hubMigration.local.selectedLabel">
              <SettingsListRow v-for="(mapping, index) in groups" :key="mapping.localGroupId" :title="groupName(mapping.localGroupId)" :description="t.hubMigration.local.hubId(mapping.externalGroupId)" icon="users"><template #actions><Button variant="ghost" size="sm" :disabled="busy" @click="groups.splice(index, 1)">{{ t.hubMigration.local.removeFromSelection }}</Button></template></SettingsListRow>
            </SettingsList>
          </SettingsSection>
          <SettingsSection :title="t.hubMigration.grants.title" icon="key-round" advanced :summary="t.hubMigration.grants.summary(removeGrants.length, removeAdmins.length)">
            <p class="st-note">{{ t.hubMigration.grants.note }}</p>
            <label v-for="grant in currentGrants" :key="grant.id" class="migration-check"><input v-model="removeGrants" type="checkbox" :value="grant.id" :disabled="busy" /><span>{{ grantLabel(grant) }}<small>{{ grant.id }} · {{ grant.expiresAt ? t.hubMigration.grants.until(date(grant.expiresAt)) : t.hubMigration.grants.noEnd }}</small></span></label>
            <label v-for="user in administrators" :key="user.id" class="migration-check"><input v-model="removeAdmins" type="checkbox" :value="user.id" :disabled="busy" /><span>{{ t.hubMigration.impact.removeAdmin(user.displayName) }}<small>{{ user.id }}</small></span></label>
            <p v-if="!currentGrants.length && !administrators.length" class="st-note">{{ t.hubMigration.grants.none }}</p>
          </SettingsSection>
          <SettingsSection :title="t.hubMigration.pages.title" icon="page" advanced :summary="t.hubMigration.pages.summary(boundaries.length)">
            <p class="st-note">{{ t.hubMigration.pages.note }}</p>
            <SearchableChoiceList v-model="page" :options="pageOptions" :label="t.hubMigration.pages.page" :hide-label="false" data-migration-page />
            <InlineReveal :open="!!page">
              <SearchableChoiceList v-model="pageGroup" :options="pageGroupOptions" :label="t.hubMigration.pages.groupForPage" :hide-label="false" data-migration-page-group />
              <OptionPills v-model="pageRole" :options="roleOptions" :label="t.hubMigration.pages.accessLevel" />
              <Button variant="outline" :disabled="busy || !pageGroup" @click="addPageGrant">{{ t.hubMigration.pages.addGroup }}</Button>
              <SettingsList v-if="pageGrants.length" :label="t.hubMigration.pages.fullAccessLabel"><SettingsListRow v-for="(grant, index) in pageGrants" :key="grant.groupId" :title="groupName(grant.groupId)" :description="roleLabel(grant.role)"><template #actions><Button variant="ghost" :disabled="busy" @click="pageGrants.splice(index, 1)">{{ t.hubMigration.pages.remove }}</Button></template></SettingsListRow></SettingsList>
              <p v-else class="st-note">{{ t.hubMigration.pages.noGroupGrants }}</p>
              <div class="st-actions"><Button :disabled="busy || !!pageGroup" @click="addBoundary">{{ t.hubMigration.pages.addBoundary }}</Button><Button variant="ghost" :disabled="busy" @click="page = pageGroup = null; pageGrants = []">{{ t.hubMigration.pages.cancelPage }}</Button></div>
            </InlineReveal>
            <SettingsList v-if="boundaries.length" :label="t.hubMigration.pages.selectedLabel"><SettingsListRow v-for="(boundary, index) in boundaries" :key="boundary.pageId" :title="pageName(boundary.pageId)" :description="`${boundary.pageId} · ${boundary.grants.map(grant => `${groupName(grant.groupId)}: ${roleLabel(grant.role)}`).join(', ') || t.hubMigration.impact.noGroupGrants}`"><template #actions><Button variant="ghost" :disabled="busy" @click="boundaries.splice(index, 1)">{{ t.hubMigration.local.removeFromSelection }}</Button></template></SettingsListRow></SettingsList>
          </SettingsSection>
        </template>
        <template #impact>
          <template v-if="review">
            <ImpactPreview :items="impact" />
            <p v-for="warning in review.warnings" :key="warning" class="st-note">{{ warning }}</p>
            <SettingsList v-if="review.observedIdentities.length" :label="t.hubMigration.review.identitiesLabel">
              <SettingsListRow v-for="identity in review.observedIdentities" :key="identity.identityId" :title="userName(identity.userId)" :description="identity.state === 'Active' ? t.hubMigration.review.active : identity.state === 'Disabled' ? t.hubMigration.review.disabled : t.hubMigration.review.missing" icon="account" />
            </SettingsList>
            <SettingsSection :title="t.hubMigration.review.before" icon="key-round" advanced :summary="t.hubMigration.review.routesSummary(review.beforeRoutes.length)"><ul><li v-for="(route, index) in review.beforeRoutes" :key="index">{{ routeLabel(route) }}</li></ul><p v-if="!review.beforeRoutes.length">{{ t.hubMigration.review.noAccess }}</p></SettingsSection>
            <SettingsSection :title="t.hubMigration.review.after" icon="key-round" advanced :summary="t.hubMigration.review.routesSummary(review.afterRoutes.length)"><ul><li v-for="(route, index) in review.afterRoutes" :key="index">{{ routeLabel(route) }}</li></ul><p v-if="!review.afterRoutes.length">{{ t.hubMigration.review.noAccess }}</p></SettingsSection>
            <p class="st-note">{{ t.hubMigration.review.inventory(review.inventory.users.length, review.inventory.groups.length, review.inventory.identities.length, date(review.expiresAt)) }}</p>
          </template>
        </template>
        <template #confirmation>
          <ConfirmWithReauth v-if="review" :confirm-label="t.hubMigration.confirmation.confirm" :busy="busy" :disabled="expired" :error="error" @confirm="apply" @cancel="step = 'impact'">
            <ImpactPreview :items="impact" />
            <p>{{ t.hubMigration.confirmation.text(date(review.expiresAt)) }}</p>
          </ConfirmWithReauth>
        </template>
      </StepFlow>
    </template>
  </div>
</template>

<style scoped>
/* A column of flex items, not a grid: the error and expiry notes unfold and give the gap above them back (settings/disclosure.ts).
   They are errors like the ones of the Hub page itself: red, 13 px, not plain text. */
.hub-migration { display: flex; flex-direction: column; gap: 14px; min-width: 0; }
.migration-alert { margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
.migration-check { display: flex; align-items: flex-start; gap: 10px; padding: 8px 0; font-size: 14px; }
.migration-check input { margin-top: 4px; accent-color: var(--accent); }
.migration-check span { display: grid; gap: 3px; }
.migration-check small { color: var(--muted); overflow-wrap: anywhere; }
</style>
