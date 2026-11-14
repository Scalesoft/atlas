<script setup lang="ts">
import { defineAsyncComponent, ref, shallowRef } from "vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { loadEditorAssist } from "../../../assist/loadAssist";
import type { LinkCandidate, LinkCandidateFetch } from "../../../assist/linkSuggestions";
import type { AtlasAssistContext, AtlasAssistFactory } from "../../../assist/types";
import EmptyState from "../../settings/EmptyState.vue";
import { Button } from "../../ui/button";
import SettingsSection from "../../settings/SettingsSection.vue";

// E2 „Propojení zmínek“ (plan §19): the real page editor (DocumentEditor) with the real lazily loaded adapter; only the
// link candidates are local, so nothing reaches a server. Prototype: „Odkazy a úpravy textu“, moment 1 (the link marker
// after a finished paragraph) and moment 2 (Propojit: the link's colour and underline draw from the left, 280 ms; the
// module's tokens, so the gallery's „Zpomalit 3×“ and „Omezený pohyb“ apply). Search, not a model: the type icon `link`,
// never the AI glyph. The pinned editor (0.9.1) has everything E2 needs; until the editor change of feature/assist-w4
// is released, the marker stays after „Propojit“ until the caret leaves the next paragraph (Propojit again is no change).
const DocumentEditor = defineAsyncComponent(() => import("../../DocumentEditor.vue"));

const pages = {
  vpn: { id: "5a0e8c1e-2b7d-4c55-9a51-6f0c1d2e3a01", title: "Připojení k VPN", space: "IT a infrastruktura" },
  backup: { id: "5a0e8c1e-2b7d-4c55-9a51-6f0c1d2e3a02", title: "Zálohování a obnova", space: "Provoz" },
  desk: { id: "5a0e8c1e-2b7d-4c55-9a51-6f0c1d2e3a03", title: "Service Desk", space: "Provoz" },
};
type Sample = keyof typeof pages;
/** What the server would answer for these phrases: an exact title, an alias and one page close in meaning. */
const answers: Record<string, [Sample, LinkCandidate["matchedBy"]]> = {
  VPN: ["vpn", "title"],
  SD: ["desk", "alias"],
  "noční zálohy serverů": ["backup", "meaning"],
};
const linkCandidates: LinkCandidateFetch = async ({ phrases }) => phrases.flatMap(phrase => {
  const answer = answers[phrase];
  if (!answer) return [];
  const page = pages[answer[0]];
  return [{ phrase, pageId: page.id, spaceId: "gallery", spaceName: page.space, title: page.title, matchedBy: answer[1] }];
});

const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const value: MarkdownRichValue = {
  format: "atlas-rich",
  version: 1,
  document: {
    type: "doc",
    content: [
      paragraph("Před vzdálenou správou si nastavte VPN podle návodu."),
      paragraph("Kontrolu, že noční zálohy serverů doběhly, hlaste přes SD."),
      { type: "paragraph" },
    ],
  },
};

const local: Partial<AtlasAssistContext> = {
  pageId: () => "5a0e8c1e-2b7d-4c55-9a51-6f0c1d2e3a00",
  linkCandidates,
  // The gallery has no account: every suggestion type on, and meaning matches as with the capability `editorLinks`.
  suggestionTypeEnabled: () => true,
  editorLinks: () => true,
  pages: {
    search: async () => [],
    resolve: async id => {
      const page = Object.values(pages).find(item => item.id === id);
      return page ? { id, title: page.title, url: `#/pages/${id}` } : null;
    },
  },
  notify: message => { status.value = message; },
  warn: message => { status.value = message; },
};
const assist = (loaded: AtlasAssistFactory): AtlasAssistFactory => context => loaded({ ...context, ...local });

const factory = shallowRef<AtlasAssistFactory | null>();
const status = ref("");
const loading = ref(false);
async function start() {
  loading.value = true;
  const loaded = await loadEditorAssist({ features: { editorAssist: true } });
  factory.value = loaded && assist(loaded);
  loading.value = false;
}
</script>

<template>
  <SettingsSection title="Propojení zmínek (E2)" description="assist/linkSuggestions.ts — zmínka stránky v dokončeném odstavci se nabídne k propojení: značka s ikonou odkazu a dvě akce, „Ponechat text jako odkaz“ (výchozí, věta se nemění) a „Nahradit názvem stránky“ (komponenta, která sleduje přejmenování). Každá je jeden krok Zpět. Je to hledání (přesný název, alias, se schopností editorLinks i význam), ne výstup modelu, proto bez ikony AI. Proč ne SuggestionChip: návrh v textu stránky nese značka editoru u bloku, stejně jako ostatní Návrhy při psaní. Klikněte do odstavce a stiskněte Alt+Enter (nebo odstavec upravte a přejděte na další), Enter propojí, šipka dolů a Enter nahradí názvem.">
    <div class="link-example" data-link-suggestion-example>
      <Button v-if="factory === undefined" class="link-example-start" variant="outline" :disabled="loading" data-link-suggestion-start @click="start">
        {{ loading ? "Načítám editor…" : "Otevřít editor se zmínkami" }}
      </Button>
      <EmptyState v-else-if="factory === null" icon="info" title="Návrhy při psaní nejsou k dispozici" text="Modul asistence editoru se nepodařilo načíst." />
      <template v-else-if="factory">
        <p class="link-example-status" role="status">{{ status }}</p>
        <component :is="DocumentEditor" :value="value" :readonly="false" :dark="false" accent="blue" space-id="gallery" document-title="Vzdálená správa" :assist="factory" />
      </template>
    </div>
  </SettingsSection>
</template>

<style scoped>
.link-example { display: grid; gap: 8px; min-width: 0; padding-left: 36px; }
.link-example-start { justify-self: start; }
.link-example-status { min-height: 1.4em; margin: 0; color: var(--muted); font-size: 12px; }
</style>
