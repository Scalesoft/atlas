import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `slash` section of `MarkdownStrings` (see ../strings.ts). */
export interface SlashStrings {
  /** Suggestion list accessible label, shared by the `/` slash menu and its Vue list. */
  menuLabel: string
  /** Visible heading above the slash menu's rows. */
  menuHeading: string
  calloutTitle: string
  calloutDescription: string
  heading1Title: string
  heading1Description: string
  heading2Title: string
  heading2Description: string
  heading3Title: string
  heading3Description: string
  heading4Title: string
  heading4Description: string
  heading5Title: string
  heading5Description: string
  heading6Title: string
  heading6Description: string
  bulletListTitle: string
  bulletListDescription: string
  orderedListTitle: string
  orderedListDescription: string
  taskListTitle: string
  taskListDescription: string
  timedListTitle: string
  timedListDescription: string
  blockquoteTitle: string
  blockquoteDescription: string
  codeBlockTitle: string
  codeBlockDescription: string
  horizontalRuleTitle: string
  horizontalRuleDescription: string
  tableTitle: string
  tableDescription: string
  /** Row of an action that takes a typed parameter after its name (`/kontakt Petr`): the action's label and the text typed after it. */
  parameterTitle(label: string, text: string): string
  /** Row of `/tabulka 3x4`: a table of `columns` × `rows`. */
  tableSizeTitle(columns: number, rows: number): string
  /** Row of `/kód ts`: a code block in `language`. */
  codeLanguageTitle(language: string): string
}

export const slashStrings: SlashStrings = registerMarkdownStrings('slash', {
  menuLabel: 'Slash menu příkazy',
  menuHeading: 'Vložit',
  calloutTitle: 'Informační box',
  calloutDescription: 'Poznámka, tip nebo upozornění',
  heading1Title: 'Nadpis 1',
  heading1Description: 'Velký nadpis sekce',
  heading2Title: 'Nadpis 2',
  heading2Description: 'Střední nadpis',
  heading3Title: 'Nadpis 3',
  heading3Description: 'Menší nadpis',
  heading4Title: 'Nadpis 4',
  heading4Description: 'Malý nadpis',
  heading5Title: 'Nadpis 5',
  heading5Description: 'Drobný nadpis',
  heading6Title: 'Nadpis 6',
  heading6Description: 'Nejmenší nadpis',
  bulletListTitle: 'Odrážkový seznam',
  bulletListDescription: 'Seznam s odrážkami',
  orderedListTitle: 'Číslovaný seznam',
  orderedListDescription: 'Seznam s čísly',
  taskListTitle: 'Checklist',
  taskListDescription: 'Úkoly s odškrtáváním',
  timedListTitle: 'Časová odrážka',
  timedListDescription: 'Poznámky s datem a časem zápisu',
  blockquoteTitle: 'Citace',
  blockquoteDescription: 'Blokový citát',
  codeBlockTitle: 'Blok kódu',
  codeBlockDescription: 'Víceřádkový kód, jazyk: /kód ts',
  horizontalRuleTitle: 'Vodorovná čára',
  horizontalRuleDescription: 'Oddělovač',
  tableTitle: 'Tabulka',
  tableDescription: 'Tabulka 3×3, velikost: /tabulka 4x5',
  parameterTitle: (label, text) => `${label}: vyhledat „${text}“`,
  tableSizeTitle: (columns, rows) => `Tabulka ${columns} × ${rows} (sloupce × řádky)`,
  codeLanguageTitle: language => `Blok kódu: ${language}`,
} satisfies SlashStrings)
