<script setup lang="ts">
import { ref } from "vue";
import { MarkdownEditor } from "@atlas/markdown/vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { createAttachmentsPlugin } from "@atlas/markdown/plugins/attachments";
import "@atlas/markdown/styles/base.css";
import { editorChrome, useEditorTheme } from "../../editor/editorSetup";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";

// Local sample only: the "upload" waits a moment and returns an object URL, nothing leaves the gallery.
const theme = useEditorTheme();
const plugins = [createAttachmentsPlugin({ upload: { upload: async file => {
  await new Promise(done => setTimeout(done, 400));
  return { name: file.name, url: URL.createObjectURL(file), size: file.size, mimeType: file.type || undefined };
} } })];
const attachment = (data: Record<string, unknown>) => ({ type: "attachment", attrs: { data: { version: 1, ...data } } });
const page = ref<MarkdownRichValue>({ format: "atlas-rich", version: 1, plugins: [{ id: "attachments", version: 1 }],
  document: { type: "doc", content: [
    attachment({ name: "Průvodce.pdf", url: "/files/guide.pdf", size: 248000, mimeType: "application/pdf", description: "Pokyny pro nový tým" }),
    attachment({ name: "Nová příloha", url: "" }),
  ] } });
</script>

<template>
  <SettingsPage title="Příloha" description="Karta přílohy se upravuje přímo na místě. Použijte ji pro soubor ke stažení (PDF, dokument, archiv); obrázek patří do obrázku a odkaz do textu.">
    <SettingsSection title="Vyzkoušejte"
      description="Prázdná karta má tlačítko „Nahrát soubor“ a přijme soubor přetažený na kartu. Karta se souborem nabízí malé „Nahradit“. Klik nebo Enter na názvu či popisu je přepíše přímo na kartě: Enter nebo opuštění pole uloží, Esc zruší, v popisu ukládá Ctrl+Enter; změnu vrátí Ctrl+Z. Odkaz na soubor je schovaný pod „Další“. Chyba nahrávání se ukáže na kartě. Zvolená příloha otevře názvem editaci z klávesnice (Enter), „Stáhnout“ dál stahuje.">
      <div class="attachment-example">
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="page" profile="rich" ariaLabel="Ukázka přílohy" :plugins="plugins"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.attachment-example { border: 1px solid var(--line); border-radius: var(--radius-md, 10px); min-height: 220px; }
</style>
