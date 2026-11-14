import { registerMarkdownStrings, type MarkdownStrings } from '../../core'

/** Labels needed before the picker UI loads. */
export const youtrackStrings = registerMarkdownStrings('youtrack', {
  pickerHeading: 'Úkol z YouTracku',
  actionDescription: 'Vyhledat úkol a vložit odkaz',
  issueFromText: key => `Úkol YouTrack ${key}`,
  insertFailed: 'Úkol z YouTracku se nepodařilo vložit.',
  savedStateTitle: (title, state) => `${title} · ${state} (uložený stav)`,
} satisfies Partial<MarkdownStrings['youtrack']>)
