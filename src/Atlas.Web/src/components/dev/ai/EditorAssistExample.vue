<script setup lang="ts">
import { defineAsyncComponent, ref, shallowRef } from "vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import type { ContactDirectoryService, MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { aiIntentLabels } from "../../../assist/aiIntents";
import { loadEditorAssist } from "../../../assist/loadAssist";
import type { AtlasAssistContext, AtlasAssistFactory, PickedSecret, SecretPickRequest } from "../../../assist/types";
import { secretReferenceCommand } from "../../../secrets/moveSecretToVault";
import type { SecretReferenceView, SecretsApi } from "../../../secrets/secretsApi";
import EmptyState from "../../settings/EmptyState.vue";
import { Button } from "../../ui/button";
import SettingsSection from "../../settings/SettingsSection.vue";

// „Návrhy při psaní“ (plan §12 H.6): the real page editor (DocumentEditor) with the real lazily loaded adapter, only the
// directories are local, so nothing reaches a server. Prototype: „Návrhy v editoru“, moments 1–8 (marker, menu, morph
// into a chip and glow; the password's amber marker, warning bar, conceal and vault reference). Rules, not a model:
// type icons only, never the AI glyph. The pinned editor (0.9.0) ships the module; only a failed chunk leaves it out.
const DocumentEditor = defineAsyncComponent(() => import("../../DocumentEditor.vue"));
const SecretPicker = defineAsyncComponent(() => import("../../../secrets/SecretPicker.vue"));

const factory = shallowRef<AtlasAssistFactory | null>();
const status = ref("");
const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const lines = (...texts: string[]) => ({
  type: "paragraph",
  content: texts.flatMap((text, index): Record<string, string>[] => [...index ? [{ type: "hardBreak" }] : [], { type: "text", text }]),
});
const value = ref<MarkdownRichValue>({
  format: "atlas-rich",
  version: 1,
  document: {
    type: "doc",
    content: [
      paragraph("Serverovna je ve 2. patře budovy B, místnost 2.14. Vstup je jen s čipovou kartou."),
      paragraph("Pro přístup kontaktujte Jana Nováka z provozu."),
      paragraph("Poruchy čtečky řešíme v úkolu ATL-17."),
      paragraph("viz youtrack PX2-1234"),
      paragraph("Prosím kontaktujte Tedst@test.teswt kvůli návrhu."),
      lines("Server\tIP\tPatro", "srv-01\t10.0.0.11\t2", "srv-02\t10.0.0.12\t3"),
      paragraph("Nouzový účet: admin, heslo Srv#2026!rack"),
      { type: "paragraph" },
    ],
  },
});
const modelAvailable = ref(true);
const failNext = ref(false);

const jan: MarkdownContact = { name: "Jan Novák", department: "Provoz", emails: ["jan.novak@firma.example.test"], directoryId: "demo-jan", kind: "internal", internalUser: { id: "demo-jan", name: "Jan Novák" } };
const contacts: ContactDirectoryService = {
  search: async query => /jan|nov/i.test(query) ? [jan] : [],
  get: async () => jan,
};
const vaultSecret: SecretReferenceView = {
  id: "0b6a0d9e-6e4e-4a8e-9a3f-1c2d3e4f5a61", title: "Serverovna — nouzový účet", path: "Provoz / Serverovna", vaultId: "demo", vaultName: "Provoz",
  spaceId: "gallery", fields: ["userName", "password"], level: "read",
};
const secretsApi = { search: async () => [vaultSecret] } as unknown as SecretsApi;

// The example's own picker with local data; the page editor's picker would search the server.
const picker = shallowRef<SecretPickRequest & { resolve: (picked: PickedSecret | null) => void }>();
function pickSecret(request: SecretPickRequest) {
  picker.value?.resolve(null);
  return new Promise<PickedSecret | null>(resolve => { picker.value = { ...request, resolve }; });
}
function picked(reference: { secretId: string; inline: boolean; vaultName?: string } | null) {
  const pending = picker.value;
  picker.value = undefined;
  pending?.resolve(reference && { command: secretReferenceCommand(reference.secretId, reference.inline), inline: reference.inline, ...(reference.vaultName ? { vaultName: reference.vaultName } : {}) });
}

const local: Partial<AtlasAssistContext> = {
  contacts,
  pageId: () => "gallery-page",
  modelSuggestionsEnabled: () => false,
  reportInteraction: () => {},
  writing: {
    transformAllowed: () => modelAvailable.value, outlineAllowed: () => modelAvailable.value, intentAllowed: () => false,
    title: () => "Serverovna", onApplied: () => { status.value = "AI úprava použita"; },
    api: {
      intents: async () => ({suggestions: []}),
      transform: async ({text}, signal) => {
        await delay(signal); if (failNext.value) { failNext.value = false; throw new Error("Local demo failure"); }
        return {text:text.replace(/Serverovna je ve 2. patře budovy B, místnost 2.14. Vstup je jen s čipovou kartou./u,"Serverovna: budova B, 2. patro, místnost 2.14. Vstup s čipem."),provenance:{location:"local",provider:"openai",model:"gallery"}};
      },
      outline: async (_input, signal) => { await delay(signal); return {text:"## Přístup\n\n> [!TIP]\n> Popište, kdo přístup schvaluje.\n\n## Provoz\n\n> [!TIP]\n> Doplňte běžný provozní postup.",provenance:{location:"local",provider:"openai",model:"gallery"}}; },
    },
  },
  // P11: Zeptat se / Vysvětlit / Shrnout would hand the target to the assistant panel; here they only say what they would send.
  ask: { available: () => modelAvailable.value, run: (intent, text) => { status.value = `${aiIntentLabels[intent]} → asistent: ${text.length} znaků`; } },
  pages: { search: async () => [], resolve: async id => ({ id, title: "Připojení k VPN", url: `#/pages/${id}` }) },
  resolveIssue: async key => key === "ATL-17" ? { id: "demo-17", key, title: "Čtečka karet u dveří B hlásí chybu", url: "https://youtrack.example.com/issue/ATL-17", state: "In Progress" } : null,
  hasSecrets: async () => true,
  pickSecret,
  // „Úprava použita“ and the other confirmations go to the page editor's toast with „Zpět“ (P6), as in the app.
  warn: message => { status.value = message; },
};
/** The page editor's context with the directories replaced by the local ones above. */
const assist = (loaded: AtlasAssistFactory): AtlasAssistFactory => context => loaded({ ...context, ...local });

