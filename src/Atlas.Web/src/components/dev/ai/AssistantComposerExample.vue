<script setup lang="ts">
import { ref } from "vue";
import AssistantComposer from "../../assistant/AssistantComposer.vue";
import AiQuotaStatus from "../../ai/AiQuotaStatus.vue";
import AiGlyph from "../../ai/AiGlyph.vue";
import SettingsSection from "../../settings/SettingsSection.vue";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import AiEffortPicker from "../../ai/AiEffortPicker.vue";
import OptionPills from "../../settings/OptionPills.vue";
import type { AnswerEffort } from "../../../ai/answerEffort";
const question = ref("Jak se připojím k firemní VPN?");
const sent = ref("");
const busy = ref(false);
const mode = ref<"on" | "off" | "none">("off");
const tools = ref(false);
const effort = ref<AnswerEffort>("quick");
const modes = [{ value: "on", label: "Zapnuto" }, { value: "off", label: "Vypnuto" }, { value: "none", label: "Bez práva" }];
function pick(value: string) { mode.value = value as typeof mode.value; tools.value = value !== "off"; }
function send() { sent.value = question.value; question.value = ""; busy.value = true; }
</script>
<template>
  <SettingsSection title="Asistent: klidný panel" description="Skutečný AssistantComposer: Enter i tlačítko odešlou ihned, vlaštovka vyletí po oblouku. Zastavit ukončí ukázkové čekání. Kvóty patří pouze k profilu; neomezené se nezobrazují.">
    <div class="composer-demo">
      <p v-if="sent">{{ sent }}</p>
      <div v-if="busy" class="demo-working" role="status"><AiGlyph working :size="26" /><span class="visually-hidden">Připravuji odpověď</span><Button variant="ghost" size="sm" @click="busy = false">Zastavit</Button></div>
      <OptionPills :model-value="mode" label="Přepínač S nástroji" :options="modes" @update:model-value="pick" />
      <AssistantComposer v-model="question" :busy="busy" @send="send" @keydown.esc="busy = false">
        <template #actions>
          <label v-if="mode !== 'none'" class="demo-tools"><Switch v-model="tools" />S nástroji</label>
          <span :title="mode !== 'none' && tools ? 'S nástroji si asistent čte, co potřebuje' : undefined"><AiEffortPicker v-model="effort" :disabled="mode !== 'none' && tools" /></span>
        </template>
      </AssistantComposer>
      <p class="demo-note">„S nástroji“ patří do slotu <code>actions</code> vedle náročnosti: volba platí pro další otázky, ne pro jednu, proto není druhé tlačítko odeslání. Zapnuto (výchozí) = asistent si sám hledá a čte stránky a náročnost je zašedlá („S nástroji si asistent čte, co potřebuje“). Vypnuto = odpověď z wiki jako dřív. Bez práva „AI s nástroji“ se přepínač nevykreslí. Skutečné chování je ve scénách AssistantPanel.</p>
      <div class="demo-profile"><span>Profil</span><AiQuotaStatus compact /></div>
    </div>
  </SettingsSection>
</template>
<style scoped>
.composer-demo { max-width: 360px; display: grid; gap: 24px; padding-top: 100px; }
.demo-tools { display: inline-flex; align-items: center; gap: 6px; margin-right: 4px; color: var(--muted); font-size: 12px; font-weight: 550; }
.demo-note { margin: 0; color: var(--muted); font-size: 12.5px; }
.demo-working, .demo-profile { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
</style>
