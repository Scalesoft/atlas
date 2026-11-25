<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { errorMessage } from "../../api";
import { contactTypeId, type ContactData, type ContactPatch } from "../../contacts/contactData";
import { uploadContactPhoto } from "../../contacts/contactPhoto";
import { liveObjects, type LiveObjects } from "../../objects/liveObjects";
import { pageCommentHost } from "../../comments/pageCommentHost";
import type { BoardDetailTab } from "../../boards/boardDetail";
import { formatDateTime } from "../../formatDate";
import { plainTitle, typeInfo, type DetailRow, type RelationsApi, type UsageApi } from "../../objects/relationsApi";
import AtlasIcon from "../AtlasIcon.vue";
import CommentThread from "../comments/CommentThread.vue";
import ContactCard from "../contacts/ContactCard.vue";
import ContactSkillLine from "../contacts/ContactSkillLine.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import EmptyState from "../settings/EmptyState.vue";
import { Button } from "../ui/button";
import ObjectRelations from "./ObjectRelations.vue";
import UsagePanel from "./UsagePanel.vue";
import t from "../../i18n/components-objects";
import { lazyComponent } from "../../lazyComponent";

// The parts of a person's profile load with it: the first load of the app carries none of them.
const PersonActivity = lazyComponent(() => import("../contacts/PersonActivity.vue"), { loading: LoadingSkeleton });
const SkillGraph = lazyComponent(() => import("../contacts/SkillGraph.vue"), { loading: LoadingSkeleton });
const PersonAccount = lazyComponent(() => import("../settings/people/PersonAccount.vue"));

/**
 * The detail of one row of Obsah in the dock (WorkspaceSidePanel with the tabs of the board's object detail, minus Popis): Přehled
 * from the row, Vazby from the relations endpoint for any object (ObjectRelations), Použito na from the usage service (UsagePanel), Diskuse (CommentThread) and
 * Historie. It is BoardObjectDetail's sibling, not a copy: that one knows a card of a board, this one an object of a space.
 * A contact's Přehled is the one person profile (contactDock.ts opens it from search, the directory, Lidé, a board card, a tree node and the
 * page's person card): any contact's card, edited in place through the directory's live objects as everywhere else (an external person
 * too), and for the contact of an account (`directoryId`) their competences (SkillGraph of that person above the level bars) and Aktivita
 * (PersonActivity); the account part (PersonAccount) shows a wiki administrator the account, or „Vytvořit účet“ for a contact without one.
 * `graph`: the host has a graph to put the object into (Ukázat v grafu). `store` is replaced by tests.
 */
const props = withDefaults(defineProps<{ row: DetailRow; tab: BoardDetailTab; api?: RelationsApi; usage?: UsageApi; graph?: boolean;
  store?: Pick<LiveObjects, "get" | "load" | "refresh" | "edit"> }>(), { api: undefined, usage: undefined, graph: true, store: () => liveObjects });
const emit = defineEmits<{ open: [id: string, typeId: string]; focus: [id: string]; comments: [objectId: string, count: number] }>();

const type = computed(() => typeInfo(props.row.typeId));
const title = computed(() => plainTitle(props.row.title) || t.detail.untitled);
const scope = computed(() => (props.row.scope === "wiki" ? t.detail.scopeWiki : props.row.scope === "private" ? t.detail.scopePrivate : t.detail.scopeSpace));
const openable = computed(() => props.row.typeId === "atlas.page" || props.row.typeId === "atlas.board");
const loading = ref(false);
watch(() => [props.row.id, props.row.typeId], async () => {
  if (props.row.typeId !== contactTypeId) return;
  loading.value = true;
  try { await props.store.load(props.row.id); } finally { loading.value = false; }
}, { immediate: true });
// The directory's live object: what the profile shows and edits is what every card of the contact shows.
const contact = computed(() => {
  if (props.row.typeId !== contactTypeId) return null;
  const object = props.store.get(props.row.id);
  return loading.value && !object ? { status: "loading" as const } : !object ? { status: "failed" as const }
    : !object.available || !object.data ? { status: "missing" as const }
    : { status: "ready" as const, data: object.data as unknown as ContactData, canEdit: object.canEdit, name: object.title };
});
// The contact of an account (a colleague): competences, activity and the account. Any contact's card names them and says what they do,
// so a profile has no object head or facts.
const person = computed(() => (contact.value?.status === "ready" ? contact.value.data.directoryId : undefined));
const editError = ref("");
async function change(patch: ContactPatch) {
  editError.value = "";
  try { await props.store.edit(props.row.id, patch); } catch (failure) { editError.value = errorMessage(failure); }
}
</script>

<template>
  <div class="content-detail" :data-content-detail="tab">
    <template v-if="tab === 'overview'">
      <header v-if="contact?.status !== 'ready'" class="content-detail-head">
        <AtlasIcon :name="type.icon" :size="18" />
        <h3 class="content-detail-title" :title="title">{{ title }}</h3>
      </header>
      <LoadingSkeleton v-if="contact?.status === 'loading'" :rows="3" :label="t.detail.loading" />
      <EmptyState v-else-if="contact?.status === 'missing'" compact icon="lock" :title="t.detail.missingTitle" :text="t.detail.missingText" />
      <EmptyState v-else-if="contact?.status === 'failed'" compact icon="alert" :title="t.detail.failedTitle" />
      <template v-else-if="contact?.status === 'ready'">
        <ContactCard :contact="contact.data" :editable="contact.canEdit" :upload="contact.canEdit ? file => uploadContactPhoto(row.id, file) : undefined" @change="change" />
        <p v-if="editError" class="content-detail-error" role="alert">{{ editError }}</p>
        <template v-if="person">
          <SkillGraph :key="`${row.id}:skills`" :contact-id="row.id" />
          <ContactSkillLine :key="row.id" :contact-id="row.id" bars :name="title" />
        </template>
      </template>
      <dl v-if="contact?.status !== 'ready'" class="content-detail-facts">
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
      <PersonActivity v-if="person" :key="person" :account-id="person"
        @open="emit('open', $event, 'atlas.page')" />
      <PersonAccount v-if="contact?.status === 'ready'" :key="`${row.id}:${person ?? ''}`" :account-id="person" :contact="{ id: row.id, name: contact.name }"
        @created="store.refresh(row.id)" />
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
.content-detail-error { margin: 0; color: var(--error); font-size: 12.5px; }
</style>
