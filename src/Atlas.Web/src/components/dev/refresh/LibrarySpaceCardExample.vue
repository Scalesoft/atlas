<script setup lang="ts">
import type { SpaceCover } from "../../../spaceCover";
import SettingsSection from "../../settings/SettingsSection.vue";
import SpaceCard from "../../library/SpaceCard.vue";
import SpaceCoverImage from "../../library/SpaceCoverImage.vue";
import { TooltipProvider } from "../../ui/tooltip";
import { galleryDrawing, gallerySpaces } from "./libraryGalleryData";
// The settings design system's stagger step (--st-stagger) for the cards' entrance.
import "../../settings/settings.css";

// Gallery only: the four sources of a space's image. Drawings come from the built-in catalog; an "upload" is a local
// drawing handed in as coverSrc; the cards link nowhere.
const [runbooks, platform, meetings, sales, people, atlas3, sandbox] = gallerySpaces;
const architecture = galleryDrawing("architecture");
const servers = galleryDrawing("servers");
const cover = (source: SpaceCover["source"], catalogKey: string | null = null, tinted = true): SpaceCover => ({ source, catalogKey, version: source === "Upload" ? "1" : null, tinted });
</script>

<template>
  <SettingsSection
    title="Karta prostoru: SpaceCard a SpaceCoverImage"
    icon="layout-dashboard"
    description="library/SpaceCard.vue — karta prostoru v Knihovně (deska 01): obrázek prostoru nahoře, ikona prostoru přes jeho okraj, název, popis a řádek faktů. Obrázek má zdroj; v pořadí karet: kresba z vestavěného katalogu, kterou někdo vybral, kresba, kterou server vybral automaticky podle názvu, popisu a ikony, vlastní nahraný obrázek s tinted a bez něj, a prostor bez obrázku. Kresba a obrázek s tinted jsou duotón ve dvou barvách palety prostoru (v tmavém režimu převrácený), nahraný obrázek bez tinted je pod jemným závojem tak, jak byl nahrán; prostor bez obrázku má vzor ve svých barvách s velkou ikonou. Řádek faktů zůstává na jednom řádku a čas poslední změny se nikdy neutne uprostřed slova: když by se utnul, ustoupí jako první role, která se na té kartě ukáže jen ikonou (její slova zůstanou v title a pro čtečky). Obrázek drží svůj box a objeví se prolnutím, jakmile se načte (už načtený je tam hned). Odkazem je jen název roztažený přes kartu, takže #corner nese vlastní ovládání (připnutí). preview je menší karta vedle ovládání obrázku v Nastavení prostoru. Pro jiné karty v mřížce volte TileCard; tahle existuje kvůli obrázku, pro který TileCard nemá místo. Samotnou vrstvu obrázku kreslí library/SpaceCoverImage.vue (pruh pod kartami): použijte ji všude, kde prostor ukazuje svůj obrázek mimo kartu, tedy v hlavě prostoru, v náhledu kresby nebo v jiném přehledu; velikost jí dává hostitel. Pro kartu volte SpaceCard."
  >
    <TooltipProvider>
      <div class="settings-ui library-card-example">
        <SpaceCard :space="{ ...sales!, cover: cover('Catalog', 'ops-sales') }" href="#/gallery/refresh" :index="0" @navigate.prevent />
        <SpaceCard :space="{ ...people!, cover: cover('Auto', 'ops-hr') }" href="#/gallery/refresh" :index="1" @navigate.prevent />
        <SpaceCard :space="{ ...platform!, cover: cover('Upload') }" :cover-src="architecture" href="#/gallery/refresh" :index="2" @navigate.prevent />
        <SpaceCard :space="{ ...runbooks!, cover: cover('Upload', null, false) }" :cover-src="servers" href="#/gallery/refresh" :index="3" @navigate.prevent />
        <SpaceCard :space="{ ...atlas3!, cover: cover('None') }" href="#/gallery/refresh" :index="4" @navigate.prevent />
        <SpaceCard :space="{ ...meetings!, cover: cover('Auto', 'ops-meetings') }" preview :index="5" />
        <SpaceCoverImage class="library-card-band" :space="{ ...sandbox!, cover: cover('Catalog', 'gen-ideas') }" instant />
      </div>
    </TooltipProvider>
  </SettingsSection>
</template>

<style scoped>
.library-card-example { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 260px), 1fr)); align-items: start; gap: 16px; }
/* The image layer on its own takes its size from the host: here a band, as in the head of a space. */
.library-card-band { grid-column: 1 / -1; height: 120px; border-radius: 16px; }
</style>
