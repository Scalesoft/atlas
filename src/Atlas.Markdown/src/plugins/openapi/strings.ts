import { registerMarkdownStrings, type MarkdownStrings } from '../../core'
import { operationsCount, schemasCount } from './plural'

/** Default (Czech) text of the `openapi` section that the plugin entry renders. */
const entry = {
  label: 'API',
  slashDescription: 'Dokumentace REST API ze specifikace OpenAPI',
  summaryText: summary => `${summary.title} · v${summary.version} · ${operationsCount(summary.operations)} · ${schemasCount(summary.schemas)}${summary.author ? ` · Autor: ${summary.author}` : ''}`,
  lightSummary: (title, version, filename) => version ? `${title} · v${version} · ${filename}` : `${title} · ${filename}`,
  statsLine: (operations, schemas) => `${operationsCount(operations)} · ${schemasCount(schemas)}`,
  authorLine: author => `Autor: ${author}`,

  loadingDocumentation: 'Načítá se dokumentace API…',
  timeoutError: 'Dokumentace API se nenačetla včas. Zkontrolujte připojení a zkuste to znovu.',
  loadFailedError: reason => `Dokumentaci API se nepodařilo načíst: ${reason}`,

  sourceLabel: 'OpenAPI zdroj',
  emptySourceError: 'Vložte zdroj OpenAPI ve formátu YAML nebo JSON.',
  sourceTooLargeError: 'Zdroj OpenAPI může mít nejvýše 512 KiB.',
  invalidSyntaxError: message => `Neplatný YAML nebo JSON: ${message}`,
  invalidAliasError: message => `Neplatný nebo příliš složitý YAML alias: ${message}`,
  rootMustBeObjectError: 'Kořenem OpenAPI musí být objekt.',
  unsupportedVersionError: 'Podporovány jsou pouze dokumenty OpenAPI 3.0 a 3.1.',
  missingInfoError: 'OpenAPI potřebuje textové info.title a info.version.',
  missingPathsError: 'OpenAPI potřebuje objekt paths.',
  invalidPathsError: 'OpenAPI paths musí obsahovat cesty a objekty operací.',
  missingResponsesError: (method, path) => `Operace ${method.toUpperCase()} ${path} potřebuje objekt responses.`,
  externalRefError: 'Externí odkaz $ref není podporován; vložte všechny definice do jednoho souboru.',
  unsupportedRefError: 'Podporovány jsou pouze místní odkazy JSON Pointer (#/…).',
  invalidPointerError: 'Neplatný místní odkaz JSON Pointer.',
  refNotFoundError: ref => `Místní odkaz ${ref} nebyl nalezen.`,
  cyclicAliasError: 'Cyklický YAML alias není podporován.',
  tooComplexError: 'Zdroj OpenAPI je příliš složitý.',
  invalidDataError: 'Neplatná data OpenAPI.',
  invalidModeError: 'Neplatná data OpenAPI: režim zobrazení.',
  invalidUploaderError: 'Neplatná data OpenAPI: autor nahrání.',
} satisfies Partial<MarkdownStrings['openapi']>
/** Keys registered here; ./runtimeStrings adds the rest when the lazy runtime loads. */
export type OpenApiEntryKey = keyof typeof entry
export const openapiStrings: Pick<MarkdownStrings['openapi'], OpenApiEntryKey> = registerMarkdownStrings('openapi', entry)
