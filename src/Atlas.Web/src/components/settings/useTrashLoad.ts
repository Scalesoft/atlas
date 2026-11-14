import { onBeforeUnmount, onMounted, ref, type Ref } from "vue";
import { errorMessage } from "../../api";

/**
 * The rows of a TrashList host (ARCH3-5): loaded once mounted, again by `load()` (Načíst znovu) or with `more` (Načíst další), each
 * call cancelling the one before and the last one cancelled on unmount. `fetch` answers the new rows (with `more`, the known ones
 * followed by the next page's). The state is what TrashList takes: `loaded` after the first answer, `error` of the last one.
 */
export function useTrashLoad<T>(fetch: (signal: AbortSignal, more: boolean) => Promise<T[]>) {
  const items = ref([]) as Ref<T[]>;
  const loading = ref(true), loaded = ref(false), error = ref("");
  let abort: AbortController | undefined;

  async function load(more = false) {
    abort?.abort();
    const current = (abort = new AbortController());
    loading.value = true;
    error.value = "";
    try {
      const rows = await fetch(current.signal, more);
      if (!current.signal.aborted) items.value = rows;
    } catch (failure) {
      if (!current.signal.aborted) error.value = errorMessage(failure);
    } finally {
      if (current === abort) {
        loading.value = false;
        loaded.value = true;
      }
    }
  }
  onMounted(() => void load());
  onBeforeUnmount(() => abort?.abort());
  return { items, loading, loaded, error, load };
}
