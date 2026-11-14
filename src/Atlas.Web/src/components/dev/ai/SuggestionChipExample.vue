<script setup lang="ts">
import { nextTick, onMounted, ref } from "vue";
import FormField from "../../FormField.vue";
import { Button } from "../../ui/button";
import { Input } from "../../ui/input";
import SettingsSection from "../../settings/SettingsSection.vue";
import SuggestionChip, { type SuggestionState } from "../../settings/SuggestionChip.vue";
import { glow, meterFill } from "../../../motion/aiMotion";
import { useStoryboard } from "./storyboard";

// SuggestionChip in every state, both layouts, light and dark and in a narrow column. The flow
// below is what a parent does: apply runs the work (solidify), the value lands in the field and
// glows; dismiss removes the chip and the parent puts focus back into the field.
const root = ref<HTMLElement>();
const { sleep } = useStoryboard(root);

const location = ref("IT a infrastruktura");
const flowState = ref<SuggestionState>("idle");
const flowVisible = ref(true);
const locationField = ref<HTMLElement>();
async function applyLocation() {
  flowState.value = "applying";
  await sleep(900);
  flowState.value = "applied";
  location.value = "IT a infrastruktura › Síť › VPN";
  await nextTick();
  glow(locationField.value!.querySelector("input")!);
}
async function dismissLocation() {
  flowVisible.value = false;
  // The parent returns focus to where the person was; the chip never moves it.
  await nextTick();
  locationField.value?.querySelector("input")?.focus();
}
function resetFlow() {
  location.value = "IT a infrastruktura";
  flowState.value = "idle";
  flowVisible.value = true;
}

const dismissedVisible = ref(true);
const panel = ref<HTMLElement>();
onMounted(() => {
  // Prototype: Založení a publikace · moment 3, the match meters fill one after another.
  if (panel.value) meterFill(panel.value.querySelectorAll(".ai-sc-meter > i"));
});
const similar = [
  { title: "Připojení k VPN", path: "IT a infrastruktura › Síť · sekce Z mobilu", match: 86, actions: ["Otevřít sekci"] },
  { title: "VPN pro dodavatele", path: "Provoz", match: 48, actions: ["Otevřít"] },
];
const modes = [{ mode: "light", label: "Světlý" }, { mode: "dark", label: "Tmavý" }] as const;
</script>

