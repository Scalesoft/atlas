<script setup lang="ts" generic="T extends { id: string; title: string }">
import t from "../../i18n/settings-shell";
import { computed, nextTick, ref, shallowRef, watch } from "vue";
import { errorMessage } from "../../api";
import { rowKeys } from "../../keyboard/navigation";
import AtlasIcon from "../AtlasIcon.vue";
import type { ActionMenuItem } from "../ActionMenu.vue";
import RowActionsMenu from "../RowActionsMenu.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import ConfirmDialog from "./ConfirmDialog.vue";
import EmptyState from "./EmptyState.vue";
import ErrorState from "./ErrorState.vue";
import SettingsList from "./SettingsList.vue";
import SettingsListRow from "./SettingsListRow.vue";

/**
 * One trash list for every kind (unified trash spec §2 rule 5, §3 UI): contacts, Můj prostor, the space's pages and boards, the
 * spaces of Správa wiki. A row is a SettingsListRow with the kind's icon, the title and who and when; Obnovit and Smazat trvale
 * carry the title in their labels; Ctrl+mezerník, Alt+Enter, Ctrl+., Shift+F10 and the right button open the same two as „Co s tím jde dělat“ (RowActionsMenu, P2). A row that leaves fades where it stood (SettingsList) and the focus goes to the next row (to
 * `fallback` when none is left); ↑ ↓ Home End walk the rows. Smazat trvale always asks first (ConfirmDialog, `#purge` says what
 * goes; every confirmation also says the earlier backups keep a copy, spec R3), with `confirmName` only after the name is typed. The host loads the rows (`v-model:items`); `restore` answering false
 * keeps the row (the page trash first asks where). Use it for any trash; a list of live items is SettingsList itself.
 */
const props = withDefaults(defineProps<{
  label: string;
  icon: (item: T) => string;
  detail: (item: T, index: number) => string;
  restore: (item: T) => Promise<boolean | void>;
  /** Missing: whoever reads the list may not delete permanently. */
  purge?: (item: T) => Promise<void>;
  purgeTitle?: string;
  /** The name to type before a permanent delete (a space: unified trash spec §3). */
  confirmName?: (item: T) => string;
  loaded: boolean;
  loading?: boolean;
  error?: string;
  emptyTitle?: string;
  emptyText?: string;
  /** Focus the first Obnovit once the rows are there. */
  autofocus?: boolean;
  /** Where the focus goes when no row is left. */
  fallback?: () => HTMLElement | null | undefined;
}>(), {
  purge: undefined, purgeTitle: undefined, confirmName: undefined, loading: false, error: "", emptyTitle: undefined, emptyText: "",
  autofocus: false, fallback: undefined,
});
const items = defineModel<T[]>("items", { required: true });
const emit = defineEmits<{ retry: []; left: [item: T, how: "restored" | "purged"] }>();

const root = ref<HTMLElement>();
const busy = ref<string>(), rowError = ref<{ id: string; text: string }>();
const purging = shallowRef<T | null>(null), purgeBusy = ref(false), purgeError = ref(""), typed = ref("");
const named = computed(() => !props.confirmName || !purging.value || typed.value.trim() === props.confirmName(purging.value));
let purgedAt: number | undefined;
/** „Co s tím jde dělat“ of a row (P2): what the row's two buttons do. */
const rowActions = computed<ActionMenuItem[]>(() => [
  { id: "restore", label: t.trash.restore, icon: "undo-2", disabled: !!busy.value },
  ...(props.purge ? [{ id: "purge", label: t.trash.purge, icon: "trash", destructive: true, group: "purge", disabled: !!busy.value }] : []),
]);

function focusRow(index: number) {
  const buttons = root.value?.querySelectorAll<HTMLElement>("li:not(.st-list-leave-active) [data-trash-restore]") ?? [];
  (buttons[Math.min(index, buttons.length - 1)] ?? props.fallback?.())?.focus({ preventScroll: true });
}
watch(() => props.loaded, async loaded => {
  if (!loaded || !props.autofocus) return;
  await nextTick();
  focusRow(0);
}, { immediate: true });

