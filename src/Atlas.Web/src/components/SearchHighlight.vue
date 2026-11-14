<script setup lang="ts">
import { computed } from "vue";
import { highlightSearch } from "../searchHighlight";
const props = defineProps<{ text: string; query: string }>();
const parts = computed(() => highlightSearch(props.text, props.query));
</script>
<template><template v-for="(part, index) in parts" :key="index"><mark v-if="part.matched">{{ part.text }}</mark><template v-else>{{ part.text }}</template></template></template>
<!-- The negative margin gives back the padding, so the tint breathes around the match without opening a gap
     inside the word (on a selected row the tint matches the row and only the gap would show). -->
<style scoped>mark{background:var(--accent-soft);color:var(--accent);border-radius:3px;font-weight:650;padding:0 1px;margin:0 -1px;-webkit-box-decoration-break:clone;box-decoration-break:clone}</style>
