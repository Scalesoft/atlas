/**
 * Machine-readable rules for document attribute values. An attribute declares its rule next to its editor validator
 * (`defineMarkdownAttributeValidator`), and `describeMarkdownSchema` publishes it, so a server (Atlas.Markdown.Sync's
 * MarkdownDocumentValidator) enforces the rule the editor enforces when it opens a document. The editor validators stay
 * the executable rule in the browser; the verdict corpus (src/schema/verdictCorpus.test.ts) proves that the server's
 * reading of every declaration agrees with them.
 */

/** A JSON-schema-like description of a structured value, for readers and generated documentation. */
export interface MarkdownJsonSchema {
  readonly type?: 'object' | 'array' | 'string' | 'number' | 'integer' | 'boolean' | 'null' | readonly ('object' | 'array' | 'string' | 'number' | 'integer' | 'boolean' | 'null')[]
  readonly description?: string
  readonly properties?: Readonly<Record<string, MarkdownJsonSchema>>
  readonly required?: readonly string[]
  readonly items?: MarkdownJsonSchema
  readonly enum?: readonly (string | number | boolean | null)[]
  readonly const?: string | number | boolean | null
  readonly minimum?: number
  readonly maximum?: number
  readonly minLength?: number
  readonly maxLength?: number
  readonly minItems?: number
  readonly maxItems?: number
  readonly uniqueItems?: boolean
  readonly pattern?: string
  readonly format?: string
}

/**
 * The value rule of one attribute. Lengths count UTF-16 code units (JavaScript `length`); `pattern` is an anchored
 * ECMAScript regular expression without flags; "no control characters" means none of U+0000–U+001F and U+007F;
 * "blank" is what `String.prototype.trim()` empties.
 */
export type MarkdownValueConstraint =
  /** Any JSON value: the attribute declares no rule (third-party attributes without a declaration). */
  | { readonly kind: 'unconstrained' }
  | { readonly kind: 'enum'; readonly values: readonly string[]; readonly nullable?: boolean }
  /** `Number.isInteger` (3.0 counts) within the inclusive bounds. */
  | { readonly kind: 'integer'; readonly minimum: number; readonly maximum: number; readonly nullable?: boolean }
  /** A finite number within the inclusive bounds. */
  | { readonly kind: 'number'; readonly minimum: number; readonly maximum: number; readonly nullable?: boolean }
  | { readonly kind: 'boolean'; readonly nullable?: boolean }
  | {
    readonly kind: 'text'
    readonly nullable?: boolean
    readonly minLength?: number
    readonly maxLength?: number
    /** No control characters. */
    readonly singleLine?: boolean
    /** Not blank. */
    readonly nonBlank?: boolean
    readonly pattern?: string
  }
  /** A finite number, null, or a string of at most 100 characters without control characters (HTML width/height). */
  | { readonly kind: 'dimension' }
  /**
   * A URL, read as the editor reads it:
   * - `image`: features/images/resourcePolicy.ts `imageSource` under `imageSources` (null and '' are the empty image);
   * - `http`: a non-blank single-line string whose trimmed value resolves against https://editor.invalid/ to http(s)
   *   without credentials (relative URLs allowed);
   * - `absolute-http`: a non-blank single-line string that is an absolute http(s) URL without credentials;
   * - `link`: a string or null whose scheme, read with whitespace and control characters removed anywhere, is not
   *   javascript:, vbscript: or data: (server rule; the editor opens any stored href).
   */
  | {
    readonly kind: 'url'
    readonly policy: 'image' | 'http' | 'absolute-http' | 'link'
    readonly maxLength?: number
    readonly nullable?: boolean
    readonly imageSources?: {
      readonly allowRelative?: boolean
      readonly allowedOrigins?: readonly string[]
      readonly allowDataImages?: boolean
      readonly allowBlobImages?: boolean
    }
  }
  /**
   * A value checked by a named validator that every implementation provides (the manifest's `validators` section
   * describes each one). `context` names sibling attributes the validator reads (table `colwidth` reads `colspan`).
   */
  | { readonly kind: 'structured'; readonly validator: string; readonly context?: readonly string[] }

/**
 * Where a rule holds: `editor` rules are what the editor itself refuses when it opens a document; `server` rules are
 * deliberately stricter server-side rules for attributes the editor stores without validating them (their JSON type,
 * script links), which server readers of stored documents rely on.
 */
export type MarkdownConstraintEnforcement = 'editor' | 'server'

/** A structured validator's published description. */
export interface MarkdownStructuredValidator {
  readonly description: string
  readonly schema: MarkdownJsonSchema
}

const constraintKey = Symbol.for('atlas.markdown.valueConstraint')
type Tagged = ((value: unknown) => void) & { [constraintKey]?: MarkdownValueConstraint }

/**
 * An attribute validator that also declares its rule for the schema manifest. `validate` stays the editor's executable
 * check (throw to refuse); the declaration must describe exactly what it accepts.
 */
export function defineMarkdownAttributeValidator(constraint: MarkdownValueConstraint, validate: (value: unknown) => void): (value: unknown) => void {
  const tagged: Tagged = value => { validate(value) }
  Object.defineProperty(tagged, constraintKey, { value: Object.freeze(constraint) })
  return tagged
}

/** The rule declared by `defineMarkdownAttributeValidator`, if any. */
export function markdownValueConstraintOf(validate: unknown): MarkdownValueConstraint | undefined {
  return typeof validate === 'function' ? (validate as Tagged)[constraintKey] : undefined
}
