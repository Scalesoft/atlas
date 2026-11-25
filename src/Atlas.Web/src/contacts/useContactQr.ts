import { ref, watch, type Ref } from "vue";
import t from "../i18n/contacts-ui";
import { drawContactQr } from "./contactQr";
import { plainContact } from "./contactShare";
import type { ContactData } from "./contactData";

/**
 * The QR code of a contact card as a phone reads it (the contacts plugin's contactQr: the whole card inside the code, no link to the
 * wiki, so it works in a closed network): drawn when `active` turns on and again when the contact changes meanwhile. `run` counts the
 * drawings: keying the element on it plays the rise of the modules (contactQr.css) anew on every opening, which `replay` asks for
 * instead of the first look within ten minutes only. `failed` when the code could not be made; `omitted` the fields left out to keep it readable.
 */
export function useContactQr(contact: () => ContactData, active: () => boolean, replay = false) {
  const svg: Ref<string> = ref(""), omitted = ref<string[]>([]), failed = ref(false), run = ref(0);
  const labels = { website: t.card.website, address: t.card.address };
  watch([active, contact], async ([on]) => {
    if (!on) return;
    run.value++;
    try {
      const { contactQr } = await import("@atlas/markdown/plugins/contacts");
      const drawn = await contactQr(plainContact(contact()), text => drawContactQr(text, replay));
      svg.value = drawn.svg;
      omitted.value = drawn.omitted.map(field => labels[field === "website" ? "website" : "address"]);
      failed.value = false;
    } catch { svg.value = ""; omitted.value = []; failed.value = true; }
  }, { immediate: true });
  return { svg, omitted, failed, run };
}
