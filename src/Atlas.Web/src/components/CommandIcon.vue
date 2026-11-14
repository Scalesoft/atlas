<script setup lang="ts">
import AiGlyph from "./ai/AiGlyph.vue";
import AtlasIcon from "./AtlasIcon.vue";

/**
 * The icon of a palette command or settings identity. The command `ai` (Asistent, Zeptat se na tuto stránku) starts a model task.
 * AI settings also use `ai` to identify the same feature. Other commands and settings keep their Atlas icon. App.vue imports this
 * instead of the glyph, so the boundary of the glyph stays a short list of files (aiGlyphBoundary.guard.test.ts).
 */
withDefaults(defineProps<{ name: string; size?: number }>(), { size: 18 });
</script>

<template>
  <AiGlyph v-if="name === 'ai'" :size="size" state="ready" />
  <AtlasIcon v-else :name="name" :size="size" />
</template>
