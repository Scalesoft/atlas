import { describe, expect, it } from 'vitest'
import { confirmDocumentSchema, normalizeDocumentSchema } from './documentSchema'

describe('built-in content schema revision', () => {
  it.each([1, 2, 3, 4] as const)('normalizes revision %i without upgrading it', version => {
    const schema = normalizeDocumentSchema({ Version: version, Profile: 'rich', Plugins: [{ Id: 'z', Version: 1 }, { Id: 'a', Version: 3 }] })
    expect(schema).toEqual({ version, profile: 'rich', plugins: [{ id: 'a', version: 3 }, { id: 'z', version: 1 }] })
    expect(Object.isFrozen(schema)).toBe(true)
    expect(() => confirmDocumentSchema(schema, { version, profile: 'rich', plugins: [...schema.plugins].reverse() })).not.toThrow()
  })

  it.each([[1, 2], [2, 1], [2, 3], [3, 2], [3, 4], [4, 3]] as const)('rejects requested revision %i against stored revision %i', (requested, stored) => {
    expect(() => confirmDocumentSchema({ version: requested, profile: 'rich', plugins: [] }, { version: stored, profile: 'rich', plugins: [] })).toThrow(/SCHEMA_MISMATCH.*built-in schema revision/)
  })

  it.each([0, 8, 1.5, '2', null])('rejects unsupported revision %j', version => {
    expect(() => normalizeDocumentSchema({ version, profile: 'rich', plugins: [] })).toThrow(/Unsupported/)
  })

  it('retains the explicit legacy boundary and canonicalizes both sides', () => {
    expect(() => confirmDocumentSchema(undefined, null)).not.toThrow()
    expect(() => confirmDocumentSchema({ version: 2, profile: 'rich', plugins: [] }, null)).toThrow(/explicit migration/)
    expect(() => confirmDocumentSchema(undefined, { version: 2, profile: 'rich', plugins: [] })).toThrow(/explicit migration/)
    const plugins = [{ id: 'z', version: 1 }, { id: 'a', version: 1 }]
    expect(() => confirmDocumentSchema({ version: 2, profile: 'rich', plugins }, { version: 2, profile: 'rich', plugins: [...plugins].reverse() })).not.toThrow()
  })
})
