<script setup lang="ts">
import { onBeforeUnmount, ref } from "vue";
import { MarkdownEditor } from "@atlas/markdown/vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import "@atlas/markdown/styles/base.css";
import { createDocumentWidgets } from "../../documentWidgets";
import { editorChrome, useEditorTheme } from "../../editor/editorSetup";
import { useOpenApiDetail } from "../../openApiDetail";
import OpenApiDetailPanels from "../OpenApiDetailPanels.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";

// The API block as the page editor shows it, with the page's own detail dialog (DocumentEditor wires the same two parts):
// local sample specification, nothing leaves the gallery.
const theme = useEditorTheme();
const detail = useOpenApiDetail({ readonly: () => false });
onBeforeUnmount(detail.dispose);
const plugins = createDocumentWidgets({ kind: "edit", uploaderName: "Jana Nováková", openDetail: detail.openDetail });
const source = "openapi: 3.1.0\ninfo:\n  title: Objednávky\n  version: '1.2'\npaths:\n  /orders:\n    get:\n      responses:\n        '200': { description: Seznam objednávek }\n";
const page = ref<MarkdownRichValue>({ format: "atlas-rich", version: 1, plugins: [{ id: "openapi", version: 1 }],
  document: { type: "doc", content: [{ type: "paragraph" }, { type: "openapi", attrs: { data: { version: 1, filename: "objednavky.yaml", source } } }, { type: "paragraph" }] } });
</script>

<template>
  <SettingsPage title="API ve stránce" description="Blok API (lomítko „API“) je ve stránce karta pevné výšky bez vlastního posuvníku; dokumentace a YAML se otevírají v panelu přes celou výšku okna.">
    <SettingsSection title="Karta API upravitelná na místě"
      description="Klepnutí na název souboru ho přepíše (Enter uloží, Esc vrátí), tři volby režimu (Dokumentace a vyzkoušení / Dokumentace / Pouze stažení) se použijí hned a jdou vrátit zpět. „Nahrát soubor“ nebo přetažení souboru .yaml, .yml, .json na kartu nahradí specifikaci po stejné kontrole jako YAML; chyba se ukáže pod kartou a stránka se nezmění. Volte tuto kartu místo samostatného formuláře: pole nejsou odděleně od toho, co čtenář uvidí.">
      <div class="openapi-example">
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="page" profile="rich" ariaLabel="Ukázka bloku API" :plugins="plugins"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
    </SettingsSection>
    <SettingsSection title="Detail API: Dokumentace a YAML"
      description="„Dokumentace“ na kartě nebo Enter na vybraném bloku otevře panel přes celou výšku (na telefonu celou obrazovku, AppDialog size=&quot;side&quot;). Posouvá se jen jeho obsah: Scalar (vyzkoušení vypnuté), stránka za ním stojí. Autor má vedle záložku YAML (Monaco se načte až s ní, kontrola jako dosud); „Použít“ zapíše změnu do bloku jako jeden krok zpět, neuložený návrh se před zavřením potvrdí. Čtenář vidí jen Dokumentaci. Esc nebo Zavřít vrátí výběr na blok. Zdroj stránky je jiný případ: přepínač Dokument | Zdroj jen vymění stránku za její Markdown." />
  </SettingsPage>
  <OpenApiDetailPanels :detail="detail" />
</template>

<style scoped>
.openapi-example { border: 1px solid var(--line); border-radius: var(--radius-md, 10px); min-height: 220px; }
</style>
