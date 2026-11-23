import { describe, expect, it } from 'vitest'
import { contactQr, normalizeContact, normalizeContacts, normalizeInternalContactUser, serializeVCard } from './data'

describe('contact role', () => {
  it('is a trimmed single line of at most 400 characters', () => {
    expect(normalizeContact({ name: 'A', role: ' PM projektu ' }).role).toBe('PM projektu')
    expect(normalizeContact({ name: 'A', role: '  ' })).toEqual({ name: 'A' })
    expect(() => normalizeContact({ name: 'A', role: 'a\nb' })).toThrow()
    expect(() => normalizeContact({ name: 'A', role: 'x'.repeat(401) })).toThrow()
  })
})
describe('contact data and portable vCard export', () => {
  it('escapes field separators and newlines instead of injecting vCard properties', () => {
    const output = serializeVCard({ name: 'Novák, Jan; tým\\A\r\nEMAIL:injected', address: 'Praha; Česko', phones: ['+420 123 456 789'], emails: ['jan@example.cz', 'jan.work@example.cz'] })
    expect(output).toContain('FN:Novák\\, Jan\\; tým\\\\A\\nEMAIL:injected\r\n')
    expect(output).toContain('ADR:;;Praha\\; Česko;;;;\r\n')
    expect(output.match(/^EMAIL:/gm)).toHaveLength(2)
    expect(output).toContain('TEL;VALUE=text:+420 123 456 789\r\n')
    expect(output.startsWith('BEGIN:VCARD\r\nVERSION:4.0\r\n')).toBe(true)
    expect(output.endsWith('END:VCARD\r\n')).toBe(true)
  })
  it('folds UTF-8 at 75 octets while preserving multi-byte characters and continuation width', () => {
    const name = 'Žluťoučký 🦊 '.repeat(40).trim()
    const output = serializeVCard({ name })
    for (const line of output.split('\r\n')) expect(new TextEncoder().encode(line).length).toBeLessThanOrEqual(75)
    expect(output.replace(/\r\n /g, '')).toContain(`FN:${name}\r\n`)
    expect(output).not.toContain('�')
  })
  it('writes the QR code card as vCard 3.0 with N, escaped and accented, without the photo, empty or omitted fields', async () => {
    const jana = { name: 'Jana Nováková', title: 'Vedoucí; provozu', organization: 'Žluťoučký kůň, s.r.o.', department: 'IT\\podpora', phones: ['+420 777 123 456'],
      emails: ['jana@example.test'], website: 'https://example.test', address: 'Dlouhá 1\n110 00 Praha', photo: 'https://example.test/jana.png' }
    expect(serializeVCard(jana, {}).split('\r\n')).toEqual(['BEGIN:VCARD', 'VERSION:3.0', 'N:Nováková;Jana;;;', 'FN:Jana Nováková',
      'ORG:Žluťoučký kůň\\, s.r.o.;IT\\\\podpora', 'TITLE:Vedoucí\\; provozu', 'ADR;TYPE=WORK:;;Dlouhá 1\\n110 00 Praha;;;;', 'TEL;TYPE=VOICE:+420 777 123 456',
      'EMAIL;TYPE=INTERNET:jana@example.test', 'URL:https://example.test/', 'END:VCARD', ''])
    expect(serializeVCard({ name: 'Recepce', website: 'https://example.test', address: 'Hlavní 2' }, { omit: ['address', 'website'] }).split('\r\n'))
      .toEqual(['BEGIN:VCARD', 'VERSION:3.0', 'N:Recepce;;;;', 'FN:Recepce', 'END:VCARD', ''])
    // Too dense with the address (version 15): drawn again without it, the website stays.
    const drawn: string[] = []
    const code = await contactQr(jana, async text => { drawn.push(text); return { svg: '<svg/>', version: text.includes('ADR') ? 15 : 12 } })
    expect(code).toEqual({ svg: '<svg/>', version: 12, omitted: ['address'] })
    expect(drawn[1]).toContain('URL:'); expect(drawn[1]).not.toContain('ADR')
  })
  it('exports lists and excludes relative URLs from absolute URI properties', () => {
    const output = serializeVCard([{ name: 'A', website: '/team', photo: 'https://example.cz/a b.png' }, { name: 'B' }])
    expect(output.match(/BEGIN:VCARD/g)).toHaveLength(2)
    expect(output).not.toContain('URL:')
    expect(output).toContain('PHOTO:https://example.cz/a%20b.png')
  })
  it('rejects dangerous URLs, malformed arrays, unversioned data and control characters', () => {
    for (const value of [{ name: '' }, { name: 'A', photo: 'javascript:alert(1)' }, { name: 'A', website: 'data:text/html,x' }, { name: 'A', phones: ['1\r\nEMAIL:x'] }, { name: 'A', emails: ['a@b.cz?subject=x'] }, { name: 'A', phones: '123' }, { name: '\u0000' }]) expect(() => normalizeContact(value)).toThrow()
    expect(() => normalizeContacts({ contacts: [{ name: 'A' }] })).toThrow()
    expect(() => normalizeContacts({ version: 1, contacts: [] })).toThrow()
    expect(normalizeContact({ name: '  Jana  ', emails: ['a@b.cz', 'a@b.cz'] })).toEqual({ name: 'Jana', emails: ['a@b.cz'] })
  })
  it('keeps a single-line username in the document but out of the vCard', () => {
    expect(normalizeContact({ name: 'Jana', username: '  jana.n  ' })).toEqual({ name: 'Jana', username: 'jana.n' })
    expect(normalizeContact({ name: 'Jana', username: '   ' })).toEqual({ name: 'Jana' })
    for (const username of ['jana\nEMAIL:x', 'a'.repeat(257), 42]) expect(() => normalizeContact({ name: 'Jana', username })).toThrow()
    expect(serializeVCard({ name: 'Jana', username: 'jana.n' })).not.toContain('jana.n')
  })
  it('keeps a directory object id as lower-case GUID and drops anything else', () => {
    expect(normalizeContact({ name: 'Jana', objectId: ' 0B5F3C1E-2D4A-4E8B-9F7A-1C2D3E4F5A6B ' })).toEqual({ name: 'Jana', objectId: '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' })
    for (const objectId of ['', 'u1', 42, '0b5f3c1e-2d4a-4e8b-9f7a']) expect(normalizeContact({ name: 'Jana', objectId })).toEqual({ name: 'Jana' })
  })
  it('keeps presentation choices independent of directory membership and validates explicit identities', () => {
    expect(normalizeContact({ name: 'Customer', directoryId: 'external-1' })).toEqual({ name: 'Customer', directoryId: 'external-1' })
    expect(normalizeContact({ name: 'Jana', kind: 'external', internalUser: { id: 'u1', name: 'Jana', photo: '/photos/u1.png' } })).toEqual({ name: 'Jana', kind: 'external', internalUser: { id: 'u1', name: 'Jana', photo: '/photos/u1.png' } })
    for (const internalUser of [{ id: '' }, { id: 'a\nb' }, { id: 'a', photo: 'javascript:alert(1)' }, { id: 'a', photo: 'data:text/html,x' }, { id: 'a'.repeat(257) }, []]) expect(() => normalizeContact({ name: 'Jana', internalUser })).toThrow()
    expect(() => normalizeContact({ name: 'Jana', kind: 'administrator' })).toThrow()
    expect(normalizeInternalContactUser({ id: ' u1 ', name: '<b>Jana</b>' })).toEqual({ id: 'u1', name: '<b>Jana</b>' })
    expect(serializeVCard({ name: 'Jana', kind: 'internal', internalUser: { id: 'private-id' } })).not.toContain('private-id')
  })
})
