import { parseDocument } from 'yaml'
import { openapiStrings } from './strings'
import { MAX_OPENAPI_SOURCE_BYTES, normalizeOpenApiShape, type OpenApiData, type OpenApiSummary } from './shape'
export { defaultOpenApi, MAX_OPENAPI_SOURCE_BYTES } from './shape'
export type { OpenApiData, OpenApiMode, OpenApiSummary } from './shape'

const methods = new Set(['get', 'put', 'post', 'delete', 'options', 'head', 'patch', 'trace'])
const namedMaps = new Set(['paths', 'webhooks', 'properties', 'patternProperties', 'dependentSchemas', '$defs', 'definitions', 'schemas', 'responses', 'headers', 'parameters', 'requestBodies', 'securitySchemes', 'links', 'callbacks', 'content'])
const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value)

/** `deep: false` stops before the reference walk (a whole-document traversal): what a card needs to say its title and counts. */
function parseOpenApi(source: string, deep = true): Record<string, unknown> {
  if (!source.trim()) throw new TypeError(openapiStrings.emptySourceError)
  if (new TextEncoder().encode(source).byteLength > MAX_OPENAPI_SOURCE_BYTES) throw new TypeError(openapiStrings.sourceTooLargeError)
  const parsed = parseDocument(source, { uniqueKeys: true, version: '1.2' })
  if (parsed.errors.length) throw new TypeError(openapiStrings.invalidSyntaxError(parsed.errors[0].message))
  let value: unknown
  try { value = parsed.toJS({ maxAliasCount: 20 }) }
  catch (cause) { throw new TypeError(openapiStrings.invalidAliasError((cause as Error).message)) }
  if (!record(value)) throw new TypeError(openapiStrings.rootMustBeObjectError)
  if (typeof value.openapi !== 'string' || !/^3\.(?:0|1)\.\d+$/.test(value.openapi)) throw new TypeError(openapiStrings.unsupportedVersionError)
  if (!record(value.info) || typeof value.info.title !== 'string' || !value.info.title.trim() || typeof value.info.version !== 'string' || !value.info.version.trim()) throw new TypeError(openapiStrings.missingInfoError)
  if (!record(value.paths)) throw new TypeError(openapiStrings.missingPathsError)
  for (const [path, entry] of Object.entries(value.paths)) {
    if (!path.startsWith('/') || !record(entry)) throw new TypeError(openapiStrings.invalidPathsError)
    for (const [method, operation] of Object.entries(entry)) {
      if (!methods.has(method)) continue
      if (!record(operation) || !record(operation.responses)) throw new TypeError(openapiStrings.missingResponsesError(method, path))
    }
  }
  if (!deep) return value
  const visited = new WeakMap<object, Set<number>>()
  const active = new Set<object>()
  let count = 0
  const checkLocalRef = (ref: unknown): void => {
    if (typeof ref !== 'string' || !ref.startsWith('#')) throw new TypeError(openapiStrings.externalRefError)
    if (ref === '#') return
    if (!ref.startsWith('#/')) throw new TypeError(openapiStrings.unsupportedRefError)
    let pointer: string
    try { pointer = decodeURIComponent(ref.slice(2)) }
    catch { throw new TypeError(openapiStrings.invalidPointerError) }
    let target: unknown = value
    for (const key of pointer.split('/').map(part => part.replace(/~1/g, '/').replace(/~0/g, '~'))) {
      target = record(target) ? Object.hasOwn(target, key) ? target[key] : undefined : Array.isArray(target) ? target[Number(key)] : undefined
      if (target === undefined) throw new TypeError(openapiStrings.refNotFoundError(ref))
    }
  }
  const checkRefs = (part: unknown, opaque = false, examplesMap = false, exampleObject = false, namedMap = false): void => {
    if (!part || typeof part !== 'object') return
    if (active.has(part)) throw new TypeError(openapiStrings.cyclicAliasError)
    const state = opaque ? 1 : examplesMap ? 2 : exampleObject ? 3 : namedMap ? 4 : 0
    const states = visited.get(part) ?? new Set<number>()
    if (states.has(state)) return
    if (++count > 100_000) throw new TypeError(openapiStrings.tooComplexError)
    active.add(part)
    states.add(state); visited.set(part, states)
    if (Array.isArray(part)) part.forEach(item => checkRefs(item, opaque || examplesMap))
    else for (const [key, item] of Object.entries(part)) {
      const semanticKey = !namedMap && !examplesMap
      if (!opaque && semanticKey && (key === '$ref' || key === '$dynamicRef')) checkLocalRef(item)
      checkRefs(item,
        opaque || (semanticKey && (key === 'example' || key.startsWith('x-') || (key === 'examples' && Array.isArray(item)))) || (exampleObject && key === 'value'),
        semanticKey && key === 'examples' && record(item),
        examplesMap,
        semanticKey && namedMaps.has(key),
      )
    }
    active.delete(part)
  }
  checkRefs(value)
  return value
}

/** The full check: the shape, then the source parsed and checked as an OpenAPI document (the YAML parser loads with this module). */
export function normalizeOpenApi(value: unknown): OpenApiData {
  const shape = normalizeOpenApiShape(value)
  parseOpenApi(shape.source)
  return shape
}

/** For the card: parses and checks the basics (syntax, version, info, paths) but does not walk every reference; entering a spec checks those (`normalizeOpenApi`). */
export function summarizeOpenApi(data: OpenApiData): OpenApiSummary {
  const parsed = parseOpenApi(data.source, false)
  const info = parsed.info as Record<string, unknown>
  let operations = 0
  for (const path of Object.values(parsed.paths as Record<string, unknown>)) {
    if (record(path)) operations += Object.keys(path).filter(method => methods.has(method.toLowerCase())).length
  }
  const schemas = record(parsed.components) && record(parsed.components.schemas) ? Object.keys(parsed.components.schemas).length : 0
  const contact = record(info.contact) && typeof info.contact.name === 'string' ? info.contact.name.trim() : ''
  const author = contact || data.uploadedBy?.trim() || undefined
  return { title: String(info.title), version: String(info.version), operations, schemas, author }
}
