<script setup lang="ts">
import { ref } from "vue";
import AccentPicker from "../../profile/AccentPicker.vue";
import ThemePreview from "../../profile/ThemePreview.vue";
import OptionPills from "../../settings/OptionPills.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { themeAccents, type ThemeAccentId } from "../../../profile";
import { collaboratorChoices } from "../../../collaboratorColors";

// Já → Vzhled (board 12, 15): the accent the interface leans to and the three themes it paints. Local state only.
const accent = ref<ThemeAccentId>("ocean");
const mode = ref<"light" | "dark">("light");
const collaborator = ref("blue");
const modes = [{ value: "light", label: "Světlý", icon: "sun" }, { value: "dark", label: "Tmavý", icon: "moon" }];
</script>

<template>
  <SettingsSection
    title="Barva zvýraznění a motiv: AccentPicker + ThemePreview"
    icon="palette"
    description="profile/AccentPicker.vue — barva, ke které se rozhraní kloní (data-accent): kulaté vzorky jako radiogroup, šipky mění volbu, pod nimi název. profile/ThemePreview.vue — miniatura wiki v motivu a akcentu přímo z palettes.css (karty Motiv). Barvu a ikonu prostoru nebo stránky volí AppearancePicker (60 tónů obsahu); krátkou volbu bez náhledu OptionPills."
  >
    <div class="theme-example">
      <OptionPills v-model="mode" :options="modes" label="Náhled vzorků" />
      <AccentPicker v-model="accent" :accents="themeAccents" :mode="mode" label="Barva zvýraznění" />
      <p class="theme-example-note">„Moje barva“ (Já → Vzhled): tentýž AccentPicker s dvanácti barvami jediné palety spolupracovníků (collaboratorColors.ts, <code>@atlas/markdown/core</code>) — kurzor, výběr, avatar i ukazatel na tabuli mají stejnou barvu; server ji přiděluje podle volby, jinak nejbližší volnou. Vzorek má světlou a tmavou variantu, „Automaticky“ je volba OptionPills nad ním (ProfilePage).</p>
      <AccentPicker v-model="collaborator" :accents="collaboratorChoices" :mode="mode" label="Moje barva" />
      <div class="theme-example-previews">
        <figure v-for="theme in (['light', 'dark', 'system'] as const)" :key="theme">
          <ThemePreview :mode="theme" :accent="accent" />
          <figcaption>{{ theme === 'light' ? 'Světlý' : theme === 'dark' ? 'Tmavý – tónovaný do akcentu se září' : 'Podle systému' }}</figcaption>
        </figure>
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.theme-example { display: grid; gap: 16px; min-width: 0; }
.theme-example-note { margin: 0; color: var(--muted); font-size: 13px; }
.theme-example-previews { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(200px, 100%), 1fr)); gap: 12px; }
.theme-example-previews figure { display: grid; gap: 6px; margin: 0; }
.theme-example-previews figcaption { color: var(--muted); font-size: 12.5px; }
</style>
