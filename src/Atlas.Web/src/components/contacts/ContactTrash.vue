<script setup lang="ts">
import t from "../../i18n/contacts-ui";
import { computed, ref } from "vue";
import { contactTypeId, type ContactData } from "../../contacts/contactData";
import { formatDateTime } from "../../formatDate";
import type { ObjectApi, TrashedObject } from "../../objects/objectApi";
import { tellTwins } from "../../spaceTwins";
import AtlasIcon from "../AtlasIcon.vue";
import TrashList from "../settings/TrashList.vue";
import { useTrashLoad } from "../settings/useTrashLoad";
import { Button } from "../ui/button";

/**
 * Kontakty → Koš: the wiki contacts in the trash that the reader may bring back, newest first, in the one trash list (TrashList),
 * 50 at a time. Esc and Zpět na adresář go back; `restored` names each contact brought back, so the directory reloads.
 */
const props = defineProps<{ source: Pick<ObjectApi, "trashList" | "restore" | "purge"> }>();
const emit = defineEmits<{ close: []; restored: [id: string] }>();

const total = ref(0), page = ref(1);
const root = ref<HTMLElement>();
const { items, loading, loaded, error, load } = useTrashLoad<TrashedObject>(async (signal, more): Promise<TrashedObject[]> => {
  const list = await props.source.trashList({ type: contactTypeId, scope: "wiki", ...(more && { page: page.value + 1 }) }, signal);
  total.value = list.total; page.value = list.page;
  return more ? [...items.value, ...list.items.filter(item => !items.value.some(known => known.id === item.id))] : list.items;
});

const organization = (item: TrashedObject) => {
  const data = item.data as unknown as ContactData | null;
  return data?.emails?.[0] ?? data?.organization;
};
const twins = computed(() => tellTwins(items.value, item => item.title, organization));
const detail = (item: TrashedObject, index: number) =>
  [twins.value[index], t.trash.trashedAt(formatDateTime(item.trashedAt)), item.trashedByName].filter(Boolean).join(" · ");

function left(item: TrashedObject, how: "restored" | "purged") {
  total.value = Math.max(0, total.value - 1);
  if (how === "restored") emit("restored", item.id);
}
</script>

<template>
  <section ref="root" class="contact-trash" aria-labelledby="contact-trash-title" @keydown.esc="!($event.target as Element).closest('dialog') && emit('close')">
    <header class="contact-trash-header">
      <h3 id="contact-trash-title">{{ t.trash.title }}</h3>
      <Button variant="outline" type="button" data-contact-trash-back @click="emit('close')"><AtlasIcon name="arrow-left" :size="15" />{{ t.trash.back }}</Button>
    </header>
    <TrashList v-model:items="items" :label="t.trash.label" :icon="() => 'user'" :detail="detail" :restore="item => source.restore(item.id).then(() => {})"
      :purge="item => source.purge(item.id)" :purge-title="t.trash.purgeTitle" :loaded="loaded" :loading="loading" :error="error" autofocus
      :fallback="() => root?.querySelector<HTMLElement>('[data-contact-trash-back]')" :empty-text="t.trash.empty"
      @retry="load()" @left="left">
      <footer v-if="items.length" class="contact-trash-footer">
        <p class="help" role="status">{{ t.trash.count(items.length, total) }}</p>
        <Button v-if="total > items.length" variant="outline" type="button" :disabled="loading" :aria-busy="loading" @click="load(true)"><AtlasIcon name="refresh" :size="16" />{{ t.trash.more }}</Button>
      </footer>
      <template #purge="{ item }">
        <p>{{ t.trash.purgeLead }} <strong>{{ item.title }}</strong> {{ t.trash.purgeTail }}</p>
      </template>
    </TrashList>
  </section>
</template>

<style scoped>
.contact-trash { display: grid; gap: 14px; align-content: start; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.contact-trash-header { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 10px; }
.contact-trash-header h3 { margin: 0; font-size: 16px; }
.contact-trash-footer { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 10px 18px; }
.contact-trash-footer .help { margin: 0; color: var(--muted); font-size: 13px; }
</style>
