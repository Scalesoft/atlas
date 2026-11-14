<script setup lang="ts">
import { ref } from "vue";
import PagePdfExportStatus from "../../PagePdfExportStatus.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { TooltipProvider } from "../../ui/tooltip";
import { useGalleryFetch } from "./galleryFetch";
import "../../settings/settings.css";

// The status reads the page's PDF export; the example answers it locally (current, here) and shows the "unsaved typing" state beside it.
useGalleryFetch({ "/pdf-export": () => ({ settings: { enabled: true }, status: "current", lastExportedAt: new Date().toISOString(), lastFileName: "Serverovna.pdf" }) });
const typing = ref(false);
</script>

<template>
  <TooltipProvider>
    <div class="settings-ui" data-pdf-status-example>
      <SettingsSection title="PagePdfExportStatus" description="PagePdfExportStatus.vue — stav automatického PDF vedle stavu uložení stránky: Připraveno (čas a soubor), Připravuje se, Čeká, až dopíšete (neuložený text), Selhalo se Zkusit znovu, vždy se Stažením. U stránky bez zapnutého exportu se nic neukáže. Zvolte ji pro stav jedné věci, která se po uložení děje na serveru; trvalé nastavení exportu je PagePdfExportDialog, jednorázový stav StatusBadge.">
        <label><input v-model="typing" type="checkbox"> Rozepsaný text (neuloženo)</label>
        <PagePdfExportStatus page-id="gallery" :version="1" :dirty="typing" :settings-open="false" />
      </SettingsSection>
    </div>
  </TooltipProvider>
</template>
