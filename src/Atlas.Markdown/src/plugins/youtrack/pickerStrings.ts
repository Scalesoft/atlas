import { registerMarkdownStrings, type MarkdownStrings } from '../../core'
import { youtrackStrings } from './strings'

registerMarkdownStrings('youtrack', {
  pickerLabel: 'Vybrat úkol z YouTracku',
  queryLabel: 'Číslo, odkaz nebo název úkolu',
  queryPlaceholder: 'Např. PROJ-123 nebo název úkolu',
  resultsLabel: 'Nalezené úkoly',
  searchResultsStatus: count => !count ? 'Žádné úkoly nebyly nalezeny.'
    : count === 1 ? '1 nalezený úkol'
    : `${count} nalezen${count < 5 ? 'é úkoly' : 'ých úkolů'}`,
  searchFailedStatus: 'Úkoly se nepodařilo načíst. Zkuste hledání znovu.',
} satisfies Partial<MarkdownStrings['youtrack']>)
export { youtrackStrings }
