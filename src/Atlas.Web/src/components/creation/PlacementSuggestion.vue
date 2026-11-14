<script setup lang="ts">
import { computed, ref } from "vue";
import type { PlacementSuggestion } from "../../creationSuggestions";
import t from "../../i18n/components-creation";
import SuggestionChip, { type SuggestionState } from "../settings/SuggestionChip.vue";

/**
 * P2 "Návrh: {cesta}", under the location field of a page being created: SuggestionChip inline with the type icon `map-pin`
 * (a placement rule over similar pages, never the AI glyph). **Použít** emits `apply` with the value element, so the parent
 * sets the location and lets the value fly from the chip into the field (flyGhost); dismissing and what comes back are the
 * parent's. It never takes focus and is not announced.
 */
const props = withDefaults(defineProps<{ suggestion: PlacementSuggestion; rootLabel?: string; state?: SuggestionState }>(), { rootLabel: () => t.suggestions.rootLevel, state: "idle" });
const emit = defineEmits<{ apply: [from: Element | null]; dismiss: [] }>();

const chip = ref<{ $el: HTMLElement }>();
const text = computed(() => {
  const names = props.suggestion.path.map(step => step.title);
  if (names.length) return names.join(" / ");
  return props.suggestion.parentTitle ?? props.rootLabel;
});
</script>

<template>
  <SuggestionChip ref="chip" layout="inline" icon="map-pin" :label="t.suggestions.placement" :state="state" data-placement-suggestion @apply="emit('apply', chip?.$el.querySelector('.suggestion-chip-value') ?? null)" @dismiss="emit('dismiss')">{{ text }}</SuggestionChip>
</template>
