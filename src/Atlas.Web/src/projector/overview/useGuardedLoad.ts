import { onBeforeUnmount, ref, shallowRef } from "vue";
import { ApiError, errorMessage } from "../../api";

/**
 * One answer of the server for a space view: a new load aborts the one before, only the latest answer counts, and a space the
 * account may not read (404) is `denied` rather than an error.
 */
export function useGuardedLoad<T>(fetch: (signal: AbortSignal) => Promise<T>) {
  const value = shallowRef<T>();
  const loading = ref(false), denied = ref(false), error = ref("");
  let controller: AbortController | undefined;
  async function load() {
    controller?.abort();
    const mine = controller = new AbortController();
    loading.value = true;
    denied.value = false;
    error.value = "";
    try {
      const answer = await fetch(mine.signal);
      if (!mine.signal.aborted) value.value = answer;
    } catch (failure) {
      if (mine.signal.aborted) return;
      if (failure instanceof ApiError && failure.status === 404) denied.value = true;
      else error.value = errorMessage(failure);
    } finally {
      if (controller === mine) loading.value = false;
    }
  }
  onBeforeUnmount(() => controller?.abort());
  return { value, loading, denied, error, load };
}
