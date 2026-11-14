<script setup lang="ts">
import { computed, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import EmptyState from "../EmptyState.vue";
import FilterMenu from "../FilterMenu.vue";
import SearchField from "../SearchField.vue";
import t from "../../../i18n/settings-access";
import type { ChoiceOption } from "../types";
import PrincipalAccessGroup from "./PrincipalAccessGroup.vue";
import LevelLegend from "./LevelLegend.vue";
import { accessKind } from "./accessKinds";
import type { AccessChangeDraft, AccessSource, PrincipalAccess } from "./accessModel";
import { levelCounts, matchesQuery, principalGroups, sourceText, type AccessRow } from "./accessView";

/**
 * Everything one person (or group) can reach, grouped by space: the space row says "Celý prostor" and only the exceptions
 * follow (pages with their own access, vaults, secrets, plugin resources), so a 900-page wiki stays a short list. One
 * filter line: search, Druh, Odkud and the legend, which is also the level filter. The same view serves the security
 * centre (whole wiki), space settings (one space) and the profile (Moje přístupy, readonly). Store-free: the card comes
 * in as `access`; changes go out as events (the inline editor of the row being `editing`).
 */
const props = withDefaults(
  defineProps<{
    access: PrincipalAccess;
    readonly?: boolean;
    scopeNote?: string;
    /** Key of the row whose inline editor is open. */
    editing?: string | null;
    busy?: boolean;
    groupHref?: (groupId: string) => string | undefined;
    /** Where a row's title leads (a vault, the Účet or Tokeny tab); none keeps it plain text. */
    rowHref?: (row: AccessRow) => string | undefined;
    emptyText?: string;
  }>(),
  { readonly: false, scopeNote: undefined, editing: null, busy: false, groupHref: undefined, rowHref: undefined, emptyText: undefined },
);
const emit = defineEmits<{
  change: [row: AccessRow];
  revoke: [row: AccessRow, grant: AccessSource];
  save: [row: AccessRow, grant: AccessSource, draft: AccessChangeDraft];
  cancel: [row: AccessRow];
}>();

const query = ref("");
const kind = ref("");
const via = ref("");
const level = ref("");
const locked = computed(() => props.readonly || !props.access.canManage);
const groups = computed(() =>
  principalGroups(props.access).map((group) => ({ ...group, rows: group.rows.map((row) => ({ ...row, href: props.rowHref?.(row) })) })),
);
const allRows = computed(() => groups.value.flatMap((group) => group.rows));
// Only the kinds and paths this card has; a filter that can only empty the list is noise.
const kindOptions = computed<ChoiceOption[]>(() => [
  { value: "", label: t.principal.all, icon: "layers" },
  ...[...new Set(allRows.value.map((row) => row.kind))].map((id) => {
    const item = accessKind(id);
    return { value: id, label: item.many, icon: item.icon, badge: item.plugin ?? undefined };
  }),
]);
const viaLabels: Record<AccessSource["via"], { label: string; icon: string }> = {
  direct: { label: t.principal.direct, icon: "account" },
  group: { label: t.principal.viaGroup, icon: "users" },
  automatic: { label: t.principal.automatic, icon: "lock" },
  derived: { label: t.principal.derived, icon: "blocks" },
};
const viaOptions = computed<ChoiceOption[]>(() => [
  { value: "", label: t.principal.anySource, icon: "filter" },
  ...(Object.keys(viaLabels) as AccessSource["via"][]).filter((key) => allRows.value.some((row) => row.sources.some((source) => source.via === key))).map((key) => ({ value: key, ...viaLabels[key] })),
]);
const filtered = computed(() =>
  groups.value
    .map((group) => ({
      ...group,
      rows: group.rows.filter(
        (row) =>
          (!kind.value || row.kind === kind.value) &&
          (!via.value || row.sources.some((source) => source.via === via.value)) &&
          (!level.value || row.level === level.value) &&
          matchesQuery(`${group.title} ${row.title} ${row.detail ?? ""} ${row.sources.map(sourceText).join(' ')}`, query.value),
      ),
    }))
    .filter((group) => group.rows.length),
);
// The legend counts every access of the card, so it stays a stable colour key while filtering.
const counts = computed(() => levelCounts(allRows.value));
const filtering = computed(() => !!(query.value.trim() || kind.value || via.value || level.value));
function clear() {
  query.value = "";
  kind.value = "";
  via.value = "";
  level.value = "";
}
</script>

<template>
  <div class="principal-access" data-principal-access>
    <p v-if="allRows.length" class="st-note">{{ t.principal.explanation }}</p>
    <div v-if="allRows.length" class="principal-access-filters" role="search" :aria-label="t.principal.filter">
      <div class="principal-access-search"><SearchField v-model="query" :label="t.principal.searchLabel" :placeholder="t.principal.searchPlaceholder" /></div>
      <FilterMenu v-if="kindOptions.length > 2" v-model="kind" :options="kindOptions" :label="t.principal.kind" icon="layers" data-access-kind-filter />
      <FilterMenu v-if="viaOptions.length > 2" v-model="via" :options="viaOptions" :label="t.principal.source" icon="filter" data-access-via-filter />
      <LevelLegend v-model="level" :counts="counts" class="principal-access-legend" />
    </div>
    <p v-if="scopeNote" class="st-note"><AtlasIcon name="info" :size="15" />{{ scopeNote }}</p>
    <PrincipalAccessGroup v-for="group in filtered" :key="group.id" :group="group" :readonly="locked" :editing="editing" :busy="busy" :filtering="filtering" :group-href="groupHref"
      @change="emit('change', $event)" @revoke="(item, grant) => emit('revoke', item, grant)" @save="(item, grant, draft) => emit('save', item, grant, draft)" @cancel="emit('cancel', $event)" />
    <EmptyState v-if="!allRows.length" icon="key-round" :title="t.principal.emptyTitle" :text="emptyText ?? t.principal.emptyText" compact />
    <EmptyState v-else-if="!filtered.length" icon="search" :title="t.principal.nothingTitle" :text="t.principal.nothingText" compact>
      <button v-if="filtering" type="button" class="st-link" @click="clear">{{ t.principal.clear }}</button>
    </EmptyState>
  </div>
</template>

<style scoped>
.principal-access { display: grid; gap: 14px; min-width: 0; }
.principal-access-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; min-width: 0; }
.principal-access-search { flex: 1 1 220px; min-width: min(200px, 100%); max-width: 320px; }
.principal-access-legend { margin-left: auto; }
</style>
