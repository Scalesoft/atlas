<script setup lang="ts">
import AiFeedback from "../../ai/AiFeedback.vue";
import AiProvenance from "../../ai/AiProvenance.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { provenance, source } from "./aiSampleData";

// AiProvenance (plan phase 3 W8): the line of origin under model output. Local data.
const used = [source(1, { title: "Připojení k VPN" }), source(2, { title: "Certifikáty zařízení", cited: false }), source(3, { title: "Práce z domova" }), source(4, { title: "Service Desk" })];
const minutesAgo = new Date(Date.now() - 2 * 60_000);
const twoDaysAgo = new Date(Date.now() - 48 * 3600_000);
</script>

<template>
  <SettingsSection title="AiProvenance" description="components/ai/AiProvenance.vue — řádka původu pod každým výstupem modelu: Firemní model · 4 zdroje · před 2 min · Podklady odpovědi Je to samostatná malá komponenta, aby text byl všude stejný (odpověď, shrnutí, administrace). V běžném jazyce jen Firemní nebo Cloudový model; poskytovatel a model jsou v tooltipu. Počet zdrojů i Podklady odpovědi otevírají jeden popover s rozsahem, větou, že odpověď používá jen publikované stránky, a všemi podklady, i necitovanými. Čas je relativní do 24 hodin, pak absolutní. Řádka nese AiGlyph, protože popisuje výstup modelu; pod pravidlový návrh ji nedávejte. Slot trailing nese palce.">
    <div class="aip-example">
      <div class="aip-box" data-aip-company><span class="aip-caption">Firemní model, před 2 minutami, s palci</span>
        <AiProvenance :provenance="provenance" :sources="used" :created-at="minutesAgo" :scope="{ kind: 'space' }" scope-name="IT a infrastruktura">
          <template #trailing><AiFeedback /></template>
        </AiProvenance>
      </div>
      <div class="aip-box"><span class="aip-caption">Cloudový model, starší než 24 hodin (absolutní čas)</span>
        <AiProvenance :provenance="{ location: 'cloud', provider: 'Anthropic', model: 'claude' }" :sources="used.slice(0, 1)" :created-at="twoDaysAgo" :scope="{ kind: 'wiki' }" />
      </div>
      <div class="aip-box"><span class="aip-caption">Právě napsaná odpověď na stránce</span>
        <AiProvenance :provenance="provenance" :sources="used.slice(0, 3)" :scope="{ kind: 'page', pageId: 'p' }" />
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.aip-example { display: grid; gap: 12px; max-width: 560px; }
.aip-box { display: grid; gap: 2px; padding: 10px 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--canvas); }
.aip-caption { color: var(--muted); font-size: 12px; font-weight: 600; }
</style>
