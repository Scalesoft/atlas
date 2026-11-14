// The persisted reference model (README "Document contract"): field names, attribute rules and validation.
// Shared by the node schema, the commands, the settings panel and the Markdown form.

/** KeePass entry fields: `path` is the group path of the entry, `notes` its free-text notes. */
export type SecretField = 'password' | 'username' | 'url' | 'title' | 'path' | 'notes'
export interface SecretReferencePresentation {
  /** Legacy single field, shown while `fields` is null. */
  readonly field?: SecretField
  /** The entry fields shown by the reference, always in canonical order. Null shows `field`. */
  readonly fields?: readonly SecretField[] | null
  /** Copy beside each revealed value; a single field offers it in the action rail instead. */
  readonly showCopy?: boolean
  /** A Detail control that expands the reference into a card with every field of the entry. */
  readonly showDetails?: boolean
  /** Seconds after which revealed values are covered again at the latest (5–3600), or null for no extra limit. */
  readonly autoHideSeconds?: number | null
}
export interface SecretReference extends SecretReferencePresentation { readonly secretId: string }

/** Every attribute of a reference node, defaults filled in. */
export interface ReferenceAttrs {
  readonly secretId: string; readonly field: SecretField; readonly fields: readonly SecretField[] | null
  readonly showCopy: boolean; readonly showDetails: boolean; readonly autoHideSeconds: number | null
}

export const FIELD_ORDER: readonly SecretField[] = ['title', 'path', 'username', 'password', 'url', 'notes']
export const DEFAULT_FIELD: SecretField = 'password'
export const DEFAULT_SHOW_COPY = true
export const DEFAULT_SHOW_DETAILS = false
export const DEFAULT_AUTO_HIDE_SECONDS = 60
export const MIN_AUTO_HIDE_SECONDS = 5
export const MAX_AUTO_HIDE_SECONDS = 3600

export const SECRET_ID_PATTERN = '^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'
const secretId = new RegExp(SECRET_ID_PATTERN)
export function validateId(value: unknown): asserts value is string {
  if (typeof value !== 'string' || !secretId.test(value))
    throw new TypeError('Invalid secret reference.')
}
export function validateField(value: unknown): asserts value is SecretField {
  if (!FIELD_ORDER.includes(value as SecretField)) throw new TypeError('Invalid secret field.')
}
export function validateFields(value: unknown): asserts value is readonly SecretField[] | null {
  if (value === null) return
  if (!Array.isArray(value) || value.length < 1 || value.length > FIELD_ORDER.length) throw new TypeError('Invalid secret fields.')
  const seen = new Set<unknown>()
  for (const item of value) { validateField(item); if (seen.has(item)) throw new TypeError('Invalid secret fields.'); seen.add(item) }
}
export function validateAutoHide(value: unknown): asserts value is number | null {
  if (value === null) return
  if (typeof value !== 'number' || !Number.isInteger(value) || value < MIN_AUTO_HIDE_SECONDS || value > MAX_AUTO_HIDE_SECONDS) throw new TypeError('Invalid secret presentation.')
}
export function validateBoolean(value: unknown): asserts value is boolean {
  if (typeof value !== 'boolean') throw new TypeError('Invalid secret presentation.')
}
export function presentation(value: unknown, withId = false): SecretReferencePresentation {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new TypeError('Invalid secret presentation.')
  const result: { field?: SecretField; fields?: readonly SecretField[] | null; showCopy?: boolean; showDetails?: boolean; autoHideSeconds?: number | null } = {}
  for (const key of Reflect.ownKeys(value)) {
    if (withId && key === 'secretId') continue
    const property = Object.getOwnPropertyDescriptor(value, key)
    if (!property || !('value' in property)) throw new TypeError('Invalid secret presentation.')
    if (key === 'field') { validateField(property.value); result.field = property.value }
    else if (key === 'fields') {
      validateFields(property.value)
      const fields = property.value
      result.fields = fields === null ? null : Object.freeze(FIELD_ORDER.filter(field => fields.includes(field)))
    }
    else if (key === 'showCopy' || key === 'showDetails') { validateBoolean(property.value); result[key] = property.value }
    else if (key === 'autoHideSeconds') { validateAutoHide(property.value); result.autoHideSeconds = property.value }
    else throw new TypeError('Invalid secret presentation.')
  }
  return result
}
/** Validates a reference and fills in the defaults; `fields` becomes canonical. */
export function reference(value: unknown): ReferenceAttrs {
  const display = presentation(value, true)
  if (!value || typeof value !== 'object') throw new TypeError('Invalid secret reference.')
  const property = Object.getOwnPropertyDescriptor(value, 'secretId')
  if (!property || !('value' in property)) throw new TypeError('Invalid secret reference.')
  validateId(property.value)
  return {
    secretId: property.value, field: display.field ?? DEFAULT_FIELD, fields: display.fields ?? null,
    showCopy: display.showCopy ?? DEFAULT_SHOW_COPY, showDetails: display.showDetails ?? DEFAULT_SHOW_DETAILS,
    autoHideSeconds: display.autoHideSeconds === undefined ? DEFAULT_AUTO_HIDE_SECONDS : display.autoHideSeconds,
  }
}
