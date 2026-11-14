<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useSimilarPages } from "../../../composables/useSimilarPages";
import type { SimilarPage, SimilarPagesApi } from "../../../similarPages";
import FormField from "../../FormField.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import SimilarPagesSuggestion from "../../SimilarPagesSuggestion.vue";
import { Input } from "../../ui/input";
import { useStoryboard } from "./storyboard";

// P1 "Podobné stránky už existují:" while a page is being created (plan §8). Local data, no server.
// It is a rule (title, alias and heading similarity), not a model: the icon is the type icon `info`, never the AI glyph,
// and nothing loops. Prototype: "Založení a publikace", moments 1 to 3 (wait 400 ms after the last character, the panel
// unfolds in 280 ms, the meters fill 400 ms each, 80 ms apart). The first block runs the real composable with a local
// lookup, so the debounce is live: type into the field yourself and the panel follows.
const root = ref<HTMLElement>();
const { sleep } = useStoryboard(root);

const page = (id: string, extra: Partial<SimilarPage>): SimilarPage => ({ id, spaceId: "it", spaceName: "IT a infrastruktura", title: "", isDraft: false, matchedBy: "title", headingTitle: null, headingId: null, score: 0.5, ...extra });
const byTitle = page("vpn", { title: "Připojení k VPN", score: 0.86 });
const bySection = page("vpn-mobil", { title: "Připojení k VPN", matchedBy: "heading", headingTitle: "Z mobilu", headingId: "z-mobilu", score: 0.86 });
const draft = page("vpn-dodavatele", { title: "VPN pro dodavatele", spaceId: "ops", spaceName: "Provoz", isDraft: true, score: 0.48 });
const alias = page("vzdaleny-pristup", { title: "Vzdálený přístup", matchedBy: "alias", score: 0.41 });

const sample = "Připojení na VPN z mobilu";
const title = ref("");
const typing = ref(true);
const local: SimilarPagesApi = { find: async (value) => (/vpn/i.test(value) ? [bySection, draft, alias] : []) };
const { items, dismiss } = useSimilarPages({ title, spaceId: ref("it"), enabled: ref(true), api: local });
const field = ref<HTMLElement>();
const panel = ref<{ focusFirst: () => void }>();
const created = ref(false);
const toTitle = () => field.value?.querySelector("input")?.focus();

onMounted(async () => {
  // Prototype moment 1: typing; the lookup waits 400 ms after the last character.
  await sleep(500);
  for (const char of sample) {
    if (!typing.value) return;
    title.value += char;
    await sleep(50);
  }
  typing.value = false;
});
function dismissed() {
  dismiss();
  // The panel never took focus; the parent returns it to the title, where the person was.
  field.value?.querySelector("input")?.focus();
}
</script>

<template>
  <SettingsSection title="SimilarPagesSuggestion" description="components/SimilarPagesSuggestion.vue — podobné stránky pod názvem zakládané stránky (krok „Název a umístění“). Proč ne existující: je to složení SuggestionChip layout=&quot;panel&quot; (bez Použít) a seznamu řádků, nic jiného než řádky, proužek shody a odkazy v něm není nové; ui/badge, ResultBanner ani SettingsListRow nenesou význam nezávazného návrhu, který lze skrýt. Výsledky hledání (omnisearch) jsou samostatný pohled a nezakládají nic. Volte ji, když se při psaní hodnoty ukazují existující záznamy, které mohou být totéž; nikdy neblokuje a při chybě se nezobrazí. Žádná ikona AI: podobnost není model.">
    <div ref="root" class="similar-example">
      <h4 class="similar-heading">Při psaní názvu (živé, 400 ms po posledním znaku)</h4>
      <div class="similar-stage">
        <div ref="field"><FormField label="Název stránky"><Input v-model="title" name="similar-title" autocomplete="off" @keydown="typing = false" @keydown.down.prevent="panel?.focusFirst()" /></FormField></div>
        <SimilarPagesSuggestion v-if="items.length" ref="panel" :items="items" @dismiss="dismissed" @back="toTitle" @create="created = true" />
        <span v-if="created" class="similar-note" data-similar-created>Ukázka: „Přesto založit“ by stránku založil.</span>
        <span class="similar-note">Od 8 znaků a 400 ms po posledním znaku. Skrýt panel skryje pro tento název a vrátí fokus do pole; kratší název ho zruší. Tlačítko „Vytvořit stránku“ nikdy nečeká. Název napsaný znovu odpoví hned z paměti toku, bez čekání.</span>
        <span class="similar-note" data-similar-rules><strong>Ovládání (P3, P8, P15):</strong> ↓ v názvu přejde na první podobnou stránku, ↑ ↓ mezi stránkami a „Přesto založit“, Enter na stránce ji otevře místo založení duplicity, Esc nebo ↑ z první vrátí do názvu (tok nezruší), „Přesto založit“ stránku založí. Panel si fokus sám nebere.</span>
      </div>

      <h4 class="similar-heading">Shoda názvem, sekcí a konceptem</h4>
      <div class="similar-grid">
        <div class="similar-cell">
          <span class="similar-caption">Shoda názvem</span>
          <SimilarPagesSuggestion :items="[byTitle, alias]" />
        </div>
        <div class="similar-cell">
          <span class="similar-caption">Shoda sekcí: Otevřít sekci vede na nadpis</span>
          <SimilarPagesSuggestion :items="[bySection]" />
        </div>
        <div class="similar-cell">
          <span class="similar-caption">Koncept a shoda jiným názvem</span>
          <SimilarPagesSuggestion :items="[draft, alias]" />
        </div>
        <div class="similar-cell">
          <span class="similar-caption">Bez skóre: proužek se nevykreslí</span>
          <SimilarPagesSuggestion :items="[{ ...byTitle, score: Number.NaN }]" />
        </div>
        <div class="similar-cell similar-narrow">
          <span class="similar-caption">Úzký sloupec 320 px</span>
          <SimilarPagesSuggestion :items="[bySection, draft]" />
        </div>
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.similar-example { display: grid; gap: 12px; }
.similar-heading { margin: 4px 0 0; font-size: 13px; font-weight: 650; }
.similar-stage, .similar-cell { display: grid; align-content: start; gap: 8px; min-width: 0; padding: 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--canvas); }
.similar-stage { max-width: 560px; }
.similar-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap: 12px; }
.similar-narrow { max-width: 320px; }
.similar-caption { color: var(--muted); font-size: 12px; font-weight: 600; }
.similar-note { color: var(--muted); font-size: 12px; line-height: 1.5; }
</style>
