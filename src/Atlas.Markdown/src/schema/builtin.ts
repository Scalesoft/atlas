import { HEADING_LEVELS, MAX_TABLE_COLUMN_WIDTH, MAX_TABLE_ROW_HEIGHT, MAX_TABLE_SPAN, MIN_TABLE_ROW_HEIGHT } from '../core/documentConstraints'
import type { MarkdownConstraintEnforcement, MarkdownStructuredValidator, MarkdownValueConstraint } from './constraints'

const alignments = { kind: 'enum', values: ['left', 'center', 'right'], nullable: true } as const satisfies MarkdownValueConstraint
const span = { kind: 'integer', minimum: 1, maximum: MAX_TABLE_SPAN } as const satisfies MarkdownValueConstraint
const text = { kind: 'text', nullable: true } as const satisfies MarkdownValueConstraint
const editor = (constraint: MarkdownValueConstraint) => ({ constraint, enforcement: 'editor' as MarkdownConstraintEnforcement })
const server = (constraint: MarkdownValueConstraint) => ({ constraint, enforcement: 'server' as MarkdownConstraintEnforcement })
const cell = {
  textAlign: editor(alignments), align: editor(alignments),
  backgroundColor: editor({ kind: 'enum', values: ['accent', 'green', 'amber', 'red'], nullable: true }),
  colspan: editor(span), rowspan: editor(span),
  colwidth: editor({ kind: 'structured', validator: 'columnWidths', context: ['colspan'] }),
}

/**
 * Rules of attributes whose extension carries no declared validator, keyed "nodes:type:attribute" or
 * "marks:type:attribute". `editor` rules are checked by engine/builtinDocumentValidation.ts when a document opens.
 * `server` rules cover attributes that Tiptap, StarterKit or tiptap-markdown store without a validator: a server keeps
 * the JSON type the editor itself writes (readers such as Markdown projections rely on it) and refuses script links.
 * describeMarkdownSchema fails on an attribute that has neither a declared validator nor an entry here.
 */
export const builtinAttributeConstraints: Readonly<Record<string, { constraint: MarkdownValueConstraint; enforcement: MarkdownConstraintEnforcement }>> = {
  'nodes:paragraph:textAlign': editor(alignments),
  'nodes:heading:textAlign': editor(alignments),
  ...Object.fromEntries(['tableCell', 'tableHeader'].flatMap(type => Object.entries(cell).map(([name, rule]) => [`nodes:${type}:${name}`, rule]))),
  'nodes:tableRow:height': editor({ kind: 'number', minimum: MIN_TABLE_ROW_HEIGHT, maximum: MAX_TABLE_ROW_HEIGHT, nullable: true }),
  // The editor's levels (HEADING_LEVELS, H1–H6). Tiptap stores any level it is given, and the Markdown projection writes the
  // level as that many '#' (a level in the billions exhausts the heap).
  'nodes:heading:level': server({ kind: 'integer', minimum: HEADING_LEVELS[0], maximum: HEADING_LEVELS[HEADING_LEVELS.length - 1] }),
  'nodes:bulletList:tight': server({ kind: 'boolean', nullable: true }),
  'nodes:orderedList:tight': server({ kind: 'boolean', nullable: true }),
  'nodes:timedList:tight': server({ kind: 'boolean', nullable: true }),
  // Paste gives what parseInt reads (<ol start="3000000000">): any JavaScript-safe integer.
  'nodes:orderedList:start': server({ kind: 'integer', minimum: -Number.MAX_SAFE_INTEGER, maximum: Number.MAX_SAFE_INTEGER, nullable: true }),
  'nodes:orderedList:type': server({ kind: 'text', nullable: true, maxLength: 100, singleLine: true }),
  'nodes:codeBlock:language': server({ kind: 'text', nullable: true, maxLength: 200, singleLine: true }),
  'nodes:taskItem:checked': server({ kind: 'boolean', nullable: true }),
  'nodes:image:alt': server(text),
  'nodes:image:title': server(text),
  'nodes:image:width': server({ kind: 'dimension' }),
  'nodes:image:height': server({ kind: 'dimension' }),
  'nodes:emoji:name': server(text),
  'marks:link:href': server({ kind: 'url', policy: 'link', nullable: true }),
  // A link opens in a new tab or in place: core/kit.ts renders no other target and never a stored rel or class, nothing is taken from
  // paste, and engine/legacyDocument.ts drops other stored values. They stay text here: live documents still hold pasted values, and
  // refusing them would refuse every edit of their paragraphs.
  'marks:link:target': server(text),
  'marks:link:rel': server(text),
  'marks:link:class': server(text),
  'marks:link:title': server(text),
}

