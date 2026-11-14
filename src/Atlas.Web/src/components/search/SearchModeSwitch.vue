<script setup lang="ts">
import AtlasIcon from "../AtlasIcon.vue";
import AiGlyph from "../ai/AiGlyph.vue";
import componentsSearchMode from "../../i18n/components-search-mode";

/**
 * The modes of the one search field as the tab row of the palette (refresh 2.5, board „Hledání (Ctrl K)“): Vše (the results,
 * with their count), Zeptat se (a question for the answer from the wiki, the AI entry of the search; `?`) and Příkazy (`>`).
 * A switch keeps the text and its selection and never starts the model by itself. Without the answer capability there is no
 * Zeptat se and no AI glyph.
 */
defineProps<{ mode: "pages" | "ask" | "commands"; answer: boolean; count?: string | null }>();
const emit = defineEmits<{ change: [mode: "pages" | "ask" | "commands"] }>();
</script>
<template>
  <div class="search-modes" role="group" :aria-label="componentsSearchMode.search.modes">
    <button type="button" :aria-pressed="mode === 'pages'" data-search-pages-mode @click="emit('change', 'pages')"><AtlasIcon name="search" :size="15" />{{ componentsSearchMode.search.all }}<Transition name="search-count"><span v-if="count" class="search-mode-count">{{ count }}</span></Transition></button>
    <button v-if="answer" type="button" :aria-pressed="mode === 'ask'" data-search-ask-mode @click="emit('change', 'ask')"><AiGlyph :size="15" state="ready" />{{ componentsSearchMode.search.ask }} <kbd>?</kbd></button>
    <button type="button" :aria-pressed="mode === 'commands'" @click="emit('change', 'commands')"><AtlasIcon name="menu" :size="15" />{{ componentsSearchMode.search.commands }} <kbd>&gt;</kbd></button>
  </div>
</template>
