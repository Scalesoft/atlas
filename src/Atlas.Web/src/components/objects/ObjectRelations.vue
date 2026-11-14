<script setup lang="ts">
import { computed } from "vue";
import { startObjectDrag } from "../../boards/boardDrop";
import t from "../../i18n/components-objects";
import { kindInfo, neighboursOf, plainTitle, relationKinds, typeInfo, type Neighbour, type RelationKind, type RelationsApi } from "../../objects/relationsApi";
import { useRelations } from "../../objects/useRelations";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingRegion from "../LoadingRegion.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import RelationsGraph from "./RelationsGraph.vue";

/**
 * Vazby: what an object is connected with, for ANY object (a page, a board, a contact, a deployment board), in the dock of Obsah:
 * the places grouped by kind with a small graph of the neighbourhood
 * above, both from the same answer (GET /api/objects/{id}/relations). A place the reader may not read is a lock without a name.
 * Where the object is used is UsagePanel's („Použito na“, plan 3.1 M5), not a mode of this list.
 */
const props = defineProps<{ objectId: string; api?: RelationsApi }>();
const emit = defineEmits<{ open: [id: string, typeId: string] }>();

const { graph, loading, failed, reload } = useRelations(() => props.objectId, () => 1, props.api);
type Place = Neighbour;
const places = computed(() => neighboursOf(props.objectId, graph.value));
const groups = computed(() => relationKinds.map(kind => ({ kind, places: places.value.filter(place => place.edge.kind === kind) })).filter(group => group.places.length));

const titleOf = (place: Place) => plainTitle(place.node?.title) || t.relations.untitled;
function describe(place: Place) {
  const where = place.node?.spaceName ? ` · ${place.node.spaceName}` : "";
  const count = place.edge.count > 1 ? ` ×${place.edge.count}` : "";
  const part = place.edge.anchor ? t.relations.pageSection : "";
  // The group above names the relation; the row says what the other end is and where it lives.
  return `${typeInfo(place.node?.typeId ?? "").label}${where}${part}${count}`;
}
const live = (place: Place) => place.readable;
function open(place: Place) {
  if (live(place) && place.node) emit("open", place.node.id, place.node.typeId);
}
function drag(event: DragEvent, place: Place) {
  if (live(place) && place.node) startObjectDrag(event, { id: place.node.id.toLowerCase(), typeId: place.node.typeId, title: titleOf(place) });
  else event.preventDefault();
}
</script>

<template>
  <LoadingRegion class="object-relations" :loading="loading" :ready="!!graph || !!failed" :rows="3" :label="t.relations.loading" data-object-relations="relations">
    <ErrorState v-if="failed" :title="t.relations.failed" :text="failed" @retry="reload" />
    <template v-else>
      <RelationsGraph v-if="graph && groups.length" class="object-relations-graph" :graph="graph" :focus-id="objectId" :label="t.relations.surroundings" compact
        @open="(id, typeId) => emit('open', id, typeId)" />
      <section v-for="group in groups" :key="group.kind" class="object-relations-group" :aria-label="kindInfo[group.kind as RelationKind].label">
        <h4><AtlasIcon :name="kindInfo[group.kind as RelationKind].icon" :size="14" />{{ kindInfo[group.kind as RelationKind].label }}</h4>
        <ul class="atlas-stagger">
          <li v-for="place in group.places" :key="place.key">
            <!-- Not a <button>: Firefox cannot drag one. Enter and Space still open it. -->
            <div role="button" class="object-relations-row" :class="{ 'is-locked': !place.readable }" :draggable="live(place)" :tabindex="place.readable ? 0 : -1" :aria-disabled="!place.readable"
              data-object-relation :data-kind="place.edge.kind" @click="open(place)" @keydown.enter.prevent="open(place)" @keydown.space.prevent="open(place)" @dragstart="drag($event, place)">
              <AtlasIcon :name="place.readable ? typeInfo(place.node!.typeId).icon : 'lock'" :size="15" />
              <span class="object-relations-body">
                <span class="object-relations-title" :title="place.readable ? titleOf(place) : undefined">{{ place.readable ? titleOf(place) : t.relations.unavailablePlace }}</span>
                <span class="object-relations-label">{{ place.readable ? describe(place) : t.relations.noAccess }}</span>
              </span>
              <AtlasIcon :name="place.direction === 'out' ? 'arrow-right' : 'arrow-left'" :size="14" class="object-relations-arrow" />
            </div>
          </li>
        </ul>
      </section>
      <EmptyState v-if="graph && !groups.length" compact icon="network" :title="t.relations.noneTitle" :text="t.relations.noneText" />
    </template>
  </LoadingRegion>
</template>

<style scoped>
.object-relations { display: flex; flex-direction: column; gap: 14px; min-width: 0; }
.object-relations :deep(.loading-region-content) { display: flex; flex-direction: column; gap: 14px; }
.object-relations-graph { border-radius: var(--atlas-radius-lg); overflow: hidden; }
.object-relations-group { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
.object-relations-group h4 { display: flex; align-items: center; gap: 6px; margin: 0; padding: 0 2px; color: var(--muted); font-size: 12px; font-weight: 650; }
.object-relations-group ul { display: flex; flex-direction: column; gap: 6px; margin: 0; padding: 0; list-style: none; }
.object-relations-row { display: flex; align-items: center; gap: 10px; width: 100%; padding: 9px 12px; border: 1px solid color-mix(in srgb, var(--line) 80%, transparent); border-radius: 10px;
  background: var(--surface); color: var(--text); font: inherit; text-align: left; cursor: pointer;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .object-relations-row:hover:not([aria-disabled="true"]) { border-color: var(--line); background: color-mix(in srgb, var(--hover) 50%, var(--surface)); } }
.object-relations-row:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.object-relations-row[aria-disabled="true"] { cursor: default; }
.object-relations-row.is-locked { color: var(--muted); }
.object-relations-row > svg { flex: none; color: var(--muted); }
.object-relations-body { display: flex; flex: 1; flex-direction: column; min-width: 0; }
.object-relations-title, .object-relations-label { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.object-relations-title { font-size: 13px; }
.object-relations-label { color: var(--muted); font-size: 12px; }
</style>
