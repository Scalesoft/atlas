/** Keys of the `charts` section of `MarkdownStrings`; the Czech defaults live in plugins/charts/. */
export interface ChartsStrings {
  /** Slash menu item and toolbar button label for the chart block module. */
  label: string
  /** Second line of the slash menu item. */
  slashDescription: string
  /** Caption/summary for the accessible data table under a chart's visual. */
  dataCaption: string
  /** Default title shown for a newly inserted chart before the user renames it. */
  newChartTitle: string
  /** Toolbar action inserting a chart from a selected table range. */
  fromTableActionLabel: string
  fromTableActionDescription: string
  /** Default chart title when none is supplied while inserting from a table selection. */
  fromTableDefaultTitle: string
  kindBar: string
  kindLine: string
  kindPie: string
  kindDoughnut: string
  hintBar: string
  hintLine: string
  hintPie: string
  hintDoughnut: string
  /** Toolbar button label and type-picker popover title. */
  typeLabel: string
  /** aria-label for the group of chart-kind choice buttons. */
  kindGroupAriaLabel: string
  /** "{kind} graf: {title}. Hodnoty jsou v tabulce dat níže." aria-label for the chart visual. */
  visualAriaLabel: (kindLabel: string, title: string) => string
  renderFailed: string
  linked: string
  sourceMissing: string
  sourceInvalid: string
  retryRender: string
  editTitle: string
  titleFieldLabel: string
  linkedHelp: string
  manualHelp: string
  addRowButton: string
  addColumnButton: string
  detachButton: string
  useSelectionButton: string
  addRowAriaLabel: string
  addColumnAriaLabel: string
  useSelectionReadyTitle: string
  useSelectionEmptyTitle: string
  linkedDataCaption: string
  /** Button in the data editor of a linked chart: moves the caret into the source table. */
  editSourceButton: string
  /** aria-label/title of the chart title when it is editable in place. */
  titleEditLabel: string
  /** aria-label of the chart type chip; the argument is the current kind label. */
  typeChipLabel: (kindLabel: string) => string
  /** Title of the data table under the chart when a click opens the editor. */
  dataTableEditTitle: string
  /** The panel's data were changed by someone else while it was open: confirming again writes the draft over them. */
  changedMeanwhile: string
  editDataCaption: string
  categoryNameAriaLabel: string
  /** "Název řady {index}" aria-label for a series-name header input. */
  seriesNameAriaLabel: (index: number) => string
  /** "Popisek {row}" aria-label for a category-name row input. */
  rowLabelAriaLabel: (row: number) => string
  /** "Řádek {row}, řada {series}" aria-label for a data-cell input. */
  cellAriaLabel: (row: number, series: number) => string
  /** "Odebrat řadu {index}" label/title for a remove-series button. */
  removeSeriesLabel: (index: number) => string
  /** "Odebrat řádek {row}" label/title for a remove-row button. */
  removeRowLabel: (row: number) => string
  /** Default header text for a newly added series column, e.g. "Řada 3". */
  newSeriesName: (index: number) => string
  notANumber: (value: string) => string
  numberTooLarge: string
  gridNeedsHeaderAndRows: string
  gridNeedsLabelsAndSeries: string
  rowsMustMatchWidth: string
  cellTooLong: string
  /** "Řádek {row}, sloupec {col}: {message}" wrapping a cell-level validation error. */
  cellError: (row: number, col: number, message: string) => string
  invalidChartData: string
  invalidVersionOrKind: string
  titleTooLong: string
  negativeValuesNotAllowed: string
  invalidTableReference: string
  mergedCellsNotSupported: string
  invalidSourceStatus: string
  /** Markdown dialect: the ```a-chart body is not an a-YAML header, a `---` line and a GFM table. */
  invalidMarkdownGrid: string
  /** Markdown dialect: no table with this `{#id}` paragraph exists for the chart's `source`. */
  linkedTableMissing: (id: string) => string
  /** Markdown dialect warning: the chart's grid differs from its linked table, whose values are used. */
  linkedTableWins: (id: string) => string
}
