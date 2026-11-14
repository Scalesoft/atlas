<script setup lang="ts">
import { ref } from "vue";
import AiCitation from "../../ai/AiCitation.vue";
import AiSourceList from "../../ai/AiSourceList.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { source } from "./aiSampleData";

// AiSourceList and AiCitation (plan phase 3 W8): the sources of an answer as a tray, as a list, and as the inline [n]. Local data.
// The sources carry no AI glyph: they are links to pages (type icon of the page), not model output.
const tray = [source(1, { title: "Připojení k VPN", headingTitle: "Z domova" }), source(2, { title: "Certifikáty zařízení", headingTitle: null }), source(3, { title: "Práce z domova" })];
const list = [tray[0]!, source(2, { title: "Certifikáty zařízení", headingTitle: null, cited: false }), source(3, { title: "Práce z domova", changed: true })];
const changed = source(3, { title: "Práce z domova", headingTitle: "Bezpečnost", changed: true });
const highlighted = ref<number | null>(null);
const last = ref("");
</script>

<template>
  <SettingsSection title="AiSourceList a AiCitation" description="components/ai/AiSourceList.vue a AiCitation.vue — zásobník zdrojů (tray), seznam (list) a inline [n] s náhledem v popoveru. Proč ne výsledkový řádek omnisearch: ten je vázaný na App.vue a jeho klávesnici a neumí inline formu ani stav „Zdroj se od té doby změnil“. Citace se kreslí jen z pole sources; číslo v textu věty se nikdy nečte. Odkaz vede na aktuální stránku u nadpisu, nikdy na historickou revizi; Ctrl nebo Cmd + klik otevře novou kartu. Volte tray pod odpovědí, list v „Podklady odpovědi“.">
    <div class="aiw-example">
      <h4 class="aiw-heading">Zásobník (tray): najetí propojí zdroj s citací</h4>
      <div class="aiw-box">
        <AiSourceList :sources="tray" :highlighted="highlighted" @highlight="highlighted = $event" />
        <p class="aiw-sentence">Připojíte se klientem <code>FortiClient</code>.<AiCitation :source="tray[0]!" :highlighted="highlighted === 1" @highlight="highlighted = $event" />
          Notebook potřebuje certifikát.<AiCitation :source="tray[1]!" :highlighted="highlighted === 2" @highlight="highlighted = $event" /></p>
        <span class="aiw-note">Zvýrazněné číslo: {{ highlighted ?? "žádné" }}. Klik na [n] otevře náhled s pasáží a odkazem na stránku.</span>
      </div>

      <h4 class="aiw-heading">Seznam (list): použité podklady i necitované, změněný zdroj</h4>
      <div class="aiw-box"><AiSourceList :sources="list" variant="list" @ask-again="last = 'Zeptat se znovu'" /><span v-if="last" class="aiw-note" role="status">{{ last }}</span></div>

      <h4 class="aiw-heading">Citace změněného zdroje</h4>
      <div class="aiw-box"><p class="aiw-sentence">Veřejné Wi-Fi používejte jen přes VPN.<AiCitation :source="changed" @ask-again="last = 'Zeptat se znovu'" /></p></div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.aiw-example { display: grid; gap: 12px; }
.aiw-heading { margin: 4px 0 0; font-size: 13px; font-weight: 650; }
.aiw-box { display: grid; gap: 8px; max-width: 520px; padding: 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--canvas); }
.aiw-sentence { margin: 0; font-size: 14px; line-height: 1.55; }
.aiw-note { color: var(--muted); font-size: 12px; }
</style>
