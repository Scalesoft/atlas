<script setup lang="ts">
import { nextTick, onMounted, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import ResultPassage from "../../search/ResultPassage.vue";
import SemanticBadge from "../../search/SemanticBadge.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { createArrivalTracker } from "../../../searchArrival";
import { mergeSearchResults } from "../../../searchMerge";
import { useStoryboard } from "./storyboard";

// Omnisearch rows of hybrid search (plan W2, spec 7.1). Local data, no server. Semantic matching is embeddings, not model
// output: the badge carries the type icon `book-open`, never the AI glyph, and nothing loops. Prototype: "Hledání s odpovědí",
// moment 2 (rows rise 8 px in 200 ms, 30 ms apart, the full pass 180 ms after the prefix pass) and moment 3 (meaning-only
// results slip in below the selected row and glow once). Replay with the buttons of the page ("Zpomalit 3×", "Omezený pohyb").
type Row = { id: string; title: string; context: string; snippet: string; semantic?: boolean; passage?: { text: string; headingTitle: string } };
const query = "zapomenuté heslo";
const lexical: Row = { id: "login", title: "Přihlášení do systému", context: "IT a infrastruktura", snippet: "Zapomenuté heslo obnovíte na přihlašovací stránce." };
const withPassage: Row = { id: "profile", title: "Profil a nastavení účtu", context: "IT a infrastruktura", snippet: "", passage: { text: "Heslo se mění v profilu, v části Zabezpečení.", headingTitle: "Zabezpečení" } };
const meaning: Row = { id: "recovery", title: "Obnovení přístupu", context: "Provoz", snippet: "", semantic: true, passage: { text: "Když ztratíte přístup, pošleme vám odkaz na e-mail.", headingTitle: "Ztráta přístupu" } };
const meaning2: Row = { id: "reset", title: "Reset účtu správcem", context: "Provoz", snippet: "", semantic: true, passage: { text: "Správce účet odemkne a nastaví jednorázový přístup.", headingTitle: "Odemčení" } };

const root = ref<HTMLElement>();
const { sleep } = useStoryboard(root);
const shown = ref<Row[]>([]);
const selectedId = ref("");
const arrivals = createArrivalTracker();
const stage = ref<HTMLElement>();

async function settle(animate: boolean) {
  await nextTick();
  arrivals.settle(shown.value.map((row) => [row.id, stage.value?.querySelector(`[data-row="${row.id}"]`) ?? null] as [string, Element | null]), animate);
}
onMounted(async () => {
  // Moment 2: the prefix pass answers at once, the full pass follows 180 ms later. The person has already selected the second row.
  shown.value = [lexical, withPassage];
  selectedId.value = "profile";
  await settle(false);
  await sleep(180);
  // Moment 3: the meaning-only rows go below the selected row, which does not move.
  shown.value = mergeSearchResults(shown.value, [meaning, lexical, withPassage, meaning2], selectedId.value, (row) => row.id);
  await settle(true);
});
</script>

<template>
  <SettingsSection title="Významová shoda v omnisearch" description="components/search/SemanticBadge.vue a ResultPassage.vue — odznak „Významová shoda“ a pasáž na řádku výsledku hledání; řádek i seznam zůstávají v App.vue. Proč ne existující: ui/badge a StatusBadge nenesou vysvětlení v tooltipu a výsledek hledání nemá jiný způsob, jak ukázat pasáž s nadpisem. Odznak je neutrální a s ikonou typu, ne AI: význam se hledá přes vektory, ne modelem. Volte ho jen u výsledku, jehož stránka neobsahuje přesná slova dotazu; výsledek konceptu a lexikální shoda ho nikdy nemají. Bez schopnosti semanticSearch se nevykreslí vůbec a seznam je beze změny.">
    <div ref="root" class="semantic-example">
      <h4 class="semantic-heading">Příchod významových výsledků pod vybraný řádek (živě)</h4>
      <div ref="stage" class="semantic-stage" role="listbox" aria-label="Výsledky hledání">
        <a v-for="row in shown" :key="row.id" :data-row="row.id" :data-semantic="row.semantic || undefined" href="#/gallery/ai" role="option" :aria-selected="row.id === selectedId" class="search-result" :class="{ active: row.id === selectedId }" @click.prevent="selectedId = row.id">
          <span class="space-icon" data-color="blue"><AtlasIcon name="page" :size="18" /></span>
          <span class="search-result-main"><strong class="search-result-title">{{ row.title }}</strong>
            <small class="search-result-meta"><span class="search-result-context">{{ row.context }}</span><SemanticBadge v-if="row.semantic" /></small>
            <ResultPassage v-if="row.passage" :text="row.passage.text" :heading-title="row.passage.headingTitle" :query="query" />
            <span v-else class="search-snippet">{{ row.snippet }}</span>
          </span><AtlasIcon name="right" :size="16" />
        </a>
        <span class="semantic-note">Vybraný řádek drží pozici, nové výsledky přibudou pod něj. Nový řádek vystoupá o 8 px za 200 ms, významový řádek se navíc jednou rozsvítí; při omezeném pohybu jen prolnutí.</span>
      </div>

      <h4 class="semantic-heading">Řádky</h4>
      <div class="semantic-grid">
        <div class="semantic-cell"><span class="semantic-caption">Lexikální řádek: beze změny</span>
          <a class="search-result" href="#/gallery/ai" @click.prevent><span class="space-icon" data-color="blue"><AtlasIcon name="page" :size="18" /></span><span class="search-result-main"><strong class="search-result-title">{{ lexical.title }}</strong><small class="search-result-meta"><span class="search-result-context">{{ lexical.context }}</span></small><span class="search-snippet">{{ lexical.snippet }}</span></span><AtlasIcon name="right" :size="16" /></a></div>
        <div class="semantic-cell"><span class="semantic-caption">Významová shoda s pasáží (odkaz vede na nadpis)</span>
          <a class="search-result" href="#/gallery/ai" @click.prevent><span class="space-icon" data-color="blue"><AtlasIcon name="page" :size="18" /></span><span class="search-result-main"><strong class="search-result-title">{{ meaning.title }}</strong><small class="search-result-meta"><span class="search-result-context">{{ meaning.context }}</span><SemanticBadge /></small><ResultPassage :text="meaning.passage!.text" :heading-title="meaning.passage!.headingTitle" :query="query" /></span><AtlasIcon name="right" :size="16" /></a></div>
        <div class="semantic-cell"><span class="semantic-caption">Řádek konceptu: nikdy odznak</span>
          <a class="search-result" href="#/gallery/ai" @click.prevent><span class="space-icon" data-color="blue"><AtlasIcon name="page" :size="18" /></span><span class="search-result-main"><strong class="search-result-title">Obnovení přístupu (koncept)</strong><small class="search-result-meta"><span class="search-result-context">Provoz</span><span>Uloženo včera</span></small><span class="search-snippet">Rozpracovaný postup pro ztrátu přístupu.</span></span><AtlasIcon name="right" :size="16" /></a></div>
      </div>

      <h4 class="semantic-heading">Měli jste na mysli (bez AI, i bez schopnosti semanticSearch)</h4>
      <p class="search-did-you-mean">Měli jste na mysli: <button type="button" data-did-you-mean>„heslo“?</button></p>
    </div>
  </SettingsSection>
</template>

<style scoped>
.semantic-example { display: grid; gap: 12px; }
.semantic-heading { margin: 4px 0 0; font-size: 13px; font-weight: 650; }
.semantic-stage, .semantic-cell { display: grid; align-content: start; gap: 6px; min-width: 0; padding: 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--canvas); }
.semantic-stage { max-width: 640px; }
.semantic-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap: 12px; }
.semantic-caption, .semantic-note { color: var(--muted); font-size: 12px; font-weight: 600; line-height: 1.5; }
.semantic-note { font-weight: 400; }
</style>
