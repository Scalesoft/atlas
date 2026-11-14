<script setup lang="ts">
import { nextTick, ref } from "vue";
import type { PlacementSuggestion as Placement, TemplateSuggestion as Template } from "../../../creationSuggestions";
import type { SimilarPage } from "../../../similarPages";
import PlacementSuggestion from "../../creation/PlacementSuggestion.vue";
import TemplateSuggestion from "../../creation/TemplateSuggestion.vue";
import { flyValue } from "../../creation/flyValue";
import SettingsSection from "../../settings/SettingsSection.vue";
import SimilarPagesSuggestion from "../../SimilarPagesSuggestion.vue";
import { Button } from "../../ui/button";

// P1 (meaning matches), P2 (placement) and P3 (template) while a page is being created (plan §20). Local data, no server.
// They are rules over similar pages and a dictionary, not a model: the icons are the type icons `info`, `map-pin` and
// `layout-template`, never the AI glyph. Prototype: "Založení a publikace", moment 4 (Použít: the value flies from the chip
// into the field, 520 ms; under reduced motion it only fades in there). Nothing here waits for anything.
const page = (id: string, extra: Partial<SimilarPage>): SimilarPage => ({ id, spaceId: "it", spaceName: "IT a infrastruktura", title: "", isDraft: false, matchedBy: "title", headingTitle: null, headingId: null, score: 0.5, ...extra });
const lexical = page("vpn", { title: "Připojení k VPN", score: 0.86 });
const meaning = page("remote", { title: "Vzdálený přístup do sítě", matchedBy: "meaning", score: 0.78 });
const meaningOnly = page("home", { title: "Práce z domova", matchedBy: "meaning", score: 0.76, spaceName: "Provoz" });

const placement: Placement = { parentId: "vpn", parentTitle: "VPN", path: [{ id: "net", title: "Síť" }, { id: "vpn", title: "VPN" }] };
const root: Placement = { parentId: null, parentTitle: null, path: [] };
const template: Template = { templateId: "runbook", title: "Provozní postup", matchedBy: "dictionary" };

const location = ref("IT a infrastruktura");
const placementVisible = ref(true);
const placementState = ref<"idle" | "applied">("idle");
const field = ref<HTMLElement>();
async function applyPlacement(from: Element | null) {
  placementState.value = "applied";
  location.value = "IT a infrastruktura / Síť / VPN";
  await nextTick();
  const to = field.value?.querySelector("[data-location-value]");
  if (to) flyValue(from, to, "Síť / VPN");
}
function dismissPlacement() {
  placementVisible.value = false;
  // The parent returns focus to where the person was; the chip never moves it.
  void nextTick(() => field.value?.querySelector<HTMLElement>("[data-example-title]")?.focus());
}
function resetPlacement() {
  location.value = "IT a infrastruktura";
  placementState.value = "idle";
  placementVisible.value = true;
}

const templateVisible = ref(true);
const templateState = ref<"idle" | "applied">("idle");
function resetTemplate() { templateVisible.value = true; templateState.value = "idle"; }
</script>

<template>
  <SettingsSection title="PlacementSuggestion a TemplateSuggestion" description="components/creation/PlacementSuggestion.vue a TemplateSuggestion.vue — návrh umístění a šablony pod polem, kterého se týkají, při zakládání stránky. Proč nové komponenty: obě jsou jen složení SuggestionChip layout=&quot;inline&quot; s ikonou typu (map-pin, layout-template) a textem návrhu, takže nic jiného než popisek a událost Použít v nich není; SuggestionChip sám neví, co je cesta ani šablona. Volte je, když jde o návrh hodnoty pole PageCreationFlow; jiné návrhy (podobné stránky) dál patří do SuggestionChip nebo SimilarPagesSuggestion. Nikdy neblokují krok ani Vytvořit stránku, nevracejí se pro zahozený název a bez schopnosti nebo při chybě se nezobrazí. Žádná ikona AI: umístění a šablona vycházejí z podobnosti a slovníku.">
    <div class="creation-example">
      <h4 class="creation-heading">P1: shoda významem za shodou názvu</h4>
      <div class="creation-stage">
        <SimilarPagesSuggestion :items="[lexical, meaning, meaningOnly]" />
        <span class="creation-note">Řádky s „podobné tématem“ přicházejí od serveru až za lexikálními a jen se schopností; panel i proužek shody jsou stejné jako dnes, ikona je <code>info</code>.</span>
      </div>

      <h4 class="creation-heading">P2: umístění s přeletem hodnoty (moment 4)</h4>
      <div ref="field" class="creation-stage">
        <label class="creation-field">Název stránky<input data-example-title value="Připojení na VPN z mobilu" readonly></label>
        <p class="creation-location"><span>Vybrané umístění</span><span data-location-value>{{ location }}</span></p>
        <PlacementSuggestion v-if="placementVisible" :suggestion="placement" :state="placementState" @apply="applyPlacement" @dismiss="dismissPlacement" />
        <Button v-else type="button" size="xs" variant="outline" class="creation-reset" @click="resetPlacement">Zobrazit znovu (jiný název)</Button>
        <span class="creation-note">Použít nastaví umístění a hodnota přeletí z čipu do pole; Zahodit čip skryje a vrátí fokus do názvu. Pro tentýž název se nevrátí.</span>
      </div>

      <h4 class="creation-heading">P2: kořen prostoru, úzký sloupec, po použití</h4>
      <div class="creation-grid">
        <div class="creation-stage"><span class="creation-caption">Kořen (parentId: null)</span><PlacementSuggestion :suggestion="root" /></div>
        <div class="creation-stage creation-narrow"><span class="creation-caption">Úzký sloupec 280 px</span><PlacementSuggestion :suggestion="placement" /></div>
        <div class="creation-stage"><span class="creation-caption">Po použití</span><PlacementSuggestion :suggestion="placement" state="applied" /></div>
      </div>

      <h4 class="creation-heading">P3: šablona jen u „Prázdná“</h4>
      <div class="creation-stage">
        <TemplateSuggestion v-if="templateVisible" :suggestion="template" :state="templateState" @apply="templateState = 'applied'" @dismiss="templateVisible = false" />
        <Button v-else type="button" size="xs" variant="outline" class="creation-reset" @click="resetTemplate">Zobrazit znovu (jiný název)</Button>
        <span class="creation-note">Ukáže se jen tehdy, když krok 1 nechal „Prázdná“; slovníková část funguje i bez AI. Použít vybere šablonu, jako by ji člověk zvolil v kroku 1.</span>
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.creation-example { display: grid; gap: 12px; }
.creation-heading { margin: 4px 0 0; font-size: 13px; font-weight: 650; }
.creation-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 12px; }
.creation-stage { display: grid; align-content: start; justify-items: start; gap: 8px; min-width: 0; padding: 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--canvas); }
.creation-stage:not(.creation-narrow) { max-width: 560px; }
.creation-narrow { max-width: 280px; }
.creation-field { display: grid; gap: 4px; width: 100%; font-size: 13px; font-weight: 600; }
.creation-field input { padding: 6px 8px; border: 1px solid var(--line); border-radius: 8px; background: var(--surface); color: var(--text); font: inherit; font-weight: 400; }
.creation-location { display: grid; gap: 1px; margin: 0; font-size: 13px; }
.creation-location > span:first-child { color: var(--muted); font-size: 12px; }
.creation-caption { color: var(--muted); font-size: 12px; font-weight: 600; }
.creation-note { color: var(--muted); font-size: 12px; line-height: 1.5; }
</style>
