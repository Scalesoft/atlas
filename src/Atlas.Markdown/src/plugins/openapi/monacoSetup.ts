// OpenAPI language services for the shared source editor (`@atlas/markdown/source`): the OpenAPI
// schema, monaco-yaml with its worker, and Monaco's JSON mode with its worker. A lazy chunk of its own,
// loaded (in parallel with the editor chunk) only when an OpenAPI source view opens or is prefetched;
// the Markdown page source never downloads it.
import { configureMonacoYaml, type JSONSchema } from 'monaco-yaml'
// Registers the `json` language and `monaco.languages.json`; its `jsonMode` loads on the first JSON model.
import 'monaco-editor/esm/vs/language/json/monaco.contribution.js'
import type * as Monaco from 'monaco-editor/esm/vs/editor/editor.api.js'
import JsonWorker from './monaco-json.worker?worker'
import YamlWorker from './monaco-yaml.worker?worker'
import type { SourceDisposable, SourceEditorSetupEnvironment } from '../../source'
import { OPENAPI_MODEL_SCOPE } from './sourceEditorLoader'

const operation: JSONSchema = {
  type: 'object',
  properties: {
    tags: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' },
    description: { type: 'string' },
    operationId: { type: 'string' },
    parameters: { type: 'array' },
    requestBody: { type: 'object' },
    responses: { type: 'object', description: 'HTTP response definitions by status code.' },
    security: { type: 'array' },
    deprecated: { type: 'boolean' },
  },
  additionalProperties: true,
}
const pathItem: JSONSchema = {
  type: 'object',
  properties: {
    summary: { type: 'string' }, description: { type: 'string' }, servers: { type: 'array' }, parameters: { type: 'array' },
    get: operation, put: operation, post: operation, delete: operation,
    options: operation, head: operation, patch: operation, trace: operation,
  },
  additionalProperties: true,
}
const openApiSchema: JSONSchema = {
  type: 'object',
  description: 'Self-contained OpenAPI 3.0 or 3.1 document.',
  required: ['openapi', 'info', 'paths'],
  properties: {
    openapi: { type: 'string', pattern: '^3\\.(0|1)\\.\\d+$', description: 'OpenAPI specification version, for example 3.1.0.' },
    info: { type: 'object', required: ['title', 'version'], properties: {
      title: { type: 'string' }, version: { type: 'string' }, description: { type: 'string' },
      termsOfService: { type: 'string' }, contact: { type: 'object', properties: {
        name: { type: 'string' }, url: { type: 'string' }, email: { type: 'string' },
      }, additionalProperties: true }, license: { type: 'object' },
    }, additionalProperties: true },
    servers: { type: 'array', items: { type: 'object', properties: {
      url: { type: 'string' }, description: { type: 'string' }, variables: { type: 'object' },
    }, additionalProperties: true } },
    paths: { type: 'object', patternProperties: { '^/': pathItem }, additionalProperties: true },
    components: { type: 'object', properties: {
      schemas: { type: 'object' }, responses: { type: 'object' }, parameters: { type: 'object' },
      examples: { type: 'object' }, requestBodies: { type: 'object' }, headers: { type: 'object' },
      securitySchemes: { type: 'object' }, links: { type: 'object' }, callbacks: { type: 'object' },
    }, additionalProperties: true },
    security: { type: 'array' }, tags: { type: 'array' }, externalDocs: { type: 'object' },
  },
  additionalProperties: true,
}

const modelMatch = [`**/__atlas_${OPENAPI_MODEL_SCOPE}__/**`]
const schemaUri = 'inmemory://atlas-markdown/openapi-schema.json'

type JsonDefaults = typeof Monaco.languages.json.jsonDefaults
let activeEditors = 0
let yamlService: { dispose(): void } | undefined
let jsonDefaults: JsonDefaults | undefined
let previousJsonOptions: JsonDefaults['diagnosticsOptions'] | undefined
let installedJsonOptions: JsonDefaults['diagnosticsOptions'] | undefined

function acquireLanguages(monaco: typeof Monaco): void {
  if (activeEditors++) return
  try {
    yamlService = configureMonacoYaml(monaco, {
      enableSchemaRequest: false,
      schemas: [{ uri: schemaUri, fileMatch: modelMatch, schema: openApiSchema }],
      validate: true,
      yamlVersion: '1.2',
    }) as unknown as { dispose(): void }
    jsonDefaults = monaco.languages.json.jsonDefaults
    previousJsonOptions = jsonDefaults.diagnosticsOptions
    installedJsonOptions = { ...previousJsonOptions, schemas: [
      ...(previousJsonOptions.schemas ?? []),
      { uri: schemaUri, fileMatch: modelMatch, schema: openApiSchema },
    ] }
    jsonDefaults.setDiagnosticsOptions(installedJsonOptions)
  } catch (error) {
    activeEditors = 0
    releaseServices()
    throw error
  }
}

function releaseServices(): void {
  yamlService?.dispose(); yamlService = undefined
  if (jsonDefaults && jsonDefaults.diagnosticsOptions === installedJsonOptions && previousJsonOptions) jsonDefaults.setDiagnosticsOptions(previousJsonOptions)
  jsonDefaults = undefined; previousJsonOptions = undefined; installedJsonOptions = undefined
}

/** `setup` hook for `mountSourceEditor`: OpenAPI schema assistance for YAML and JSON models. */
export function setupOpenApiSource(monaco: unknown, environment: SourceEditorSetupEnvironment): SourceDisposable {
  const api = monaco as typeof Monaco
  // YAML is ours alone; a host's own Monaco may already answer `json` for its editors.
  const yaml = environment.registerWorker('yaml', () => new YamlWorker({ name: 'yaml' }))
  const json = environment.registerWorker('json', () => new JsonWorker({ name: 'json' }), { hostFirst: true })
  try { acquireLanguages(api) } catch (error) { yaml.dispose(); json.dispose(); throw error }
  let released = false
  return {
    dispose() {
      if (released) return
      released = true
      yaml.dispose(); json.dispose()
      if (!--activeEditors) releaseServices()
    },
  }
}
