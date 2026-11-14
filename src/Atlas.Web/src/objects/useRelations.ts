import { onBeforeUnmount, ref, shallowRef, watch, type Ref } from "vue";
import { errorMessage } from "../api";
import { relationsApi, type RelationGraph, type RelationsApi } from "./relationsApi";

/**
 * The relations of one object, read again when the object or the depth changes (the last read wins; an earlier one is dropped).
 * `graph` keeps the previous answer while the next one loads, so a graph that gets deeper changes in place instead of blinking.
 */
export function useRelations(objectId: () => string | null | undefined, depth: () => 1 | 2 = () => 1, api: RelationsApi = relationsApi) {
  const graph = shallowRef<RelationGraph | null>(null);
  const loading = ref(false), failed = ref(""), missing = ref(false);
  let request: AbortController | undefined;
  async function load() {
    request?.abort();
    const id = objectId();
    if (!id) { graph.value = null; loading.value = false; return; }
    const current = request = new AbortController();
    loading.value = true; failed.value = ""; missing.value = false;
    try {
      const answer = await api.relations(id, depth(), current.signal);
      if (current.signal.aborted) return;
      graph.value = answer; missing.value = !answer;
    } catch (failure) {
      if (!current.signal.aborted) failed.value = errorMessage(failure);
    } finally {
      if (!current.signal.aborted) loading.value = false;
    }
  }
  // Another object starts from nothing; only a deeper or shallower read of the same one keeps the graph on screen.
  watch(objectId, () => { graph.value = null; });
  watch([objectId, depth], () => { void load(); }, { immediate: true });
  onBeforeUnmount(() => request?.abort());
  return { graph, loading, failed, missing, reload: load } satisfies Record<string, Ref<unknown> | (() => unknown)>;
}
