<script setup lang="ts">
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import EmptyState from "../EmptyState.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import t from "../../../i18n/settings-access";
import { accessKind } from "./accessKinds";
import type { AccessException } from "./accessModel";

/**
 * Výjimky of a space: what inside it has access other than the whole space (a page with its own access, a vault or a
 * secret with grants of its own). Each exception opens the same Kdo má přístup panel for that resource (`href`).
 */
const props = withDefaults(defineProps<{ exceptions: AccessException[]; href?: (item: AccessException) => string | undefined; description?: string }>(), {
  href: undefined,
  description: undefined,
});
const title = (item: AccessException) => (item.parentTitle ? `${item.parentTitle} › ${item.title}` : item.title);
function detail(item: AccessException) {
  const kind = accessKind(item.kind).one;
  if (item.mode === "own") return `${kind} · ${t.exceptions.selectedOnly} · ${t.exceptions.own(item.grantCount)}`;
  return `${kind}${item.kind === "secret" && item.parentTitle ? t.exceptions.inVault(item.parentTitle) : ""} · ${t.exceptions.own(item.grantCount)}`;
}
const link = (item: AccessException) => props.href?.(item);
</script>

<template>
  <SettingsSection :title="t.exceptions.title" icon="layers" :count="exceptions.length" :description="description ?? t.exceptions.description" data-access-exceptions>
    <SettingsList v-if="exceptions.length" :label="t.exceptions.listLabel">
      <SettingsListRow v-for="item in exceptions" :key="`${item.kind}:${item.id}`" :title="title(item)" :description="detail(item)" :icon="accessKind(item.kind).icon" icon-tone="neutral" :data-access-exception="`${item.kind}:${item.id}`">
        <template #meta>
          <StatusBadge v-if="item.mode === 'own'" tone="warning" icon="eye-off" :label="t.exceptions.badgeSelected" size="sm" />
          <StatusBadge v-else tone="info" icon="key-round" :label="t.exceptions.badgeOwn" size="sm" />
        </template>
        <template v-if="link(item)" #actions>
          <Button variant="ghost" size="sm" as-child><a :href="link(item)" :aria-label="t.exceptions.whoHasAccessFor(title(item))"><AtlasIcon name="users" :size="15" />{{ t.exceptions.whoHasAccess }}</a></Button>
        </template>
      </SettingsListRow>
    </SettingsList>
    <EmptyState v-else compact icon="layers" :title="t.exceptions.emptyTitle" :text="t.exceptions.emptyText" />
  </SettingsSection>
</template>
