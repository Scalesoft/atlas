<script setup lang="ts">
import { ref } from "vue";
import ChoiceCards from "../../settings/ChoiceCards.vue";
import SettingsSection from "../../settings/SettingsSection.vue";

const provider = ref<string | null>(null);
const options = [
  { value: "openai", label: "OpenAI", description: "Značka označuje jen poskytovatele.", icon: "brand-openai" },
  { value: "anthropic", label: "Anthropic (Claude)", description: "Značka označuje jen poskytovatele.", icon: "brand-anthropic" },
  { value: "company", label: "Firemní server", description: "Ostatní poskytovatelé používají běžnou ikonu typu.", icon: "server" },
];
</script>

<template>
  <SettingsSection title="Značky poskytovatelů" description="Skutečné AtlasIcon v kartách volby poskytovatele. Značky OpenAI a Claude používejte jen k rozpoznání jejich služeb; pro firemní servery patří běžná ikona typu a pro spuštění AI úlohy AiGlyph.">
    <ChoiceCards v-model="provider" :options="options" label="Ukázka volby poskytovatele" :columns="3" />
  </SettingsSection>
</template>
