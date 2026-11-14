<script setup lang="ts">
import { ref } from "vue";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import SettingsList from "../../settings/SettingsList.vue";
import SettingsListRow from "../../settings/SettingsListRow.vue";
import SettingsRow from "../../settings/SettingsRow.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import StatTile from "../../settings/StatTile.vue";
import StatusBadge from "../../settings/StatusBadge.vue";
import "../../settings/settings.css";

// Refresh 2.5, settings (boards 06–10): one grid for every settings card. Local data only.
const name = ref("Care Portal");
const address = ref("");
const enabled = ref(false);
const summaries = ref(true);
</script>

<template>
  <div class="settings-ui settings-grid-example" data-settings-grid-example>
    <SettingsSection title="Nastavení: jedna mřížka karet" description="SettingsSection + SettingsRow + SettingsListRow (components/settings). Řádky sahají k okrajům karty a mají stejný poměr sloupců popisek : ovládací prvek v každé kartě, takže se ovládací prvky zarovnají napříč stránkou. Karta v kartě je pás (bez vlastního rámu). Volitelná ikona řádku je klidná dlaždice před popiskem. Novou komponentu pro řádek nastavení nezakládejte – jiné rozložení řeší stacked, výběry ChoiceCards a OptionPills uvnitř řádku.">
      <SettingsRow v-slot="{ id, describedBy }" label="Název" help="V navigaci a ve výsledcích hledání." icon="pencil">
        <input :id="id" v-model="name" class="st-input" :aria-describedby="describedBy" />
      </SettingsRow>
      <SettingsRow v-slot="{ id, describedBy }" label="Adresa služby" help="Bez ikony: popisek začíná na stejném okraji, ovládací prvek ve stejném sloupci.">
        <input :id="id" v-model="address" class="st-input" placeholder="https://example.cz" :aria-describedby="describedBy" />
      </SettingsRow>
      <SettingsSection title="Pás v kartě" description="Vnořená SettingsSection: řádky pokračují ve stejné mřížce.">
        <SettingsRow v-slot="{ id, describedBy }" label="Shrnutí stránek" help="Přepínač zůstává na začátku sloupce ovládacích prvků.">
          <Switch :id="id" v-model="summaries" :aria-describedby="describedBy" />
        </SettingsRow>
      </SettingsSection>
      <div class="st-card-footer">
        <p class="st-card-footer-text">Patička karty (.st-card-footer): poznámka vlevo, tlačítka karty vpravo.</p>
        <Button variant="outline" size="sm">Ověřit připojení</Button>
      </div>
    </SettingsSection>

    <div class="settings-grid-example-pair">
      <SettingsSection title="Seznam v kartě" icon="users">
        <template #actions><span class="settings-grid-example-count">3 položky</span></template>
        <SettingsList label="Ukázkový seznam">
          <SettingsListRow title="Chat" description="Asistent, odpověď z wiki" icon="quote" icon-tone="neutral">
            <template #meta><StatusBadge tone="success" label="Dostupné" size="sm" /></template>
          </SettingsListRow>
          <SettingsListRow title="Rychlé úlohy" description="Návrhy v editoru" icon="activity" icon-tone="neutral">
            <template #meta><StatusBadge tone="warning" label="Rozpracováno" size="sm" /></template>
          </SettingsListRow>
          <SettingsListRow title="Embeddingy" description="Významové hledání" icon="database" icon-tone="neutral">
            <template #meta><StatusBadge label="Nenastaveno" size="sm" /></template>
          </SettingsListRow>
        </SettingsList>
      </SettingsSection>
      <section class="st-group" aria-labelledby="settings-grid-example-state">
        <h3 id="settings-grid-example-state" class="st-group-label">Stav wiki (.st-group-label)</h3>
        <div class="settings-grid-example-tiles">
          <StatTile label="Lidé" icon="account" value="41" href="#/gallery/refresh">36 čeká na aktivaci</StatTile>
          <StatTile label="Žádosti" icon="user-check" value="Nic nečeká" tone="success">Všechny vyřízené</StatTile>
          <StatTile label="AI" icon="ai" value="Vypnuto" tone="warning">0 ze 3 modelů</StatTile>
          <StatTile label="Zálohy" icon="download" value="Chyba" tone="danger">Poslední selhala</StatTile>
        </div>
        <div class="st-actions">
          <StatusBadge tone="success" label="Aktivní" dot size="sm" />
          <StatusBadge tone="warning" label="Čeká na aktivaci" dot size="sm" />
          <StatusBadge tone="accent" icon="shield-check" label="Správce wiki" size="sm" />
          <label class="st-switch-pill"><Switch v-model="enabled" aria-label="Ukázkový modul" />{{ enabled ? "Zapnuto" : "Vypnuto" }}</label>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.settings-grid-example { display: grid; gap: 18px; container: settings-shell / inline-size; }
.settings-grid-example-pair { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 320px), 1fr)); gap: 18px; align-items: start; }
.settings-grid-example-tiles { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
.settings-grid-example-count { color: var(--muted); font-size: 13px; }
</style>
