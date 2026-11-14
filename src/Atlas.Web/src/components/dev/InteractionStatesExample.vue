<script setup lang="ts">
import { ref } from "vue";
import { Button } from "../ui/button";
import ChoiceCards from "../settings/ChoiceCards.vue";
import OptionPills from "../settings/OptionPills.vue";
import AccessLevelPicker from "../settings/AccessLevelPicker.vue";
import SearchableChoiceList from "../settings/SearchableChoiceList.vue";
import SpaceCard from "../library/SpaceCard.vue";
import SpacePinToggle from "../library/SpacePinToggle.vue";
import type { ChoiceOption } from "../settings/types";

const selected = ref<string | string[] | null>("available");
const period = ref("week");
const pending = ref(false);
const pinCount = ref(0);
const space = { id: "interaction-example", name: "Ukázkový prostor", icon: "book", color: "blue", role: "Reader" as const, pageCount: 12, lastChangedAt: null, pinned: false, pinPolicy: "None" as const };
const choices: ChoiceOption[] = [
  { value: "available", label: "Dostupná volba", description: "Myš, klávesnice i dotyk mají stejný výsledek.", icon: "book" },
  { value: "disabled", label: "Nedostupná volba", description: "Nedostupný stav se nechová jako akce.", icon: "lock", disabled: true },
];
</script>

<template>
  <div class="interaction-example" data-interaction-example>
    <p class="st-muted">Při přejetí okraje zůstává plocha karty na místě. Výběr pilulky nemění její šířku; nedostupné volby nereagují na stisk. Vyzkoušejte také Tab, šipky a omezený pohyb nahoře.</p>
    <ChoiceCards v-model="selected" label="Stavy karet" :options="choices" />
    <OptionPills v-model="period" label="Stálá šířka voleb" :options="[{ value: 'week', label: 'Týden', icon: 'calendar' }, { value: 'month', label: 'Měsíc', icon: 'calendar' }, { value: 'year', label: 'Rok', icon: 'lock', disabled: true }]" />
    <div class="interaction-columns">
      <SearchableChoiceList label="Stavy seznamu" :options="choices" />
      <AccessLevelPicker label="Přístup pouze pro čtení" :options="choices" model-value="available" readonly />
    </div>
    <div class="interaction-card">
      <SpaceCard :space="space" href="#/gallery/controls" @navigate.prevent>
        <template #corner><SpacePinToggle :space="space" :pending="pending" @toggle="pinCount++" /></template>
      </SpaceCard>
    </div>
    <div class="st-actions">
      <Button type="button" variant="outline" :aria-pressed="pending" @click="pending = !pending">{{ pending ? 'Ukončit čekání připnutí' : 'Simulovat čekání připnutí' }}</Button>
      <output aria-label="Počet připnutí">{{ pinCount }}</output>
    </div>
  </div>
</template>

<style scoped>
.interaction-example { display: grid; gap: 20px; min-width: 0; }
.interaction-example > p { margin: 0; }
.interaction-columns { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 320px), 1fr)); gap: 20px; }
.interaction-card { width: min(100%, 360px); }
</style>
