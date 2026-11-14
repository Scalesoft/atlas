<script setup lang="ts">
import { computed, ref } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import SpaceSwitcher from "../settings/SpaceSwitcher.vue";

// Gallery example of the settings rail's chrome (the markup App.vue puts above the sections of space
// settings) with the real SpaceSwitcher: local sample spaces, no API, nothing navigates.
const spaces = [
  { id: "provoz", name: "Provoz", icon: "workflow", color: "green" },
  { id: "vyvoj", name: "Vývoj", icon: "code-xml", color: "blue" },
  { id: "lide", name: "Lidé, nábor a zaškolení nových kolegů ve všech týmech", icon: "compass", color: "amber" },
  { id: "smlouvy", name: "Smlouvy", icon: "scroll-text", color: "coral" },
];
// In the application the shell opens the chosen space and hands it back as `current`.
const openId = ref("provoz");
const rails = computed(() => [
  { label: "Více spravovaných prostorů: čip otevírá seznam", current: spaces.find((space) => space.id === openId.value)!, spaces },
  { label: "Jeden spravovaný prostor: jen popisek", current: spaces[0]!, spaces: spaces.slice(0, 1) },
]);
</script>

<template>
  <SettingsSection
    title="Lišta nastavení s přepínačem prostoru"
    description="settings/SpaceSwitcher.vue v liště Nastavení prostoru, pod cestou zpět a nadpisem místa. Čip s otevřeným prostorem otevře vyhledávací seznam prostorů, které účet spravuje. Šipky prostor jen označí; otevře se až klávesou Enter nebo kliknutím, ve stejné sekci nastavení. S jedním spravovaným prostorem je z čipu popisek, bez spravovaného prostoru se nevykreslí nic. Přepínač je navigace: kde volba plní hodnotu formuláře (krok průvodce trezorem nebo přidělení přístupu), patří SearchableChoiceList přímo do kroku, bez čipu a vyskakovacího okna, a volbu potvrdí až průvodce."
  >
    <div class="settings-rail-example">
      <div v-for="rail in rails" :key="rail.label" class="settings-rail-example-variant">
        <p class="settings-rail-example-caption">{{ rail.label }}</p>
        <div class="settings-rail-example-rail">
          <div class="sidebar-top settings-sidebar-top">
            <button type="button" class="settings-back"><AtlasIcon name="chevron-left" :size="16" />Zpět do prostoru</button>
          </div>
          <div class="settings-sidebar-scope"><span class="settings-sidebar-title">Nastavení prostoru</span></div>
          <div class="settings-switcher-slot"><SpaceSwitcher :current="rail.current" :spaces="rail.spaces" @switch="openId = $event" /></div>
        </div>
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.settings-rail-example { display: flex; flex-wrap: wrap; gap: 16px 24px; }
.settings-rail-example-variant { display: grid; align-content: start; gap: 6px; width: min(272px, 100%); }
.settings-rail-example-caption { margin: 0; color: var(--muted); font-size: 12px; }
/* The sidebar's width, padding, gap and surface (refresh.css §2). */
.settings-rail-example-rail { display: flex; flex-direction: column; gap: 10px; padding: 12px 10px 10px; border: 1px solid var(--line); border-radius: var(--atlas-radius-lg); background: var(--sidebar); }
</style>
