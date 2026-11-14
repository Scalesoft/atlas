<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from "vue";
import { answerTurnStatus } from "../../../ai/aiStatus";
import AtlasIcon from "../../AtlasIcon.vue";
import AiAnswer from "../../ai/AiAnswer.vue";
import AiStatus from "../../ai/AiStatus.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { Button } from "../../ui/button";
import { provenance, sentences, sources } from "./aiSampleData";
import { useStoryboard } from "./storyboard";

// Board „AI znak – velikosti, stavy a průběh“, row „Jeden stavový řádek všude, kde AI pracuje“: the same AiStatus in its
// three sizes, driven by the two real phases of an answer turn (retrieving → preparing → done). Local data only.
const root = ref<HTMLElement>();
const { sleep } = useStoryboard(root);
const phase = ref<"retrieving" | "preparing" | "done">("retrieving");
const stopped = ref(false);
let run = 0;
async function play() {
  const mine = ++run;
  stopped.value = false;
  phase.value = "retrieving";
  await sleep(1800); if (mine !== run) return;
  phase.value = "preparing";
  await sleep(3200); if (mine !== run) return;
  phase.value = "done";
}
function stop() { run++; stopped.value = true; }
void play();
onBeforeUnmount(() => { run++; });
const answerState = computed(() => phase.value);
const shownSources = computed(() => (phase.value === "retrieving" ? [] : sources));
const search = computed(() => answerTurnStatus("retrieving", { kind: "answer" })!);
</script>

<template>
  <SettingsSection title="AiStatus: jeden stavový řádek" description="components/ai/AiStatus.vue — jediný způsob, jak Atlas říká „model na tom pracuje“: znak ve stavu právě běžícího kroku, sloveso (co přesně se děje), co už je známo, jak dlouho to běží a Zastavit tam, kde úlohu jde opravdu zastavit (přeruší požadavek). Tři velikosti téhož řádku: XS ve vyhledávacím poli, S v kartě odpovědi (Odpověď z wiki, Shrnutí stránky; znak pak nese nadpis karty), M v asistentovi se seznamem kroků. Kroky jsou jen dvě skutečné fáze úlohy, které server hlásí (hledání podkladů, psaní odpovědi), žádný předstíraný průběh. Kdy jinak: hotový výstup modelu ukazuje AiAnswer (ten si řádek během práce skládá sám), práce pravidel nebo hledání bez modelu má spinner a ikonu typu.">
    <div ref="root" class="ai-status-example" data-atlas-motion>
      <div class="ase-controls">
        <Button size="sm" variant="outline" @click="play"><AtlasIcon name="rotate-cw" :size="14" />Přehrát průběh</Button>
        <small role="status">{{ stopped ? 'Zastaveno: požadavek se přeruší, karta se zavře.' : phase === 'done' ? 'Hotovo: znak se jednou slije a je opět v klidu.' : phase === 'retrieving' ? 'Fáze 1: hledání podkladů' : 'Fáze 2: model píše odpověď' }}</small>
      </div>
      <div class="ase-grid">
        <div class="ase-card">
          <span class="ase-caption">Ve vyhledávání (XS)</span>
          <div class="ase-field">
            <AtlasIcon name="search" :size="16" />
            <span class="ase-query">jak obnovit certifikát na IIS</span>
            <AiStatus size="xs" state="thinking" label="Hledám význam…" />
          </div>
          <small>Textové výsledky jsou hned, znak jen ukazuje, že ještě dobíhá významové hledání.</small>
        </div>
        <div class="ase-card">
          <span class="ase-caption">Odpověď z wiki (S)</span>
          <AiAnswer v-if="!stopped" :state="answerState" variant="compact" :sources="shownSources" :sentences="phase === 'done' ? sentences : []" :provenance="phase === 'done' ? provenance : null" @stop="stop" />
          <p v-else class="ase-note">Odpověď jste zastavili.</p>
        </div>
        <div class="ase-card">
          <span class="ase-caption">Asistent (M) – kroky a zastavení</span>
          <AiAnswer v-if="!stopped" :state="answerState" variant="full" title="Odpověď asistenta" :sources="shownSources" :sentences="phase === 'done' ? sentences : []" :provenance="phase === 'done' ? provenance : null" collapsible-sources @stop="stop" />
          <p v-else class="ase-note">Odpověď jste zastavili; otázka zůstává v konverzaci.</p>
        </div>
      </div>
      <div class="ase-card ase-alone">
        <span class="ase-caption">Samotný řádek (S), bez karty</span>
        <AiStatus size="s" :state="search.glyph" :label="search.label" elapsed stoppable progress @stop="stop" />
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.ai-status-example { display: grid; gap: 12px; min-width: 0; }
.ase-controls { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; }
.ase-controls small { color: var(--muted); font-size: 12.5px; }
.ase-grid { display: grid; grid-template-columns: 1fr 1fr 1.15fr; gap: 12px; align-items: start; }
.ase-card { display: grid; grid-template-columns: minmax(0, 1fr); gap: 8px; min-width: 0; padding: 14px; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); }
.ase-card > small, .ase-note { margin: 0; color: var(--muted); font-size: 12px; }
.ase-caption { color: var(--muted); font-size: 12px; }
.ase-field { display: flex; align-items: center; gap: 8px; height: 40px; padding: 0 12px; border: 1px solid var(--accent); border-radius: 10px; box-shadow: 0 0 0 3px var(--accent-soft); color: var(--accent); }
.ase-query { flex: 1; min-width: 0; overflow: hidden; color: var(--text); text-overflow: ellipsis; white-space: nowrap; }
.ase-alone { max-width: 520px; }
@media (max-width: 1100px) { .ase-grid { grid-template-columns: minmax(0, 1fr); } }
</style>
