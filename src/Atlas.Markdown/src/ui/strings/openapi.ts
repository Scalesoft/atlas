/** Keys of the `openapi` section of `MarkdownStrings`; the Czech defaults live in plugins/openapi/. */
export interface OpenApiStrings {
  /** Module label: slash/toolbar item, and the "Edit: …"/"Remove: …" toolbar buttons in core's block module. */
  label: string
  /** Second line of the slash menu item. */
  slashDescription: string
  /** Whole plain-text summary sentence rendered for the block (fallback preview, copy/paste, Markdown export). */
  summaryText: (summary: { title: string; version: string; operations: number; schemas: number; author?: string }) => string
  /** Operation/schema counts shown under the title in the rendered HTML preview. */
  /** What a stub, a copy and a projection say of a spec before it is parsed. */
  lightSummary: (title: string, version: string, filename: string) => string
  statsLine: (operations: number, schemas: number) => string
  /** Author line shown under the stats in the rendered HTML preview and the card, when the document declares one. */
  authorLine: (author: string) => string

  // Rendered card (runtime.ts).
  /** Small caption above the card title. */
  eyebrow: string
  /** Mode/action label: view the interactive documentation and try requests. */
  tryItLabel: string
  /** Mode/action label: view documentation only, no requests. */
  docsOnlyLabel: string
  downloadLabel: string
  /** Card summary line: version and operation/schema counts. */
  versionSummary: (version: string, operations: number, schemas: number) => string
  /** Selection-owned toolbar action that downloads the OpenAPI source. */
  downloadAction: string
  /** Accessible name for the full-page documentation dialog. */
  dialogLabel: string
  /** Accessible name for the documentation dialog's close button (distinct from the shared `close` word). */
  closeDocumentationLabel: string

  // Reference loading (reference.ts).
  loadingDocumentation: string
  timeoutError: string
  loadFailedError: (reason: string) => string

  // Scalar frame (scalarView.ts). English by design: Scalar's own configuration and iframe title.
  scalarModelsLabel: string
  frameTitle: (filename: string) => string
  scalarStartFailed: string
  scalarRenderFailed: (error: string) => string
  scalarLoadFailed: string

  // Source editor dialog (sourceDialog.ts).
  editTitle: string
  maximizeLabel: string
  restoreLabel: string
  closeEditorLabel: string
  filenameLabel: string
  uploadLabel: string
  /** The house button of the file field (the native one speaks the browser's language). */
  chooseFileLabel: string
  noFileChosen: string
  modeGroupLabel: string
  downloadOnlyLabel: string
  /** Accessible name for the source editor; shared with the standalone Monaco mount. */
  sourceLabel: string
  draftHint: string
  /** In-place controls on the card (edit mode). */
  editSpecLabel: string
  uploadFileLabel: string
  renameFileLabel: string
  notSpecFileError: string
  editorLoadFailed: string

  // Validation errors (data.ts), shown as `cause.message` in the source dialog's alert.
  emptySourceError: string
  sourceTooLargeError: string
  invalidSyntaxError: (message: string) => string
  invalidAliasError: (message: string) => string
  rootMustBeObjectError: string
  unsupportedVersionError: string
  missingInfoError: string
  missingPathsError: string
  invalidPathsError: string
  missingResponsesError: (method: string, path: string) => string
  externalRefError: string
  unsupportedRefError: string
  invalidPointerError: string
  refNotFoundError: (ref: string) => string
  cyclicAliasError: string
  tooComplexError: string
  invalidDataError: string
  invalidModeError: string
  invalidUploaderError: string
}
