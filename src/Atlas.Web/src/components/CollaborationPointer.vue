<script setup lang="ts">
import { useId } from "vue";
import { COLLABORATION_POINTER_PATH } from "../live/collaborationPointer";
import AiGlyph from "./ai/AiGlyph.vue";

/**
 * A co-editor's mouse pointer in a Vue view (a board's BoardPresence, the release demos): the arrow in the person's `color` with their
 * name (a pointer without `name` is a bare arrow), as live/collaborationPointer.ts draws it over a page. An `agent` (an „ai:“ user) has the arrow in the AI gradient and its stars
 * writing in the name pill. The caller moves it (pointerGlide.ts on its root element) and sets is-pressed / is-leaving.
 */
defineProps<{ name?: string; color?: string; agent?: boolean }>();
// An SVG paint server per arrow: an id another arrow could take away is not shared.
const gradient = useId();
</script>

<template>
  <div class="collaboration-pointer" :class="{ 'is-agent': agent }" :style="color ? { '--collaboration-color': color } : undefined">
    <svg viewBox="0 0 22 26">
      <template v-if="agent">
        <defs>
          <linearGradient :id="gradient" x1="0" y1="0" x2="1" y2="1">
            <stop style="stop-color: var(--ai-line-from)" />
            <stop offset=".5" style="stop-color: var(--ai-line-via)" />
            <stop offset="1" style="stop-color: var(--ai-aura)" />
          </linearGradient>
        </defs>
        <path :d="COLLABORATION_POINTER_PATH" :fill="`url(#${gradient})`" />
      </template>
      <path v-else :d="COLLABORATION_POINTER_PATH" />
    </svg>
    <span v-if="name" class="collaboration-pointer__name"><AiGlyph v-if="agent" :size="12" state="writing" working class="collaboration-pointer__glyph" />{{ name }}</span>
  </div>
</template>
