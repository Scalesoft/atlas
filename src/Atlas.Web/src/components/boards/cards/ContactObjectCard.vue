<script setup lang="ts">
import t from "../../../i18n/boards";
import { computed, inject, onBeforeUnmount, ref, watch } from "vue";
import { errorMessage } from "../../../api";
import { startObjectDrag } from "../../../boards/boardDrop";
import { contactCardShare, showsQrInPlace } from "../../../boards/contactCardShare";
import { boardCanvasKey } from "../canvas/boardCanvasContext";
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
 * card and the contact is read again. The card shows what its room allows (ContactCard `fit`); its business card (.vcf) and QR code are
 * actions of the board's context toolbar, the code drawn in the card's place while it is on (contactCardShare.ts; a card too low for a
 * code that can be scanned leaves it to the toolbar's dialog) and ended by leaving the selection, by editing or by „Zpět“. Its grip (on hover)
 * drags the contact out, onto a page as a contact block by its `objectId` or onto another board (spec 2026-10-30 §8); the card itself
 * still moves on the board.
 */
const props = defineProps<{ object: LiveObject; editing: boolean; compact: boolean; host: BoardBlockHost;
  /** The board item this card is (ObjectCard) and its height: the toolbar's QR action is about this item. */
  itemId?: string; height?: number }>();
/** `edited`: what a change wrote, for the board's undo (P6): the fields before and after. */
const emit = defineEmits<{ done: []; edited: [edit: { objectId: string; before: Record<string, unknown>; after: Record<string, unknown> }] }>();
const failure = ref("");
const root = ref<HTMLElement>();
const canvas = inject(boardCanvasKey, undefined);
const qr = computed(() => !!props.itemId && !props.editing && contactCardShare.isOpen(props.itemId) && showsQrInPlace(props.height ?? 0));
// The code belongs to the selected card: it ends with the selection (also by Esc, which clears it) and while the card is edited.
watch(() => !!props.itemId && (props.editing || canvas?.per.single(props.itemId) === false), ended => { if (ended) contactCardShare.set(props.itemId!, false); });
onBeforeUnmount(() => { if (props.itemId) contactCardShare.set(props.itemId, false); });

async function change(patch: ContactPatch) {
  failure.value = "";
  const data = (props.object.data ?? {}) as Record<string, unknown>;
  emit("edited", { objectId: props.object.id, before: Object.fromEntries(Object.keys(patch).map(key => [key, data[key] ?? null])), after: { ...patch } });
  try { await liveObjects.edit(props.object.id, patch); }
  catch (error) { failure.value = errorMessage(error); }
}
/** Ends the code in the card's place. Its „Zpět“ turns inert, so the focus inside the card moves to the card's node: the board keys keep working. */
function closeQr() {
  if (root.value?.contains(document.activeElement)) root.value.closest<HTMLElement>(".vue-flow__node")?.focus({ preventScroll: true });
  contactCardShare.set(props.itemId!, false);
}
function key(event: KeyboardEvent) {
  if (event.key !== "Escape" || event.defaultPrevented) return;
  event.stopPropagation();
  // The first Esc takes the code away (from its „Zpět“ or the card), the next ends the editing as ever.
  if (qr.value) { closeQr(); return; }
  emit("done");
}
</script>

<template>
  <div ref="root" class="bcard bcard-contact" :class="{ 'nodrag nopan nowheel': editing }" data-board-card="contact" @keydown="key">
    <!-- A mouse affordance beside the board's own move: a page's keyboard path to a contact is /Kontakt. -->
    <span v-if="!editing" class="bcard-contact-drag nodrag" draggable="true" aria-hidden="true" :title="t.contactCard.drag" data-board-contact-drag
      @dragstart="startObjectDrag($event, { id: object.id, typeId: object.typeId, title: object.title }, { effect: 'copy' })"><AtlasIcon name="grip-vertical" :size="14" /></span>
    <ContactCard :contact="object.data as unknown as ContactData" :editable="editing && object.canEdit" :compact="compact" :host="host" fit :qr="qr" @close-qr="closeQr" :upload="file => uploadContactPhoto(object.id, file)" @change="change" />
    <p v-if="failure" class="bcard-error" role="alert">{{ failure }}</p>
  </div>
</template>

<style scoped>
/* At rest the card shows what its room allows and clips the rest; edited, it shows every field and scrolls. */
.bcard-contact { position: relative; padding: 0; overflow: hidden; scrollbar-width: thin; }
.bcard-contact.nodrag { overflow: auto; }
.bcard-contact .bcard-error { position: absolute; right: 14px; bottom: 6px; left: 14px; }
.bcard-contact-drag { position: absolute; top: 6px; right: 6px; display: grid; place-items: center; width: 22px; height: 22px; border-radius: var(--atlas-radius-sm); color: var(--muted); cursor: grab; opacity: 0; transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out); }
.bcard-contact:hover .bcard-contact-drag, .bcard-contact:focus-within .bcard-contact-drag, .board-node.is-selected .bcard-contact-drag { opacity: 1; }
/* No hover on touch: the grip is always there. */
@media (hover: none) { .bcard-contact-drag { opacity: 1; } }
@media (prefers-reduced-motion: reduce) { .bcard-contact-drag { transition: none; } }
.bcard-contact-drag:hover { background: var(--hover); color: var(--text); }
.bcard-error { margin: 0; color: var(--error); font-size: 12.5px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
</style>
