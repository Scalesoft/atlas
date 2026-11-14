<script setup lang="ts">
import { computed, ref } from "vue";
import t from "../../../i18n/settings-access";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import FilterMenu from "../FilterMenu.vue";
import SearchField from "../SearchField.vue";
import SettingsList from "../SettingsList.vue";
import SettingsSection from "../SettingsSection.vue";
import type { ChoiceOption } from "../types";
import AccessEntryRow from "./AccessEntryRow.vue";
import LevelLegend from "./LevelLegend.vue";
import { levelHierarchy } from "./accessKinds";
import type { AccessChangeDraft, AccessSource, AccessSubject, ResourceAccess, ResourceAccessView } from "./accessModel";
import { automaticRows, directSource, levelCounts, matchesQuery, resourceRows, searchesOnServer, type AccessRow } from "./accessView";

/**
 * Who can reach one resource, for every kind (space, page, vault, secret, plugin kinds): the automatic administrators as
 * locked rows, then groups, then people. Efektivně lists everyone who gets in (people through groups included); Jen
 * přidělené lists only the grants set here. One filter line: search, Zobrazit and the legend (also the level filter).
 * Store-free: the answer of GET /api/access/resources/{kind}/{id} comes in as `access`; `view` is bound so the owner
 * reloads with it; every change goes out as an event.
 */
const props = withDefaults(
  defineProps<{
    access: ResourceAccess;
    editing?: string | null;
    busy?: boolean;
    title?: string;
    /** A link to the grant flow (its own route); without it Přidat přístup emits `add`. */
    addHref?: string;
    /** Hides Přidat přístup (the flow is open in place). */
    hideAdd?: boolean;
    /** Only the rows with their change and removal, under a form that adds (ResourceShareForm): no filter line, no add, no legend. */
    compact?: boolean;
    subjectHref?: (subject: AccessSubject) => string | undefined;
    groupHref?: (groupId: string) => string | undefined;
    note?: string;
  }>(),
  { editing: null, busy: false, title: undefined, addHref: undefined, hideAdd: false, compact: false, subjectHref: undefined, groupHref: undefined, note: undefined },
);
const view = defineModel<ResourceAccessView>("view", { default: "effective" });
const emit = defineEmits<{
  add: [];
  change: [row: AccessRow];
  revoke: [row: AccessRow, grant: AccessSource];
  save: [row: AccessRow, grant: AccessSource, draft: AccessChangeDraft];
  cancel: [row: AccessRow];
}>();

// Bound so the owner can also ask the server (a cut space list); the loaded rows narrow at once either way (P17).
const query = defineModel<string>("query", { default: "" });
const level = ref("");
const viewOptions: ChoiceOption[] = [
  { value: "effective", label: t.panel.effective, icon: "user-check", description: t.panel.effectiveHint },
  { value: "direct", label: t.panel.direct, icon: "key-round", description: t.panel.directHint },
];
const viewModel = computed({ get: () => view.value, set: (value: string) => (view.value = value === "direct" ? "direct" : "effective") });
const name = computed(() => props.access.resource.title);
const locked = computed(() => automaticRows(props.access));
const rows = computed(() => resourceRows(props.access, props.subjectHref).filter((row) => view.value === "effective" || !!directSource(row.sources)));
const counts = computed(() => levelCounts([...locked.value, ...rows.value]));
const visible = computed(() => rows.value.filter((row) => (!level.value || row.level === level.value) && matchesQuery(`${row.title} ${row.detail ?? ""}`, query.value)));
const automatic = computed(() => locked.value.filter((row) => (!level.value || row.level === level.value) && matchesQuery(row.title, query.value)));
const groups = computed(() => visible.value.filter((row) => row.subject?.kind === "group"));
const people = computed(() => visible.value.filter((row) => row.subject?.kind === "user"));
const canAdd = computed(() => props.access.canManage && !props.hideAdd && !props.compact);
const readonly = computed(() => !props.access.canManage);
</script>