<template>
  <SettingsSection title="SuggestionChip" description="components/settings/SuggestionChip.vue — návrh hodnoty, která ještě není uložená: Použít, Zahodit a Důvod návrhu, přerušovaný obrys, spojení čárek při spuštění a dosvit po přijetí. Proč nová komponenta: ui/badge je statický štítek, StatusBadge stav, ResultBanner výsledek hotové akce, ChoiceCards a OptionPills výběr z možností a SettingsListRow řádek bez významu návrhu. V textu stránky se nepoužívá: tam návrh nese značka u bloku z modulu editoru assist. Ikona je ikona typu návrhu; ikonu AI dostane přes slot glyph jen návrh od modelu.">
    <div ref="root" class="ai-chip-example">
      <h4 class="ai-sc-heading">Stavy</h4>
      <div class="ai-sc-grid">
        <div class="ai-sc-cell">
          <span class="ai-sc-caption">Nový (idle)</span>
          <SuggestionChip label="Návrh umístění" icon="map-pin">IT a infrastruktura › Síť › VPN</SuggestionChip>
        </div>
        <div class="ai-sc-cell">
          <span class="ai-sc-caption">Fokus</span>
          <SuggestionChip label="Návrh umístění" icon="map-pin" reason="Podobné stránky o VPN jsou v sekci Síť.">IT a infrastruktura › Síť › VPN</SuggestionChip>
          <span class="ai-sc-note">Klávesou Tab: Použít → Důvod návrhu → Zahodit. Když se návrh objeví, fokus zůstává, kde byl, a čtečka ho neohlašuje.</span>
        </div>
        <div class="ai-sc-cell">
          <span class="ai-sc-caption">Spouští se (applying)</span>
          <SuggestionChip label="Návrh umístění" icon="map-pin" state="applying">IT a infrastruktura › Síť › VPN</SuggestionChip>
        </div>
        <div class="ai-sc-cell">
          <span class="ai-sc-caption">Použito (applied)</span>
          <SuggestionChip label="Návrh umístění" icon="map-pin" state="applied">IT a infrastruktura › Síť › VPN</SuggestionChip>
        </div>
        <div class="ai-sc-cell">
          <span class="ai-sc-caption">Neaktuální (stale)</span>
          <SuggestionChip label="Návrh odkazu" icon="link" state="stale">Certifikáty zařízení</SuggestionChip>
        </div>
        <div class="ai-sc-cell">
          <span class="ai-sc-caption">Zahozený</span>
          <SuggestionChip v-if="dismissedVisible" label="Návrh umístění" icon="map-pin" @dismiss="dismissedVisible = false">IT a infrastruktura › Síť › VPN</SuggestionChip>
          <span v-else class="ai-sc-note">Zahozeno: návrh zmizel bez stopy a v relaci se nevrátí. <Button variant="link" size="xs" class="ai-sc-inline-button" @click="dismissedVisible = true">Vrátit ukázku</Button></span>
        </div>
      </div>

      <h4 class="ai-sc-heading">Průběh v poli</h4>
      <div class="ai-sc-flow">
        <div ref="locationField"><FormField label="Umístění"><Input :model-value="location" readonly /></FormField></div>
        <SuggestionChip
          v-if="flowVisible"
          label="Návrh umístění"
          icon="map-pin"
          reason="Podobné stránky o VPN jsou v sekci Síť."
          :state="flowState"
          @apply="applyLocation"
          @dismiss="dismissLocation"
        >IT a infrastruktura › Síť › VPN</SuggestionChip>
        <span class="ai-sc-note">Použít: obrys se spojí (360 ms), hodnota přejde do pole a pole i návrh dosvítí. Zahodit: návrh zmizí (160 ms) a rodič vrátí fokus do pole Umístění.</span>
        <Button variant="outline" size="sm" class="ai-sc-reset" @click="resetFlow">Začít znovu</Button>
      </div>

      <h4 class="ai-sc-heading">Varianty</h4>
      <div class="ai-sc-grid is-wide">
        <div ref="panel" class="ai-sc-cell">
          <span class="ai-sc-caption">Panel, informační (applyLabel: null) · P1 duplicity</span>
          <SuggestionChip layout="panel" icon="info" label="Podobné stránky už existují. Pokračovat můžete i tak." :apply-label="null" dismiss-label="Skrýt">
            <div v-for="page in similar" :key="page.title" class="ai-sc-row">
              <span><strong>{{ page.title }}</strong><small>{{ page.path }}</small><span class="ai-sc-meter" role="img" :aria-label="`Shoda ${page.match} %`"><i :style="{ width: `${page.match}%` }" /></span></span>
              <span class="ai-sc-row-actions"><Button v-for="action in page.actions" :key="action" size="xs" variant="outline">{{ action }}</Button></span>
            </div>
          </SuggestionChip>
        </div>
        <div class="ai-sc-cell">
          <span class="ai-sc-caption">Řádek s důvodem a vlastní akcí (slot actions)</span>
          <SuggestionChip label="Návrh odkazu" icon="link" reason="Stránka zmiňuje certifikáty zařízení třikrát; stránka Certifikáty zařízení na ni zatím neodkazuje.">
            Certifikáty zařízení
            <template #actions><Button size="xs" variant="ghost">Otevřít</Button></template>
          </SuggestionChip>
        </div>
      </div>

      <h4 class="ai-sc-heading">Zobrazení</h4>
      <div class="ai-sc-grid is-wide">
        <div v-for="item in modes" :key="item.mode" class="atlas ai-sc-surface" data-accent="blue" :data-mode="item.mode" :aria-label="`${item.label} režim`">
          <span class="ai-sc-caption">{{ item.label }} režim</span>
          <SuggestionChip label="Návrh umístění" icon="map-pin" reason="Podobné stránky o VPN jsou v sekci Síť.">IT a infrastruktura › Síť › VPN</SuggestionChip>
          <SuggestionChip label="Návrh odkazu" icon="link" state="stale">Certifikáty zařízení</SuggestionChip>
        </div>
        <div class="ai-sc-cell ai-sc-narrow">
          <span class="ai-sc-caption">Úzký sloupec 320 px</span>
          <SuggestionChip label="Návrh umístění" icon="map-pin" reason="Podobné stránky o VPN jsou v sekci Síť.">IT a infrastruktura › Síť › VPN pro vzdálený přístup</SuggestionChip>
          <SuggestionChip layout="panel" icon="info" label="Podobné stránky už existují." :apply-label="null" dismiss-label="Skrýt"><div class="ai-sc-row"><span><strong>Připojení k VPN</strong><small>IT a infrastruktura › Síť</small></span></div></SuggestionChip>
        </div>
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.ai-chip-example { display: grid; gap: 12px; }
.ai-sc-heading { margin: 4px 0 0; font-size: 13px; font-weight: 650; }
.ai-sc-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 12px; }
.ai-sc-grid.is-wide { grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); }
.ai-sc-cell, .ai-sc-surface { display: grid; align-content: start; justify-items: start; gap: 8px; min-width: 0; padding: 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--canvas); }
/* A nested .atlas carries the mode's tokens; it must not take the app shell's full-height flex box. */
.ai-sc-grid > .atlas.ai-sc-surface { display: grid; min-height: 0; color: var(--text); }
.ai-sc-cell > :deep(.suggestion-chip[data-layout="panel"]) { justify-self: stretch; }
.ai-sc-narrow { max-width: 320px; }
.ai-sc-narrow > :deep(.suggestion-chip[data-layout="panel"]) { justify-self: stretch; }
.ai-sc-caption { color: var(--muted); font-size: 12px; font-weight: 600; }
.ai-sc-note { color: var(--muted); font-size: 12px; line-height: 1.5; }
.ai-sc-inline-button { height: auto; padding: 0; }
.ai-sc-flow { display: grid; justify-items: start; gap: 8px; max-width: 560px; padding: 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); }
.ai-sc-flow > div:first-child { justify-self: stretch; }
.ai-sc-reset { justify-self: end; }
.ai-sc-row { display: grid; grid-template-columns: 1fr auto; gap: 2px 10px; align-items: center; padding: 7px 0; border-bottom: 1px solid var(--line); }
.ai-sc-row:last-of-type { border-bottom: 0; }
.ai-sc-row strong { display: block; font-size: 13px; }
.ai-sc-row small { display: block; color: var(--muted); font-size: 12px; }
.ai-sc-row-actions { display: flex; gap: 6px; }
.ai-sc-meter { display: block; width: 120px; height: 4px; margin-top: 3px; overflow: hidden; border-radius: 99px; background: var(--hover); }
.ai-sc-meter > i { display: block; height: 100%; border-radius: inherit; background: var(--accent); }
</style>
