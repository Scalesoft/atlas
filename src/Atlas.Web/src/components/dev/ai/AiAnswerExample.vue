<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import type { AiProvenanceView, AiSentenceView, AiSourceView } from "../../../ai/assistantApi";
import AiAnswer, { type AiAnswerState } from "../../ai/AiAnswer.vue";
import AiGlyph from "../../ai/AiGlyph.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { Button } from "../../ui/button";
import { provenance, sentences, sources } from "./aiSampleData";
import { useStoryboard } from "./storyboard";

// AiAnswer (plan phase 3 W8). Local data, no server. The scene is the prototype's "Hledání s odpovědí", moments 4 to 9: the offer
// unfolds, starting joins the outline, the sources fly in from the result rows (260 ms apart), "Připravuji odpověď…" loops once,
// the finished text is revealed sentence by sentence, and hovering a citation lights its sentence, source and result row.
// Replay with the buttons of the page ("Zpomalit 3×", "Omezený pohyb", "Přehrát znovu").
const root = ref<HTMLElement>();
const stage = ref<HTMLElement>();
const { sleep } = useStoryboard(root);

const scene = reactive<{ state: AiAnswerState; sources: AiSourceView[]; sentences: AiSentenceView[]; provenance: AiProvenanceView | null }>({ state: "offer", sources: [], sentences: [], provenance: null });
const highlightedRow = ref<number | null>(null);
const results = sources.map((source, index) => ({ number: source.number, title: ["Připojení k VPN", "Certifikáty zařízení", "Práce z domova"][index]!, context: source.spaceName }));
const origin = (number: number) => stage.value?.querySelector(`[data-result="${number}"] .aia-title`) ?? null;

onMounted(async () => {
  await sleep(1100); // moment 4: the offer card has unfolded
  scene.state = "retrieving"; // moment 5: the dashes join
  await sleep(900);
  scene.sources = sources; // moment 6: the sources fly in
  await sleep(2 * 260 + 520 + 200 + 560);
  scene.state = "preparing"; // moment 7: the one loop
  await sleep(1700);
  scene.sentences = sentences; // moment 8: the answer is whole, then revealed by sentence
  scene.provenance = provenance;
  scene.state = "done";
});

// The matrix: every state of the shared model, in both variants.
const states: { state: AiAnswerState; caption: string; filled?: boolean }[] = [
  { state: "offer", caption: "Nabídka (návrh, přerušovaný obrys)" },
  { state: "retrieving", caption: "Hledám podklady" },
  { state: "preparing", caption: "Připravuji odpověď", filled: false },
  { state: "done", caption: "Hotovo", filled: true },
  { state: "no_sources", caption: "Bez podkladů" },
  { state: "unavailable", caption: "Nedostupné (jen u spuštěné akce)" },
  { state: "error", caption: "Chyba" },
  { state: "limit", caption: "Limit" },
  { state: "withheld", caption: "Odvolané (ztráta přístupu)" },
];
const surfaces = [{ mode: "light", label: "Světlý režim" }, { mode: "dark", label: "Tmavý režim" }] as const;
const dataFor = (state: AiAnswerState) => ({
  sources: ["done", "preparing"].includes(state) ? sources : [],
  sentences: state === "done" ? sentences : [],
  provenance: state === "done" ? provenance : null,
});
</script>

