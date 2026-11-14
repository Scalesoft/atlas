<script setup lang="ts">
import { ref } from "vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import SettingsSection from "../settings/SettingsSection.vue";
import SettingsRow from "../settings/SettingsRow.vue";
import SettingsList from "../settings/SettingsList.vue";
import SettingsListRow from "../settings/SettingsListRow.vue";
import StatusBadge from "../settings/StatusBadge.vue";
const width = ref("1000");
const value = ref("");
</script>

<template>
  <SettingsSection title="Nastavení v úzkém prostoru" description="Stejné řádky formuláře a seznamu v bočním panelu, na notebooku i ve zvětšeném zobrazení. Dlouhé údaje a akce zůstávají dostupné bez vodorovného posouvání.">
    <label class="responsive-settings-width">Šířka ukázky nastavení
      <select v-model="width" aria-label="Šířka ukázky nastavení"><option value="1000">Široká · 1000 px</option><option value="480">Kompaktní · 480 px</option><option value="280">Úzká · 280 px</option></select>
    </label>
    <div data-responsive-settings :style="{ width: `${width}px` }">
      <SettingsSection title="Napojení dokumentace" icon="network">
        <SettingsRow v-slot="{ id, describedBy, invalid }" label="Identifikátor připojení" help="Dlouhé názvy připojení se zalomí stejně jako nápověda a chyba." error="Připojení documentation-production-eu-central-organization nelze ověřit." origin="Převzato z globálního nastavení organizace">
          <Input :id="id" v-model="value" :aria-describedby="describedBy" :aria-invalid="invalid" placeholder="documentation-production-eu-central" />
        </SettingsRow>
        <SettingsList label="Responzivní ukázka připojení">
          <SettingsListRow title="Produkční dokumentace týmu" description="documentation-production-eu-central-organization" icon="network">
            <template #meta><StatusBadge label="Čeká na ověření správcem" tone="warning" /><StatusBadge label="Sdílené připojení" /></template>
            <template #actions><Button variant="outline" size="sm">Ověřit připojení</Button><Button variant="outline" size="sm">Upravit</Button></template>
          </SettingsListRow>
        </SettingsList>
      </SettingsSection>
    </div>
  </SettingsSection>
</template>

<style scoped>
.responsive-settings-width { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; font-size: 13px; }
.responsive-settings-width select { max-width: 100%; }
[data-responsive-settings] { max-width: 100%; min-width: 0; }
</style>
