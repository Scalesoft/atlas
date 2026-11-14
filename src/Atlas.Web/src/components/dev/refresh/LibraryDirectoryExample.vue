<script setup lang="ts">
import { ref } from "vue";
import type { Space } from "../../../api";
import LibraryPage, { type LibraryPart } from "../../library/LibraryPage.vue";
import SpaceDirectory from "../../SpaceDirectory.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { TooltipProvider } from "../../ui/tooltip";
import { gallerySpaces } from "./libraryGalleryData";

// Gallery only: the pin flips at once and stays busy for a moment, like the shell's optimistic request;
// the personal parts are lists the server keeps, so here they only name themselves.
const spaces = ref<Space[]>(gallerySpaces.map((space) => ({ ...space })));
const pending = ref(new Set<string>());
const part = ref<LibraryPart>("spaces");
const stageWidth = ref("100%");
function pin(space: Space, pinned: boolean) {
  spaces.value = spaces.value.map((item) => (item.id === space.id ? { ...item, pinned } : item));
  pending.value = new Set([...pending.value, space.id]);
  setTimeout(() => { pending.value = new Set([...pending.value].filter((id) => id !== space.id)); }, 700);
}
</script>

<template>
  <SettingsSection
    title="Knihovna: LibraryPage a SpaceDirectory"
    icon="book-open"
    description="library/LibraryPage.vue drží nadpis Knihovny, Nový prostor a části jako odkazy (#/library/…); podtržení přejede na novou záložku, obsah se jen prolne, na telefonu jsou části pilulky. SpaceDirectory.vue je část Prostory (desky 01 a 14): hledání, filtr Zobrazit (připnuté nebo podle vašeho přístupu), počet a přepínač Karty / Seznam, který si shell pamatuje pro účet (storage-key). Karty jsou SpaceCard, seznam je zarovnaná tabulka; na telefonu jsou řádky, připnuté první a zbytek za Všechny prostory. Připnuté jsou vždy první a při přepnutí karta plynule odjede; povinně připnutý má zámek. Záložky i Karty / Seznam se prolnou v jedné buňce a její výška plynule dojede na novou, nic neposkočí; chip za volbou Karty / Seznam je jeden a přejede na novou volbu stejně jako podtržení záložek (u omezeného pohybu se jen prolne vlastní chip volby). Prostor, který z hledání vypadne, se na místě vytratí a ostatní se k němu posunou; když hledání nenechá žádný, vytratí se na místě celý seznam a prolne se s prázdným stavem (a zpět). Úvodní obrázek karty drží svůj box a objeví se prolnutím, jakmile se načte."
  >
    <label class="library-directory-width">Šířka ukázky knihovny
      <select v-model="stageWidth" aria-label="Šířka ukázky knihovny"><option value="100%">Celá pracovní plocha</option><option value="480px">Úzký kontejner · 480 px</option></select>
    </label>
    <p class="library-directory-placeholder">Záložky zůstávají v jednom řádku i při úzkém kontejneru. Klávesou Tab se skrytý odkaz posune do zobrazení.</p>
    <TooltipProvider>
      <div class="library-directory-example" :style="{ width: stageWidth }">
        <LibraryPage :view="part" :space-count="spaces.length" can-create @navigate="part = $event">
          <SpaceDirectory v-if="part === 'spaces'" :spaces="spaces" can-create can-manage :pending-ids="pending" @pin="pin" />
          <p v-else class="library-directory-placeholder">Osobní seznamy (Nedávné, Oblíbené, koncepty, publikace) načítá server; v galerii je jen část Prostory.</p>
        </LibraryPage>
      </div>
    </TooltipProvider>
  </SettingsSection>
</template>

<style scoped>
.library-directory-example { min-width: 0; max-width: 100%; border: 1px dashed var(--line); border-radius: 14px; }
.library-directory-width { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; font-size: 13px; }
.library-directory-width select { max-width: 100%; }
.library-directory-placeholder { margin: 0; color: var(--muted); font-size: 13px; }
</style>
