import { onScopeDispose, ref, watch, type Ref } from "vue";
import { spaceCoverApi, type SpaceCoverMatchInput } from "../spaceCover";

/**
 * The drawing the server would pick for a name, description and icon that are not saved yet: the live preview of the
 * automatic image in space settings and in the new-space form. The rule lives on the server only (spaceCoverApi.match).
 *
 * undefined while `input` is undefined (not asked) and until the first answer; otherwise the last answer, null for
 * "no drawing". Debounced; a newer input aborts the older request; a blank name asks nothing and gives null; a failed
 * request is no answer and gives undefined again, so the owner falls back to what it has (the pick the server stored,
 * or the pattern) and nothing else is blocked.
 */
export function useSpaceCoverMatch(
  input: Ref<SpaceCoverMatchInput | undefined>,
  match: (input: SpaceCoverMatchInput, signal: AbortSignal) => Promise<{ catalogKey: string | null }> = spaceCoverApi.match,
  delay = 300,
): Ref<string | null | undefined> {
  const key = ref<string | null>();
  let timer: ReturnType<typeof setTimeout> | undefined;
  let controller: AbortController | undefined;
  function cancel() {
    clearTimeout(timer);
    controller?.abort();
    controller = undefined;
  }
  async function ask(asked: SpaceCoverMatchInput) {
    const abort = controller = new AbortController();
    try {
      const { catalogKey } = await match(asked, abort.signal);
      if (!abort.signal.aborted) key.value = catalogKey;
    } catch {
      if (!abort.signal.aborted) key.value = undefined;
    }
  }
  // By value: a caller may hand in a new object with the same three values on every render.
  watch([() => input.value?.name, () => input.value?.description, () => input.value?.icon], () => {
    cancel();
    const asked = input.value;
    if (!asked?.name.trim()) {
      key.value = asked ? null : undefined;
      return;
    }
    timer = setTimeout(() => void ask(asked), delay);
  }, { immediate: true });
  onScopeDispose(cancel);
  return key;
}
