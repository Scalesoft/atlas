<script setup lang="ts">
import AtlasIcon from "../../AtlasIcon.vue";
import t from "../../../i18n/boards";

/** Stand-in for an item of a kind this client does not know (a newer server): a plain card, so the board still opens and keeps it. */
</script>

<template>
  <article class="board-block-fallback" data-board-block-fallback>
    <AtlasIcon name="box" :size="18" />
    <span>{{ t.block.unknownItem }}</span>
  </article>
</template>

<style scoped>
.board-block-fallback { display: flex; align-items: center; gap: 8px; width: 100%; height: 100%; padding: 12px 14px; color: var(--muted); font-size: 13px; }
</style>
