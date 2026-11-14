import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `source` section of `MarkdownStrings` (see ../strings.ts). */
export interface SourceStrings {
  /** Shown in the source-editor skeleton once loading Monaco takes longer than ~300 ms. */
  loadingLabel: string

  // Node-kind words used to build a protected block's label ("Chráněný blok: <kind> „<title>“").
  kindParagraph: string
  kindHeading: string
  kindBulletList: string
  kindOrderedList: string
  kindTaskList: string
  kindBlockquote: string
  kindCodeBlock: string
  kindTable: string
  kindImage: string
  kindHorizontalRule: string
  kindCallout: string
  kindChart: string
  kindContactCard: string
  kindAttachment: string
  kindOpenapi: string
  kindMathBlock: string
  kindDiagram: string
  kindTableOfContents: string
  /** Fallback label for a node type with no dedicated word above, e.g. "Blok myNode". */
  kindUnknown: (type: string) => string

  sourceTooLarge: string
  /** "Výsledný dokument není platný: {message}" when the edited source no longer parses back. */
  invalidResultDocument: (message: string) => string
  /** "Markdown nelze načíst: {message}" for a segment that fails to parse. */
  markdownParseFailed: (message: string) => string
  /** A known protected fence id found inside another, unclosed code fence. */
  protectedBlockInsideUnclosedCode: (id: string, fenceLine: number, fenceMarker: string) => string
  protectedLanguageReserved: string
  /** An `md-protected` fence id in the text that the session never protected. */
  unknownProtectedBlock: (id: string) => string
  /** A protected fence id repeated a second time in the text. */
  duplicateProtectedBlock: (id: string, firstLine: number) => string
  /** A protected fence whose label line no longer matches the block. */
  protectedBlockContentChanged: (id: string, label: string) => string
  /** A protected fence missing its closing line. */
  protectedBlockMustClose: (id: string) => string
  unknownHeadingId: (id: string) => string
  duplicateHeadingId: (id: string) => string
  headingNameRemoved: (id: string) => string

  /** Rejection message/live-region announcement, e.g. "…Vyberte celý blok (Ctrl+Shift+B) a smažte…". */
  protectedEditRejectedMessage: (shortcut: string) => string
  selectProtectedBlockLabel: string
  /** Hover/glyph-margin tooltip markdown for a protected block. */
  protectedBlockHover: (label: string, shortcut: string) => string
}

export const sourceStrings: SourceStrings = registerMarkdownStrings('source', {
  loadingLabel: 'Načítá se editor zdroje…',

  kindParagraph: 'Odstavec',
  kindHeading: 'Nadpis',
  kindBulletList: 'Seznam',
  kindOrderedList: 'Číslovaný seznam',
  kindTaskList: 'Seznam úkolů',
  kindBlockquote: 'Citace',
  kindCodeBlock: 'Blok kódu',
  kindTable: 'Tabulka',
  kindImage: 'Obrázek',
  kindHorizontalRule: 'Oddělovač',
  kindCallout: 'Zvýraznění',
  kindChart: 'Graf',
  kindContactCard: 'Kontakty',
  kindAttachment: 'Příloha',
  kindOpenapi: 'API',
  kindMathBlock: 'Matematický vzorec',
  kindDiagram: 'Diagram',
  kindTableOfContents: 'Obsah dokumentu',
  kindUnknown: type => `Blok ${type}`,

  sourceTooLarge: 'Zdroj stránky je příliš velký (nejvýše 5 MiB).',
  invalidResultDocument: message => `Výsledný dokument není platný: ${message}`,
  markdownParseFailed: message => `Markdown nelze načíst: ${message}`,
  protectedBlockInsideUnclosedCode: (id, fenceLine, fenceMarker) =>
    `Chráněný blok „${id}“ je uvnitř neuzavřeného bloku kódu z řádku ${fenceLine}. Uzavřete blok kódu řádkem ${fenceMarker}.`,
  protectedLanguageReserved: 'Jazyk „md-protected“ je vyhrazen pro chráněné bloky. Chráněný blok začíná na začátku řádku přesně jako ```md-protected <id>; pro ukázku kódu zvolte jiný jazyk.',
  unknownProtectedBlock: id => `Neznámý chráněný blok „${id}“. Chráněné bloky nelze vytvářet ani přejmenovávat.`,
  duplicateProtectedBlock: (id, firstLine) => `Chráněný blok „${id}“ je v textu podruhé (poprvé na řádku ${firstLine}). Chráněný blok lze přesunout nebo smazat, ne duplikovat.`,
  protectedBlockContentChanged: (id, label) => `Obsah chráněného bloku „${id}“ byl změněn. Vraťte původní řádek „${label}“, nebo smažte celý blok.`,
  unknownHeadingId: id => `Nadpis s ID „${id}“ na stránce není. Nový nadpis pište bez {#…}; ID existujícího nadpisu neměňte.`,
  headingNameRemoved: id => `Nadpis „${id}“ by přišel o své ID a s ním o stav požadavku nebo dřívější názvy: vraťte {#${id} …}, nebo smažte celý nadpis.`,
  duplicateHeadingId: id => `ID nadpisu „${id}“ je v textu dvakrát. Každý nadpis má své ID; kopii pište bez {#…}.`,
  protectedBlockMustClose: id => `Chráněný blok „${id}“ musí končit řádkem \`\`\` hned za svým textem; jeho obsah nelze upravovat.`,

  protectedEditRejectedMessage: shortcut => `Chráněný blok nelze upravovat po částech. Vyberte celý blok (${shortcut}) a smažte nebo přesuňte ho vcelku.`,
  selectProtectedBlockLabel: 'Vybrat chráněný blok',
  protectedBlockHover: (label, shortcut) => `**Chráněný blok** · ${label}\n\nLze ho jen celý smazat nebo přesunout. ${shortcut} vybere celý blok.`,
} satisfies SourceStrings)
