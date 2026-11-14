<script setup lang="ts">
import { ref } from "vue";
import type { PageSummary, Space } from "../../../api";
import SpaceAbout from "../../library/SpaceAbout.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { galleryPages, gallerySpaces } from "./libraryGalleryData";

// Gallery only: the templates are local rows, a template opens nothing and the links of Správa prostoru are kept
// from leaving the gallery. The third instance's Zkusit znovu answers with the list, so the error can be left again
// (Přehrát znovu brings it back).
const platform: Space = gallerySpaces[1]!, sales: Space = gallerySpaces[3]!, people: Space = gallerySpaces[4]!;
const template = (id: string, title: string, isPublished = true): PageSummary => ({ id, spaceId: platform.id, parentId: null, title, version: 2, isPublished });
const templates = [
  template("runbook", "Provozní postup"),
  template("meeting", "Zápis z porady vývoje, provozu a vedení s velmi dlouhým názvem, který se nevejde na jeden řádek seznamu", false),
];
const failed = ref(true);
const keepInGallery = (event: MouseEvent) => { if ((event.target as Element).closest("a")) event.preventDefault(); };
</script>

<template>
  <SettingsSection
    title="Prostor: SpaceAbout"
    icon="info"
    description="library/SpaceAbout.vue je záložka O prostoru v přehledu prostoru (#/spaces/<id>/about) pro každého, kdo prostor otevře: popis, fakta jako v hlavičce (stránky, koncepty, poslední změna) s řádkem Váš přístup a šablony prostoru. Jen s canManage přibude blok Správa prostoru s pěti odkazy do sekcí Nastavení prostoru; čtenář a píšící v něm nic nenajdou a o přístupu ostatních lidí se z této záložky nedozví nic. Skládá SummaryList, SettingsSection, SettingsListRow a TileCard; nic nemění, nastavení se mění v Nastavení prostoru. Dokud šablony nepřišly, drží místo kostra, prázdný seznam to řekne a selhání čtení je chyba na místě seznamu se Zkusit znovu, zbytek stránky zůstane. Pro hlavičku přehledu volte SpaceHeader (identita prostoru a jeho akce), pro zakládání šablon Šablony stránek v Nastavení prostoru; tahle komponenta jen informuje. Data a směrování zůstávají v shellu. Zde: správce se dvěma šablonami, čtenář bez šablon a čtenář, kterému se šablony nenačetly."
  >
    <div class="space-about-example" @click="keepInGallery">
      <figure class="space-about-example-frame is-wide" data-space-about-example="manager">
        <figcaption>Správce prostoru</figcaption>
        <SpaceAbout :space="platform" :pages="galleryPages" :templates="templates" can-manage />
      </figure>
      <figure class="space-about-example-frame" data-space-about-example="reader">
        <figcaption>Čtenář bez šablon</figcaption>
        <SpaceAbout :space="people" :pages="galleryPages.slice(0, 4)" :templates="[]" />
      </figure>
      <figure class="space-about-example-frame" data-space-about-example="templates-error">
        <figcaption>Čtenář, šablony se nenačetly</figcaption>
        <SpaceAbout :space="{ ...sales, role: 'Reader' }" :pages="galleryPages.slice(0, 7)" :templates="failed ? undefined : templates" :templates-error="failed ? 'Server neodpovídá.' : undefined" @retry-templates="failed = false" />
      </figure>
    </div>
  </SettingsSection>
</template>

<style scoped>
.space-about-example { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 360px), 1fr)); align-items: start; gap: 16px; }
.space-about-example-frame { min-width: 0; margin: 0; padding: 14px; border: 1px dashed var(--line); border-radius: 14px; }
.space-about-example-frame.is-wide { grid-column: 1 / -1; }
.space-about-example-frame figcaption { margin-bottom: 10px; color: var(--muted); font-size: 12.5px; font-weight: 600; }
</style>
