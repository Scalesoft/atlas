import { onScopeDispose, ref, shallowRef, watch } from "vue";
import type { ChoiceOption } from "../components/settings/types";
import type { PagePerson } from "./contactSources";

/** The accounts with access to one page whose name matches `text` (contactSources' pageAccounts; the gallery passes local ones). */
export type PageAccountSource = (pageId: string, text: string, signal: AbortSignal) => Promise<PagePerson[]>;

/** At most this many pages are asked: the server still reports any page it skips. */
const maxPages = 20;

/**
 * The accounts that may be a page's owner, as a SearchableChoiceList's remote options (`query`, `options`, `loading`): those with access to
 * every page of `pageIds` (one page: the verification popover; a selection: Stav obsahu). Typing waits a moment and the previous read is
 * aborted, so only the newest answer lands; `start()` reads at once with an empty query (the picker opened). A failed read shows nothing.
 */
export function useAccountSearch(pageIds: () => readonly string[], people: PageAccountSource) {
  const query = ref("");
  const options = shallowRef<ChoiceOption[]>([]);
  const loading = ref(false);
  let request: AbortController | undefined;
  let timer: ReturnType<typeof setTimeout> | undefined;
  async function find() {
    request?.abort();
    const controller = (request = new AbortController());
    const ids = pageIds().slice(0, maxPages);
    if (!ids.length) { options.value = []; loading.value = false; return; }
    loading.value = true;
    try {
      const lists = await Promise.all(ids.map((id) => people(id, query.value.trim(), controller.signal)));
      const shared = lists[0]!.filter((person) => lists.every((list) => list.some((other) => other.id === person.id)));
      if (!controller.signal.aborted) options.value = shared.map((person) => ({ value: person.id, label: person.name, description: person.username ?? person.email ?? undefined, icon: "user" }));
    } catch {
      if (!controller.signal.aborted) options.value = [];
    } finally {
      if (request === controller) loading.value = false;
    }
  }
  watch(query, (text) => { clearTimeout(timer); timer = setTimeout(() => void find(), text ? 180 : 0); });
  function start() {
    options.value = [];
    if (query.value) query.value = ""; // the watch reads at once
    else { clearTimeout(timer); void find(); }
  }
  onScopeDispose(() => { request?.abort(); clearTimeout(timer); });
  return { query, options, loading, start };
}
