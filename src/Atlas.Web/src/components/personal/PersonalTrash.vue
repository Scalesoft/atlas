<script setup lang="ts">
import t from "../../i18n/components-personal";
import { ref } from "vue";
import { formatDateTime } from "../../formatDate";
import type { TrashedObject } from "../../objects/objectApi";
import type { PersonalApi } from "../../personal/personalApi";
import AtlasIcon from "../AtlasIcon.vue";
import TrashList from "../settings/TrashList.vue";
import { useTrashLoad } from "../settings/useTrashLoad";
import { Button } from "../ui/button";

/**
 * Můj prostor → Koš (spec 2026-10-29 §5): my notes in the trash, newest first, in the one trash list (TrashList). Only the owner
 * sees them; Obnovit brings a note back with its shares, Smazat trvale closes them too. Esc and Zpět go back; `restored` tells
 * Můj prostor to reload.
 */
const props = defineProps<{ source: Pick<PersonalApi, "trashList" | "restore" | "purge"> }>();
const emit = defineEmits<{ close: []; restored: [id: string] }>();

const root = ref<HTMLElement>();
const { items, loading, loaded, error, load } = useTrashLoad<TrashedObject>(async signal => (await props.source.trashList(signal)).items);

const detail = (item: TrashedObject) => t.trash.detail(formatDateTime(item.trashedAt));

</script>

<template>
  <section ref="root" class="personal-trash" aria-labelledby="personal-trash-title" @keydown.esc="!($event.target as Element).closest('dialog') && emit('close')">
    <header class="personal-trash-header">
      <h3 id="personal-trash-title">{{ t.trash.title }}</h3>
      <Button variant="outline" type="button" data-personal-trash-back @click="emit('close')"><AtlasIcon name="arrow-left" :size="15" />{{ t.trash.back }}</Button>
    </header>
    <TrashList v-model:items="items" :label="t.trash.list" :icon="() => 'file-text'" :detail="detail"
      :restore="item => source.restore(item.id).then(() => {})" :purge="item => source.purge(item.id)" :loaded="loaded" :loading="loading" :error="error" autofocus
      :fallback="() => root?.querySelector<HTMLElement>('[data-personal-trash-back]')"
      :empty-text="t.trash.empty"
      @retry="load()" @left="(item, how) => how === 'restored' && emit('restored', item.id)">
      <template #purge="{ item }">
        <p><strong>{{ item.title }}</strong> {{ t.trash.purgeText }}</p>
      </template>
    </TrashList>
  </section>
</template>

<style scoped>
.personal-trash { display: grid; gap: 14px; align-content: start; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.personal-trash-header { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 10px; }
.personal-trash-header h3 { margin: 0; font-size: 16px; }
</style>
