<script setup lang="ts">
import { computed } from "vue";
import SearchHighlight from "../SearchHighlight.vue";
import { cleanSnippet } from "../../searchHighlight";

/** The passage that matched, with its heading, in place of the snippet of a search row. The row itself links to the heading. */
const props = defineProps<{ text: string; headingTitle?: string | null; query: string }>();
const clean = computed(() => cleanSnippet(props.text));
</script>

<template>
  <span class="search-passage" data-search-passage>
    <span v-if="headingTitle" class="search-passage-heading"><span aria-hidden="true">›</span>{{ headingTitle }}</span>
    <span class="search-snippet"><SearchHighlight :text="clean" :query="query" /></span>
  </span>
</template>

<style scoped>
.search-passage { display: grid; gap: 2px; }
.search-passage-heading { display: inline-flex; align-items: center; gap: 3px; color: var(--muted); font-size: 12px; font-weight: 600; }
</style>
