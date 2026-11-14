/** Keys of the `concealed` section of `MarkdownStrings`; the Czech defaults live in plugins/concealed/. */
export interface ConcealedStrings {
  /** Chip label shown on a sealed block; also the slash-menu item label. */
  block: string
  reveal: string
  hide: string
  /** Reveal-mode picker option: reveal by scratching the mist away. */
  scratch: string
  /** Reveal-mode picker option: reveal with an explicit button. */
  button: string
  /** Toolbar button and popover title for the reveal-mode picker. */
  revealMode: string
  /** Toolbar button letting an author preview a sealed block without leaving editing. */
  preview: string
  /** Toolbar button/action that removes the concealment wrapper, keeping its content. */
  unwrap: string
  /** Toolbar button that deletes the concealed block entirely. */
  remove: string
  /** Short toolbar button text (icon + text) for the reveal-mode picker. */
  modeButtonText: string
  /** Short toolbar button text (icon + text) for the preview toggle. */
  previewButtonText: string
  /** Slash-menu item description. */
  slashDescription: string
  /** Toolbar action label (distinct from the chip's `block` label). */
  toolbarLabel: string
  /** Selection bubble menu action label. */
  selectionLabel: string
  /** Title and accessible name of the block's settings panel. */
  settings: string
  /** Short toolbar button text (icon + text) opening the settings panel. */
  settingsButtonText: string
  /** Hints of the two reveal modes in the settings panel. */
  scratchHint: string
  buttonHint: string
  /** Settings section: when the mist returns after the reader leaves. */
  reconcealHeading: string
  reconcealNow: string
  reconcealNowHint: string
  /** A timed choice, e.g. "Za 1,5 s"; `time` is already formatted. */
  reconcealAfter: (time: string) => string
  reconcealShortHint: string
  reconcealReadHint: string
  reconcealLongHint: string
  reconcealMinute: string
  reconcealMinuteHint: string
  reconcealNever: string
  reconcealNeverHint: string
  /** Settings section: uncover the rest once most of the block is scratched. */
  completeHeading: string
  completeOff: string
  completeOffHint: string
  completeHalf: string
  completeHalfHint: string
  /** A share choice, e.g. "Po 70 %". */
  completeAt: (percent: number) => string
  completeRecommendedHint: string
  completeMostHint: string
  /** Hint of a stored value outside the presets. */
  customHint: string
  /** Reminder in the panel that this is visual protection only. */
  settingsNote: string
  /** Chip summary of the re-cover setting. */
  summaryNever: string
  summaryNow: string
  summaryAfter: (time: string) => string
}
