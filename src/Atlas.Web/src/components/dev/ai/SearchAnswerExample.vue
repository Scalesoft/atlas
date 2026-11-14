<script setup lang="ts">
import { computed, nextTick, ref, shallowRef, type ComponentPublicInstance } from "vue";
import type { AiMessageView, AiSourceView } from "../../../ai/assistantApi";
import type { AnswerTurnState } from "../../../ai/answerTurn";
import type { useSearchAnswer } from "../../../search/useSearchAnswer";
import { fieldElement } from "../../../lib/templateRef";
import { switchSearchInputMode } from "../../../search/searchInputMode";
import { moveAnswerToPanel } from "../../../search/answerHandoff";
import SearchAnswerCard from "../../search/SearchAnswerCard.vue";
import SearchModeSwitch from "../../search/SearchModeSwitch.vue";
import AiAnswer from "../../ai/AiAnswer.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { Input } from "../../ui/input";
import { provenance, sentences, sources as sampleSources } from "./aiSampleData";
import { useStoryboard } from "./storyboard";

const root = ref<HTMLElement>();
const field = ref<ComponentPublicInstance>();
const { sleep } = useStoryboard(root);
const mode = ref<"pages" | "ask" | "commands">("pages");
const state = ref<"offer" | AnswerTurnState>("offer"), sources = ref<AiSourceView[]>([]);
const message = shallowRef<AiMessageView | null>(null);
const notice = ref(""), rating = ref<"up" | "down" | null>(null);
const conversationId = ref<string | null>(null), messageId = ref<string | null>(null);
const moved = ref(false), empty = ref(false), query = ref("Jak se připojím k firemní VPN?");
const card = ref<InstanceType<typeof SearchAnswerCard>>();
const target = ref<HTMLElement>();
const highlight = ref<number | null>(null);
let version = 0;
function reset() { version++; moved.value = false; state.value = "offer"; sources.value = []; message.value = null; conversationId.value = null; notice.value = ""; }
async function ask() {
  const mine = ++version; state.value = "retrieving";
  await sleep(700); if (mine !== version) return false;
  sources.value = empty.value ? [] : sampleSources;
  state.value = empty.value ? "no_sources" : "preparing";
  if (empty.value) return false;
  await sleep(1900); if (mine !== version) return false;
  message.value = { id: "gallery-a", conversationId: "gallery-c", role: "assistant", state: "answered", sentences, sources: sampleSources, provenance, scope: { kind: "wiki" }, createdAt: new Date().toISOString() };
  conversationId.value = "gallery-c"; state.value = "done"; return true;
}
const answer: ReturnType<typeof useSearchAnswer> = {
  state, sources, message, notice, rating, conversationId, messageId, preference: ref("ask"),
  visible: computed(() => mode.value !== "commands" && (mode.value === "ask" || query.value.trim().length >= 2)), pending: computed(() => state.value === "retrieving" || state.value === "preparing"),
  ask, stop: reset, reset, resetSession: reset, refreshPreference: async () => {}, feedback: async body => { rating.value = body.rating; },
};
const origin = (number: number) => root.value?.querySelector(`[data-search-sample="${number}"]`) ?? null;
async function transfer() {
  const from = card.value?.element(); moved.value = true;
  await nextTick();
  if (from && target.value) await moveAnswerToPanel(from, target.value);
}
async function choose(value: "pages" | "ask" | "commands") {
  await switchSearchInputMode(fieldElement(field.value), query.value, value, next => { query.value = next; mode.value = value; });
}
function keys(event: KeyboardEvent) {
  if (event.key === "Enter" && !event.isComposing && !event.altKey && !event.shiftKey && (mode.value === "ask" || event.ctrlKey || event.metaKey)) {
    event.preventDefault(); void ask();
  }
}
</script>
<template>
  <SettingsSection title="Odpověď z wiki v hledání" description="SearchAnswerCard skládá AiAnswer pro jednorázovou odpověď nad výsledky. SearchModeSwitch sdílí jeden dotaz pro hledání a otázku. Vymažte dotaz: nabídka odpovědi uvolní místo výsledkům. Vrátí se po zadání tématu nebo po volbě Zeptat se. Režim zvolíte tlačítkem se zachováním výběru textu; přepnutí model nespouští. Tab a Shift+Tab přesouvají fokus běžným způsobem, Ctrl+Enter položí otázku. Asistent pokračuje v konverzaci v panelu. Použijte kartu pro otázku z hledání, panel pro návazné otázky. Přehrání používá skutečné komponenty a stejné přemístění jako aplikace; data zůstávají místní.">
    <div ref="root" class="search-answer-example">
      <div class="sae-controls"><Button size="sm" variant="outline" @click="reset(); empty = false">Začít znovu</Button><Button size="sm" variant="outline" @click="reset(); empty = true; ask()">Bez podkladů</Button></div>
      <div class="sae-stage" data-atlas-motion>
        <div class="sae-search">
          <SearchModeSwitch :mode="mode" :answer="true" @change="choose" />
          <Input ref="field" v-model="query" aria-label="Ukázka dotazu" :placeholder="mode === 'ask' ? 'Zeptejte se na obsah wiki…' : 'Hledat ve wiki…'" @keydown="keys" />
          <p v-if="mode !== 'commands'" class="search-mode-hint"><kbd>Ctrl+Enter</kbd> položí otázku · <kbd>Tab</kbd> přejde na další ovládací prvek</p>
          <div :style="moved ? { visibility: 'hidden' } : undefined">
            <SearchAnswerCard ref="card" :answer="answer" :can-continue="true" :source-origin="origin" @continue="transfer" @highlight="highlight = $event" @try-other="reset" />
          </div>
          <div v-if="!empty" class="sae-results" aria-label="Ukázkové výsledky">
            <button v-for="(source, index) in sampleSources" :key="source.number" type="button" :data-search-sample="source.number" :class="{ 'is-highlighted': highlight === source.number }"><AtlasIcon name="page" :size="18" /><span><strong>{{ ['Připojení k VPN', 'Certifikáty zařízení', 'Práce z domova'][index] }}</strong><small>{{ source.spaceName }}</small></span></button>
          </div>
          <div v-else class="sae-empty"><p>Žádné odpovídající stránky.</p><Button variant="outline" size="sm" @click="ask">Zeptat se asistenta</Button></div>
        </div>
        <aside v-if="moved" class="sae-panel" aria-label="Ukázka cílového panelu"><strong>Asistent · stejná konverzace</strong><p class="sae-question">Jak se připojím k firemní VPN?</p><div ref="target"><AiAnswer state="done" variant="full" :sources="sampleSources" :sentences="sentences" :provenance="provenance" collapsible-sources /></div></aside>
      </div>
    </div>
  </SettingsSection>
</template>
<style scoped>
.search-answer-example { display: grid; gap: 12px; min-width: 0; }
.sae-controls { display: flex; gap: 8px; flex-wrap: wrap; }
.sae-stage { position: relative; display: flex; align-items: stretch; gap: 14px; min-width: 0; }
.sae-search { flex: 1; min-width: 0; display: grid; align-content: start; gap: 12px; }
.sae-results { display: grid; gap: 8px; }
.sae-results button { display: flex; align-items: center; gap: 10px; padding: 10px 12px; border: 1px solid var(--line); border-radius: 8px; background: var(--surface); text-align: left; color: var(--text); transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.sae-results button.is-highlighted { background: var(--accent-soft); }
.sae-results span { display: grid; gap: 3px; }
.sae-results small { color: var(--muted); }
.sae-panel { position: absolute; right: 0; top: 0; width: min(360px, 100%); padding: 14px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface); }
.sae-panel > strong { font-size: 14px; }
.sae-question { padding: 10px; background: var(--accent-soft); border-radius: 10px; font-size: 13px; }
.sae-empty { color: var(--muted); }
@media (max-width: 900px) { .sae-stage { flex-direction: column; } .sae-panel { width: 100%; } }
</style>
