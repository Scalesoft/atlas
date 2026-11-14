# Atlas: validating documents with Atlas.Markdown.Sync

From 0.16.0 the editor owns document validity and `Atlas.Markdown.Sync` enforces it (see
[document validation](document-validation.md)). Atlas's own port (`EditorDocumentSchema` and its manifest, rules and
corpus) agreed with the editor on the same verdict corpus and is replaced as follows.

## Replace the port

1. Upgrade the editor and Sync to 0.16.0 together (the embedded manifest belongs to that editor release).
2. Create one validator for the plugin set Atlas binds everywhere (`contentPlugins()` of `editorSchema.test.ts`:
   editor-tools, attachments, the document widgets, heading-anchors, youtrack; their schema plugins are exactly
   `tests/fixtures/host-document-schema.json`, i.e. all thirteen built-in schema plugins):

   ```csharp
   services.AddSingleton(MarkdownDocumentValidator.Builtin);
   // Narrow it if Atlas ever drops a plugin: new MarkdownDocumentValidator(MarkdownSchemaManifest.Builtin.WithPlugins(ids))
   ```

   Atlas configures no image source policy, so the built-in manifest's image rule (any http(s), relative, data:image or
   blob source) is Atlas's. A policy, a codeBlock or another plugin would mean exporting Atlas's own manifest with
   `describeMarkdownSchema(contentPlugins(), { profile: 'rich', resourcePolicy })` in a web test and loading it with
   `MarkdownSchemaManifest.Parse`.
3. On every write path, call the validator where `EditorDocumentSchema.Require`/`Find` is called today (page create and
   update API, MCP content tools, imports and migrations, restore from history, backup import, templates) and keep the
   400 contract:

   ```csharp
   if (validator.Validate(envelope) is { } violation)
       throw new ApiException(400, $"Obsah editoru neodpovídá schématu editoru Atlasu: {violation.Path} {CzechReason(violation)}. …",
           "invalid_document", new Dictionary<string, object?> { ["path"] = violation.Path, ["code"] = violation.Code });
   ```

   Messages are English; map `violation.Code` (stable, `MarkdownDocumentViolationCodes`) to Czech texts if the API keeps
   Czech reasons. Paths keep Atlas's form (`document.content[12].attrs.width`).
4. Parse stored rich JSON with `MarkdownDocumentValidator.JsonOptions` (the same depth as `EditorDocumentSchema.JsonOptions`).
5. Collaboration: `AddAtlasMarkdownContentGuard()` now validates every value and the resulting document with the
   same manifest. Atlas's `IReservedNodeGuard` for secret references keeps working; secret ids that are not UUIDs are now
   refused before it is asked (they never were valid editor content). Documents bound with a schema may only hold the
   content of their bound plugins.

## Delete

- `src/Atlas.Server/Platform/Validation/EditorDocumentSchema.cs`, `EditorSchema.cs`, `EditorAttributeKinds.cs`,
  `EditorModuleData.cs`, `EditorValues.cs`, `EditorSchemaRules.cs` and `editor-schema.json` (and its embedding).
- The manifest and corpus generator in `src/Atlas.Web/src/editorSchema.test.ts`, with
  `tests/fixtures/editor-schema/web-document.json` and `verdicts.json`, and
  `tests/Atlas.Server.Tests/Content/EditorVerdictCorpusTests.cs`: the editor repository generates and checks the same
  corpus against the Sync validator, with the same deliberate server-stricter list.
- Keep `SecretReferenceContent` for what it does besides validation (collecting and stripping references, the reserved
  node guard of collaboration). Its use as the rule of the secret reference attributes goes: the manifest declares
  them (`secretId` as the editor's UUID pattern, `field`, `fields`, the booleans and `autoHideSeconds`).

## What stays the same

The verdicts: the editor's corpus is Atlas's corpus (1,481 cases) with the secrets probes at their real bounds
(`autoHideSeconds` 5–3600, the six fields), and the deliberate list is Atlas's: heading levels 1–6, JSON types of
unvalidated attributes, script links, nesting beyond 64 nodes, and the OpenAPI YAML parse the server does not repeat.
