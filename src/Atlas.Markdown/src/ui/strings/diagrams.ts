/** Keys of the `diagrams` section of `MarkdownStrings`; the Czech defaults live in plugins/diagrams/. */
export interface DiagramsStrings {
  /** Module label (toolbar/slash item) and the fallback `alt` text for the rendered image. */
  title: string
  /** Second line of the slash menu item. */
  slashDescription: string
  /** Slash rows of `/diagram sekvence` and the other starting diagrams. */
  kindFlowchart: string
  kindSequence: string
  kindClass: string
  kindState: string
  kindGantt: string
  sourceRequired: string
  htmlNotAllowed: string
  /** `<summary>` text disclosing the raw source below the rendered diagram. */
  sourceSummary: string
  /** Label/aria-label for the Mermaid source textarea. */
  sourceLabel: string
  hideSource: string
  /** Title above the inline source editor panel. */
  sourceEditorTitle: string
  captionLabel: string
  captionPlaceholder: string
  editHint: string
  emptySource: string
  invalidSource: string
  /** Render-failed status text; `keptPreviousImage` prefixes a note that the last valid preview is still shown. */
  renderFailed: (message: string, keptPreviousImage: boolean) => string
  saveFailed: string
}
