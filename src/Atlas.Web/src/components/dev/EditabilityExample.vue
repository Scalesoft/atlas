<script setup lang="ts">
import { ref } from "vue";
import { MarkdownEditor, MarkdownRenderer } from "@atlas/markdown/vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { emojiNode } from "@atlas/markdown/plugins/emoji";
import { contactNode, inlineContactNode } from "@atlas/markdown/plugins/contacts";
import "@atlas/markdown/styles/base.css";
import { atlasContentPlugins, type DocumentReadServices } from "../../documentWidgets";
import { editorChrome, useEditorTheme } from "../../editor/editorSetup";
import { Switch } from "../ui/switch";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";

// Local sample data only. Each component sits in the page editor's real content plugins, built once; „Upravovat“ switches the right to
// write as DocumentEditor does (readonly and canWrite()), without new plugins or a new editor. Beside it the static render of history
// and the public view. The contract 3.6-0: all three look the same, editing adds only affordances (src/contracts/editability.ts).
const theme = useEditorTheme();
const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const page = (plugin: { id: string; version: number }, ...content: object[]): MarkdownRichValue =>
  ({ format: "atlas-rich", version: 1, plugins: [plugin], document: { type: "doc", content } }) as MarkdownRichValue;
const staticPlugins = atlasContentPlugins({ kind: "history" });
const requirement = (id: string, title: string, attrs: object, ...nested: object[]) =>
  ({ type: "requirement", attrs: { id: `0b6f7c1e-5d2a-4f3b-9c8d-1e2f3a4b5c${id}`, ...attrs }, content: [paragraph(title), ...nested] });
const requirements = [
  requirement("01", "Certifikát NIA", { key: "nia", direction: "inbound", state: "waiting_for_input", date: "2026-07-21", party: "IT KBN", where: "Keepass" }),
  requirement("02", "Napojení na ÚZIS", { key: "uzis", direction: "inbound", state: "in_progress", system: "ÚZIS", requires: "nia" },
    requirement("03", "Testovací přístup", { key: "uzis-test", state: "done", date: "2026-09-29" })),
];

/** `services`: what the component needs to show itself (secrets); the static render is then the page read without the right to write. */
function sample(name: string, value: MarkdownRichValue, services?: DocumentReadServices) {
  const editable = ref(true);
  return { name, value: ref(value), editable, plugins: atlasContentPlugins({ kind: "edit", ...services, canWrite: () => editable.value }),
    staticPlugins: services ? atlasContentPlugins({ kind: "edit", ...services, canWrite: () => false }) : staticPlugins };
}
// A vault that answers every discovery with access and reveals nothing: the card, not a secret, is what is compared.
const secretsDemo: NonNullable<DocumentReadServices["secrets"]> = { discover: async () => "available", requestAccess: async () => {}, reveal: async () => { throw new Error("Ukázka nic neodhaluje."); },
  copy: async () => {}, subscribeRevocation: () => () => {} };
