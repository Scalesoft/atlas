<script setup lang="ts">
import { nextTick, onMounted, shallowRef } from "vue";
import { MarkdownEditor } from "@atlas/markdown/vue";
import { loadMarkdownDialect, prepareMarkdownModulesForExport } from "@atlas/markdown/core";
import { createCodeBlockHighlighting } from "@atlas/markdown/plugins/editor-tools";
import { atlasContentPlugins } from "../documentWidgets";
import { readDocument } from "../document";
import { formatPragueDay } from "../formatDate";
import { editorChrome, editorOutline } from "../editor/editorSetup";
import type { PrintSnapshot } from "./main";
import { dropNeutralFilter } from "./printImages";
const props = defineProps<{ snapshot: PrintSnapshot }>();
const emit = defineEmits<{ ready: []; failure: [error: unknown] }>();
const value = readDocument(props.snapshot.contentJson);
// The page's one editor, read-only, as its reader sees it (owner: one editor, no static renderer): requirement headings with their
// checkbox, chips, state and tint, the overviews over the live document. No host: history's policy (no writes, no directories).
const plugins = atlasContentPlugins({ kind: "history" });
const shows = (detail: string) => props.snapshot.details.includes(detail);
const date = (input: string) => formatPragueDay(input);
// The editor takes its code block and the `a-` dialect once, at creation (DocumentEditor waits for both the same way).
const codeBlock = shallowRef<Awaited<ReturnType<typeof createCodeBlockHighlighting>>>();
const loaded = shallowRef(false);
let created: () => void, failed: (error: unknown) => void;
const editorReady = new Promise<void>((resolve, reject) => { created = resolve; failed = reject; });
onMounted(async () => {
  try {
    [codeBlock.value] = await Promise.all([createCodeBlockHighlighting(), loadMarkdownDialect()]);
    loaded.value = true;
    await editorReady;
    await nextTick();
    const root = document.querySelector<HTMLElement>(".print-content")!;
    root.querySelectorAll("details").forEach(el => { el.open = true; });
    const preparation = await prepareMarkdownModulesForExport(root);
    if (preparation.fallback) throw new Error("Některý graf nebo diagram nelze připravit pro tisk.");
    // Rich tables use a first row of <th> inside tbody; a real thead repeats on each printed page. A read-only view ignores DOM changes.
    root.querySelectorAll<HTMLTableElement>("table").forEach(table => {
      const row = table.rows[0];
      if (!table.tHead && row?.cells.length && Array.from(row.cells).every(cell => cell.tagName === "TH")) table.createTHead().append(row);
    });
    await document.fonts.ready;
    // ProseMirror's separator after a trailing inline atom is an empty <img>, no picture.
    await Promise.all(Array.from(root.querySelectorAll<HTMLImageElement>("img:not(.ProseMirror-separator)"), async image => {
      dropNeutralFilter(image);
      image.loading = "eager";
      await image.decode();
      if (!image.naturalWidth) throw new Error("Obrázek není dostupný.");
    }));
    await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
    root.querySelectorAll<HTMLAnchorElement>("a[href]").forEach(link => {
      const href = link.getAttribute("href")!;
      // Without a configured public origin (a recurring export) Atlas links stay relative.
      if (props.snapshot.sourceOrigin && (href.startsWith("#/") || href.startsWith("/") && !href.startsWith("//"))) link.href = new URL(href, props.snapshot.sourceOrigin).href;
    });
    emit("ready");
  } catch (error) { emit("failure", error); }
});
</script>
<template>
  <main>
    <section class="print-cover">
      <p class="print-kicker">{{ snapshot.space }} · Dokumentace</p>
      <h1>{{ snapshot.title }}</h1>
      <dl v-if="['revision', 'state', 'revisionDate', 'exportedAt'].some(shows)" class="print-metadata">
        <div v-if="shows('revision')"><dt>Revize dokumentu</dt><dd>{{ snapshot.version }}</dd></div>
        <div v-if="shows('state')"><dt>Stav revize</dt><dd>{{ snapshot.isPublished ? 'Publikovaná' : 'Pracovní' }}</dd></div>
        <div v-if="shows('revisionDate')"><dt>Datum revize</dt><dd>{{ date(snapshot.activeFrom) }}</dd></div>
        <div v-if="shows('exportedAt')"><dt>Exportováno</dt><dd>{{ date(snapshot.exportedAt) }}</dd></div>
      </dl>
      <p v-if="shows('pageId')" class="print-identity">ID stránky · {{ snapshot.pageId }}</p>
    </section>
    <article class="print-content">
      <h1 class="print-document-title">{{ snapshot.title }}</h1>
      <!-- @vue-generic {'rich'} -->
      <MarkdownEditor v-if="loaded" :model-value="value" profile="rich" ariaLabel="Tisková sestava dokumentu" :plugins="plugins" readonly
        :code-block="codeBlock" v-bind="editorChrome" :outline="editorOutline" :show-block-add-button="false" :fullscreenable="false"
        :resource-policy="{ maxImagePixels: 16_000_000, maxCanvasPixels: 16_000_000 }" class="atlas-document-editor" @created="created()" @failed="failed" />
    </article>
  </main>
</template>