<template>
  <div class="resource-access" :data-resource-access="access.resource.kind">
    <div v-if="!compact" class="resource-access-filters" role="search" :aria-label="t.panel.filter">
      <div class="resource-access-search"><SearchField v-model="query" :label="t.panel.searchLabel" :placeholder="t.panel.searchPlaceholder" data-focus-landing /></div>
      <FilterMenu v-model="viewModel" :options="viewOptions" :label="t.panel.show" icon="user-check" data-access-view />
      <LevelLegend v-model="level" :counts="counts" class="resource-access-legend" />
    </div>
    <!-- A cut list (hasMore) has no total to show: its count would be the first page, not who has access. -->
    <SettingsSection :title="title ?? t.panel.title" icon="users" :count="access.hasMore ? undefined : automatic.length + visible.length">
      <template v-if="canAdd" #actions>
        <Button v-if="addHref" size="sm" as-child><a :href="addHref" data-add-access><AtlasIcon name="plus" :size="15" />{{ t.panel.add }}</a></Button>
        <Button v-else size="sm" type="button" :disabled="busy" data-add-access @click="emit('add')"><AtlasIcon name="plus" :size="15" />{{ t.panel.add }}</Button>
      </template>
      <slot name="before" />
      <SettingsList v-if="automatic.length" :label="t.panel.automaticFor(name)">
        <AccessEntryRow v-for="row in automatic" :key="row.key" :row="row" readonly />
      </SettingsList>
      <p v-if="groups.length" class="resource-access-heading"><AtlasIcon name="users" :size="14" />{{ t.panel.groups }}</p>
      <SettingsList v-if="groups.length" :label="t.panel.groupsFor(name)">
        <AccessEntryRow
          v-for="row in groups"
          :key="row.key"
          :row="row"
          :readonly="readonly"
          :editing="editing === row.key"
          :busy="busy"
          :group-href="groupHref"
          @change="emit('change', $event)"
          @revoke="(item, grant) => emit('revoke', item, grant)"
          @save="(item, grant, draft) => emit('save', item, grant, draft)"
          @cancel="emit('cancel', $event)"
        />
      </SettingsList>
      <p v-if="people.length" class="resource-access-heading"><AtlasIcon name="account" :size="14" />{{ t.panel.people }}</p>
      <SettingsList v-if="people.length" :label="t.panel.peopleFor(name)">
        <AccessEntryRow
          v-for="row in people"
          :key="row.key"
          :row="row"
          :readonly="readonly"
          :editing="editing === row.key"
          :busy="busy"
          :group-href="groupHref"
          @change="emit('change', $event)"
          @revoke="(item, grant) => emit('revoke', item, grant)"
          @save="(item, grant, draft) => emit('save', item, grant, draft)"
          @cancel="emit('cancel', $event)"
        />
      </SettingsList>
      <p v-if="!rows.length && !query.trim()" class="st-note" data-access-empty><AtlasIcon name="info" :size="15" />{{ view === "direct" ? t.panel.emptyDirect : t.panel.emptyEffective }}</p>
      <p v-else-if="!visible.length" class="st-note" role="status"><AtlasIcon name="search" :size="15" />{{ t.panel.noMatch }}</p>
      <p v-if="access.hasMore" class="st-note" data-access-more><AtlasIcon name="info" :size="15" />{{ t.panel.more(rows.length) }} {{ searchesOnServer(access.resource.kind) ? t.panel.moreServer : t.panel.moreDirect }}</p>
      <p v-if="!compact" class="st-note"><AtlasIcon name="info" :size="15" />{{ t.panel.hierarchy(levelHierarchy(access.resource.kind)) }}{{ note ? ` ${note}` : "" }}</p>
      <slot name="after" />
    </SettingsSection>
  </div>
</template>

<style scoped>
.resource-access { display: grid; gap: 14px; min-width: 0; }
.resource-access-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; min-width: 0; }
.resource-access-search { flex: 1 1 240px; min-width: min(200px, 100%); max-width: 360px; }
.resource-access-legend { margin-left: auto; }
.resource-access-heading { display: flex; align-items: center; gap: 6px; margin: 8px 0 0; color: var(--muted); font-size: 11.5px; font-weight: 650; letter-spacing: .06em; text-transform: uppercase; }
</style>
