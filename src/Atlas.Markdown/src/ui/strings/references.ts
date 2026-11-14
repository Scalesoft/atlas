import { registerMarkdownStrings } from '../strings'

export interface ReferencesStrings {
  pasted: string
  proposal: string
  toolbar: string
  address: string
  addressLabel: string
  convert: (label: string) => string
  keep: string
  open: string
  copy: string
  edit: string
  editAsLink: string
  asLink: string
  load: string
  failed: string
  invalid: string
  copyFailed: string
}

export const referencesStrings = registerMarkdownStrings('references', {
  pasted: 'Vložený odkaz',
  proposal: 'Návrh reference',
  toolbar: 'Reference',
  address: 'Adresa reference',
  addressLabel: 'Adresa odkazu',
  convert: (label: string) => `Převést na ${label}`,
  keep: 'Ponechat jako odkaz',
  open: 'Otevřít odkaz',
  copy: 'Kopírovat adresu',
  edit: 'Upravit adresu',
  editAsLink: 'Upravit jako běžný odkaz',
  asLink: 'Převést na běžný odkaz',
  load: 'Načíst referenci',
  failed: 'Referenci se nepodařilo načíst. Odkaz zůstává beze změny.',
  invalid: 'Zadejte podporovanou adresu odkazu.',
  copyFailed: 'Adresu se nepodařilo zkopírovat.',
} satisfies ReferencesStrings)
