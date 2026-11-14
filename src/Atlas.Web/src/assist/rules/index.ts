// Assist rules: pure TypeScript, no editor or Vue import (plan §0.3). The editor plugin (H2) and the publication
// check (E) both use them, the latter also with assist switched off.
export { intentCatalog, intentCatalogVersion, intentDefinition, isIntentId } from "./catalog";
export type { IntentDefinition, IntentIcon, IntentId, IntentVariant } from "./catalog";
export { detectDocumentIntents, detectIntents } from "./intentRules";
export type { CalloutVariant, DocumentOutline, DocumentRuleHit, IntentRuleOptions, RuleHit } from "./intentRules";
export { detectSecrets } from "./secretDetector";
export type { SecretHit, SecretKind } from "./secretDetector";
export { createSecretExamples, fnv1a, sessionSecretExamples } from "./examples";
export type { SecretExamples } from "./examples";
export { isSpanInside, spanOf, spanText } from "./span";
export type { TextSpan } from "./span";
export { linkPhraseLimits, linkPhrases } from "./linkPhrases";
export type { LinkPhrase, LinkPhraseOptions } from "./linkPhrases";
