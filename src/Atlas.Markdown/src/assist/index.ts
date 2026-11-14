/**
 * `@atlas/markdown/assist`: optional block suggestions and the proposal panel.
 *
 * A separate package entry with its own stylesheet (`@atlas/markdown/assist/style.css`). The core,
 * `./vue`, the root entry and the plugins never import it; without `createAssistPlugin` in an editor's
 * plugins it has no effect on behaviour, DOM, CSS, schema, keyboard or bundle size. It imports only the
 * public `@atlas/markdown/core`. See docs/assist.md.
 */
import { assistMarkersFor, createAssistPlugin } from './plugin'
import { openMarkdownProposalPanel } from './proposal'
import { projectMarkdownAssistSelection, readMarkdownAssistText } from './projection'
import { markdownAssistTargets } from './targets'

export { assistMarkersFor, createAssistPlugin, markdownAssistTargets, openMarkdownProposalPanel }
export { defaultAssistStrings } from './strings'
export type {
  AssistPluginOptions, AssistStrings, MarkdownAssistApplyContext, MarkdownAssistAvatar, MarkdownAssistBlock, MarkdownAssistProposal,
  MarkdownAssistNotice, MarkdownAssistRange, MarkdownAssistResult, MarkdownAssistSecondaryAction, MarkdownAssistSource, MarkdownAssistSuggestion,
  MarkdownAssistTarget, MarkdownAssistTargetKind, MarkdownAssistTrigger, MarkdownProposalOptions, MarkdownProposalResult,
} from './types'

/** The module namespace, for hosts that load it with `import()` and pass it on. */
export interface AssistModule {
  readonly createAssistPlugin: typeof createAssistPlugin
  readonly assistMarkersFor: typeof assistMarkersFor
  readonly openMarkdownProposalPanel: typeof openMarkdownProposalPanel
  readonly projectMarkdownAssistSelection: typeof projectMarkdownAssistSelection
  readonly readMarkdownAssistText: typeof readMarkdownAssistText
  readonly markdownAssistTargets: typeof markdownAssistTargets
  readonly defaultAssistStrings: typeof import('./strings').defaultAssistStrings
}
export { projectMarkdownAssistSelection, readMarkdownAssistText } from './projection'
export type { MarkdownAssistProjection } from './projection'
