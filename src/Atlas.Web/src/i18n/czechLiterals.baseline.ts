/*
 * Baseline of src/i18n/czechLiterals.guard.test.ts (Atlas 4.1 localization): the areas whose texts moved into src/i18n, and
 * the Czech lines each of their files still has (comments left out). The counts only go down, to an empty list.
 *
 * All of src is migrated (2026-11-07, consolidation sweep); the Czech source dictionaries (src/i18n) and the gallery
 * (components/dev) are not counted. What is left is Czech on purpose: page content the tool writes (template Markdown,
 * requirement phrases), words the tool recognizes (rules, regular expressions, search keywords), prompts sent to the model,
 * the Czech ids and group keys the code matches on, the interaction contract text (contracts/), the print page's chrome (a
 * headless browser has no language of its own) and the mock of the AI administration used by the gallery.
 */

/** Folders (ending in /) and files, relative to src/Atlas.Web. */
export const MIGRATED_AREAS: readonly string[] = [
  "src/",
];

export const CZECH_LITERAL_BASELINE: Record<string, number> = {
  "src/App.vue": 11,
  "src/components/settings/appearanceIcons.ts": 87,
  "src/assist/atlasAssist.ts": 1,
  "src/assist/conversions.ts": 1,
  "src/assist/liveEditorPlugins.ts": 2,
  "src/assist/loadAssist.ts": 1,
  "src/assist/resolvers.ts": 1,
  "src/assist/rules/catalog.ts": 5,
  "src/assist/rules/intentRules.ts": 10,
  "src/assist/rules/secretDetector.ts": 4,
  "src/assist/secretQuery.ts": 1,
  "src/assist/writing.ts": 3,
  "src/assistant/assistantEntries.ts": 9,
  "src/assistant/boardAi.ts": 1,
  "src/assistant/useAssistant.ts": 4,
  "src/boards/editorBlocks.ts": 13,
  "src/components/DocumentEditor.vue": 4,
  "src/components/PageCreationFlow.vue": 1,
  "src/components/assistant/AssistantPanel.vue": 1,
  "src/components/boards/BoardView.vue": 1,
  "src/components/settings/general/ai/aiAdminMock.ts": 25,
  "src/components/settings/global/GlobalSettings.vue": 1,
  "src/components/settings/jobs/jobFormat.ts": 1,
  "src/contracts/editability.ts": 1,
  "src/contracts/interaction.ts": 52,
  "src/keyboard/shortcuts.ts": 39,
  "src/motionLiterals.baseline.ts": 1,
  "src/overview/overviewNode.ts": 2,
  "src/pageSourceDraft.ts": 1,
  "src/pageTemplates.ts": 32,
  "src/print/PrintDocument.vue": 6,
  "src/print/main.ts": 1,
  "src/projector/RequirementCatalogPanel.vue": 1,
  "src/projector/composeDeploymentBoard.ts": 1,
  "src/projector/deploymentMap.ts": 2,
  "src/projector/requirementHeadingView.ts": 2,
  "src/projector/requirementInline.ts": 1,
  "src/projector/requirementOverview.ts": 2,
  "src/projector/requirementPhrases.ts": 3,
  "src/projector/requirementReadiness.ts": 6,
  "src/projector/requirementSummary.ts": 12,
  "src/projector/requirementsPlugin.ts": 6,
  "src/searchHighlight.ts": 1,
  "src/whatsNew/releaseNotes.ts": 1,
};
