import { onBeforeUnmount, ref, type Ref } from "vue";
import { errorMessage } from "../../../api";

/**
 * The people and groups searched as typed (GET /api/access/candidates, never the whole directory) for a share (ResourceShareForm) and
 * a grant (ResourceGrantFlow), ARCH2-3: debounced, and versioned so a late answer for older text never shows. The previous results stay
 * while the next arrive (show fast) and `searching` lets the list say „Hledám…“ instead of "nobody" (UX1-11); the list preselects the
 * first result itself (SearchableChoiceList). A failure goes to `failed`, a success clears it through `found`.
 */
export function useCandidateSearch<T>(options: { delayMs: number; failed: (message: string) => void; found?: () => void }) {
  const results = ref([]) as Ref<T[]>;
  const searching = ref(false);
  let version = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let disposed = false;
  onBeforeUnmount(() => { disposed = true; clearTimeout(timer); });

  function find(text: string, search: (term: string) => Promise<T[]>) {
    clearTimeout(timer);
    const current = ++version;
    const term = text.trim();
    if (!term) { results.value = []; searching.value = false; return; }
    searching.value = true;
    timer = setTimeout(async () => {
      try {
        const found = await search(term);
        if (disposed || current !== version) return;
        results.value = found;
        options.found?.();
      } catch (failure) {
        if (!disposed && current === version) options.failed(errorMessage(failure));
      } finally {
        if (!disposed && current === version) searching.value = false;
      }
    }, options.delayMs);
  }
  return { results, searching, find };
}