const nonBlank = (maxLength: number, description?: string) => ({ type: 'string', minLength: 1, maxLength, ...(description ? { description } : {}) }) as const
const httpUrl = { type: 'string', format: 'http-or-relative-url', description: 'http(s) or relative URL without credentials' } as const
const contact = {
  type: 'object', required: ['name'], description: 'Unknown keys are ignored; text fields hold at most 4096 characters without control characters other than tab and line breaks.',
  properties: {
    name: nonBlank(4096), role: { type: ['string', 'null'], description: 'Page-local role or note: trimmed, at most 400 characters on one line.' }, kind: { type: 'string', enum: ['auto', 'internal', 'external'] },
    organization: { type: ['string', 'null'] }, department: { type: ['string', 'null'] }, title: { type: ['string', 'null'] },
    address: { type: ['string', 'null'] }, directoryId: { type: ['string', 'null'] }, objectId: { type: ['string', 'null'], description: 'Directory contact object id (lower-case GUID); an invalid value is dropped.' },
    username: { type: ['string', 'null'], description: 'Trimmed: at most 256 characters on one line.' },
    photo: { ...httpUrl, type: ['string', 'null'] }, website: { ...httpUrl, type: ['string', 'null'] },
    phones: { type: ['array', 'null'], maxItems: 20, items: { type: 'string', format: 'phone', description: '+, digits, spaces, ( ) . - # *; at most 80 characters' } },
    emails: { type: ['array', 'null'], maxItems: 20, items: { type: 'string', format: 'email' } },
    unresolved: { type: ['boolean', 'null'], description: 'Several known people have this name and nobody was picked yet; never linked automatically.' },
    internalUser: { type: 'object', required: ['id'], properties: { id: nonBlank(256), name: { type: ['string', 'null'] }, photo: { ...httpUrl, type: ['string', 'null'] } } },
  },
} as const