/** The row leaves; the focus goes to the next one, after the confirmation of a purge has given it back (afterPurge). */
async function leave(item: T, how: "restored" | "purged") {
  const index = items.value.findIndex(known => known.id === item.id);
  items.value = items.value.filter(known => known.id !== item.id);
  emit("left", item, how);
  if (how === "purged") { purgedAt = index; return; }
  await nextTick();
  focusRow(index);
}
function afterPurge() {
  if (purgedAt !== undefined) focusRow(purgedAt);
  purgedAt = undefined;
}
async function restore(item: T) {
  if (busy.value) return;
  busy.value = item.id; rowError.value = undefined;
  let left: boolean | void;
  try {
    left = await props.restore(item);
  } catch (failure) {
    rowError.value = { id: item.id, text: errorMessage(failure) };
    return;
  } finally {
    busy.value = undefined;
  }
  if (left !== false) await leave(item, "restored");
}
function cancelPurge() { purging.value = null; }
function askPurge(item: T) {
  purging.value = item; purgeError.value = ""; typed.value = "";
}
async function purge() {
  const item = purging.value;
  if (!item || !props.purge || !named.value) return;
  purgeBusy.value = true; purgeError.value = "";
  try {
    await props.purge(item);
  } catch (failure) {
    purgeError.value = errorMessage(failure);
    return;
  } finally {
    purgeBusy.value = false;
  }
  purging.value = null;
  await leave(item, "purged");
}
defineExpose({ focusRow });
</script>

<template>
  <div ref="root" class="trash-list" :aria-busy="loading">
    <ErrorState v-if="error && !items.length" :title="t.trash.failed" :text="error" :retry-label="t.trash.reload" :busy="loading" @retry="emit('retry')" />
    <LoadingSkeleton v-else-if="!loaded" :rows="3" :label="t.trash.loading" />
    <EmptyState v-else-if="!items.length" icon="trash" :title="emptyTitle ?? t.trash.emptyTitle" :text="emptyText" />
    <SettingsList v-if="items.length" :label="label" class="trash-list-items" @keydown="rowKeys($event, '[data-trash-row]')">
      <SettingsListRow v-for="(item, index) in items" :key="item.id" :title="item.title" :icon="icon(item)" :description="detail(item, index)" :data-trash-row="item.id">
        <template #actions>
          <Button variant="outline" size="sm" type="button" :disabled="!!busy" :aria-label="t.trash.restoreItem(item.title)" data-trash-restore @click="restore(item)">
            <AtlasIcon :name="busy === item.id ? 'loader-circle' : 'undo-2'" :size="15" :class="{ 'atlas-spinner': busy === item.id }" />{{ t.trash.restore }}
          </Button>
          <Button v-if="purge" variant="ghost" size="sm" type="button" :disabled="!!busy" :aria-label="t.trash.purgeItem(item.title)" data-trash-purge @click="askPurge(item)">
            <AtlasIcon name="trash" :size="15" />{{ t.trash.purge }}
          </Button>
        </template>
        <p v-if="rowError?.id === item.id" class="trash-list-error" role="alert"><AtlasIcon name="triangle-alert" :size="14" />{{ rowError.text }}</p>
        <RowActionsMenu :items="rowActions" :label="t.trash.actions(item.title)" @select="id => (id === 'restore' ? restore(item) : askPurge(item))" />
      </SettingsListRow>
    </SettingsList>
    <slot />
    <ConfirmDialog v-if="purge" :open="!!purging" :title="purgeTitle ?? t.trash.purgeTitle" :confirm-label="t.trash.purge" confirm-icon="trash" :busy="purgeBusy" :error="purgeError"
      :confirm-disabled="!named" @confirm="purge" @cancel="cancelPurge" @closed="afterPurge">
      <slot v-if="purging" name="purge" :item="purging">
        <p><strong>{{ purging.title }}</strong> {{ t.trash.purgeBody }}</p>
      </slot>
      <p v-if="purging" class="trash-list-backup" data-trash-backup-note>{{ t.trash.backupNote }}</p>
      <label v-if="purging && confirmName?.(purging)" class="trash-list-name">
        <span>{{ t.trash.typeName }} <strong>{{ confirmName(purging) }}</strong></span>
        <Input v-model="typed" autocomplete="off" spellcheck="false" data-trash-confirm-name @keydown.enter.prevent="purge" />
      </label>
    </ConfirmDialog>
  </div>
</template>

<style scoped>
.trash-list { display: grid; gap: 14px; align-content: start; min-width: 0; }
.trash-list-items { padding: 0 16px; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); }
.trash-list-error { display: flex; align-items: center; gap: 6px; margin: 0 0 10px; color: var(--error); font-size: 13px; }
.trash-list-name { display: grid; gap: 8px; margin-top: 12px; font-size: 14px; }
.trash-list-backup { margin: 8px 0 0; color: var(--muted); font-size: 13px; }
</style>
