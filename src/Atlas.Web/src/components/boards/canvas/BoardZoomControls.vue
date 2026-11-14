<script setup lang="ts">
import t from "../../../i18n/boards";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "../../ui/dropdown-menu";
import BoardToolGlyph from "./BoardToolGlyph.vue";

/** Zoom bottom right (spec §2): −, the percentage with a menu of levels and fitting, +, and the minimap. */
defineProps<{ zoom: number; minimap: boolean; hasSelection: boolean }>();
const emit = defineEmits<{ step: [direction: 1 | -1]; zoomTo: [zoom: number]; fit: [what: "content" | "selection"]; minimap: [] }>();
</script>

<template>
  <div class="board-zoom" role="group" :aria-label="t.zoom.group" data-board-zoom-controls>
    <Button variant="ghost" size="icon-sm" type="button" :aria-label="t.zoom.out" :title="t.zoom.outTitle" data-board-zoom-out @click="emit('step', -1)"><AtlasIcon name="minus" :size="16" /></Button>
    <DropdownMenu>
      <DropdownMenuTrigger as-child>
        <Button variant="ghost" size="sm" type="button" class="board-zoom-value" :aria-label="t.zoom.level(Math.round(zoom * 100))" data-board-zoom>{{ Math.round(zoom * 100) }} %</Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" side="top">
        <DropdownMenuItem v-for="level in [0.5, 1, 2]" :key="level" @select="emit('zoomTo', level)">{{ level * 100 }} %<span v-if="level === 1" class="board-menu-key">Shift+0</span></DropdownMenuItem>
        <DropdownMenuItem data-board-fit @select="emit('fit', 'content')">{{ t.zoom.fitContent }}<span class="board-menu-key">Shift+1</span></DropdownMenuItem>
        <DropdownMenuItem :disabled="!hasSelection" @select="emit('fit', 'selection')">{{ t.zoom.fitSelection }}<span class="board-menu-key">Shift+2</span></DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
    <Button variant="ghost" size="icon-sm" type="button" :aria-label="t.zoom.in" :title="t.zoom.inTitle" data-board-zoom-in @click="emit('step', 1)"><AtlasIcon name="plus" :size="16" /></Button>
    <Button variant="ghost" size="icon-sm" type="button" :aria-label="t.zoom.minimap" :title="t.zoom.minimap" :aria-pressed="minimap" data-board-minimap-toggle @click="emit('minimap')"><BoardToolGlyph name="map" :size="16" /></Button>
  </div>
</template>

<style scoped>
.board-zoom { display: flex; align-items: center; gap: 2px; padding: 3px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); box-shadow: var(--refresh-card-shadow); }
.board-zoom-value { min-width: 60px; font-variant-numeric: tabular-nums; }
.board-zoom :deep(button[aria-pressed="true"]) { color: var(--accent); background: var(--accent-soft); }
.board-zoom :deep(button:active:not(:disabled)) { scale: var(--atlas-press); }
.board-menu-key { margin-left: auto; padding-left: 16px; color: var(--muted); font-size: 12px; }
</style>
