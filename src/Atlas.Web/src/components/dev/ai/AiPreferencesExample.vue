<script setup lang="ts">
import { ApiError } from "../../../api";
import type { AiPreferences, AiPreferencesApi } from "../../../aiPreferences";
import AiPreferencesSection from "../../profile/AiPreferencesSection.vue";
import SettingsPage from "../../settings/SettingsPage.vue";
import SettingsSection from "../../settings/SettingsSection.vue";

/**
 * Profil -> AI (G4), a section of the profile. Live form with local data, no server. Rule-based preferences (editor suggestions),
 * so no AI glyph. Progressive disclosure: the suggestion types open only while suggestions are on, the summary follows every
 * choice, and saving is explicit (SaveBar, CAS). Switch to Vypnuto: the list folds away (reveal primitive, a fade under
 * reduced motion); Uložit flashes the summary. Conversation deletion stays under explicit history maintenance;
 * the answer preference explains only its current choice. In the application the profile shows this section only while an AI capability exists.
 */
function local(initial: AiPreferences, behaviour: "ok" | "conflict" = "ok"): AiPreferencesApi {
  let current = initial;
  return {
    load: async () => current,
    deleteAllConversations: async () => {},
    save: async (draft, expected) => {
      if (behaviour === "conflict" || expected !== current.version) throw new ApiError(409, "Conflict");
      current = { ...draft, version: `v${Number(current.version?.slice(1) ?? 0) + 1}` };
      return current;
    },
  };
}
const fresh = local({ editorSuggestions: "on", disabledSuggestionTypes: [], version: "v1" });
const partly = local({ editorSuggestions: "on", disabledSuggestionTypes: ["issue", "structure"], version: "v1" });
const answers = local({ editorSuggestions: "on", disabledSuggestionTypes: [], searchAnswer: "auto", assistantScope: "context", version: "v1" });
const conflicting = local({ editorSuggestions: "on", disabledSuggestionTypes: [], version: "v1" }, "conflict");
</script>

<template>
  <SettingsPage title="Profil: AI" icon="lightbulb" description="Osobní předvolby návrhů při psaní. Pravidlová funkce bez modelu, proto bez ikony AI. Živý formulář s místními daty; v aplikaci ho profil ukáže jen tehdy, když je AI dostupná.">
    <SettingsSection title="AiPreferencesSection" description="AiQuotaStatus ukazuje serverem vypočtený zbytek denního limitu, původ a čas obnovy; V pracovních panelech se kvóty nevypisují; kompaktní varianta u profilu ukazuje pouze konečné limity. Neřídí povolení ani nesčítá skupinové limity. Zapnuto / Vypnuto, typy návrhů jako zaškrtávací karty, souhrn a stálá informace o ochraně hesel. Zkuste Vypnuto, odškrtnout typ a Uložit.">
      <AiPreferencesSection :api="fresh" />
    </SettingsSection>
    <SettingsSection title="S odpovědí z wiki a asistentem" description="Se schopností answer přibude Odpověď v hledání (Na vyžádání / Automaticky při hledání / Nikdy). Výchozí je automatická odpověď, uložená volba se zachová. Se schopností assistant přibude Výchozí rozsah asistenta, odkaz na Moje konverzace a sbalená Správa historie konverzací. Hromadné smazání se odkryje až v ní a vyžaduje potvrzení. Nápověda u odpovědi vysvětluje pouze zvolený režim. Volby se ukládají explicitně spolu se zbytkem, smazání je akce hned a je i bez AI. Schopnosti se tu nastavují ručně, aplikace je bere ze serveru.">
      <AiPreferencesSection :api="answers" :capabilities="{ answer: true, assistant: true }" />
    </SettingsSection>
    <SettingsSection title="Část typů vypnutá" description="Typ vypnutý přes Nenabízet v editoru se tu ukáže odškrtnutý.">
      <AiPreferencesSection :api="partly" />
    </SettingsSection>
    <SettingsSection title="Souběh dvou oken" description="Uložení narazí na novější verzi: změna zůstane ve formuláři a Načíst znovu ukáže aktuální stav.">
      <AiPreferencesSection :api="conflicting" />
    </SettingsSection>
    <SettingsSection title="Proč ne nová komponenta" description="Skládá OptionPills, ChoiceCards, InlineReveal, SummaryList a SaveBar stejně jako ostatní formuláře nastavení. Od Vzhledu se liší jen tím, že ukládá na server s kontrolou verze.">
      <p class="st-note">Vlastní je jen postup: pokročilé volby se otevřou, až když dávají smysl, a souhrn říká, co se po uložení stane.</p>
    </SettingsSection>
  </SettingsPage>
</template>
