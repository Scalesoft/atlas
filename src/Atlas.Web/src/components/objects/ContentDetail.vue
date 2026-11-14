<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { ApiError, errorMessage } from "../../api";
import { contactTypeId, type ContactData } from "../../contacts/contactData";
import { pageCommentHost } from "../../comments/pageCommentHost";
import type { BoardDetailTab } from "../../boards/boardDetail";
import { formatDateTime } from "../../formatDate";
import { objectApi } from "../../objects/objectApi";
import { plainTitle, typeInfo, type DetailRow, type RelationsApi, type UsageApi } from "../../objects/relationsApi";
import AtlasIcon from "../AtlasIcon.vue";
import CommentThread from "../comments/CommentThread.vue";
import ContactCard from "../contacts/ContactCard.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import EmptyState from "../settings/EmptyState.vue";
import { Button } from "../ui/button";
import ObjectRelations from "./ObjectRelations.vue";
import UsagePanel from "./UsagePanel.vue";
import t from "../../i18n/components-objects";

/**
 * The detail of one row of Obsah in the dock (WorkspaceSidePanel with the tabs of the board's object detail, minus Popis): Přehled
 * from the row, Vazby from the relations endpoint for any object (ObjectRelations), Použito na from the usage service (UsagePanel), Diskuse (CommentThread) and
 * Historie. It is BoardObjectDetail's sibling, not a copy: that one knows a card of a board, this one an object of a space.
 * A contact shows its directory card in Přehled (the tree's reference node to a contact opens here too). `graph`: the host has a
 * graph to put the object into (Ukázat v grafu).
 */
const props = withDefaults(defineProps<{ row: DetailRow; tab: BoardDetailTab; api?: RelationsApi; usage?: UsageApi; graph?: boolean }>(), { api: undefined, usage: undefined, graph: true });
const emit = defineEmits<{ open: [id: string, typeId: string]; focus: [id: string]; comments: [objectId: string, count: number] }>();

const type = computed(() => typeInfo(props.row.typeId));
const title = computed(() => plainTitle(props.row.title) || t.detail.untitled);
const scope = computed(() => (props.row.scope === "wiki" ? t.detail.scopeWiki : props.row.scope === "private" ? t.detail.scopePrivate : t.detail.scopeSpace));
const openable = computed(() => props.row.typeId === "atlas.page" || props.row.typeId === "atlas.board");

const contact = ref<{ status: "loading" } | { status: "ready"; data: ContactData } | { status: "missing" } | { status: "failed"; error: string } | null>(null);
watch(() => [props.row.id, props.row.typeId], async () => {
  const id = props.row.id;
  if (props.row.typeId !== contactTypeId) { contact.value = null; return; }
  contact.value = { status: "loading" };
  try {
    const object = await objectApi.get(id);
    if (id === props.row.id) contact.value = object.data ? { status: "ready", data: object.data as unknown as ContactData } : { status: "missing" };
  } catch (failure) {
    if (id !== props.row.id) return;
    contact.value = failure instanceof ApiError && failure.status === 404 ? { status: "missing" } : { status: "failed", error: errorMessage(failure) };
  }
}, { immediate: true });
</script>

<template>
  <div class="content-detail" :data-content-detail="tab">
    <template v-if="tab === 'overview'">
      <header class="content-detail-head">
        <AtlasIcon :name="type.icon" :size="18" />
        <h3 class="content-detail-title" :title="title">{{ title }}</h3>
      </header>
      <LoadingSkeleton v-if="contact?.status === 'loading'" :rows="3" :label="t.detail.loading" />
      <EmptyState v-else-if="contact?.status === 'missing'" compact icon="lock" :title="t.detail.missingTitle" :text="t.detail.missingText" />
      <EmptyState v-else-if="contact?.status === 'failed'" compact icon="alert" :title="t.detail.failedTitle" :text="contact.error" />
      <ContactCard v-else-if="contact?.status === 'ready'" :contact="contact.data" />
      <dl class="content-detail-facts">
        <dt>{{ t.detail.kind }}</dt><dd>{{ type.label }}</dd>
        <template v-if="row.path"><dt>{{ t.detail.location }}</dt><dd>{{ row.hostId ? t.detail.belongsTo(row.path) : row.path }}</dd></template>
        <template v-if="row.scope"><dt>{{ t.detail.scope }}</dt><dd>{{ scope }}</dd></template>
        <template v-if="row.usedCount !== undefined"><dt>{{ t.detail.used }}</dt><dd>{{ row.usedCount }}</dd></template>
        <template v-if="row.linksIn !== undefined"><dt>{{ t.detail.links }}</dt><dd>{{ t.detail.linksValue(row.linksIn, row.linksOut ?? 0) }}</dd></template>
        <template v-if="row.comments"><dt>{{ t.detail.comments }}</dt><dd>{{ row.comments }}</dd></template>
        <template v-if="row.updatedAt"><dt>{{ t.detail.changed }}</dt><dd>{{ formatDateTime(row.updatedAt) }}</dd></template>
      </dl>
      <div class="content-detail-actions">
        <Button v-if="openable" size="sm" type="button" data-content-detail-open @click="emit('open', row.id, row.typeId)"><AtlasIcon name="external" :size="15" />{{ t.detail.open }}</Button>
        <Button v-if="graph" variant="outline" size="sm" type="button" data-content-detail-focus @click="emit('focus', row.id)"><AtlasIcon name="network" :size="15" />{{ t.detail.showInGraph }}</Button>
      </div>
    </template>

    <ObjectRelations v-else-if="tab === 'relations'" :key="`${row.id}:relations`" :object-id="row.id" :api="api" @open="(id, typeId) => emit('open', id, typeId)" />
    <UsagePanel v-else-if="tab === 'usage'" :key="`${row.id}:usage`" :object-id="row.id" :api="usage" @open="(id, typeId) => emit('open', id, typeId)" />

    <CommentThread v-else-if="tab === 'comments'" :key="row.id" :object-id="row.id" :view-key="`object:${row.id}`" :host="pageCommentHost(row.id, row.spaceId)" :lazy="false"
      @count="emit('comments', row.id, $event)" />

    <EmptyState v-else icon="history" :title="t.detail.historyTitle" :text="t.detail.historyText" />
  </div>
</template>

<style scoped>
.content-detail { display: flex; flex-direction: column; gap: 14px; min-width: 0; padding: 16px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.content-detail-head { display: flex; align-items: center; gap: 8px; min-width: 0; color: var(--muted); }
.content-detail-title { min-width: 0; margin: 0; overflow: hidden; color: var(--text); font-size: 16px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.content-detail-facts { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 6px 12px; margin: 0; padding: 12px; border: 1px solid color-mix(in srgb, var(--line) 80%, transparent);
  border-radius: 10px; background: var(--surface); font-size: 13px; }
.content-detail-facts dt { color: var(--muted); }
.content-detail-facts dd { margin: 0; min-width: 0; overflow-wrap: anywhere; }
.content-detail-actions { display: flex; flex-wrap: wrap; gap: 8px; }
</style>
