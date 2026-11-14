# Document validation

The editor owns the rules of a valid document, and the server enforces the same rules on every channel: a stored rich
document (`atlas-rich` JSON) and every collaborative Yjs payload. Nothing the editor would refuse reaches storage or
other peers.

```
@atlas/markdown                                   Atlas.Markdown.Sync
  extensions + defineMarkdownAttributeValidator  ──►    MarkdownSchemaManifest (embedded Builtin, or a host export)
  describeMarkdownSchema(plugins, options)       ──►    MarkdownDocumentValidator   ← host write paths (REST, import, MCP…)
  schema/markdown-schema.json (built-in plugins)        YjsContentPolicy → ManagedDocumentContentGuard ← collaboration
  verdict corpus (the editor's real open path)   ──►    MarkdownDocumentValidatorCorpusTests
```

## The manifest

`describeMarkdownSchema(plugins, options)` (from `@atlas/markdown/schema`) describes the schema of an editor
configuration without creating a view: pass the same plugin factories and the same `profile`, `resourcePolicy` and
`codeBlock` the host gives its editor. The result is plain JSON:

- `schemaRevision`, `profile`, `topNode`;
- `plugins`: id, schema version and the older versions the editor migrates when it opens a document (`migratesFrom`);
- `nodes`: name, owning plugin, `inline`, `groups`, the ProseMirror `content` expression, the marks allowed on its
  content, and its attributes;
- `marks` in rank order, with the marks each one `excludes` and its attributes;
- every attribute: its `default` (absent when it is required), the plugin that contributes it to a node it does not
  own (`owner`, e.g. heading-anchors' `headingId`), its value `constraint` and its `enforcement`;
- `validators`: a description and JSON-schema-like `schema` of every structured validator a constraint names;
- `entities`: the kind of entity each inline atom shows (`person`, `issue`, `page`…), the plugin that declares it and
  the node; `recognizers`: how text becomes a kind, by the name of a pattern; `patterns`: those regular expression
  sources, written so JavaScript (flag `u`) and .NET read them alike (no `\d`, `\w`, `\s`, `\b` or their negations, which .NET
  reads as any Unicode digit, letter or space; `defineMarkdownEntityKind` refuses them). A plugin declares them with
  `defineMarkdownEntityKind` (its `entities`), and the manifest test fails on an inline atom without a kind;
- `limits`: table cell and span bounds.

The manifest of every built-in plugin is generated into `packages/markdown/schema/markdown-schema.json`, shipped as
`@atlas/markdown/schema.json` and embedded in `Atlas.Markdown.Sync` (`MarkdownSchemaManifest.Builtin`).
`src/schema/manifest.test.ts` fails when it no longer matches the editor; regenerate it, the web document fixture and
the verdict corpus together and run the Sync tests:

```sh
ATLAS_UPDATE_SCHEMA=1 pnpm --filter @atlas/markdown exec vitest run --configLoader runner src/schema/manifest.test.ts
dotnet test tests/Atlas.Markdown.Sync.Tests
```

## Value constraints

Each attribute declares its rule next to its editor validator, so the two are read together:

```ts
variant: { default: 'note', validate: defineMarkdownAttributeValidator({ kind: 'enum', values: variants }, value => { … }) }
```

The editor keeps running its own function; the declaration is what servers read. Kinds
(`src/schema/constraints.ts` documents each one exactly):

| Kind | Rule |
| --- | --- |
| `enum` | one of `values` (optionally `nullable`) |
| `integer`, `number` | `Number.isInteger` or a finite number within `minimum`–`maximum` |
| `boolean` | true or false |
| `text` | a string with `minLength`/`maxLength`, `singleLine` (no control characters), `nonBlank`, an anchored `pattern` |
| `dimension` | a number, null or a short single-line string (HTML width/height) |
| `url` | `image` (the host's image source policy), `http` (http(s) or relative, no credentials), `absolute-http`, `link` (no `javascript:`, `vbscript:` or `data:`) |
| `structured` | a named validator (`imagePresentation`, `columnWidths`, `secretFields`, the module data validators); `context` names sibling attributes it reads |

Attributes of Tiptap's own extensions, which carry no validator of ours, are classified in `src/schema/builtin.ts`.
`enforcement: "editor"` rules are what the editor refuses on load (attribute validators and
`engine/builtinDocumentValidation.ts`). `enforcement: "server"` marks the deliberately stricter server rules for
attributes the editor stores unvalidated: their JSON type (lists' `tight` and `start`, `codeBlock.language`, image
`alt`/`title`/`width`/`height`, link attributes, `emoji.name`), heading levels 1–6, and no script links. A block module
names its data validator with `dataValidator` (`defineMarkdownBlockModule`); without one the name is
`<id>:<name>`, which a server must register.

The manifest test fails on an attribute without a declaration. Agreement of the declarations with the editor is proven
by the verdict corpus: the same test generates about 1,500 variants of every node, mark, attribute and data field
(boundaries, wrong types, URL kinds, plugin versions, table geometry, depth), opens each through the real editor
(`createMarkdownEditorController` and `attachView`) and keeps the verdicts in
`tests/Atlas.Markdown.Sync.Tests/Fixtures/document-schema/verdicts.json`. `MarkdownDocumentValidatorCorpusTests`
requires the .NET validator to agree, apart from a commented list of the server rules above (an entry that excuses no
case fails, so the list stays exact). The editor's YAML/OpenAPI parse of an OpenAPI block's source is the one check the
server does not repeat.

## Validating a stored document (.NET)

```csharp
var validator = MarkdownDocumentValidator.Builtin;                       // every built-in plugin
// or: new MarkdownDocumentValidator(MarkdownSchemaManifest.Builtin.WithPlugins(["math", "charts"]))
// or: new MarkdownDocumentValidator(MarkdownSchemaManifest.Parse(hostExport), hostValidators)

if (validator.Validate(json) is { } violation)                           // string, UTF-8 bytes or JsonElement
    return Problem(400, violation.Message, extensions: new { path = violation.Path, code = violation.Code });
```

- `Validate` returns the first `MarkdownDocumentViolation` (JSON path such as `document.content[3].attrs.level`, a stable
  `Code` from `MarkdownDocumentViolationCodes`, an English message) or null; invalid input never throws.
- The walk is linear and bounded: at most `MaxDepth` (64) node levels, JSON depth `JsonMaxDepth` (2 × 64 + 8; use
  `MarkdownDocumentValidator.JsonOptions` when parsing stored content yourself), content expressions and patterns on the
  non-backtracking engine, tables bounded (1000 cells) before their geometry is mapped.
- A host with other plugins exports its own manifest (`describeMarkdownSchema(hostPlugins)`) and passes implementations
  of its structured validators; construction fails when one is missing, never a validation.
- Validators are immutable and thread-safe: create one per configuration and reuse it.

## Collaboration

`AddAtlasMarkdownContentGuard()` validates collaborative content with the same manifest
(`YjsContentPolicy.CreateDefault()`, or `YjsContentPolicy.FromValidator(validator)` for a host's validator): every
attribute value a payload writes is checked against its constraint, and the guard's replica of the document validates
the elements each update changes. See [the content guard](collaboration-content-guard.md).
