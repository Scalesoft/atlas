import { computed, ref, shallowRef, watch } from "vue";
import { errorMessage } from "../api";
import t from "../i18n/projector";
import { catalogModules, deploymentDocument, planSelection } from "./catalogSelection";
import { projectorApi, type CatalogDefinition } from "./projectorApi";
import { catalogBlocks } from "./requirementCatalog";

/**
 * „Nasazení z katalogu“ (owner, J2): the space's catalog while the new-page flow shows it, the modules ticked, the plan of them live,
 * and the new page's content once it is created (catalogSelection.ts). One request for the list, one for the picked blocks; the
 * page itself is created by POST /pages with that content, so it is one atomic write.
 */
export function useDeploymentDraft(spaceId: () => string, enabled: () => boolean, api: Pick<typeof projectorApi, "catalog" | "definitions"> = projectorApi) {
  const definitions = shallowRef<CatalogDefinition[]>([]);
  const status = ref<"loading" | "ready" | "error">("loading");
  const error = ref("");
  const selected = ref<string[]>([]);
  const modules = computed(() => catalogModules(definitions.value));
  const plan = computed(() => planSelection(definitions.value, modules.value, selected.value));
  let request = 0;

  watch(() => enabled() && spaceId(), async space => {
    const current = ++request;
    definitions.value = []; selected.value = []; error.value = ""; status.value = "loading";
    if (!space) return;
    try {
      const catalog = await api.catalog(space);
      if (current !== request) return;
      definitions.value = catalog.definitions;
      status.value = "ready";
    } catch (failure) {
      if (current === request) { error.value = errorMessage(failure); status.value = "error"; }
    }
  }, { immediate: true });

  /** The new page's rich document and its blocks' ids in order; a definition gone from the catalog meanwhile is said, not skipped. */
  async function content() {
    const current = plan.value;
    if ("refused" in current) throw new Error(t.draft.unresolvable);
    const blocks = await catalogBlocks({ spaceId, api }, current.items.map(item => item.definition.key));
    const gone = current.items.find(item => !blocks.has(item.definition.key));
    if (gone) throw new Error(t.draft.gone(gone.definition.title));
    const { value, ids } = deploymentDocument(current, blocks);
    return { contentJson: JSON.stringify(value), ids };
  }

  return { definitions, status, error, selected, modules, plan, content };
}
