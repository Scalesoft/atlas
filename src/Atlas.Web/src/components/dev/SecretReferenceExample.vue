<script setup lang="ts">
import { ref, shallowRef } from "vue";
import { MarkdownEditor } from "@atlas/markdown/vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { createSecretsPlugin, type SecretReplacementContext, type SecretsServices } from "@atlas/markdown/plugins/secrets";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/concealment/style.css";
import { editorChrome, editorOutline, useEditorTheme } from "../../editor/editorSetup";
import SecretPicker from "../../secrets/SecretPicker.vue";
import type { SecretReferenceView, SecretsApi } from "../../secrets/secretsApi";
import SettingsSection from "../settings/SettingsSection.vue";

// The editor's secrets plugin with local sample entries: discovery, release and search never leave the gallery. The page
// editor gets the same picker answering the plugin's `pickReplacement` (DocumentEditor `swapSecret`).
const theme = useEditorTheme();
const entry = (id: string, title: string, path: string): SecretReferenceView =>
  ({ id, title, path, vaultId: "demo", vaultName: "Provoz", spaceId: "gallery", fields: ["userName", "password", "url"], level: "read" });
const entries = [
  entry("0b6a0d9e-6e4e-4a8e-9a3f-1c2d3e4f5a61", "Serverovna — nouzový účet", "Provoz/Serverovna"),
  entry("5d1f7c2a-3b4e-4c5d-8e6f-7a8b9c0d1e2f", "Serverovna — UPS", "Provoz/Serverovna"),
  entry("9a8b7c6d-5e4f-4a3b-9c2d-1e0f9a8b7c6d", "Hlavní router", "Provoz/Síť"),
];
const api = { search: async (query: string) => entries.filter(item => `${item.title} ${item.path}`.toLowerCase().includes(query.toLowerCase())) } as unknown as SecretsApi;
const services: SecretsServices = {
  discover: async () => "available",
  requestAccess: async () => {},
  // Like the Atlas host: each value only in an element the host creates, cleared again on dispose.
  reveal: async ({ secretId, fields, ownerDocument }) => {
    const title = entries.find(item => item.id === secretId)?.title ?? "";
    const sample = { title, path: "Provoz", username: "admin", password: "ukázka-123", url: "https://ups.example.cz", notes: "Jen ukázka." };
    const values = Object.fromEntries(fields.map(field => {
      const element = ownerDocument.createElement("span");
      element.textContent = sample[field];
      return [field, element];
    }));
    return { values, dispose: () => Object.values(values).forEach(element => { element.textContent = ""; }) };
  },
  copy: async () => {},
  subscribeRevocation: () => () => {},
};
const swap = shallowRef<{ currentId: string; query: string; resolve: (secretId: string | null) => void }>();
function pickReplacement({ secretId }: SecretReplacementContext) {
  swap.value?.resolve(null);
  const current = entries.find(item => item.id === secretId);
  return new Promise<string | null>(resolve => { swap.value = { currentId: secretId, query: current?.path ?? "", resolve }; });
}
function answer(secretId: string | null) {
  const pending = swap.value;
  swap.value = undefined;
  pending?.resolve(secretId);
}
const plugins = [createSecretsPlugin({ services, pickReplacement })];
const page = ref<MarkdownRichValue>({
  format: "atlas-rich", version: 1, plugins: [{ id: "secrets", version: 4 }],
  document: { type: "doc", content: [
    { type: "secretReference", attrs: { secretId: entries[0]!.id, fields: ["username", "password"] } },
    { type: "paragraph", content: [{ type: "text", text: "Router: " }, { type: "secretInlineReference", attrs: { secretId: entries[2]!.id } }, { type: "text", text: " (heslo pro správu)." }] },
  ] },
});
</script>

<template>
  <SettingsSection
    title="Odkaz na heslo v editoru (karta a v textu)"
    description="Plugin secrets v @atlas/markdown: do stránky ukládá jen odkaz, hodnotu vydá host až po odkrytí. Upravuje se tam, kde je (P1): klik na kartu nebo na klíč a názvy polí hesla v textu (klepnutí na mlhu ji odkryje jako při čtení), Enter či F2 na vybraném odkazu otevře u něj panel zobrazení (která pole, Kopírovat, Detail, sekundy automatického skrytí; Použít je jeden krok zpět, Esc ho zahodí), druhý klik panel zavře a „Upravit“ v liště ho otevře na stejném místě a druhým stiskem zavře. V úpravách má karta přímo na sobě chips zobrazení: která pole ukáže a automatické skrytí, každá změna je jeden krok zpět; „Další…“ otevře tentýž panel. Výměna hesla je pojmenovaná akce, ne Enter: chip „Vyměnit heslo“ na kartě nebo „Vyměnit heslo“ v liště (i u hesla v textu) otevře SecretPicker v režimu swap, hledá ve skupině KeePassu, aktuální heslo je označené a výběr změní jen secretId, zobrazení zůstane. „Odebrat heslo“ vezme odkaz ze stránky, heslo v trezoru zůstává. Heslo v textu je sdílený čip v textu (Čip v textu): klíč a názvy polí, hodnoty pod mlhou, stejné klávesy a vybraný stav jako kontakt a úkol. Při čtení se karta nemění. Proč ne nový dialog: SecretPicker už je výběr hesla pro vložení i přesun do trezoru, swap je jeho třetí režim. Otevírá se jako popover u textu (openVuePopover, jako ostatní výběry v editoru): první výsledek je předvybraný, Enter ho vloží, Esc vrátí kurzor do editoru; ukázka níže ho kreslí na místě."
  >
    <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="page" profile="rich" ariaLabel="Ukázka odkazu na heslo" :plugins="plugins" :outline="editorOutline"
      v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
    <!-- In the page editor the picker is an editor popover (Esc returns the caret); the example has no caret, so it shows its content in place. -->
    <div v-if="swap" class="secret-example-picker">
      <SecretPicker :open="true" space-id="gallery" :api="api" mode="swap" :initial-query="swap.query" :current-id="swap.currentId" @insert="picked => answer(picked.secretId)" />
      <button type="button" class="secret-example-picker-close" @click="answer(null)">Zavřít</button>
    </div>
  </SettingsSection>
</template>

<style scoped>
.secret-example-picker { display: grid; gap: 8px; max-width: 420px; padding: 12px; border: 1px solid var(--line); border-radius: 10px; }
.secret-example-picker-close { justify-self: start; }
</style>