/** The structured validators the built-in plugins declare. Every implementation of the manifest provides them by name. */
export const builtinStructuredValidators: Readonly<Record<string, MarkdownStructuredValidator>> = {
  imagePresentation: {
    description: 'features/images/presentation.ts imagePresentation: null, or the image presentation; absent keys take their defaults. The crops of each axis leave at least 5 % of the image; naturalWidth and naturalHeight are both 0 or both positive.',
    schema: { type: ['object', 'null'], properties: {
      width: { type: 'number', minimum: 10, maximum: 100 }, align: { type: 'string', enum: ['left', 'center', 'right'] }, caption: { type: 'string' },
      rotate: { type: 'number', enum: [0, 90, 180, 270] }, flip: { type: 'boolean' },
      brightness: { type: 'number', minimum: 0, maximum: 200 }, contrast: { type: 'number', minimum: 0, maximum: 200 }, saturation: { type: 'number', minimum: 0, maximum: 200 },
      filter: { type: 'string', enum: ['none', 'grayscale', 'sepia'] },
      cropLeft: { type: 'number', minimum: 0, maximum: 90 }, cropRight: { type: 'number', minimum: 0, maximum: 90 },
      cropTop: { type: 'number', minimum: 0, maximum: 90 }, cropBottom: { type: 'number', minimum: 0, maximum: 90 },
      naturalWidth: { type: 'integer', minimum: 0, maximum: 1_000_000 }, naturalHeight: { type: 'integer', minimum: 0, maximum: 1_000_000 },
    } },
  },
  columnWidths: {
    description: `core/documentConstraints.ts isValidColumnWidths: null, or one width from 0 to ${MAX_TABLE_COLUMN_WIDTH} per column the cell spans (its colspan).`,
    schema: { type: ['array', 'null'], items: { type: 'number', minimum: 0, maximum: MAX_TABLE_COLUMN_WIDTH } },
  },
  secretFields: {
    description: 'plugins/secrets/model.ts validateFields: null, or 1–6 distinct secret fields.',
    schema: { type: ['array', 'null'], minItems: 1, maxItems: 6, uniqueItems: true, items: { type: 'string', enum: ['title', 'path', 'username', 'password', 'url', 'notes'] } },
  },
  attachment: {
    description: 'plugins/attachments/data.ts normalizeAttachment. Unknown keys are ignored.',
    schema: { type: 'object', required: ['version', 'name', 'url'], properties: {
      version: { const: 1 }, name: { ...nonBlank(512), description: 'No control characters.' },
      url: { type: 'string', maxLength: 8192, format: 'http-or-relative-url', description: 'Empty, or an http(s) or relative URL without credentials, on one line.' },
      size: { type: 'integer', minimum: 0 }, mimeType: { type: 'string', pattern: '^$|^[a-z0-9!#$&^_.+-]+/[a-z0-9!#$&^_.+-]+$' },
      description: { type: 'string', maxLength: 4096 },
    } },
  },
  contacts: {
    description: 'plugins/contacts/data.ts normalizeContacts: 1–100 contacts.',
    schema: { type: 'object', required: ['version', 'contacts'], properties: { version: { const: 1 }, contacts: { type: 'array', minItems: 1, maxItems: 100, items: contact } } },
  },
  contact: {
    description: 'The inline contact: normalizeContacts with exactly one contact.',
    schema: { type: 'object', required: ['version', 'contacts'], properties: { version: { const: 1 }, contacts: { type: 'array', minItems: 1, maxItems: 1, items: contact } } },
  },
  chart: {
    description: 'plugins/charts/data.ts normalizeChart: a header row and 1–200 rows of 1–20 series; value cells are empty or numbers (Czech decimal comma and grouped thousands allowed; pie and doughnut charts refuse negative values). A linked chart names its table and one distinct cell id per grid cell.',
    schema: { type: 'object', required: ['version', 'kind', 'title', 'grid'], properties: {
      version: { const: 1 }, kind: { type: 'string', enum: ['bar', 'line', 'pie', 'doughnut'] }, title: { type: 'string', maxLength: 300 },
      grid: { type: 'array', minItems: 2, maxItems: 201, items: { type: 'array', minItems: 2, maxItems: 21, items: { type: 'string', maxLength: 2000 } } },
      source: { type: ['object', 'null'], required: ['tableId', 'cells'], properties: {
        tableId: { type: 'string', minLength: 1, maxLength: 100 },
        cells: { type: 'array', items: { type: 'array', items: { type: 'string', minLength: 1, maxLength: 100 } } },
      } },
      sourceStatus: { type: ['string', 'null'], enum: ['linked', 'missing', 'invalid', null] },
    } },
  },
  diagram: {
    description: 'plugins/diagrams/index.ts normalizeDiagram: Mermaid source without init directives (%%{), front matter (---) or HTML tags.',
    schema: { type: 'object', required: ['version', 'source', 'caption'], properties: {
      version: { const: 1 }, source: { type: 'string', maxLength: 20_000 }, caption: { type: 'string', maxLength: 500 },
    } },
  },
  math: {
    description: 'plugins/math/index.ts normalizeMath.',
    schema: { type: 'object', required: ['version', 'expression'], properties: { version: { const: 1 }, expression: nonBlank(10_000, 'TeX') } },
  },
  tableOfContents: {
    description: 'plugins/toc/index.ts normalizeToc: 1 ≤ minLevel ≤ maxLevel ≤ 6.',
    schema: { type: 'object', required: ['version', 'title', 'minLevel', 'maxLevel'], properties: {
      version: { const: 1 }, title: { type: 'string', maxLength: 200 },
      minLevel: { type: 'integer', minimum: 1, maximum: 6 }, maxLevel: { type: 'integer', minimum: 1, maximum: 6 },
    } },
  },
  openapi: {
    description: 'plugins/openapi/data.ts normalizeOpenApi. The editor also parses the source as an OpenAPI 3.0/3.1 YAML document; a server checks the fields and the size of the source (512 KiB of UTF-8) only.',
    schema: { type: 'object', required: ['version', 'filename', 'source'], properties: {
      version: { const: 1 }, filename: { ...nonBlank(255), description: 'A file name without slashes or control characters.' },
      source: { type: 'string', minLength: 1, format: 'openapi-yaml' },
      mode: { type: 'string', enum: ['download', 'reference', 'interactive'] }, uploadedBy: nonBlank(255),
    } },
  },
}
