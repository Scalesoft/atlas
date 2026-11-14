import type { Editor } from "@tiptap/core";
import { emptyDocument } from "./document";
import t from "./i18n/components-creation";

/** A place for a person (contacts plugin `contactPlace`) in the editor's dialect: the unresolved chip with its role until someone is picked. */
const place = (role: string) => `[${role}](a-contact:?name=${encodeURIComponent(role)}&role=${encodeURIComponent(role)}&unresolved=true)`;

// Built-in starting content only. Created pages have no live link to these templates.
export const pageTemplates = [
  {
    value: "blank", get label() { return t.templates.blank.label; }, get description() { return t.templates.blank.description; }, markdown: "",
  },
  // P15 „Struktura z šablon“: the people are places (Tab goes to the next one, Enter picks the person, PMs first), so they end as contact
  // chips, not free text. With module Projector the requirements start with an empty requirement whose title takes „do …“ and „čekáme na“.
  {
    value: "project", get label() { return t.templates.project.label; }, places: true,
    get description() { return t.templates.project.description; },
    markdown: `## Lidé

PM: ${place("PM")}

Čekáme na: ${place("Čekáme na")}

## Cíl

## Termíny

## Požadavky

## Odkazy
`,
  },
  {
    value: "contacts", get label() { return t.templates.contacts.label; },
    get description() { return t.templates.contacts.description; },
    markdown: `## Tým

## Kontaktní osoby

| Role | Jméno | Kontakt | Zastupování |
| --- | --- | --- | --- |
| | | | |
| | | | |

## Podpora

| Kanál | Kontakt | Dostupnost |
| --- | --- | --- |
| | | |

## Eskalace

## Důležité odkazy
`,
  },
  {
    value: "deployment", get label() { return t.templates.deployment.label; },
    get description() { return t.templates.deployment.description; },
    markdown: `## Účel a přehled

## Prostředí a adresy

| Prostředí | Adresa | Účel |
| --- | --- | --- |
| | | |

## Komponenty a závislosti

| Komponenta | Verze | Závislosti |
| --- | --- | --- |
| | | |

## Konfigurace

| Nastavení | Popis | Zdroj hodnoty |
| --- | --- | --- |
| | | |

## Postup nasazení

### Příprava

### Nasazení

## Ověření výsledku

## Rollback

## Monitoring

## Odpovědný kontakt
`,
  },
  {
    value: "runbook", get label() { return t.templates.runbook.label; },
    get description() { return t.templates.runbook.description; },
    markdown: `## Kdy postup použít

## Předpoklady

## Postup

### Příprava

### Provedení

## Ověření výsledku

## Řešení potíží

| Problém | Možná příčina | Řešení |
| --- | --- | --- |
| | | |

## Odpovědný kontakt
`,
  },
] as const;

/**
 * Module Projector (U2-4): a deployment page described by requirements from the space's catalog. Its content is a summary
 * block and the way in, built as the rich document itself: Markdown has no requirement blocks.
 */
export const projectorTemplate = {
  value: "projector-deployment",
  get label() { return t.templates.catalog.label; },
  get description() { return t.templates.catalog.description; },
} as const;
// The way in („napište / a zvolte Moduly produktu“) is a placeholder on the empty line (shell.css), not text of the page
// that would be published and copied into every deployment (U3-3).
function projectorDeploymentContent() {
  const value = { format: "atlas-rich", version: 1, plugins: [{ id: "atlas-requirements", version: 2 }], document: { type: "doc", content: [
    { type: "overview", attrs: { id: crypto.randomUUID(), kind: "requirements" } },
    { type: "paragraph" },
  ] } };
  return { contentJson: JSON.stringify(value), markdown: "" };
}

/** `projector`: the space has module Projector, so a template's „Požadavky“ start with an empty requirement. */
export async function createPageTemplateContent(templateId: string, { projector = false } = {}) {
  if (templateId === projectorTemplate.value) return projectorDeploymentContent();
  const template = pageTemplates.find(item => item.value === templateId);
  if (!template) throw new Error("Vybraná šablona není dostupná.");
  if (!template.markdown) return { contentJson: JSON.stringify(emptyDocument()), markdown: "" };

  // Parse simple built-in content through the editor's public API. Keep its engine out of the initial shell bundle.
  // Places for people are contact chips: a rich editor with the contacts plugin reads the Markdown as the page editor does (as boardTextDocument).
  const places = "places" in template;
  const [{ createMarkdownEditorController, loadMarkdownDialect, parseMarkdownContent }, contacts] = await Promise.all([import("@atlas/markdown/core"),
    places ? import("@atlas/markdown/plugins/contacts") : undefined]);
  if (contacts) await loadMarkdownDialect();
  let reader: Editor | undefined;
  const controller = contacts
    ? createMarkdownEditorController({ profile: "rich", source: { kind: "local", value: emptyDocument() }, editable: false,
      plugins: [contacts.createContactsPlugin(), { id: "atlas-template-reader", apiVersion: 1, create: () => ({ onCreate: ({ editor }) => { reader = editor; } }) }] })
    : createMarkdownEditorController({ profile: "markdown-safe", source: { kind: "local", value: template.markdown }, editable: false });
  try {
    const detach = controller.attachView(document.createElement("div"), { ariaLabel: "Obsah šablony stránky" });
    try {
      if (reader) reader.commands.setContent(parseMarkdownContent(reader, template.markdown).toJSON());
      const projection = controller.getProjection();
      const rich = projector && places ? withEmptyRequirement(projection.rich) : projection.rich;
      return { contentJson: JSON.stringify(rich), markdown: projection.markdown };
    } finally { detach(); }
  } finally { controller.destroy(); }
}

/**
 * An empty requirement heading one level under the „Požadavky“ heading, as /Požadavek inserts it: its title is the place to write, its
 * „do …“ writes its facts line, „čekáme na“ its party. A heading, never the legacy block (the server's conversion would refuse a titleless one).
 */
function withEmptyRequirement(rich: { plugins?: readonly { id: string; version: number }[]; document: { content?: readonly unknown[] } }) {
  const content = [...rich.document.content ?? []];
  const at = content.findIndex(node => (node as { type?: string; content?: { text?: string }[] }).type === "heading" && (node as { content?: { text?: string }[] }).content?.[0]?.text === "Požadavky");
  if (at < 0) return rich;
  const level = Number((content[at] as { attrs?: { level?: unknown } }).attrs?.level ?? 1);
  content.splice(at + 1, 0, { type: "heading", attrs: { level: Math.min(6, level + 1), state: "planned" } });
  return { ...rich, plugins: [...(rich.plugins ?? []).filter(plugin => plugin.id !== "atlas-requirements"), { id: "atlas-requirements", version: 4 }], document: { ...rich.document, content } };
}
