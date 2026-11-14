/**
 * `@atlas/markdown/schema`: the document schema of an editor configuration as data, for build tools and servers
 * (docs/document-validation.md). Its own entry, so editors never load it.
 */
export { describeMarkdownSchema } from './describe'
export type { DescribeMarkdownSchemaOptions, MarkdownSchemaAttribute, MarkdownSchemaManifest, MarkdownSchemaMark, MarkdownSchemaNode, MarkdownSchemaPluginEntry, MarkdownSchemaEntity, MarkdownSchemaRecognizer } from './describe'
export { builtinStructuredValidators } from './builtin'
export { defineMarkdownAttributeValidator, markdownValueConstraintOf } from './constraints'
export type { MarkdownConstraintEnforcement, MarkdownJsonSchema, MarkdownStructuredValidator, MarkdownValueConstraint } from './constraints'