<template>
  <SettingsSection title="AiAnswer" description="components/ai/AiAnswer.vue (+ AiSourceList, AiCitation, AiFeedback, AiProvenance) — výstup modelu s citacemi: věty, číslované zdroje, stavový model §5.3, řádka původu, palce a upozornění. Varianta compact je karta odpovědi v omnisearch, full zpráva asistenta. Proč nové: ResultBanner je výsledek hotové akce, EmptyState a ErrorState jen jeden stav; nic jiného neumí citace se stavy AI, a tak místo tří podobných karet je jedna. Proč tady AiGlyph je a u Významové shody ne: odpověď je text napsaný modelem a tlačítko Zeptat se asistenta spouští modelovou úlohu; Významová shoda je výsledek vektorů, ne modelu, a má ikonu typu. Glyf proto nikdy nezobrazí Nedostupné, Chyba, Limit, Odvolané ani Bez podkladů. Bez podkladů přijme slot retry s dalšími pokusy před „Zkusit jiný dotaz“ (asistent: Hledat v celé wiki, Hledat do hloubky / naplno). Volte ji vždy, když se pod výstup modelu patří zdroje; pravidlový návrh bez zdrojů je SuggestionChip. Komponenta nevolá server: stav jí předává rodič (ai/answerTurn.ts).">
    <div ref="root" class="aia-example">
      <h4 class="aia-heading">Scéna Hledání s odpovědí: moment 4 až 9 (živě)</h4>
      <div ref="stage" class="aia-stage" data-ai-scene>
        <ul class="aia-results" aria-label="Výsledky hledání">
          <li v-for="row in results" :key="row.number" :data-result="row.number" class="aia-result" :class="{ 'is-hl': highlightedRow === row.number }">
            <strong class="aia-title">{{ row.title }}</strong><small>{{ row.context }}</small>
          </li>
        </ul>
        <AiAnswer
          :state="scene.state"
          :sources="scene.sources"
          :sentences="scene.sentences"
          :provenance="scene.provenance"
          :source-origin="origin"
          @highlight="highlightedRow = $event"
        >
          <template #offer>
            Vypadá to na otázku.
            <Button type="button" size="sm" class="aia-ask"><AiGlyph :size="14" />Zeptat se asistenta</Button>
            <span><kbd>Ctrl</kbd> + <kbd>Enter</kbd></span>
          </template>
          <template #actions><Button type="button" size="sm" variant="outline">Pokračovat v asistentovi</Button></template>
        </AiAnswer>
        <span class="aia-note">Zdroje přeletí z řádků výsledků do karty (520 ms po oblouku, odstup 260 ms), věty se odhalí z rozostření (420 ms, odstup 130 ms) až nad hotovým textem. Najetím na [2] se rozsvítí věta, zdroj i řádek výsledku (120 ms). Při omezeném pohybu zdroje jen vyskočí s prolnutím a věty se prolnou.</span>
      </div>

      <template v-for="surface in surfaces" :key="surface.mode">
        <h4 class="aia-heading">Všechny stavy, varianta compact · {{ surface.label.toLowerCase() }}</h4>
        <div class="atlas aia-surface" data-accent="blue" :data-mode="surface.mode" :aria-label="surface.label">
          <div class="aia-grid">
            <div v-for="item in states" :key="item.state" class="aia-cell" :data-aia-state="item.state">
              <span class="aia-caption">{{ item.caption }}</span>
              <AiAnswer :state="item.state" v-bind="dataFor(item.state)" :heading-level="4">
                <template v-if="item.state === 'offer'" #offer>Vypadá to na otázku. <Button type="button" size="sm" class="aia-ask"><AiGlyph :size="14" />Zeptat se asistenta</Button></template>
              </AiAnswer>
            </div>
          </div>
        </div>
      </template>

      <h4 class="aia-heading">Varianta full (zpráva asistenta) a úzký sloupec 360 px</h4>
      <div class="aia-narrow" data-ai-narrow>
        <div class="aia-cell"><span class="aia-caption">full · hotovo</span><AiAnswer state="done" variant="full" v-bind="dataFor('done')" :heading-level="4" title="Odpověď asistenta" /></div>
        <div class="aia-cell"><span class="aia-caption">full · připravuji</span><AiAnswer state="preparing" variant="full" v-bind="dataFor('preparing')" :heading-level="4" /></div>
        <div class="aia-cell"><span class="aia-caption">compact · hotovo</span><AiAnswer state="done" v-bind="dataFor('done')" :heading-level="4" /></div>
        <div class="aia-cell" data-aia-retry><span class="aia-caption">full · bez podkladů v asistentovi (slot retry: celá wiki, hlouběji; tlačítka se vysunou postupně)</span>
          <AiAnswer state="no_sources" variant="full" v-bind="dataFor('no_sources')" :heading-level="4">
            <template #retry><Button type="button" size="sm">Hledat v celé wiki</Button><Button type="button" variant="outline" size="sm">Hledat do hloubky</Button></template>
          </AiAnswer>
        </div>
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.aia-example { display: grid; gap: 12px; }
.aia-heading { margin: 4px 0 0; font-size: 13px; font-weight: 650; }
.aia-stage, .aia-cell { display: grid; align-content: start; gap: 8px; min-width: 0; padding: 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--canvas); }
.aia-stage { max-width: 640px; }
.aia-results { display: grid; gap: 4px; margin: 0; padding: 0; list-style: none; }
.aia-result { display: grid; gap: 1px; padding: 6px 10px; border-radius: 8px; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.aia-result small { color: var(--muted); font-size: 12px; }
.aia-result.is-hl { background: var(--accent-soft); }
.aia-title { font-size: 13px; }
.aia-note, .aia-caption { color: var(--muted); font-size: 12px; font-weight: 600; line-height: 1.5; }
.aia-note { font-weight: 400; }
/* A nested .atlas carries the mode's tokens; it must not take the app shell's full-height flex box. */
.aia-surface { display: block; min-height: 0; padding: 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--canvas); color: var(--text); }
.aia-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 12px; }
/* The glyph is accent coloured; on the filled button it takes the button's text colour. */
.aia-ask :deep(.ai-glyph) { color: inherit; }
.aia-narrow { display: grid; gap: 12px; max-width: 360px; }
kbd { padding: 0 4px; border: 1px solid var(--line); border-radius: 4px; font-size: 11px; }
</style>