// The page editor and the module load on request, so the gallery page itself stays light.
function delay(signal: AbortSignal): Promise<void> {
  return new Promise((resolve,reject) => {
    const abort = () => { clearTimeout(timer); reject(new DOMException('Aborted','AbortError')); };
    const timer = setTimeout(() => { signal.removeEventListener('abort',abort); resolve(); },800);
    signal.addEventListener('abort',abort,{once:true}); if(signal.aborted) abort();
  });
}
function emptyPage() { value.value = {format:"atlas-rich",version:1,document:{type:"doc",content:[{type:"paragraph"}]}}; }
const loading = ref(false);
async function start() {
  loading.value = true;
  // As with the wiki switch on; the gallery never depends on the server's `features`.
  const loaded = await loadEditorAssist({ features: { editorAssist: true } });
  factory.value = loaded && assist(loaded);
  loading.value = false;
}
</script>

<template>
  <SettingsSection title="Návrhy při psaní (atlasAssist)" description="assist/atlasAssist.ts — obsah pro volitelný modul editoru @atlas/markdown/assist: pravidla (src/assist/rules) najdou kontakt, úkol YouTrack, tabulku nebo heslo, resolver je ověří a modul nabídne značku u bloku. Proč ne SuggestionChip: návrh v textu stránky nese značka editoru (jeden seznam se slash menu a @), SuggestionChip je pro pole a panely mimo text. Volte ho jen v editoru stránky; načítá se líně a jen se zapnutým modulem „Návrhy při psaní“. Upravte odstavec a přejděte na další (nebo počkejte 1,5 s), Alt+Enter otevře seznam, Enter použije. Heslo má trvalou jantarovou značku a pruh „Přesunout do trezoru“. Neověřený klíč YouTrack zobrazí vysvětlení bez vložení úkolu; e-mail mimo adresář nabídne místní e-mailový odkaz.">
    <div class="assist-example" data-editor-assist-example>
      <Button v-if="factory === undefined" class="assist-example-start" variant="outline" :disabled="loading" data-editor-assist-start @click="start">
        {{ loading ? "Načítám editor…" : "Otevřít editor s návrhy" }}
      </Button>
      <EmptyState v-else-if="factory === null" icon="info" title="Návrhy při psaní nejsou k dispozici" text="Modul asistence editoru se nepodařilo načíst." />
      <template v-else-if="factory">
        <p>Modelové akce používají skutečný host a jediný proposal panel editoru. AI glyph patří jen vstupu do modelové úpravy; lokální pravidla zachovávají ikonu cílového typu. Provider v této ukázce je lokální a neposílá žádné požadavky.</p>
        <p>Pravidlo P11 (assist/aiIntents.ts): Ctrl+mezerník nabídne tytéž záměry nad tím, na co ukazuje kurzor – výběr, vybraný prvek, nadpis s celou sekcí, odstavec, seznam či tabulku a celou stránku. Zeptat se, Vysvětlit a Shrnout jdou do asistenta (prvek i se svým zdrojem, například vzorec); Přepsat, Přeložit a Doplnit připraví návrh s Použít / Zahodit, jeden krok zpět. Jeden limit 1 600 znaků; když je sekce delší, nabídne se samotný nadpis. Tabulka vybraná úchytem má tytéž záměry jako s kurzorem uvnitř. Tajné a skryté části se neposílají; bez modelu položky AI nejsou vidět.</p>
        <label><input v-model="modelAvailable" type="checkbox" data-editor-writing-available> Model dostupný (vypnutí skryje akce AI)</label>
        <label><input v-model="failNext" type="checkbox"> Příští příprava selže</label>
        <Button variant="outline" @click="emptyPage">Prázdná stránka pro osnovu</Button>
        <p class="assist-example-status" role="status">{{ status }}</p>
        <component :is="DocumentEditor" :value="value" :readonly="false" :dark="false" accent="blue" space-id="gallery" document-title="Serverovna" :assist="factory" />
        <!-- In the page editor the picker is an editor popover (Esc returns the caret); the example has no caret, so it shows its content in place. -->
        <div v-if="picker" class="assist-example-picker">
          <component :is="SecretPicker" :open="true" space-id="gallery" :api="secretsApi" :mode="picker.mode" :initial-query="picker.initialQuery" @insert="picked" />
          <Button variant="outline" size="sm" @click="picked(null)">Zavřít</Button>
        </div>
      </template>
    </div>
  </SettingsSection>
</template>

<style scoped>
.assist-example-picker { display: grid; gap: 8px; max-width: 420px; padding: 12px; border: 1px solid var(--line); border-radius: 10px; }
.assist-example { display: grid; gap: 8px; min-width: 0; padding-left: 36px; }
.assist-example-start { justify-self: start; }
.assist-example-status { min-height: 1.4em; margin: 0; color: var(--muted); font-size: 12px; }
</style>
