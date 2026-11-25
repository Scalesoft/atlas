<script setup lang="ts">
import t from "../../i18n/contacts-ui";
import { useContactQr } from "../../contacts/useContactQr";
import type { ContactData } from "../../contacts/contactData";

/**
 * The QR code of a contact card with what to do with it (the card's unfolded code, the board toolbar's popover): black on white in any
 * theme, its modules rising from the centre (contactQr.css). Drawn when `active` turns on; `replay` plays the rise on every opening.
 */
const props = withDefaults(defineProps<{ contact: ContactData; active?: boolean; replay?: boolean }>(), { active: true, replay: false });
const { svg, omitted, failed, run } = useContactQr(() => props.contact, () => props.active, props.replay);
</script>

<template>
  <figure class="contact-qr" data-contact-qr-panel>
    <!-- uqr's drawing of the code (rects and a path, no text of the contact in it). -->
    <div v-if="svg" :key="`qr-${run}`" class="contact-qr-code" role="img" :aria-label="t.card.qr" v-html="svg" />
    <div v-else class="contact-qr-code" aria-hidden="true" />
    <figcaption class="contact-qr-hint">{{ failed ? t.card.qrFailed : t.card.qrHint }}<template v-if="omitted.length"> {{ t.card.qrOmitted(omitted.join(", ")) }}</template></figcaption>
  </figure>
</template>

<style scoped>
/* A phone reads black modules on white only: the code keeps its own white, quiet zone included, in the dark theme too. */
.contact-qr { display: grid; justify-items: start; gap: 8px; margin: 0; }
.contact-qr-code { width: 216px; height: 216px; max-width: 100%; border-radius: var(--atlas-radius-md); overflow: hidden; background: #fff; box-shadow: 0 0 0 1px var(--line); }
.contact-qr-code :deep(svg) { width: 100%; height: 100%; }
.contact-qr-hint { max-width: 340px; color: var(--muted); font-size: 12.5px; line-height: 1.45; }
</style>
