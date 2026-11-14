import { registerMarkdownStrings, type MarkdownStrings } from '../../core'

/** Default (Czech) text of the `charts` section that the plugin entry renders. */
const entry = {
  label: 'Graf',
  slashDescription: 'Sloupcový, čárový nebo koláčový graf z čísel',
  // Also the slash argument (`/graf koláč`), so in the entry.
  kindBar: 'Sloupcový',
  kindLine: 'Spojnicový',
  kindPie: 'Koláčový',
  kindDoughnut: 'Prstencový',
  dataCaption: 'Data grafu',
  newChartTitle: 'Nový graf',
  fromTableActionLabel: 'Graf z výběru tabulky',
  fromTableActionDescription: 'Vyberte záhlaví, popisky a číselné hodnoty.',
  fromTableDefaultTitle: 'Graf z tabulky',
  linked: 'Propojeno s tabulkou',
  notANumber: value => `„${value}“ není číslo. Použijte například 1 234,50.`,
  numberTooLarge: 'Číslo je příliš velké.',
  gridNeedsHeaderAndRows: 'Graf potřebuje záhlaví a 1–200 datových řádků.',
  gridNeedsLabelsAndSeries: 'Graf potřebuje popisky a 1–20 datových řad.',
  rowsMustMatchWidth: 'Řádky tabulky musí mít stejný počet buněk.',
  cellTooLong: 'Buňka musí být text do 2 000 znaků.',
  cellError: (row, col, message) => `Řádek ${row}, sloupec ${col}: ${message}`,
  invalidChartData: 'Neplatná data grafu.',
  invalidVersionOrKind: 'Neplatná verze nebo typ grafu.',
  titleTooLong: 'Název grafu může mít nejvýše 300 znaků.',
  negativeValuesNotAllowed: 'Koláčové a prstencové grafy potřebují nezáporné hodnoty.',
  invalidTableReference: 'Neplatný odkaz na tabulku.',
  mergedCellsNotSupported: 'Graf nepodporuje sloučené buňky.',
  invalidSourceStatus: 'Neplatný stav propojení grafu.',
} satisfies Partial<MarkdownStrings['charts']>
/** Keys registered here; ./runtimeStrings adds the rest when the lazy runtime loads. */
export type ChartsEntryKey = keyof typeof entry
export const chartsStrings: Pick<MarkdownStrings['charts'], ChartsEntryKey> = registerMarkdownStrings('charts', entry)