const jana = { name: "Jana Nováková", role: "PM projektu", organization: "Modelsoft", emails: ["jana@example.com"], phones: ["+420 777 111 222"] };
const secretAttrs = { secretId: "0b9f6a52-7c1e-4d3a-9a58-3f2b1c4d5e6f", field: "password", fields: null, showCopy: true, showDetails: false, autoHideSeconds: null };
// A component that meets the contract, or is being aligned with it, adds its sample here.
const samples = [
  sample("Emoji", page({ id: "emoji", version: 1 }, { type: "paragraph", content: [{ type: "text", text: "Hotovo " }, emojiNode("grinning")] })),
  sample("Skrytý obsah", page({ id: "concealed", version: 2 }, paragraph("Úvod."), { type: "concealedContent", content: [paragraph("Překvapení v pátek.")] })),
  sample("Graf", page({ id: "charts", version: 1 }, { type: "chart", attrs: { data: { version: 1, title: "Příjmy podle čtvrtletí", kind: "bar", source: null,
    sourceStatus: null, grid: [["Čtvrtletí", "Kč"], ["Q1", "1 250"], ["Q2", "1 800"], ["Q3", "1 430"]] } } })),
  sample("Kontakty", page({ id: "contacts", version: 2 }, contactNode(jana) as object, { type: "paragraph", content: [{ type: "text", text: "Ptejte se " }, inlineContactNode(jana) as object] })),
  sample("Odkaz na stránku", page({ id: "pages", version: 1 }, { type: "paragraph", content: [{ type: "text", text: "Viz " },
    { type: "pageReference", attrs: { id: "page-1", title: "Nasazení", url: "/?page=page-1" } }] })),
  sample("YouTrack", page({ id: "youtrack", version: 1 }, { type: "paragraph", content: [{ type: "text", text: "Úkol " },
    { type: "youtrackIssue", attrs: { id: "2-1", key: "PX2-1", title: "Zrychlit načtení", url: "https://youtrack.example/issue/PX2-1", state: "Open" } }] })),
  sample("Tajemství", page({ id: "secrets", version: 4 }, { type: "secretReference", attrs: secretAttrs },
    { type: "paragraph", content: [{ type: "text", text: "Heslo: " }, { type: "secretInlineReference", attrs: secretAttrs }] }), { secrets: secretsDemo }),
  sample("Kotvy nadpisů", page({ id: "heading-anchors", version: 1 }, { type: "heading", attrs: { level: 2, headingId: "intro-1" }, content: [{ type: "text", text: "Úvod" }] }, paragraph("Text pod nadpisem."))),
  sample("Požadavky", page({ id: "atlas-requirements", version: 2 }, ...requirements)),
  sample("Přehled požadavků", page({ id: "atlas-requirements", version: 2 }, { type: "overview", attrs: { id: "5d1b8a3e-2c47-4e9f-8a60-7b3c9d2e1f40", kind: "requirements" } }, ...requirements)),
  sample("Diagram", page({ id: "diagrams", version: 2 }, { type: "diagram", attrs: { data: { version: 1, source: "flowchart LR\n  A[Požadavek] --> B[Schválení] --> C[Nasazení]", caption: "Postup zakázky" } } })),
  sample("Vzorec", page({ id: "math", version: 1 }, { type: "mathBlock", attrs: { data: { version: 1, expression: "E = mc^2" } } })),
  sample("Obsah", page({ id: "toc", version: 1 }, { type: "tableOfContents", attrs: { data: { version: 1, title: "Obsah dokumentu", minLevel: 1, maxLevel: 3 } } },
    { type: "heading", attrs: { level: 1 }, content: [{ type: "text", text: "Úvod" }] }, paragraph("Text úvodu."),
    { type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: "Podrobnosti" }] }, paragraph("Text podrobností."))),
  sample("Příloha", page({ id: "attachments", version: 1 }, { type: "attachment", attrs: { data: { version: 1, name: "Zpráva.pdf", url: "/files/report.pdf", size: 2048, mimeType: "application/pdf" } } })),
  sample("OpenAPI", page({ id: "openapi", version: 1 }, { type: "openapi", attrs: { data: { version: 1, filename: "openapi.yaml", source: "openapi: 3.1.0\ninfo:\n  title: Zakázky\n  version: '1.0'\npaths: {}\n" } } })),
];
</script>

<template>
  <SettingsPage title="Editovatelnost komponent"
    description="Komponenta dokumentu vypadá stejně ve statickém renderu, ve čtení i při úpravách; úpravy přidají jen afordance (najetí, menu, kurzor). Přepínač „Upravovat“ odebere nebo vrátí právo zápisu za běhu, jako zámek nebo ztráta práva: otevřený popover se zavře, nic se nezapíše a editor se nepřestaví.">
    <SettingsSection v-for="item in samples" :key="item.name" :title="item.name">
      <label class="editability-switch"><Switch v-model="item.editable.value" />Upravovat</label>
      <div class="editability-states">
        <figure>
          <figcaption>Statický render</figcaption>
          <MarkdownRenderer :value="item.value.value" profile="rich" :ariaLabel="`${item.name}: statický render`" :plugins="item.staticPlugins" variant="document" :class="[theme, 'atlas-document-editor']" />
        </figure>
        <figure>
          <figcaption>{{ item.editable.value ? "Editor: úpravy" : "Editor: čtení" }}</figcaption>
          <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="item.value.value" profile="rich" :ariaLabel="`${item.name}: editor`" :plugins="item.plugins"
            :readonly="!item.editable.value" v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
        </figure>
      </div>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.editability-switch { display: inline-flex; align-items: center; gap: .5rem; margin-bottom: .75rem; }
.editability-states { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 18rem), 1fr)); align-items: start; gap: 1rem; }
.editability-states figure { margin: 0; border: 1px solid var(--line); border-radius: var(--radius-md, 10px); min-width: 0; }
.editability-states figcaption { padding: .4rem .75rem; border-bottom: 1px solid var(--line); color: var(--muted); font-size: .8125rem; }
</style>
