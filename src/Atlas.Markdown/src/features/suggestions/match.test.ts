import { expect, it } from 'vitest'
import { foldSearchText, rankSuggestion, rankSuggestions } from './match'

it('folds case and diacritics', () => {
  expect(foldSearchText('Zmínka ŽLUŤOUČKÝ kůň')).toBe('zminka zlutoucky kun')
  expect(foldSearchText('zminka')).toBe(foldSearchText('Zmínka'))
})

it('ranks title prefix before word start, keyword and substring, and finds nothing otherwise', () => {
  const tier = (query: string, title: string, keywords?: string[]) => rankSuggestion(query, { title, keywords })
  expect(tier('poz', 'Poznámka')).toBe(0)
  expect(tier('zna', 'Informační box poznámka')).toBe(3)
  expect(tier('box', 'Informační box')).toBe(1)
  expect(tier('upoz', 'Informační box', ['upozorneni'])).toBe(2)
  expect(tier('znamka', 'Poznámka')).toBe(3)
  expect(tier('ormac', 'Informační box')).toBe(3)
  expect(tier('xyz', 'Poznámka', ['note'])).toBeUndefined()
  expect(tier('', 'Cokoli')).toBe(0)
  expect(tier('  ', 'Cokoli')).toBe(0)
})

it('sorts by tier and keeps the input order inside a tier', () => {
  const items = [
    { title: 'Tabulka grid', keywords: [] }, { title: 'Datová tabulka', keywords: [] }, { title: 'Další', keywords: ['tabulka'] },
    { title: 'Tabulkový kalkulátor', keywords: [] }, { title: 'Nesouvisí', keywords: [] },
  ]
  expect(rankSuggestions('tabulk', items, item => item).map(item => item.title))
    .toEqual(['Tabulka grid', 'Tabulkový kalkulátor', 'Datová tabulka', 'Další'])
})

it('puts a whole title or keyword match first, then the tiers, and lets usage order items inside one tier', () => {
  const items = [{ title: 'Grafy' }, { title: 'Graf' }, { title: 'Kódový příklad' }, { title: 'Kód', keywords: ['kod', 'code'] }]
  expect(rankSuggestions('graf', items, item => item).map(item => item.title)).toEqual(['Graf', 'Grafy'])
  expect(rankSuggestions('kod', items, item => item).map(item => item.title)).toEqual(['Kód', 'Kódový příklad'])
  const used = new Map([['Grafy', 3]])
  expect(rankSuggestions('gra', items, item => item, { usage: item => used.get(item.title) ?? 0 }).map(item => item.title)).toEqual(['Grafy', 'Graf'])
  expect(rankSuggestions('', items, item => item, { usage: item => used.get(item.title) ?? 0 })[0]!.title).toBe('Grafy')
})

it('matches several typed words as the starts of the title words in order („nadp 2“ is „Nadpis 2“)', () => {
  expect(rankSuggestion('nadp 2', { title: 'Nadpis 2' })).toBe(1)
  expect(rankSuggestion('kon v t', { title: 'Kontakt v textu' })).toBe(1)
  expect(rankSuggestion('nadp 3', { title: 'Nadpis 2' })).toBeUndefined()
  expect(rankSuggestions('nadp 2', [{ title: 'Nadpis 1' }, { title: 'Nadpis 2' }], item => item).map(item => item.title)).toEqual(['Nadpis 2'])
})

it('finds an abbreviation as the letters of the title in order, after every other tier', () => {
  expect(rankSuggestion('tbl', { title: 'Tabulka' })).toBeUndefined()
  expect(rankSuggestion('tbl', { title: 'Tabulka' }, true)).toBe(4)
  expect(rankSuggestion('nd2', { title: 'Nadpis 2' }, true)).toBe(4)
  expect(rankSuggestion('x', { title: 'Tabulka' }, true)).toBeUndefined()
  expect(rankSuggestion('lbt', { title: 'Tabulka' }, true)).toBeUndefined()
})

it('puts the name typed exactly before a keyword typed exactly, however often the other was picked', () => {
  const items = [{ title: 'Požadavek z nasazení', keywords: ['požadavek'] }, { title: 'Moduly produktu', keywords: ['požadavek'] }, { title: 'Požadavek', keywords: ['požadavek'] }]
  const used = new Map([['Požadavek z nasazení', 9], ['Moduly produktu', 5]])
  expect(rankSuggestions('Požadavek', items, item => item, { usage: item => used.get(item.title) ?? 0 }).map(item => item.title))
    .toEqual(['Požadavek', 'Požadavek z nasazení', 'Moduly produktu'])
})
