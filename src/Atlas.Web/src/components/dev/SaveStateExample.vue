<script setup lang="ts">
import { ref } from "vue";
import { Button } from "../ui/button";
import SaveState, { type SaveStateKind } from "../SaveState.vue";

const live = ref<SaveStateKind>("saved");
const states: SaveStateKind[] = ["saving", "saved", "offline", "paused", "error"];
let timer: ReturnType<typeof setTimeout> | undefined;
/** As typing does: Ukládám… a moment after a change, Uloženo once the checkpoint is back. */
function type() {
  clearTimeout(timer);
  live.value = "saving";
  timer = setTimeout(() => { live.value = "saved"; }, 1200);
}
</script>

<template>
  <div class="save-state-example" data-save-state-example>
    <p class="st-muted">
      Stav ukládání (SaveState.vue) říká, co se děje s prací, která se ukládá sama: stránka po úhozech a tabule po každém kroku.
      Nemá tlačítko, nic nepotvrzuje a změny ohlašuje čtečce. Tam, kde uživatel ukládá vlastním tlačítkem (nastavení, profil), zůstává SaveBar.
    </p>
    <div class="save-state-row">
      <figure v-for="state in states" :key="state"><SaveState :state="state" error="Server odmítl uložení." /><figcaption>{{ state }}</figcaption></figure>
    </div>
    <div class="save-state-row">
      <SaveState :state="live" />
      <Button type="button" variant="outline" data-save-state-type @click="type">Napsat znak</Button>
    </div>
  </div>
</template>

<style scoped>
.save-state-example { display: grid; gap: 16px; min-width: 0; }
.save-state-example > p { margin: 0; }
.save-state-row { display: flex; flex-wrap: wrap; align-items: center; gap: 16px 28px; }
figure { display: grid; justify-items: start; gap: 6px; margin: 0; }
figcaption { color: var(--muted); font-size: 12px; }
</style>
