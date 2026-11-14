import { describe, expect, it } from 'vitest'
import { defaultOpenApi, lightOpenApiSummary, normalizeOpenApiShape, MAX_OPENAPI_SOURCE_BYTES } from './shape'
import { normalizeOpenApi } from './data'

/** J8.5: the node's schema check has no YAML parser; a spec that does not parse still opens (its card says why), the full check refuses it. */
describe('the OpenAPI node schema', () => {
  it('accepts data whose source is not a valid spec, where the full check refuses it', () => {
    const broken = { ...defaultOpenApi, source: 'openapi: [unclosed' }
    expect(normalizeOpenApiShape(broken)).toEqual(broken)
    expect(() => normalizeOpenApi(broken)).toThrow()
  })

  it('still refuses data of the wrong shape and a source over the limit', () => {
    expect(() => normalizeOpenApiShape({ ...defaultOpenApi, filename: 'a/b.yaml' })).toThrow()
    expect(() => normalizeOpenApiShape({ ...defaultOpenApi, mode: 'x' })).toThrow()
    expect(() => normalizeOpenApiShape({ ...defaultOpenApi, source: 'x'.repeat(MAX_OPENAPI_SOURCE_BYTES + 1) })).toThrow()
  })

  it('reads the title and version of a YAML or a JSON spec without parsing it', () => {
    expect(lightOpenApiSummary({ ...defaultOpenApi, source: "openapi: 3.1.0\ninfo:\n  title: Zakázky\n  version: '2.1'\npaths: {}\n" })).toEqual({ title: 'Zakázky', version: '2.1' })
    expect(lightOpenApiSummary({ ...defaultOpenApi, filename: 'a.json', source: '{"openapi":"3.1.0","info":{"title":"Orders","version":"3"},"paths":{}}' })).toEqual({ title: 'Orders', version: '3' })
    expect(lightOpenApiSummary({ ...defaultOpenApi, source: 'nonsense' })).toEqual({ title: 'openapi.yaml', version: '' })
  })
})
