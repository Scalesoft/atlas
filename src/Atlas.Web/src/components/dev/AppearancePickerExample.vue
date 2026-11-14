<script setup lang="ts">
import { ref } from "vue";
import AppearanceIconBrowser from "../settings/AppearanceIconBrowser.vue";
import AppearancePicker from "../settings/AppearancePicker.vue";
import SearchField from "../settings/SearchField.vue";
import { Button } from "../ui/button";
import { appearanceIcons, quickAppearanceIconIds } from "../settings/appearanceIcons";
import { iconPackCatalog, iconPackCount } from "../../iconPack";

const icon = ref("server"), color = ref("teal-2");
const narrow = ref(false), disabled = ref(false);
const total = (appearanceIcons.length + iconPackCount).toLocaleString("cs-CZ");
// The icon browser on its own in two states the picker does not open with. They wait for the icon pack, which the
// gallery never loads by itself: opening Další ikony above brings it.
const found = ref("databáze"), nothing = ref("xqz");
</script>

<template>
  <div class="appearance-example" data-appearance-example>
    <p class="st-muted">{{ quickAppearanceIconIds.length }} běžných ikon je dostupných hned. Další ikony ({{ total }}) načtou a odkryjí celou sadu, {{ appearanceIcons.length }} doporučených a balík ikon, s hledáním a kategoriemi; další barvy rozbalíte zvlášť. Zúžení ukazuje chování uvnitř panelu i na širokém monitoru.</p>
    <p class="st-muted">Odkrytá část je prohlížeč ikon (settings/AppearanceIconBrowser.vue): šipky jen posouvají fokus, volí Enter, mezerník nebo klik a Escape vrací do hledání. Krátký dotaz najde stovky ikon: kreslí se nejlepších 200 a tlačítko „Zobrazit všech N“ přidá zbytek. Patří jen do AppearancePicker; dlouhý seznam lidí nebo souborů zůstává SearchableChoiceList.</p>
    <p class="st-muted">Barevná část je samostatná komponenta settings/AppearanceColorPicker.vue (10 rychlých barev, pod „Další barvy a tóny“ celá paleta); sdílí ji i „Pozadí“ komponent na tabuli, které před rychlé barvy přidává Bez pozadí, Bílou a Černou, takže druhý výběr barev nevzniká.</p>
    <div class="st-actions">
      <Button variant="outline" :aria-pressed="narrow" @click="narrow = !narrow">{{ narrow ? 'Plná šířka výběru vzhledu' : 'Zúžit výběr vzhledu na 320 px' }}</Button>
      <Button variant="outline" :aria-pressed="disabled" @click="disabled = !disabled">{{ disabled ? 'Povolit výběr vzhledu' : 'Zakázat výběr vzhledu' }}</Button>
    </div>
    <div class="appearance-example-stage" :style="{ width: narrow ? '320px' : '100%', maxWidth: '100%' }" data-appearance-stage>
      <AppearancePicker v-model:icon="icon" v-model:color="color" :disabled="disabled" name="Provoz" description="Runbooky, pohotovost a infrastruktura" />
      <template v-if="iconPackCatalog">
        <p class="st-muted">Prohlížeč ikon s dotazem: nejlepší shoda první, doporučené ikony před ostatními.</p>
        <AppearanceIconBrowser v-model="icon" v-model:query="found" :color="color" :disabled="disabled" />
        <p class="st-muted">Dotaz, kterému nic neodpovídá.</p>
        <AppearanceIconBrowser v-model="icon" v-model:query="nothing" :color="color" :disabled="disabled" />
      </template>
      <p v-else class="st-muted">Prohlížeč s dotazem a s prázdným výsledkem se tu ukáže po načtení ikon: otevřete Další ikony.</p>
    </div>
    <div class="appearance-example-fields">
      <SearchField label="Nedostupný filtr" model-value="Provoz" disabled />
      <SearchField label="Filtr pouze pro čtení" model-value="Provoz" readonly />
    </div>
  </div>
</template>

<style scoped>
.appearance-example { display: grid; gap: 16px; min-width: 0; }
.appearance-example > p, .appearance-example-stage > p { margin: 0; }
.appearance-example-stage { display: grid; grid-template-columns: minmax(0, 1fr); gap: 16px; }
.appearance-example-fields { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 240px), 1fr)); gap: 12px; }
</style>
