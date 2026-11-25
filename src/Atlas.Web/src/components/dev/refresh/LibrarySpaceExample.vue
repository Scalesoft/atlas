<script setup lang="ts">
import { memoryRelations } from "../../../objects/memoryRelations";
import SpaceHeader from "../../library/SpaceHeader.vue";
import SpacePageList from "../../SpacePageList.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { TooltipProvider } from "../../ui/tooltip";
import { galleryPages, gallerySpaces } from "./libraryGalleryData";

// Gallery only: the actions and the rows route nowhere; publishing would call the server, so the list is read-only here. The rows
// are answered from memory (objects/memoryRelations.ts) as the server answers Obsah.
const platform = gallerySpaces[1]!;
const byId = new Map(galleryPages.map(page => [page.id, page]));
const pathOf = (parentId: string | null): string[] => { const parent = parentId ? byId.get(parentId) : undefined; return parent ? [...pathOf(parent.parentId), parent.title] : []; };
const relations = memoryRelations(galleryPages.map(page => ({
  id: page.id, typeId: "atlas.page", title: page.title, spaceId: page.spaceId, spaceName: platform.name, readable: true, path: pathOf(page.parentId).join(" › ") || null,
})), [], { pageSize: 100 });
</script>

<template>
  <SettingsSection
    title="Prostor: SpaceHeader a SpacePageList"
    icon="file-text"
    description="library/SpaceHeader.vue je hlava přehledu prostoru (deska 02): ikona, název, popis, řádek faktů (stránky, koncepty, váš přístup, poslední změna) a dvě akce prostoru, Nastavení prostoru pro jeho správce a Nová stránka pro ty, kdo píšou. Dokud seznam stránek nedorazí (pagesLoaded vypnuté), hlava počítá stránky podle prostoru ze seznamu prostorů, místo aby tvrdila 0 stránek. SpacePageList.vue je záložka Obsah (viz sestava „Obsah prostoru a vazby“ s dlaždicemi, filtrem Vztah, dokem a grafem); tahle ukázka ukazuje jen jeho stránky a publikování. Hledání je jedno pole nad tabulkou (když nic nenajde, tabulka se vymění za prázdný stav), dlouhý prostor jde po stovkách přes Načíst další, na telefonu je z řádku karta. Kdo píše, zaškrtne koncepty přímo v řádcích (BulkSelectBar, Notionův model bez režimu „Vybrat“): políčko se ukáže při najetí a fokusu, sloupec pro něj stojí stále, takže nic neposkočí; mezerník na řádku zaškrtne, nad tabulkou je „Vybrat zobrazené (n)“ a lišta s počtem a Publikovat vybrané se rozbalí při prvním zaškrtnutí; poznámky o průběhu nebo selhání publikace se rozbalí do výšky s mezerou nad sebou (disclosureHooks). Řádek má „Co s tím jde dělat“ (Otevřít, Detail, Kde je použito, Vybrat). Hlavička sloupců zůstane pod horní lištou, dokud se tabulka posouvá."
  >
    <TooltipProvider>
      <section class="overview space-overview library-space-example">
        <SpaceHeader :space="platform" :pages="galleryPages" can-manage can-write />
        <SpacePageList :space-id="platform.id" :pages="galleryPages" :can-write="false" :relations="relations" />
      </section>
    </TooltipProvider>
  </SettingsSection>
</template>

<style scoped>
.library-space-example { border: 1px dashed var(--line); border-radius: 14px; }
</style>
