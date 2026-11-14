<script setup lang="ts">
import { computed, ref } from "vue";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import FlowProgress from "../../settings/FlowProgress.vue";
import SaveBar from "../../settings/SaveBar.vue";
import SettingsPage from "../../settings/SettingsPage.vue";
import SettingsRow from "../../settings/SettingsRow.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import StatusBadge from "../../settings/StatusBadge.vue";
import ModuleContributions from "../../settings/general/ModuleContributions.vue";
import "../../settings/settings.css";

// Refresh 2.5, settings (board 10): the template of every module page. Local data only, nothing is saved.
const enabled = ref(false);
const saved = ref({ url: "", enabled: false });
const url = ref("");
const dirty = computed(() => url.value !== saved.value.url || enabled.value !== saved.value.enabled);
const steps = computed(() => {
  const done = [!!saved.value.url, saved.value.enabled];
  const next = done.findIndex((value) => !value);
  const state = (index: number) => (done[index] ? "done" : index === next ? "current" : "todo") as "done" | "current" | "todo";
  return [
    { id: "connection", label: "Připojení", detail: done[0] ? "uloženo" : "adresa služby", state: state(0), available: true },
    { id: "enable", label: "Zapnout", detail: done[1] ? "modul běží" : "modul je vypnutý", state: state(1), available: true },
  ];
});
function save() { saved.value = { url: url.value, enabled: enabled.value }; }
function cancel() { url.value = saved.value.url; enabled.value = saved.value.enabled; }
</script>

<template>
  <div class="settings-ui settings-module-example" data-settings-module-example>
    <p class="st-note">Šablona stránky modulu (ModulesPage + formulář modulu): v záhlaví značka modulu v dlaždici (SettingsPage icon), jeho stav a vypínač formuláře (.st-switch-pill, přenesený přes useModuleHeader); pod ním FlowProgress variant="strip" s uloženým stavem, karty s řádky a „Co modul přidá do wiki“ (ModuleContributions). Vypínač patří do konceptu formuláře a ukládá ho SaveBar. FlowProgress jako proužek ukazuje stav, nevede krok za krokem – fáze se Zpět/Další jsou StepFlow, navazující pole ProgressiveFlow.</p>
    <SettingsPage title="Ukázkový modul" icon="box" icon-tone="neutral" description="Úkoly z jiné služby ve výsledcích hledání. Jen ke čtení.">
      <template #actions>
        <StatusBadge :tone="saved.enabled ? 'success' : 'neutral'" :icon="saved.enabled ? 'circle-check' : 'ban'" :label="saved.enabled ? 'Zapnuto' : 'Vypnuto'" />
        <label class="st-switch-pill"><Switch v-model="enabled" aria-label="Používat ukázkový modul" />{{ enabled ? "Zapnuto" : "Vypnuto" }}</label>
      </template>
      <FlowProgress variant="strip" :items="steps" label="Stav ukázkového modulu" />
      <SettingsSection title="Připojení" icon="plug">
        <SettingsRow v-slot="{ id, describedBy }" label="Adresa služby" help="Kořenová adresa instance.">
          <input :id="id" v-model="url" class="st-input" placeholder="https://sluzba.example.cz" :aria-describedby="describedBy" />
        </SettingsRow>
        <div class="st-card-footer">
          <p class="st-card-footer-text">Test ověří uložené spojení.</p>
          <Button variant="outline" size="sm" :disabled="dirty || !saved.url">Otestovat uložené spojení</Button>
        </div>
      </SettingsSection>
      <ModuleContributions :items="[{ kind: 'Hledání', label: 'Úkoly ve výsledcích' }, { kind: 'Stránka', label: 'Odkaz na úkol s jeho stavem' }]" />
      <SaveBar :dirty="dirty" save-label="Uložit modul" @save="save" @cancel="cancel" />
    </SettingsPage>
  </div>
</template>

<style scoped>
.settings-module-example { display: grid; gap: 14px; container: settings-shell / inline-size; }
</style>
