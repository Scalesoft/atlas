<script setup lang="ts">
import StatusBadge from "../settings/StatusBadge.vue";
import componentsSearchMode from "../../i18n/components-search-mode";
import { Tooltip, TooltipContent, TooltipTrigger } from "../ui/tooltip";

/**
 * "Významová shoda" on a search row whose page does not contain the exact words of the query. Semantic matching is
 * embeddings, not model output, so the badge carries a type icon and never the AI glyph. The parent decides whether
 * to render it (a meaning-only result and the semanticSearch capability).
 */
</script>

<template>
  <Tooltip>
    <TooltipTrigger as-child>
      <span class="semantic-badge" data-semantic-badge><StatusBadge tone="neutral" icon="book-open" :label="componentsSearchMode.search.semantic" size="sm" /></span>
    </TooltipTrigger>
    <TooltipContent inline>{{ componentsSearchMode.search.semanticTip }}</TooltipContent>
  </Tooltip>
</template>

<style scoped>
.semantic-badge { display: inline-flex; max-width: 100%; }
</style>
