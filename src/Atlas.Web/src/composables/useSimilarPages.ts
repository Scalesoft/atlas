import { computed, onScopeDispose, ref, shallowRef, watch, type Ref } from "vue";
import { createSimilarPagesWatcher, similarPagesApi, titleDrift, type SimilarPage, type SimilarPagesApi } from "../similarPages";

/** A dismissed proposal stays hidden while the title is within this many edits of the title it was dismissed for. */
export const DISMISS_DRIFT = 3;

/**
 * P1 duplicates for the title being typed: the debounced lookup (similarPages.ts) plus what dismissing means.
 * Dismissing hides the proposal for the current title; a title that moved by more than a few characters may
 * show one again, but never a page that was already dismissed (the ids are remembered until the flow ends,
 * i.e. while this composable lives). It never throws and never blocks anything: no result is simply nothing.
 */
export function useSimilarPages(options: { title: Ref<string>; spaceId: Ref<string>; enabled: Ref<boolean>; api?: SimilarPagesApi }) {
  const api = options.api ?? similarPagesApi;
  const found = shallowRef<SimilarPage[]>([]);
  const dismissedIds = ref(new Set<string>());
  const dismissedTitle = ref<string | null>(null);
  // A title looked up already (typed back to, or again) answers in the same frame, without the debounce (P17).
  const answers = new Map<string, SimilarPage[]>();
  const key = (title: string) => `${options.spaceId.value}\n${title.trim().toLocaleLowerCase()}`;
  const watcher = createSimilarPagesWatcher({
    find: async (title, signal) => { const items = await api.find(title, options.spaceId.value, signal); answers.set(key(title), items); return items; },
    onResult: items => { found.value = items; },
  });

  watch([options.title, options.spaceId, options.enabled], ([title, , enabled]) => {
    const known = enabled ? answers.get(key(title)) : undefined;
    if (known) { watcher.cancel(); found.value = known; return; }
    if (enabled) return watcher.update(title);
    watcher.cancel();
    found.value = [];
  }, { immediate: true });
  onScopeDispose(watcher.cancel);

  const items = computed(() => {
    if (dismissedTitle.value !== null && titleDrift(options.title.value, dismissedTitle.value) <= DISMISS_DRIFT) return [];
    return found.value.filter(item => !dismissedIds.value.has(item.id));
  });

  function dismiss() {
    for (const item of items.value) dismissedIds.value.add(item.id);
    dismissedTitle.value = options.title.value;
  }
  return { items, dismiss };
}
