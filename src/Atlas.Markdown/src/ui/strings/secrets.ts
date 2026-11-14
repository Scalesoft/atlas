/** Keys of the `secrets` section of `MarkdownStrings`; the Czech defaults live in plugins/secrets/. */
type SecretFieldName = 'title' | 'path' | 'username' | 'password' | 'url' | 'notes'

export interface SecretsStrings {
  // Mirrors the plugin's public `SecretsLabels` option (createSecretsPlugin merges these with
  // `options.labels` at factory time; keep this text and the option's own defaulting behavior).
  card: string
  reveal: string
  hide: string
  copy: string
  copied: string
  copyFailed: string
  details: string
  collapse: string
  /** Placeholder for a field the entry does not have. */
  missing: string
  request: string
  pending: string
  sending: string
  /** Ordinary requestable content only; password requests show their button at rest. */
  hold: string
  denied: string
  /** The one-line message beside the visible request button: the values are hidden, access can be requested. */
  requestHint: string
  /** A request that ended without access (rejected, cancelled or expired); the button offers a new one. */
  refused: string
  /** Sending a request failed; the button stays for another try. */
  requestFailed: string

  /** Field names shown beside released values (and in copy button names). */
  fieldLabels: Record<SecretFieldName, string>
  /** Field names shown as tiles in the anchored settings panel. */
  settingsFieldLabels: Record<SecretFieldName, string>
  /** Hints shown under each field tile in the settings panel. */
  fieldHints: Record<SecretFieldName, string>

  /** Settings popover title for an inline reference. */
  inlineSettingsTitle: string
  /** Settings popover title for a card reference. */
  cardSettingsTitle: string
  /** Selection-owned toolbar's accessible name for an inline reference. */
  inlineToolbarLabel: string
  /** Selection-owned toolbar's accessible name for a card reference. */
  cardToolbarLabel: string
  /** The toolbar's edit button („Upravit“, P12), the same as a click on the reference, Enter or F2: opens the settings popover. */
  settingsButtonText: string
  /** Toolbar button that removes the reference from the page („Odebrat“, P12); the secret itself stays in the vault. */
  removeLabel: string
  /** „Vyměnit heslo“: the card chip and the toolbar button's accessible name; the host picker chooses another secret. */
  swapLabel: string
  /** Short toolbar button text of the swap. */
  swapButtonText: string
  /** Accessible name of the edit chips on a card in edit mode. */
  cardEditLabel: string
  /** Auto-hide chip on the card, given the delay in seconds. */
  autoHideChip: (seconds: number) => string
  /** Card chip that opens the settings popover with the less common options. */
  moreSettingsText: string

  inlineFieldsHeading: string
  cardFieldsHeading: string
  actionsHeading: string
  autoHideHeading: string
  actionCopyLabel: string
  actionCopyHint: string
  actionDetailsLabel: string
  actionDetailsHint: string
  autoHideTimedLabel: string
  autoHideTimedHint: string
  autoHideNeverLabel: string
  autoHideNeverHint: string
  delayLabel: string
  noFieldsError: string
  /** Validation error for the auto-hide delay input, given its allowed range. */
  secondsRangeError: (min: number, max: number) => string
  applyFailedError: string

  /** Diagnostic for a `:secret{…}` directive in page Markdown that is not a valid reference. Never echoes the text. */
  markdownInvalidReference: string

  /** Accessible name of the anchored access-request panel. */
  requestReasonTitle: string
  /** Label of the reason text area when a reason is required. */
  requestReasonLabel: string
  /** Label of the reason text area when a reason is optional. */
  requestReasonOptionalLabel: string
  requestReasonPlaceholder: string
  /** Submit button of the request panel. */
  requestReasonSubmit: string
  requestReasonRequiredError: string
  /** The reason is longer than the limit, given the limit in characters. */
  requestReasonTooLongError: (max: number) => string
}
