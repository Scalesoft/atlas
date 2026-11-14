import { registerMarkdownStrings } from '../strings'
import type { DialectStringKey, InputStrings } from './input'

/** Default (Czech) text of the Markdown dialect's messages; they join the `input` section when the dialect loads. */
registerMarkdownStrings('input', {
  dialectUnknownComponent: name => `Neznámá komponenta ${name}.`,
  dialectInvalidComponent: (name, reason) => `Neplatná komponenta ${name}: ${reason}`,
  dialectInvalidLink: 'Neplatný cíl odkazu komponenty.',
  dialectUnknownKey: key => `Neznámý klíč ${key}.`,
  dialectInvalidShape: 'Data komponenty nemají očekávaný tvar.',
  dialectYamlAt: (line, reason) => `Řádek ${line} těla: ${reason}`,
  dialectYamlUnsupported: 'nepodporovaný zápis (a-YAML dovoluje mapy, seznamy, krátké seznamy [a, b] a bloky |; kotvy, aliasy, tagy, flow mapy ani víc dokumentů ne).',
  dialectYamlIndentation: 'neplatné odsazení.',
  dialectYamlTooDeep: 'vnoření je hlubší než 4 úrovně.',
  dialectYamlDuplicateKey: key => `duplicitní klíč ${key}.`,
} satisfies Pick<InputStrings, DialectStringKey>)
