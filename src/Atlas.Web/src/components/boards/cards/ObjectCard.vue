<script setup lang="ts">
import t from "../../../i18n/boards";
import { computed, watch } from "vue";
import type { ObjectEdit } from "../../../boards/boardApi";
import type { BoardViewItem } from "../../../boards/boardSession";
import { inertBlockHost, type BoardBlockHost } from "../../../boards/editorBlocks";
import { objectCardViews } from "../../../boards/objectViews";
import { liveObjects } from "../../../objects/liveObjects";
import AtlasIcon from "../../AtlasIcon.vue";
import CardSwap from "./CardSwap.vue";
import "./boardCard.css";

/**
 * A live card of an object on a board (`kind: "object"`, objects spec §5.1): the object of the tab's live objects (liveObjects,
 * seeded by the board's read, kept current by what its viewers hear), drawn by its type's card (objectViews.ts). While it is read a
 * placeholder after `--atlas-motion-defer`, an object the reader may not read (any more) a locked card without its name or content,
 * then a cross-fade (CardSwap). Edits go through the object path, never through a board batch; `edited` makes each a step of the
 * board's undo (P6).
 */
const props = withDefaults(defineProps<{ item: BoardViewItem; editing?: boolean; host?: BoardBlockHost }>(), { editing: false, host: () => inertBlockHost });
const emit = defineEmits<{ done: []; edited: [edit: Pick<ObjectEdit, "objectId" | "before" | "after">] }>();

const id = computed(() => props.item.targetResourceId ?? props.item.props?.objectId ?? "");
const view = computed(() => objectCardViews[props.item.props?.typeId ?? ""]);
watch(id, value => { if (value) void liveObjects.load(value); }, { immediate: true });
const object = computed(() => liveObjects.get(id.value));
const state = computed(() => (!object.value ? "pending" : object.value.available ? "ready" : "locked"));
</script>

<template>
  <CardSwap :state="state">
    <template #pending>
      <article class="bcard" data-board-card="object">
        <header class="bcard-head">
          <span class="bcard-icon" aria-hidden="true"><AtlasIcon :name="view?.icon ?? 'box'" :size="18" /></span>
          <span class="bcard-heading"><span class="bcard-bar" aria-hidden="true" /><span class="bcard-sr">{{ t.objectCard.loading }}</span></span>
        </header>
      </article>
    </template>
    <template #locked>
      <article class="bcard bcard-locked" data-board-card="object" data-unavailable>
        <span class="bcard-icon" data-tone="neutral" aria-hidden="true"><AtlasIcon name="lock" :size="18" /></span>
        <span class="bcard-heading"><strong class="bcard-title">{{ view ? t.objectCard.unavailable(view.label) : t.objectCard.object }}</strong></span>
      </article>
    </template>
    <template v-if="object?.available">
      <component :is="view.card" v-if="view" :object="object" :editing="editing" :compact="item.props?.view === 'compact'" :host="host" :item-id="item.id" :height="item.height" @done="emit('done')" @edited="emit('edited', $event)" />
      <article v-else class="bcard" data-board-card="object">
        <span class="bcard-heading"><strong class="bcard-title" :title="object.title">{{ object.title }}</strong></span>
      </article>
    </template>
  </CardSwap>
</template>
