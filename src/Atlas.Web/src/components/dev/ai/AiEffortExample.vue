<script setup lang="ts">
import { ref } from "vue";
import type { AnswerEffort } from "../../../ai/answerEffort";
import AiEffortMeter from "../../ai/AiEffortMeter.vue";
import AiEffortPicker from "../../ai/AiEffortPicker.vue";
import AssistantComposer from "../../assistant/AssistantComposer.vue";
import SettingsSection from "../../settings/SettingsSection.vue";

const question = ref("");
const chosen = ref<AnswerEffort>("thorough");
const steps = ref<AnswerEffort[]>(["quick", "balanced", "thorough", "deep", "max"]);
</script>

<template>
  <SettingsSection title="Asistent: náročnost odpovědi" description="components/ai/AiEffortPicker.vue v poli otázky (slot actions AssistantComposeru): tlačítko ukazuje zvolený krok, klepnutí otevře nad ním AiEffortMeter (ui/slider) v úzkém popoveru. Kolik wiki si další odpověď přečte (AnswerEffort na serveru, 6–14 úryvků). Pět kroků, každý o kousek živější a jen v barvách AI: Vyváženě pošle k úchytu lesk, od Důkladně do něj létají jiskry, Do hloubky přidá hvězdičky a záři, Naplno záře dýchá. S omezeným pohybem zůstanou jen barvy. Jen pro náročnost odpovědi; jiná uspořádaná volba je obyčejný Slider.">
    <div class="effort-demo">
      <AssistantComposer v-model="question" @send="question = ''">
        <template #actions><AiEffortPicker v-model="chosen" /></template>
      </AssistantComposer>
      <p class="effort-demo-caption">Obsah popoveru ve všech pěti krocích:</p>
      <div v-for="(_, index) in steps" :key="index" class="effort-demo-card"><AiEffortMeter v-model="steps[index]!" /></div>
    </div>
  </SettingsSection>
</template>

<style scoped>
.effort-demo { display: grid; gap: 14px; max-width: 360px; padding-top: 140px; }
.effort-demo-caption { margin: 8px 0 -4px; color: var(--muted); font-size: 12px; }
.effort-demo-card { width: 280px; padding: 14px 16px 16px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); }
</style>
