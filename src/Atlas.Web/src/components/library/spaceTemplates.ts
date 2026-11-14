import { ref, watch } from "vue";
import { errorMessage } from "../../api";
import { pageTemplateApi, type PageTemplateSummary } from "../../pageTemplateApi";

/**
 * The template pages of one space, for O prostoru (any reader of the space may list them). The space comes from a
 * getter: while it returns nothing nothing is asked for, and a change of space forgets the previous answer, so the
 * shell reads the list only while the tab is open. `items` stays `undefined` until the first answer, which lets the
 * view tell "not loaded yet" from "no templates". A late answer for a space that has been left is dropped.
 */
export function useSpaceTemplates(spaceId: () => string | undefined) {
  const items = ref<PageTemplateSummary[]>();
  const loading = ref(false);
  const error = ref("");
  let request = 0;

  async function load(id: string | undefined) {
    const current = ++request;
    loading.value = !!id;
    error.value = "";
    if (!id) return;
    try {
      const result = await pageTemplateApi.list(id);
      if (current === request) items.value = result;
    } catch (cause) {
      if (current === request) error.value = errorMessage(cause);
    } finally {
      if (current === request) loading.value = false;
    }
  }
  watch(spaceId, (id) => { items.value = undefined; void load(id); }, { immediate: true });

  return { items, loading, error, reload: () => load(spaceId()) };
}
