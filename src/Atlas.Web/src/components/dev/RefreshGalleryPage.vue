<script setup lang="ts">
import { ref, watch, type Component } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import SettingsPage from "../settings/SettingsPage.vue";
import { useGalleryMotion } from "./galleryMotion";

// #/gallery/refresh: the components the Atlas 2.5 UI refresh adds or reshapes. Every area adds its examples as
// dev/refresh/<Area><Name>Example.vue; this page picks them up in file-name order, so no area edits the gallery itself.
const modules = import.meta.glob<{ default: Component }>("./refresh/*Example.vue", { eager: true });
const examples = Object.keys(modules).sort().map((path) => ({
  name: path.slice(path.lastIndexOf("/") + 1, -".vue".length),
  component: modules[path]!.default,
}));

const { slow, reduced } = useGalleryMotion();
const run = ref(0);
watch([slow, reduced], () => run.value++);
</script>

<template>
  <SettingsPage title="Refresh 2.5" description="Komponenty a sestavy UI refreshe „Ohnisko“: shell, knihovna, stránka a editor, AI, nastavení a vzhled. Místní data, žádné požadavky na server.">
    <div class="refresh-gallery" data-atlas-motion>
      <div class="refresh-gallery-controls" role="group" aria-label="Přehrávání ukázek">
        <Button size="sm" @click="run++"><AtlasIcon name="rotate-cw" :size="14" />Přehrát znovu</Button>
        <small>Zpomalit 3× a Omezený pohyb jsou v hlavičce galerie.</small>
      </div>
      <p v-if="!examples.length" class="refresh-gallery-empty">Zatím žádné ukázky.</p>
      <div v-for="example in examples" :key="`${example.name}-${run}`" class="refresh-gallery-example" :data-refresh-example="example.name">
        <component :is="example.component" />
      </div>
    </div>
  </SettingsPage>
</template>

<style scoped>
.refresh-gallery { display: grid; gap: 14px; min-width: 0; }
.refresh-gallery-controls { display: flex; flex-wrap: wrap; align-items: center; gap: 10px 18px; justify-self: start; padding: 8px 10px 8px 14px; border: 1px solid var(--line); border-radius: 999px; background: var(--surface); }
.refresh-gallery-controls small, .refresh-gallery-empty { color: var(--muted); }
.refresh-gallery-example { min-width: 0; }
</style>
