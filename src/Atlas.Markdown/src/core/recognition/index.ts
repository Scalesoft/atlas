export { foldMarkdownText, nameKey, stemOf, tokenize } from './text'
export type { MarkdownToken } from './text'
export type { MarkdownLexicon, MarkdownLexiconPage, MarkdownLexiconPerson, MarkdownLexiconSource } from './lexicon'
export { ISSUE_KEY_DENY, ISSUE_KEY_PATTERN } from './patterns'
// The index and the decisions (recognize.ts) are the lazy engine's (engine.ts): exported, they would load with every editor.
export type { MarkdownRecognitionDecision, MarkdownRecognitionEntry, MarkdownRecognitionHit, MarkdownRecognitionIndex } from './recognize'
export { isProseBlock, isRemoteTransaction, proseText, recognitionWindow, RECOGNITION_ENDS, RECOGNITION_WINDOW, typedTrigger } from './gate'
export { dismissMarkdownRecognition, markdownRecognitionAt, markdownRecognitionCandidates, MarkdownRecognition, refreshMarkdownRecognition, registerMarkdownRecognizer, RECOGNITION_LATE_MS, RECOGNITION_UNDERLINE_LIMIT } from './runtime'
export type { MarkdownRecognizer } from './runtime'
