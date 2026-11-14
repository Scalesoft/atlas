<script setup lang="ts">
import { computed, ref, useId } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import SettingsSection from "../SettingsSection.vue";
import SettingsList from "../SettingsList.vue";
import InlineReveal from "../InlineReveal.vue";
import AccessEntryRow from "./AccessEntryRow.vue";
import t from "../../../i18n/settings-access";
import type { AccessChangeDraft, AccessSource } from "./accessModel";
import type { AccessRow, AccessRowGroup } from "./accessView";

const props = defineProps<{ group: AccessRowGroup; readonly: boolean; editing?: string | null; busy?: boolean; filtering: boolean; groupHref?: (id: string) => string | undefined }>();
const emit = defineEmits<{ change: [row: AccessRow]; revoke: [row: AccessRow, grant: AccessSource]; save: [row: AccessRow, grant: AccessSource, draft: AccessChangeDraft]; cancel: [row: AccessRow] }>();
const expanded = ref(false);
const id = `access-details-${useId()}`;
const space = computed(() => props.group.rows.find(row => row.kind === "space"));
const details = computed(() => props.group.rows.filter(row => row.kind !== "space"));
const collapsible = computed(() => !!space.value && details.value.length > 0);
const open = computed(() => expanded.value || props.filtering || details.value.some(row => row.key === props.editing));
const visibleRows = computed(() => collapsible.value ? [space.value!] : props.group.rows);
</script>

<template>
  <SettingsSection class="principal-access-group" :title="group.title" :icon="group.icon" :description="group.description" :data-access-group="group.id">
    <template v-if="group.color" #icon><span class="access-space-icon" :data-color="group.color" aria-hidden="true"><AtlasIcon :name="group.icon" :size="22" /></span></template>
    <SettingsList :label="t.principal.listLabel(group.title)">
      <AccessEntryRow v-for="row in visibleRows" :key="row.key" :row="row" :readonly="readonly" :editing="editing === row.key" :busy="busy" :group-href="groupHref"
        @change="emit('change', $event)" @revoke="(item, grant) => emit('revoke', item, grant)" @save="(item, grant, draft) => emit('save', item, grant, draft)" @cancel="emit('cancel', $event)" />
    </SettingsList>
    <template v-if="collapsible">
      <Button variant="ghost" class="access-details-toggle" :aria-expanded="open" :aria-controls="id" @click="expanded = !open">
        <AtlasIcon :name="open ? 'chevron-up' : 'chevron-down'" :size="16" />{{ t.principal.details(details.length) }}
      </Button>
      <InlineReveal :id="id" :open="open">
        <p class="st-note access-details-note">{{ t.principal.detailsHelp }}</p>
        <SettingsList :label="t.principal.details(details.length)">
          <AccessEntryRow v-for="row in details" :key="row.key" :row="row" :readonly="readonly" :editing="editing === row.key" :busy="busy" :group-href="groupHref"
            @change="emit('change', $event)" @revoke="(item, grant) => emit('revoke', item, grant)" @save="(item, grant, draft) => emit('save', item, grant, draft)" @cancel="emit('cancel', $event)" />
        </SettingsList>
      </InlineReveal>
    </template>
  </SettingsSection>
</template>

<style scoped>
.access-space-icon { display: inline-flex; align-items: center; justify-content: center; background: var(--appearance-bg); color: var(--appearance-fg); width: 38px; height: 38px; border-radius: var(--atlas-radius-md); flex-shrink: 0; }
.access-details-toggle { align-self: flex-start; max-width: 100%; white-space: normal; text-align: left; height: auto; min-height: 36px; }
.access-details-note { margin-bottom: 8px; }
.principal-access-group :deep(.settings-list-row-actions) { position: static; grid-column: 1 / -1; grid-row: 3; justify-content: flex-start; padding-left: 44px; background: none; opacity: 1; pointer-events: auto; }
</style>
