<script setup lang="ts">
import { ref } from "vue";
import { MarkdownEditor, MarkdownRenderer } from "@atlas/markdown/vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { createChartsPlugin } from "@atlas/markdown/plugins/charts";
import "@atlas/markdown/styles/base.css";
import { editorChrome, useEditorTheme } from "../../editor/editorSetup";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";

// Local sample data only; nothing leaves the gallery. The editor is the real one with the real charts plugin.
const theme = useEditorTheme();
const plugins = [createChartsPlugin()];
const cell = (text: string, header = false) => ({ type: header ? "tableHeader" : "tableCell", content: [{ type: "paragraph", content: [{ type: "text", text }] }] });
const row = (cells: string[], header = false) => ({ type: "tableRow", content: cells.map((text) => cell(text, header)) });
const page = ref<MarkdownRichValue>({
  format: "atlas-rich",
  version: 1,
  plugins: [{ id: "charts", version: 1 }],
  document: {
    type: "doc",
    content: [
      { type: "chart", attrs: { data: { version: 1, title: "Příjmy podle čtvrtletí", kind: "bar", source: null, sourceStatus: null,
        grid: [["Čtvrtletí", "Kč"], ["Q1", "1 250"], ["Q2", "1 800"], ["Q3", "1 430"]] } } },
      { type: "paragraph", content: [{ type: "text", text: "Tabulka níže: označte buňky a vložte „Graf z výběru tabulky“, nebo v grafu zvolte „Upravit data“." }] },
      { type: "table", content: [row(["Měsíc", "Příjem", "Výdaj"], true), row(["Leden", "1 250", "100"]), row(["Únor", "2 000", "150"])] },
    ],
  },
});
</script>

<template>
  <SettingsPage title="Graf" description="Graf (slash „Graf“, plugin charts) se upravuje tam, kde je vidět; nepřidává žádnou jinou navigaci. Použijte ho pro několik číselných řad; pro tabulková data bez grafu zůstává tabulka.">
    <SettingsSection title="Vyzkoušejte"
      description="Klik nebo Enter na název ho přepíše přímo v grafu (Enter nebo opuštění uloží, Esc vrátí, Ctrl+Z krok zpět). Čip s typem („Sloupcový“) otevře volbu typu a použije ji hned. Klik na tabulku dat pod grafem, Enter na grafu nebo „Upravit: Graf“ otevře editor dat: první buňka je předvybraná, Ctrl+Enter použije změny, Esc je zahodí; klik mimo panel nebo odchod fokusu z něj platné změny použije, neplatné nechá panel otevřený s chybou (D3), a graf zůstane vybraný. U grafu propojeného s tabulkou tlačítko „Upravit data“ přenese kurzor do zdrojové tabulky; „Odpojit od tabulky“ z něj udělá vlastní data. Oproti ostatním komponentám je tu mřížka více buněk, proto zůstává koncept a „Použít“ místo ukládání po buňce.">
      <div class="chart-example">
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="page" profile="rich" ariaLabel="Ukázka grafu" :plugins="plugins"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
    </SettingsSection>
    <SettingsSection title="Akce čtenáře"
      description="Ve čtečce i v editoru má blok (graf, diagram, vzorec) tytéž tlačítka v rohu, po najetí nebo zaostření: zvětšit a kopírovat zdroj (addMarkdownReadActions, .md-read-actions). Použijte je pro to, co smí každý, kdo stránku vidí; úpravy jsou jinde (klik na název, Upravit data) a jen pro pisatele. Geometrie je stejná pro čtenáře i pisatele, takže zamčení stránky nic neposune. Nová tlačítka tam nepřidávejte, dokud nejde o akci pro každého čtenáře.">
      <div class="chart-example" data-example-read-actions>
        <MarkdownRenderer :value="page" profile="rich" :plugins="plugins" ariaLabel="Ukázka grafu při čtení" :class="theme" />
      </div>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.chart-example { border: 1px solid var(--line); border-radius: var(--radius-md, 10px); min-height: 320px; }
</style>
