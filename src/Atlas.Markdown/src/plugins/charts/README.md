# Charts module

Import `createChartsPlugin`, `insertChart`, `chartNode`, and
`insertChartFromSelection` from `@atlas/markdown/plugins/charts`.
Register `createChartsPlugin()` when creating a **rich** controller. The core
entry does not import this module. Install the optional peer `echarts` (`^6.1.0`).
Data/schema load synchronously; the editor grid and Apache ECharts load on first use.
The renderer uses modular `echarts/core`, the bar/line/pie series, grid, legend,
tooltip and SVG renderer. Doughnut charts use a pie series with an inner radius.
A chart outside a document (Atlas.Web's page history) uses the same renderer:
`(await loadChartRenderer()).createChartRenderer(host, data, look)`, where `ChartLook` stacks the series,
sets their colours, makes a compact strip, draws the last series as diamond marks, softens all but one
category and takes a tooltip text and a click per category.

```ts
controller.dispatch(insertChart({
  version: 1,
  title: 'Příjmy',
  kind: 'bar',
  grid: [['Měsíc', 'Kč'], ['Leden', '1 250,50'], ['Únor', '1800']],
  source: null,
  sourceStatus: null,
}))

// Select a rectangular area in an editor table first. It must include a header
// row, category column and at least one numerical value. Merged cells are excluded.
controller.dispatch(insertChartFromSelection({ title: 'Graf z tabulky', kind: 'line' }))
```

The table selection action is available in the toolbar. The graph is inserted
after the table without replacing selected cells. Chart settings contain labelled
cell inputs, row/series add and remove controls, chart type and title. Linked
values are edited in the source table; **Odpojit a upravit vlastní data** converts
the current snapshot into editable standalone data. To use a different table
selection, create a new linked chart from that selection.

`grid[0]` contains series names and the first column contains category labels.
The remaining cells accept finite numbers with decimal comma or point, optional
properly grouped space separators and a sign. Blank values are chart gaps;
pie/doughnut represent them as zero and reject negative numbers. Limits are
200 data rows, 20 series and 2,000 characters per cell. Labels use plain text.

The plugin adds persisted `chartRef` attributes to tables, rows and cells on
document changes. References follow the selected **cell identities** when rows
or columns move; the original graph order is preserved. New rows/columns do not
automatically expand the selection. Duplicate IDs from paste are regenerated,
preserving the original table's identity even when a copy is pasted before it.

Linked snapshots update in the same history event as the source edit. Missing
tables, deleted/merged selected cells or invalid numeric edits retain the last
valid snapshot and show a status message. Returning valid source data resumes
updates. Rendering includes an accessible data table and remains readable if
ECharts cannot load. One renderer instance remains attached to a chart block;
data/type changes update it through `setOption`, and removing the block disposes
the instance and resize observers. Size changes follow the chart container.

Editing happens where the chart is shown. The title is edited in place (click or
Enter; Enter or blur saves, Esc cancels). The chart type is a chip next to it that
opens the type picker and applies at once. A click on the data table under the
chart (or Enter on the block) opens the data editor: the grid stays a draft with
**Použít** because it has many cells, the first cell is focused and Ctrl+Enter
applies. For a linked chart the editor offers **Upravit data**, which moves the
caret into the first source cell (scrolled into view); **Odpojit od tabulky** stays.

The type picker shows labelled SVG previews. Selecting a type preserves the
data grid, title and source binding. ECharts provides short ordinary update
transitions; `prefers-reduced-motion` disables them. This implementation does
not enable Universal Transition, dataset transforms, or multiple rendering
engines. Tooltip labels are rendered as text, never document-supplied HTML.

Rich JSON is authoritative. Markdown export includes the title and human-readable
data rows and explicitly reports loss; it cannot preserve presentation or live
table references. Static HTML contains the same values before lazy enhancement.

Implementation references:
- [Apache ECharts modular imports](https://echarts.apache.org/handbook/en/basics/import/)
- [Apache ECharts chart sizing and disposal](https://echarts.apache.org/handbook/en/concepts/chart-size/)
- [Apache ECharts dynamic updates](https://echarts.apache.org/handbook/en/how-to/data/dynamic-data/)
