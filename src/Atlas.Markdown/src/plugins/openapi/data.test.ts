import { describe, expect, it } from 'vitest'
import { normalizeOpenApi, summarizeOpenApi } from './data'

const yaml = `openapi: 3.1.0\ninfo:\n  title: Patient API\n  version: '2.3'\npaths:\n  /patients:\n    get:\n      summary: List patients\n      responses:\n        '200':\n          description: OK\ncomponents:\n  schemas:\n    Patient:\n      type: object\n`

describe('OpenAPI source boundary', () => {
  it('preserves the exact source while deriving display metadata', () => {
    const data = normalizeOpenApi({ version: 1, filename: 'patients.yaml', source: yaml })
    expect(data.source).toBe(yaml)
    expect(summarizeOpenApi(data)).toEqual({ title: 'Patient API', version: '2.3', operations: 1, schemas: 1, author: undefined })
  })

  it.each(['3.2.0', '2.0.0'])('rejects unsupported OpenAPI version %s', openapi => {
    expect(() => normalizeOpenApi({ version: 1, filename: 'bad.yaml', source: yaml.replace('3.1.0', openapi) })).toThrow(/OpenAPI 3\.0|3\.1/)
  })

  it('rejects external references without discarding the source', () => {
    const source = yaml.replace('type: object\n', "$ref: 'https://example.com/patient.yaml#/Patient'\n")
    expect(() => normalizeOpenApi({ version: 1, filename: 'bad.yaml', source })).toThrow(/Externí odkaz/)
  })

  it('rejects sources larger than 512 KiB', () => {
    expect(() => normalizeOpenApi({ version: 1, filename: 'large.yaml', source: yaml + ' '.repeat(512 * 1024) })).toThrow(/512 KiB/)
  })

  it('rejects malformed YAML and excessive aliases', () => {
    expect(() => normalizeOpenApi({ version: 1, filename: 'broken.yaml', source: yaml + '\ninvalid: [\n' })).toThrow(/YAML|JSON/)
    const aliases = yaml + `x-value: &v [1, 2, 3]\nx-many: [${Array(30).fill('*v').join(', ')}]\n`
    expect(() => normalizeOpenApi({ version: 1, filename: 'aliases.yaml', source: aliases })).toThrow(/alias|odkaz|složit/i)
  })

  it('rejects numeric info.version and malformed operations', () => {
    expect(() => normalizeOpenApi({ version: 1, filename: 'bad.yaml', source: yaml.replace("version: '2.3'", 'version: 2.3') })).toThrow(/info.version/)
    expect(() => normalizeOpenApi({ version: 1, filename: 'bad.yaml', source: yaml.replace('responses:\n', 'responses: []\n') })).toThrow(/responses/)
  })

  it('rejects cyclic YAML aliases', () => {
    const source = yaml + 'x-cycle: &loop\n  self: *loop\n'
    expect(() => normalizeOpenApi({ version: 1, filename: 'cycle.yaml', source })).toThrow(/cyklick|alias/i)
  })

  it('requires local references to resolve inside the imported document', () => {
    const source = yaml.replace('type: object\n', "$ref: '#/components/schemas/Missing'\n")
    expect(() => normalizeOpenApi({ version: 1, filename: 'missing.yaml', source })).toThrow(/nenalezen|odkaz/i)
  })

  it('treats $ref in examples and extensions as literal data', () => {
    const source = yaml.replace('type: object\n', 'type: object\n      examples:\n        - $ref: https://example.com/literal\n') + `x-sample:\n  $ref: https://example.com/literal\nexample:\n  $ref: https://example.com/literal\nexamples:\n  patient:\n    value:\n      $ref: https://example.com/literal\n`
    expect(normalizeOpenApi({ version: 1, filename: 'literal.yaml', source }).source).toBe(source)
  })

  it('never mistakes schema property names for example payloads', () => {
    const source = yaml.replace('type: object\n', `type: object\n      properties:\n        example:\n          $ref: https://example.com/remote.yaml\n`)
    expect(() => normalizeOpenApi({ version: 1, filename: 'bad.yaml', source })).toThrow(/Externí odkaz/)
    const nested = yaml.replace('type: object\n', `type: object\n      properties:\n        examples:\n          properties:\n            value:\n              $ref: https://example.com/remote.yaml\n`)
    expect(() => normalizeOpenApi({ version: 1, filename: 'bad.yaml', source: nested })).toThrow(/Externí odkaz/)
    const dependent = yaml.replace('type: object\n', `type: object\n      dependentSchemas:\n        example:\n          $ref: https://example.com/remote.yaml\n`)
    expect(() => normalizeOpenApi({ version: 1, filename: 'bad.yaml', source: dependent })).toThrow(/Externí odkaz/)
  })

  it('keeps legacy data interactive and projects document author before uploader', () => {
    const legacy = normalizeOpenApi({ version: 1, filename: 'patients.yaml', source: yaml })
    expect(legacy).toEqual({ version: 1, filename: 'patients.yaml', source: yaml })
    const withUploader = normalizeOpenApi({ ...legacy, uploadedBy: 'Jana Nováková', mode: 'reference' })
    expect(withUploader).toMatchObject({ uploadedBy: 'Jana Nováková', mode: 'reference' })
    expect(summarizeOpenApi(withUploader).author).toBe('Jana Nováková')
    const withContact = normalizeOpenApi({ ...withUploader, source: yaml.replace("  version: '2.3'", "  version: '2.3'\n  contact:\n    name: '  NIS Team  '") })
    expect(summarizeOpenApi(withContact).author).toBe('NIS Team')
  })

  it('rejects invalid presentation mode or uploader metadata', () => {
    expect(() => normalizeOpenApi({ version: 1, filename: 'patients.yaml', source: yaml, mode: 'unsafe' })).toThrow(/data/i)
    expect(() => normalizeOpenApi({ version: 1, filename: 'patients.yaml', source: yaml, uploadedBy: { name: 'Jana' } })).toThrow(/data/i)
  })
})
