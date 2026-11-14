import { markdownStrings } from '../core'
import type { AssistStrings } from './types'

const plural = (count: number, one: string, few: string, many: string) => count === 1 ? one : count >= 2 && count <= 4 ? few : many

/**
 * Czech defaults of the module. They live here, not in the core dictionary: the core never learns
 * about the module. Hosts override per plugin (`createAssistPlugin({ strings })`) or per panel.
 */
export const defaultAssistStrings: Readonly<AssistStrings> = Object.freeze({
  marker: (title: string) => `Návrh: ${title}`,
  listLabel: 'Návrhy pro tento odstavec',
  count: (count: number) => `${count} ${plural(count, 'návrh', 'návrhy', 'návrhů')} pro tento odstavec.`,
  empty: 'Pro tento odstavec nejsou žádné návrhy.',
  why: 'Proč tento návrh?',
  suppressKind: 'Nenabízet v této stránce',
  stale: 'Návrh už neodpovídá textu.',
  statusLabel: 'Návrhy',
  aiTag: 'AI',
  proposalLabel: 'Návrh změny',
  loading: 'Připravuji návrh…',
  loadFailed: 'Návrh se nepodařilo připravit.',
  retry: 'Zkusit znovu',
  helpful: 'Užitečný návrh',
  unhelpful: 'Neužitečný návrh',
  before: 'Před',
  after: 'Po',
  get apply() { return markdownStrings.apply },
  edit: 'Upravit před použitím',
  get cancel() { return markdownStrings.cancel },
  discard: 'Zahodit',
  summary: (removed: number, added: number) =>
    `Odebráno ${removed} ${plural(removed, 'slovo', 'slova', 'slov')}, přidáno ${added} ${plural(added, 'slovo', 'slova', 'slov')}`,
})

export function assistStrings(overrides?: Partial<AssistStrings>): AssistStrings {
  const result = Object.create(null) as AssistStrings
  for (const key of Object.keys(Object.getOwnPropertyDescriptors(defaultAssistStrings)) as (keyof AssistStrings)[]) {
    Object.defineProperty(result, key, { enumerable: true, get: () => overrides?.[key] ?? defaultAssistStrings[key] })
  }
  return result
}
