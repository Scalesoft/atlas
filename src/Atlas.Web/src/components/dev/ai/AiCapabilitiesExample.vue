<script setup lang="ts">
import { computed, reactive, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import { AI_CAPABILITY_KEYS, noAiCapabilities } from "../../../ai/capabilities";
import { aiUnavailableText, runAiAction } from "../../../ai/aiAction";
import { ApiError } from "../../../api";

// useAiCapabilities (W1, plan phase 2 §16). Local flags stand in for GET /api/ai/capabilities; no server. Every AI surface is a plain
// `v-if` on its flag, so an outage makes it vanish without a banner, toast or greyed button (AI-25), and with all flags false the
// DOM holds no AI element at all (AI-24). Hunting the rule: these are search and similarity, so their icons are type icons, not AiGlyph.
const labels: Record<(typeof AI_CAPABILITY_KEYS)[number], string> = {
  editorIntent:"Návrhy komponent",editorTransform:"Úpravy výběru",editorOutline:"Osnova",publishCheck:"Pravopis",changeSummary:"Popis změny",pageSummary:"Shrnutí stránky",tagSuggestions:"Návrhy tagů",agent:"Asistent s nástroji",arrangePages:"Uspořádání menu",
  semanticSearch: "semanticSearch (Významová shoda)",
  related: "related (Související)",
  editorLinks: "editorLinks (propojení zmínek)",
  createSuggestions: "createSuggestions (čip umístění)",
  answer: "answer (Odpověď z wiki v hledání)",
  assistant: "assistant (panel Asistent)",
};
const flags = reactive({ ...noAiCapabilities(), semanticSearch: true, related: true, createSuggestions: true, answer: true, assistant: true });
const any = computed(() => Object.values(flags).some(Boolean));

const outcome = ref("");
const starting = ref(false);
async function startAction() {
  starting.value = true;
  outcome.value = "";
  // The action the user just started meets a 503 ai_unavailable: it ends with the neutral sentence and writes nothing.
  const result = await runAiAction(async () => { throw new ApiError(503, aiUnavailableText(), "ai_unavailable"); });
  starting.value = false;
  if (result.state === "unavailable") { outcome.value = result.text; for (const key of AI_CAPABILITY_KEYS) flags[key] = false; }
}
function reset() { Object.assign(flags, { ...noAiCapabilities(), semanticSearch: true, related: true, createSuggestions: true, answer: true, assistant: true }); outcome.value = ""; }
</script>

<template>
  <SettingsSection title="useAiCapabilities" description="composables/useAiCapabilities.ts — booleany schopností po úlohách, načtené se session, obnovené při návratu na kartu, jednou za minutu jen na viditelné kartě a hned po 503 ai_unavailable. Každý vstup AI je v šabloně jen v-if na svou schopnost: při výpadku tiše zmizí (žádný banner, toast ani zašedlé tlačítko), bez AI ho DOM vůbec neobsahuje. Akci, kterou uživatel právě spustil, nahradí věta „Tahle funkce teď není k dispozici.“ (runAiAction) a nic se nezapíše. Volte tento vzor pro každý nový prvek AI; zvláštní komponentu pro výpadek nestavte.">
    <div class="caps-example">
      <div class="caps-switches" role="group" aria-label="Schopnosti">
        <label v-for="key in AI_CAPABILITY_KEYS" :key="key" class="caps-switch"><Switch v-model="flags[key]" />{{ labels[key] }}</label>
      </div>

      <div class="caps-stage" data-caps-stage>
        <div class="caps-search">
          <span class="caps-field"><AtlasIcon name="search" :size="14" />vpn z mobilu</span>
          <span v-if="flags.semanticSearch" class="caps-label" data-caps-semantic><AtlasIcon name="link" :size="12" />Významová shoda</span>
        </div>
        <div class="caps-footer">
          <div class="caps-col">Odkazuje sem<small>Připojení k VPN</small></div>
          <div v-if="flags.related" class="caps-col" data-caps-related>Související<small>VPN pro dodavatele</small></div>
        </div>
        <span v-if="flags.createSuggestions" class="caps-chip" data-caps-placement><AtlasIcon name="map-pin" :size="12" />Navrhované umístění: IT a infrastruktura</span>
        <span v-if="flags.answer" class="caps-chip" data-caps-answer><AtlasIcon name="search" :size="12" />Zeptat se asistenta (karta v hledání)</span>
        <span v-if="flags.assistant" class="caps-chip" data-caps-assistant><AtlasIcon name="panel-right-close" :size="12" />Asistent (položka v sidebaru)</span>
        <span v-if="!any" class="caps-note">Žádná schopnost: bez štítku, sloupce i čipu, a nic nenaznačuje, že tu něco chybí.</span>
      </div>

      <div class="caps-actions">
        <Button size="sm" :disabled="starting" @click="startAction">Spustit akci a nechat ji narazit na 503</Button>
        <Button size="sm" variant="outline" @click="reset">Obnovit schopnosti</Button>
        <span v-if="outcome" class="caps-outcome" role="status" data-caps-outcome>{{ outcome }}</span>
      </div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.caps-example { display: grid; gap: 12px; }
.caps-switches { display: flex; flex-wrap: wrap; gap: 8px 16px; }
.caps-switch { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; }
.caps-stage { display: grid; gap: 10px; max-width: 560px; padding: 12px; border: 1px solid var(--line); border-radius: 12px; background: var(--canvas); }
.caps-search { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.caps-field { display: inline-flex; align-items: center; gap: 6px; padding: 6px 10px; border: 1px solid var(--line); border-radius: 8px; font-size: 13px; }
.caps-label, .caps-chip { display: inline-flex; align-items: center; gap: 4px; width: fit-content; padding: 2px 8px; border-radius: 999px; background: var(--accent-soft, var(--canvas)); color: var(--muted); font-size: 12px; font-weight: 600; }
.caps-footer { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 8px; }
.caps-col { display: grid; gap: 2px; padding: 8px 10px; border: 1px solid var(--line); border-radius: 8px; font-size: 13px; font-weight: 600; }
.caps-col small, .caps-note, .caps-outcome { color: var(--muted); font-size: 12px; font-weight: 400; }
.caps-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
</style>
