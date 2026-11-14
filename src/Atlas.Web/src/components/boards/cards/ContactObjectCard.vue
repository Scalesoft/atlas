<script setup lang="ts">
import t from "../../../i18n/boards";
import { ref } from "vue";
import { errorMessage } from "../../../api";
import { startObjectDrag } from "../../../boards/boardDrop";
import type { BoardBlockHost } from "../../../boards/editorBlocks";
import type { ContactData, ContactPatch } from "../../../contacts/contactData";
import { liveObjects, type LiveObject } from "../../../objects/liveObjects";
import AtlasIcon from "../../AtlasIcon.vue";
import ContactCard from "../../contacts/ContactCard.vue";
import { uploadContactPhoto } from "../../../contacts/contactPhoto";
import "./boardCard.css";

/**
 * A directory contact on a board (objectViews.ts): the contact card, its fields edited in place while the card is edited (Enter, F2,
 * double click; Esc ends it) by whom may change the contact. A change goes to the contact (liveObjects → the object path), so every
 * board, page and the directory show it, and is a step of the board's undo (`edited`, P6); a refusal of the server is said under the
 * card and the contact is read again. Its grip (on hover)
 * drags the contact out, onto a page as a contact block by its `objectId` or onto another board (spec 2026-10-30 §8); the card itself
 * still moves on the board.
 */
const props = defineProps<{ object: LiveObject; editing: boolean; compact: boolean; host: BoardBlockHost }>();
/** `edited`: what a change wrote, for the board's undo (P6): the fields before and after. */
const emit = defineEmits<{ done: []; edited: [edit: { objectId: string; before: Record<string, unknown>; after: Record<string, unknown> }] }>();
const failure = ref("");

async function change(patch: ContactPatch) {
  failure.value = "";
  const data = (props.object.data ?? {}) as Record<string, unknown>;
  emit("edited", { objectId: props.object.id, before: Object.fromEntries(Object.keys(patch).map(key => [key, data[key] ?? null])), after: { ...patch } });
  try { await liveObjects.edit(props.object.id, patch); }
  catch (error) { failure.value = errorMessage(error); }
}
function key(event: KeyboardEvent) {
  if (event.key !== "Escape" || event.defaultPrevented) return;
  event.stopPropagation();
  emit("done");
}
</script>

<template>
  <div class="bcard bcard-contact" :class="{ 'nodrag nopan nowheel': editing }" data-board-card="contact" @keydown="key">
    <!-- A mouse affordance beside the board's own move: a page's keyboard path to a contact is /Kontakt. -->
    <span v-if="!editing" class="bcard-contact-drag nodrag" draggable="true" aria-hidden="true" :title="t.contactCard.drag" data-board-contact-drag
      @dragstart="startObjectDrag($event, { id: object.id, typeId: object.typeId, title: object.title }, { effect: 'copy' })"><AtlasIcon name="grip-vertical" :size="14" /></span>
    <ContactCard :contact="object.data as unknown as ContactData" :editable="editing && object.canEdit" :compact="compact" :host="host" :upload="file => uploadContactPhoto(object.id, file)" @change="change" />
    <p v-if="failure" class="bcard-error" role="alert">{{ failure }}</p>
  </div>
</template>

<style scoped>
.bcard-contact { position: relative; overflow: auto; scrollbar-width: thin; }
.bcard-contact-drag { position: absolute; top: 6px; right: 6px; display: grid; place-items: center; width: 22px; height: 22px; border-radius: var(--atlas-radius-sm); color: var(--muted); cursor: grab; opacity: 0; transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out); }
.bcard-contact:hover .bcard-contact-drag, .bcard-contact:focus-within .bcard-contact-drag, :global(.board-node.is-selected) .bcard-contact-drag { opacity: 1; }
/* No hover on touch: the grip is always there. */
@media (hover: none) { .bcard-contact-drag { opacity: 1; } }
@media (prefers-reduced-motion: reduce) { .bcard-contact-drag { transition: none; } }
.bcard-contact-drag:hover { background: var(--hover); color: var(--text); }
.bcard-error { margin: 0; color: var(--error); font-size: 12.5px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
</style>
