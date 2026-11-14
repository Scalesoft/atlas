<script setup lang="ts">
import { ref } from "vue";
import type { TemplateSuggestion } from "../../creationSuggestions";
import t from "../../i18n/components-creation";
import SuggestionChip, { type SuggestionState } from "../settings/SuggestionChip.vue";

/**
 * P3 "Návrh šablony: {název}", shown in the creation step only while step 1 left the content "Prázdná": SuggestionChip inline
 * with the type icon `layout-template` (a dictionary or similarity rule, never the AI glyph). **Použít** emits `apply` and the
 * parent selects the template (its preview loads as if the person had picked it). Same contract as PlacementSuggestion.
 */
defineProps<{ suggestion: TemplateSuggestion; state?: SuggestionState }>();
const emit = defineEmits<{ apply: [from: Element | null]; dismiss: [] }>();
const chip = ref<{ $el: HTMLElement }>();
</script>

<template>
  <SuggestionChip ref="chip" layout="inline" icon="layout-template" :label="t.suggestions.template" :state="state" data-template-suggestion @apply="emit('apply', chip?.$el.querySelector('.suggestion-chip-value') ?? null)" @dismiss="emit('dismiss')">{{ suggestion.title }}</SuggestionChip>
</template>
