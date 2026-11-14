import { computed, onScopeDispose, ref, shallowRef, watch, type Ref } from "vue";
import {
  createSuggestionWatcher, creationSuggestionsApi, PLACEMENT_MIN_LENGTH, TEMPLATE_MIN_LENGTH,
  type CreationSuggestionsApi, type PlacementSuggestion, type TemplateSuggestion,
} from "../creationSuggestions";

const normalize = (title: string) => title.trim().toLowerCase().replace(/\s+/g, " ");

/**
 * P2 placement and P3 template for the title being typed (plan §20): two debounced lookups plus what dismissing means.
 * Neither feeds a step's `ready` and neither is awaited by create(). A dismissed suggestion does not come back for the same
 * title for as long as this composable lives (the flow); a different title may get a new one. No result, 204, 503, an error
 * or a lookup that is switched off is simply nothing, so the flow is the one it is without them.
 */
export function useCreationSuggestions(options: {
  title: Ref<string>;
  spaceId: Ref<string>;
  /** Placement needs the `createSuggestions` capability, so the caller folds it in. */
  placementEnabled: Ref<boolean>;
  /** The dictionary part of P3 works without AI. */
  templateEnabled: Ref<boolean>;
  api?: CreationSuggestionsApi;
}) {
  const api = options.api ?? creationSuggestionsApi;
  const placementFound = shallowRef<PlacementSuggestion | null>(null);
  const templateFound = shallowRef<TemplateSuggestion | null>(null);
  const dismissed = ref(new Set<string>());

  const placementWatcher = createSuggestionWatcher<PlacementSuggestion>({
    find: (title, signal) => api.placement(title, options.spaceId.value, signal),
    onResult: value => { placementFound.value = value; },
    minLength: PLACEMENT_MIN_LENGTH,
  });
  const templateWatcher = createSuggestionWatcher<TemplateSuggestion>({
    find: (title, signal) => api.template(title, options.spaceId.value, signal),
    onResult: value => { templateFound.value = value; },
    minLength: TEMPLATE_MIN_LENGTH,
  });

  watch([options.title, options.spaceId, options.placementEnabled], ([title, , enabled]) => {
    if (enabled) return placementWatcher.update(title);
    placementWatcher.cancel();
    placementFound.value = null;
  }, { immediate: true });
  watch([options.title, options.spaceId, options.templateEnabled], ([title, , enabled]) => {
    if (enabled) return templateWatcher.update(title);
    templateWatcher.cancel();
    templateFound.value = null;
  }, { immediate: true });
  onScopeDispose(() => { placementWatcher.cancel(); templateWatcher.cancel(); });

  const key = (kind: string, value: string | null) => `${kind}|${normalize(options.title.value)}|${value ?? ""}`;
  const placement = computed(() => placementFound.value && !dismissed.value.has(key("placement", placementFound.value.parentId)) ? placementFound.value : null);
  const template = computed(() => templateFound.value && !dismissed.value.has(key("template", templateFound.value.templateId)) ? templateFound.value : null);

  function dismissPlacement() {
    if (placementFound.value) dismissed.value = new Set(dismissed.value).add(key("placement", placementFound.value.parentId));
  }
  function dismissTemplate() {
    if (templateFound.value) dismissed.value = new Set(dismissed.value).add(key("template", templateFound.value.templateId));
  }
  return { placement, template, dismissPlacement, dismissTemplate };
}
