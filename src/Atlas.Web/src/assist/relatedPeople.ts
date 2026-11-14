import { shallowReactive } from "vue";
import { useAiCapabilities } from "../composables/useAiCapabilities";
import { pageConnectionsApi, type PageConnectionsApi } from "../pageConnections";

const none: ReadonlySet<string> = new Set();

/**
 * P15 „s AI embeddingy navíc zpřesní řazení“: the contacts that the page's related pages name (R1 Související, the embedding
 * index's neighbours: GET /api/pages/{id}/connections `related.people`), by lower-case object id. Only a ranking signal for the
 * people of the `@` list and of a name's offer: never shown, never a link by itself. Asked once per page while the `related`
 * capability is on and never awaited: before its answer it ranks nothing and the next list uses it (reactive, so a view that shows
 * it updates). No text leaves the browser (the server reads its own index) and a space excluded from AI has no related pages.
 * shortcut: one answer per page and tab, not refreshed while the page is edited; refresh on publish if the order turns stale.
 */
export function createRelatedPeople(api: Pick<PageConnectionsApi, "get"> = pageConnectionsApi, enabled = () => useAiCapabilities().capabilities.value.related) {
  // Asked apart from the answers: a computed that reads the people writes nothing reactive.
  const asked = new Set<string>(), known = shallowReactive(new Map<string, ReadonlySet<string>>());
  return (pageId: string | undefined): ReadonlySet<string> => {
    if (!pageId || !enabled()) return none;
    if (!asked.has(pageId)) {
      asked.add(pageId);
      api.get(pageId, 1).then(answer => { known.set(pageId, new Set((answer?.related?.people ?? []).map(id => id.toLowerCase()))); }, () => undefined);
    }
    return known.get(pageId) ?? none;
  };
}

export const relatedPeople = createRelatedPeople();
