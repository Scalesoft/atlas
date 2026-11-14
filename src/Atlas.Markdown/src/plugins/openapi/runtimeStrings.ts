import { registerMarkdownStrings, type MarkdownStrings } from '../../core'
import type { OpenApiEntryKey } from './strings'
import { operationsCount, schemasCount } from './plural'
import './strings'

/** Default (Czech) text of the `openapi` section that only the lazy runtime renders. */
export const openapiStrings: MarkdownStrings['openapi'] = registerMarkdownStrings('openapi', {
  eyebrow: 'API DEFINICE · OPENAPI',
  tryItLabel: 'Dokumentace a vyzkoušení',
  docsOnlyLabel: 'Dokumentace',
  downloadLabel: 'Stáhnout',
  versionSummary: (version, operations, schemas) => `Verze ${version} · ${operationsCount(operations)} · ${schemasCount(schemas)}`,
  downloadAction: 'Stáhnout OpenAPI',
  dialogLabel: 'OpenAPI dokumentace',
  closeDocumentationLabel: 'Zavřít dokumentaci',

  scalarModelsLabel: 'Schemas',
  frameTitle: filename => `${filename} — OpenAPI reference`,
  scalarStartFailed: 'Knihovnu Scalar se nepodařilo spustit.',
  scalarRenderFailed: error => `Dokumentaci API se nepodařilo vykreslit: ${error}`,
  scalarLoadFailed: 'Knihovnu Scalar se nepodařilo načíst.',

  editTitle: 'Upravit API',
  maximizeLabel: 'Maximalizovat editor',
  restoreLabel: 'Obnovit velikost editoru',
  closeEditorLabel: 'Zavřít editor',
  filenameLabel: 'Název souboru',
  uploadLabel: 'Načíst soubor YAML nebo JSON',
  chooseFileLabel: 'Vybrat soubor…',
  noFileChosen: 'Žádný soubor',
  modeGroupLabel: 'Režim zobrazení',
  downloadOnlyLabel: 'Pouze stažení',
  draftHint: 'Změny se do stránky zapíší až po potvrzení.',
  editSpecLabel: 'Upravit specifikaci',
  uploadFileLabel: 'Nahrát soubor',
  renameFileLabel: 'Přejmenovat soubor',
  notSpecFileError: 'Vyberte nebo přetáhněte soubor YAML nebo JSON.',
  editorLoadFailed: 'Editor zdroje se nepodařilo načíst.',
} satisfies Omit<MarkdownStrings['openapi'], OpenApiEntryKey>)
