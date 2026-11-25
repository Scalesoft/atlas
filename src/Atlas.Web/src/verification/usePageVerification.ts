import { ref, shallowRef, watch } from "vue";
import { api, type Page } from "../api";
import type { PageVerification } from "./pageVerification";

/**
 * Ověření stránky (5.2) of the open page, as the shell holds it for the chip at the title and the dock's facts: from the page's bundle,
 * replaced by the answer of every change made in the chip or the dock (`verification`), whether the chip's popover is `open`, and the
 * panel's Čtenost (`readers`, 90 days). A newly published version reads the state again („Změněno po ověření“ follows without a
 * reload); the bundle leaves out the readers while verification is off, so switching it on reads them once from the bundle.
 * The feature's texts and client stay out of the first screen (a dynamic import).
 */
export function usePageVerification(page: () => Page | null | undefined) {
  const verification = shallowRef<PageVerification | null>(null);
  const open = ref(false);
  const readers = ref<number>();
  watch(() => page()?.bundle?.verification, (value) => { if (value) verification.value = value; });
  watch(() => page()?.id, () => { verification.value = page()?.bundle?.verification ?? null; readers.value = page()?.bundle?.readers; open.value = false; }, { immediate: true });
  const on = () => !!verification.value && verification.value.state !== "off";
  watch(() => { const shown = page(); return shown && !shown.isTemplate ? `${shown.id}:${shown.publishedVersion}` : ""; }, (key, before) => {
    const [id, was] = [key.split(":")[0], before?.split(":")[0]];
    if (!id || id !== was || !on()) return;
    void import("./pageVerification").then((m) => m.pageVerificationApi.read(id)).then((value) => { if (page()?.id === id) verification.value = value; }).catch(() => undefined);
  });
  watch(() => verification.value?.state, () => {
    const id = page()?.id;
    if (!id || !on() || readers.value !== undefined) return;
    void api<Page>(`/pages/${encodeURIComponent(id)}?bundle=true`).then((value) => { if (page()?.id === id) readers.value = value.bundle?.readers; }).catch(() => undefined);
  });
  return { verification, open, readers };
}
