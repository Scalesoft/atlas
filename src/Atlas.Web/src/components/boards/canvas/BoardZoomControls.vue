<script setup lang="ts">
import { computed } from "vue";
import t from "../../../i18n/boards";
import ActionMenu, { type ActionMenuItem } from "../../ActionMenu.vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import BoardToolGlyph from "./BoardToolGlyph.vue";

/** Zoom bottom right (spec §2): −, the percentage with a menu of levels and fitting (the board's ActionMenu, as every board menu), +, and the minimap. */
const props = defineProps<{ zoom: number; minimap: boolean; hasSelection: boolean }>();
const emit = defineEmits<{ step: [direction: 1 | -1]; zoomTo: [zoom: number]; fit: [what: "content" | "selection"]; minimap: [] }>();
const items = computed<ActionMenuItem[]>(() => [
  { id: "0.5", label: "50 %", group: "level" }, { id: "1", label: "100 %", shortcut: "Shift+0", group: "level" }, { id: "2", label: "200 %", group: "level" },
  { id: "content", label: t.zoom.fitContent, shortcut: "Shift+1", group: "fit" },
  { id: "selection", label: t.zoom.fitSelection, shortcut: "Shift+2", disabled: !props.hasSelection, group: "fit" },
]);
function select(id: string) {
  if (id === "content" || id === "selection") emit("fit", id);
  else emit("zoomTo", Number(id));
}
</script>

<template>
  <div class="board-zoom" role="group" :aria-label="t.zoom.group" data-board-zoom-controls>
    <Button variant="ghost" size="icon-sm" type="button" :aria-label="t.zoom.out" :title="t.zoom.outTitle" data-board-zoom-out @click="emit('step', -1)"><AtlasIcon name="minus" :size="16" /></Button>
    <ActionMenu :items="items" :label="t.zoom.group" item-attr="data-board-zoom-item" side="top" align="end" @select="select">
      <Button variant="ghost" size="sm" type="button" class="board-zoom-value" :aria-label="t.zoom.level(Math.round(zoom * 100))" data-board-zoom>{{ Math.round(zoom * 100) }} %</Button>
    </ActionMenu>
    <Button variant="ghost" size="icon-sm" type="button" :aria-label="t.zoom.in" :title="t.zoom.inTitle" data-board-zoom-in @click="emit('step', 1)"><AtlasIcon name="plus" :size="16" /></Button>
    <Button variant="ghost" size="icon-sm" type="button" :aria-label="t.zoom.minimap" :title="t.zoom.minimap" :aria-pressed="minimap" data-board-minimap-toggle @click="emit('minimap')"><BoardToolGlyph name="map" :size="16" /></Button>
  </div>
</template>

<style scoped>
.board-zoom { display: flex; align-items: center; gap: 2px; padding: 3px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); box-shadow: var(--refresh-card-shadow); }
.board-zoom-value { min-width: 60px; font-variant-numeric: tabular-nums; }
.board-zoom :deep(button[aria-pressed="true"]) { color: var(--accent); background: var(--accent-soft); }
.board-zoom :deep(button:active:not(:disabled)) { scale: var(--atlas-press); }
</style>
