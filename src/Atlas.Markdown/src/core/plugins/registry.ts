import type { AnyExtension, Editor } from '@tiptap/core'
import type { Node } from '@tiptap/pm/model'
import type { MarkdownProfile } from '../kit'
import type { MarkdownAction, MarkdownLossyOptions, MarkdownPlugin, MarkdownPluginCommandCall, MarkdownPluginCommandDefinition, MarkdownPluginController, MarkdownPluginInstance, MarkdownPluginPanel, MarkdownReadonlyJSONContent, MarkdownReferenceProvider, MarkdownSchemaPlugin } from './types'
import { isRichFormat } from '../types'
import { isCommandDefinition, resolveCommandCall } from './commands'
import { clonePlainData } from '../plainData'
import { registerMarkdownIntentions } from '../../features/intentions/intentions'

// Ordinal, not localeCompare: the saved `plugins` list and the action order must not depend on the user's locale (cs puts "ch" after "h").
const compareIds = (a: { readonly id: string }, b: { readonly id: string }) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0)

type Cleanup = () => void
function isRecord(value: unknown): boolean {
  return !!value && typeof value === 'object' && !Array.isArray(value)
}
function assertPluginInstance(value: unknown, id: string): asserts value is MarkdownPluginInstance {
  if (!isRecord(value)) throw new TypeError(`Plugin ${id} returned an invalid plugin instance.`)
  const instance = value as Record<string, unknown>
  const actions = instance.actions as Record<string, unknown> | undefined
  if ((instance.extensions !== undefined && !Array.isArray(instance.extensions))
    || (instance.commands !== undefined && !Array.isArray(instance.commands))
    || (instance.panels !== undefined && !Array.isArray(instance.panels))
    || (instance.references !== undefined && !Array.isArray(instance.references))
    || (instance.markdownLossy !== undefined && typeof instance.markdownLossy !== 'function')
    || (instance.onCreate !== undefined && typeof instance.onCreate !== 'function')
    || (instance.onAttach !== undefined && typeof instance.onAttach !== 'function')
    || (instance.onRender !== undefined && typeof instance.onRender !== 'function')
    || (instance.intentions !== undefined && typeof instance.intentions !== 'function')
    || (actions !== undefined && (!isRecord(actions)
      || (actions.slash !== undefined && !Array.isArray(actions.slash))
      || (actions.toolbar !== undefined && !Array.isArray(actions.toolbar))
      || (actions.selection !== undefined && !Array.isArray(actions.selection))))) {
    throw new TypeError(`Plugin ${id} returned an invalid plugin instance.`)
  }
}
export interface PluginExtensionContribution {
  readonly id: string
  readonly schemaVersion?: number
  readonly profiles?: readonly MarkdownProfile[]
  readonly extensions: readonly AnyExtension[]
}

export class MarkdownPluginRegistry {
  readonly references: readonly MarkdownReferenceProvider[]
  readonly extensions: AnyExtension[] = []
  readonly extensionContributions: PluginExtensionContribution[] = []
  readonly installedSchemaVersions: readonly MarkdownSchemaPlugin[]
  private _schemaPlugins: readonly MarkdownSchemaPlugin[] = Object.freeze([])
  readonly actions: Readonly<{ slash: readonly MarkdownAction[]; toolbar: readonly MarkdownAction[]; selection: readonly MarkdownAction[] }>
  readonly panels: Readonly<{ inspector: readonly MarkdownPluginPanel[]; status: readonly MarkdownPluginPanel[] }>
  private readonly instances: MarkdownPluginInstance[] = []
  private readonly lossChecks = new Map<string, MarkdownPluginInstance['markdownLossy']>()
  private readonly commands = new Map<string, MarkdownPluginCommandDefinition>()
  private readonly migrations = new Map<string, ReadonlyMap<number, NonNullable<MarkdownPlugin['migrations']>[number]>>()
  private readonly migrationOrder: readonly string[]
  private readonly lifetime = new AbortController()
  private engineCleanup: Cleanup[] = []
  private viewCleanup: Cleanup[] = []
  private renderCleanup: Cleanup[] = []
  private engineCreated = false
  private viewAttached = false
  private rendered = false
  private destroyed = false

  constructor(plugins: readonly MarkdownPlugin[], private readonly profile: MarkdownProfile) {
    if (!Array.isArray(plugins)) throw new TypeError('Plugins must be an array.')
    const byId = new Map<string, MarkdownPlugin>()
    for (const plugin of plugins) {
      if (!isRecord(plugin) || typeof plugin.id !== 'string' || !plugin.id.trim()
        || plugin.apiVersion !== 1 || typeof plugin.create !== 'function'
        || (plugin.requires !== undefined && (!Array.isArray(plugin.requires)
          || plugin.requires.some((id: unknown) => typeof id !== 'string' || !id.trim())))
        || (plugin.profiles !== undefined && (!Array.isArray(plugin.profiles)
          || plugin.profiles.some((value: unknown) => typeof value !== 'string' || !['markdown-safe', 'rich'].includes(value))))
        || (plugin.migrations !== undefined && !Array.isArray(plugin.migrations))
        || (plugin.entities !== undefined && !Array.isArray(plugin.entities))) {
        throw new TypeError('Unsupported plugin definition or API version.')
      }
      if (byId.has(plugin.id)) throw new TypeError(`Duplicate plugin: ${plugin.id}`)
      if (plugin.profiles && !plugin.profiles.includes(profile)) throw new TypeError(`Plugin ${plugin.id} does not support ${profile}.`)
      if (plugin.schemaVersion !== undefined && (!Number.isSafeInteger(plugin.schemaVersion) || plugin.schemaVersion < 1)) throw new TypeError(`Invalid schema version for ${plugin.id}.`)
      if (plugin.migrations?.length && plugin.schemaVersion === undefined) throw new TypeError(`Plugin ${plugin.id} declares migrations without a schema version.`)
      const steps = new Map<number, NonNullable<MarkdownPlugin['migrations']>[number]>()
      for (const migration of plugin.migrations ?? []) {
        if (!isRecord(migration) || !Number.isSafeInteger(migration.from) || migration.from < 1 || migration.to !== migration.from + 1
          || migration.to > (plugin.schemaVersion ?? 0) || typeof migration.migrate !== 'function' || steps.has(migration.from)) {
          throw new TypeError(`Plugin ${plugin.id} contains an invalid migration step.`)
        }
        steps.set(migration.from, migration)
      }
      this.migrations.set(plugin.id, steps)
      byId.set(plugin.id, plugin)
    }
    const ordered: MarkdownPlugin[] = []
    const visiting = new Set<string>()
    const visited = new Set<string>()
    const visit = (plugin: MarkdownPlugin) => {
      if (visiting.has(plugin.id)) throw new TypeError(`Cyclic plugin dependency: ${plugin.id}`)
      if (visited.has(plugin.id)) return
      visiting.add(plugin.id)
      for (const id of plugin.requires ?? []) {
        const dependency = byId.get(id)
        if (!dependency) throw new TypeError(`Plugin ${plugin.id} requires ${id}.`)
        visit(dependency)
      }
      visiting.delete(plugin.id)
      visited.add(plugin.id)
      ordered.push(plugin)
    }
    plugins.forEach(visit)
    this.migrationOrder = Object.freeze(ordered.map(plugin => plugin.id))
    const actions: { slash: MarkdownAction[]; toolbar: MarkdownAction[]; selection: MarkdownAction[] } = { slash: [], toolbar: [], selection: [] }
    const actionIds = { slash: new Set<string>(), toolbar: new Set<string>(), selection: new Set<string>() }
    const panels: { inspector: MarkdownPluginPanel[]; status: MarkdownPluginPanel[] } = { inspector: [], status: [] }
    const panelIds = new Set<string>()
    const references: MarkdownReferenceProvider[] = []
    const referenceIds = new Set<string>()
    const referenceNodeTypes = new Set<string>()
    try {
      for (const plugin of ordered) {
        const instance = plugin.create(Object.freeze({ profile, signal: this.lifetime.signal }))
        assertPluginInstance(instance, plugin.id)
        this.instances.push(instance)
        this.lossChecks.set(plugin.id, instance.markdownLossy)
        this.extensions.push(...instance.extensions ?? [])
        if (instance.extensions?.length) this.extensionContributions.push({
          id: plugin.id, schemaVersion: plugin.schemaVersion, profiles: plugin.profiles,
          extensions: [...instance.extensions],
        })
        for (const reference of instance.references ?? []) {
          if (!isRecord(reference) || (['id', 'label', 'nodeType'] as const).some(key => typeof reference[key] !== 'string' || !reference[key].trim())
            || typeof reference.read !== 'function'
            || (['matchUrl', 'resolveUrl', 'search'] as const).some(key => reference[key] !== undefined && typeof reference[key] !== 'function')
            || referenceIds.has(reference.id) || referenceNodeTypes.has(reference.nodeType)) {
            throw new TypeError(`Invalid or duplicate reference provider in plugin ${plugin.id}.`)
          }
          referenceIds.add(reference.id); referenceNodeTypes.add(reference.nodeType)
          references.push(Object.freeze({
            id: reference.id, label: reference.label, nodeType: reference.nodeType,
            read: reference.read.bind(reference),
            matchUrl: reference.matchUrl?.bind(reference),
            resolveUrl: reference.resolveUrl?.bind(reference),
            search: reference.search?.bind(reference),
          }))
        }
        for (const command of instance.commands ?? []) {
          if (!isCommandDefinition(command)) throw new TypeError(`Plugin ${plugin.id} contains an invalid command factory.`)
          if (this.commands.has(command.id)) throw new TypeError(`Duplicate command: ${command.id}`)
          this.commands.set(command.id, command)
        }
        for (const surface of ['slash', 'toolbar', 'selection'] as const) {
          for (const action of instance.actions?.[surface] ?? []) {
            if (!isRecord(action) || typeof action.id !== 'string' || !action.id.trim()
              || typeof action.label !== 'string' || !action.label.trim() || actionIds[surface].has(action.id)
              || !isRecord(action.command) || typeof action.command.type !== 'string'
              || (action.priority !== undefined && !Number.isFinite(action.priority))
              || (action.parameter !== undefined && (!isRecord(action.parameter) || typeof action.parameter.label !== 'string' || !action.parameter.label.trim() || typeof action.parameter.command !== 'function'))
              || (action.keywords !== undefined && (!Array.isArray(action.keywords) || action.keywords.some(value => typeof value !== 'string')))
              || (action.variants !== undefined && (!Array.isArray(action.variants)
                || action.variants.some(value => !['document', 'compact'].includes(value))))) {
              throw new TypeError(`Invalid or duplicate ${surface} action: ${isRecord(action) ? String(action.id) : ''}`)
            }
            actionIds[surface].add(action.id)
            actions[surface].push(Object.freeze({ ...action,
              command: action.command.type === 'plugin-command' ? action.command : Object.freeze(structuredClone(action.command)),
              keywords: Object.freeze([...(action.keywords ?? [])]), variants: action.variants && Object.freeze([...action.variants]) }))
          }
        }
        for (const panel of instance.panels ?? []) {
          if (!isRecord(panel) || typeof panel.id !== 'string' || !panel.id.trim()
            || typeof panel.label !== 'string' || !panel.label.trim() || !['inspector', 'status'].includes(panel.placement)
            || typeof panel.mount !== 'function' || panelIds.has(panel.id)
            || (panel.priority !== undefined && !Number.isFinite(panel.priority))
            || (panel.variants !== undefined && (!Array.isArray(panel.variants)
              || panel.variants.some(value => !['document', 'compact'].includes(value))))) {
            throw new TypeError(`Invalid or duplicate plugin panel: ${isRecord(panel) ? String(panel.id) : ''}`)
          }
          panelIds.add(panel.id)
          panels[panel.placement].push(Object.freeze({ ...panel,
            variants: panel.variants && Object.freeze([...panel.variants]) }))
        }
      }
      for (const action of [...actions.slash, ...actions.toolbar, ...actions.selection]) {
        if (action.command.type === 'plugin-command' && !this.resolve(action.command)) throw new TypeError(`Action ${action.id} references an unregistered command.`)
      }
    } catch (error) {
      this.destroy()
      throw error
    }
    this.references = Object.freeze(references)
    this.installedSchemaVersions = Object.freeze(ordered.filter(plugin => plugin.schemaVersion !== undefined)
      .map(plugin => Object.freeze({ id: plugin.id, version: plugin.schemaVersion! })).sort(compareIds))
    const byPriority = <T extends { readonly priority?: number; readonly id: string }>(left: T, right: T) =>
      (left.priority ?? 0) - (right.priority ?? 0) || compareIds(left, right)
    this.actions = Object.freeze({ slash: Object.freeze(actions.slash.sort(byPriority)), toolbar: Object.freeze(actions.toolbar.sort(byPriority)), selection: Object.freeze(actions.selection.sort(byPriority)) })
    this.panels = Object.freeze({ inspector: Object.freeze(panels.inspector.sort(byPriority)), status: Object.freeze(panels.status.sort(byPriority)) })
  }

  get schemaPlugins(): readonly MarkdownSchemaPlugin[] { return this._schemaPlugins }
  migrate(value: unknown): unknown {
    if (!value || typeof value !== 'object') return value
    const envelope = clonePlainData(value) as { format?: unknown; plugins?: Array<{ id: string; version: number }>; document: import('@tiptap/core').JSONContent }
    if (!isRichFormat(envelope.format)) return envelope
    if (!Array.isArray(envelope.plugins)) return envelope
    const requirements = new Map(envelope.plugins.map(requirement => [requirement.id, requirement]))
    for (const pluginId of this.migrationOrder) {
      const requirement = requirements.get(pluginId)
      if (!requirement) continue
      const installed = this.installedSchemaVersions.find(plugin => plugin.id === requirement.id)
      if (!installed || installed.version === requirement.version) continue
      if (requirement.version > installed.version) throw new TypeError(`Plugin ${requirement.id} data is newer than the installed schema.`)
      const steps = this.migrations.get(requirement.id)
      while (requirement.version < installed.version) {
        const migration = steps?.get(requirement.version)
        if (!migration) throw new TypeError(`Missing migration for plugin ${requirement.id} from version ${requirement.version}.`)
        let migrated: import('@tiptap/core').JSONContent
        try { migrated = migration.migrate(clonePlainData(envelope.document)) }
        catch (error) {
          throw new Error(`Migration for plugin ${requirement.id} from version ${requirement.version} failed.`, { cause: error })
        }
        if (!migrated || typeof migrated !== 'object') throw new TypeError(`Plugin ${requirement.id} migration returned an invalid document.`)
        envelope.document = clonePlainData(migrated)
        requirement.version = migration.to
      }
    }
    return envelope
  }
  initializeSchema(pluginIds: ReadonlySet<string>): void {
    const schemas: MarkdownSchemaPlugin[] = []
    for (const contribution of this.extensionContributions) {
      if (!pluginIds.has(contribution.id)) continue
      if (contribution.schemaVersion === undefined) throw new TypeError(`Plugin ${contribution.id} must declare its schema version when changing the document schema.`)
      if (!schemas.some(schema => schema.id === contribution.id)) {
        if (this.profile === 'markdown-safe' && !contribution.profiles?.includes('markdown-safe')) {
          throw new TypeError(`Content plugin ${contribution.id} must explicitly support markdown-safe.`)
        }
        if (this.profile === 'markdown-safe' && !this.lossChecks.get(contribution.id)) {
          throw new TypeError(`Content plugin ${contribution.id} must declare Markdown round-trip fidelity for markdown-safe.`)
        }
        schemas.push(Object.freeze({ id: contribution.id, version: contribution.schemaVersion }))
      }
    }
    this._schemaPlugins = Object.freeze(schemas.sort(compareIds))
  }

  resolve(call: MarkdownPluginCommandCall) { return resolveCommandCall(call, this.commands) }
  get hasCommands(): boolean { return this.commands.size > 0 }
  isLossy(document: Node, usedSchemaPlugins: ReadonlySet<string>, options: MarkdownLossyOptions = {}): boolean {
    return [...this.lossChecks].some(([id, check]) => check ? check(document, options) : usedSchemaPlugins.has(id))
  }
  isSchemaLossy(document: Node, usedSchemaPlugins: ReadonlySet<string>, options: MarkdownLossyOptions = {}): boolean {
    return this.schemaPlugins.some(({ id }) => {
      const check = this.lossChecks.get(id)
      return check ? check(document, options) : usedSchemaPlugins.has(id)
    })
  }

  create(editor: Editor, controller: MarkdownPluginController): void {
    if (this.destroyed) throw new Error('Plugin registry is destroyed.')
    if (this.engineCreated) throw new Error('Plugin engine hooks may only be created once.')
    this.engineCreated = true
    const context = Object.freeze({ editor, controller, signal: this.lifetime.signal })
    for (const instance of this.instances) {
      if (instance.intentions) this.engineCleanup.push(registerMarkdownIntentions(editor, instance.intentions))
      const cleanup = instance.onCreate?.(context)
      this.acceptCleanup(cleanup, this.engineCleanup)
      if (this.destroyed) throw new Error('Plugin registry was destroyed during engine creation.')
    }
  }
  attach(editor: Editor, controller: MarkdownPluginController): void {
    if (this.destroyed) throw new Error('Plugin registry is destroyed.')
    if (!this.engineCreated) throw new Error('Plugin engine hooks must be created before attaching a view.')
    if (this.viewAttached) throw new Error('Plugin view hooks are already attached.')
    this.viewAttached = true
    const context = Object.freeze({ editor, controller, signal: this.lifetime.signal })
    for (const instance of this.instances) {
      const cleanup = instance.onAttach?.(context)
      this.acceptCleanup(cleanup, this.viewCleanup)
      if (this.destroyed) throw new Error('Plugin registry was destroyed during view attachment.')
    }
  }
  detach(): void {
    if (!this.viewAttached && !this.viewCleanup.length) return
    this.viewAttached = false
    this.clean(this.viewCleanup)
  }
  render(container: HTMLElement, document: MarkdownReadonlyJSONContent, ownerDocument: Document): void {
    if (this.destroyed) throw new Error('Plugin registry is destroyed.')
    if (this.rendered) throw new Error('Plugin render hooks may only be mounted once.')
    this.rendered = true
    const context = Object.freeze({ container, document, ownerDocument, signal: this.lifetime.signal })
    for (const instance of this.instances) {
      const cleanup = instance.onRender?.(context)
      this.acceptCleanup(cleanup, this.renderCleanup)
      if (this.destroyed) throw new Error('Plugin registry was destroyed during static rendering.')
    }
  }
  destroy(): void {
    if (this.destroyed) return
    this.destroyed = true
    this.lifetime.abort()
    const errors: unknown[] = []
    this.viewAttached = false
    this.rendered = false
    this.cleanInto(this.renderCleanup, errors)
    this.cleanInto(this.viewCleanup, errors)
    this.cleanInto(this.engineCleanup, errors)
    if (errors.length) throw new AggregateError(errors, 'Plugin cleanup failed.')
  }
  private acceptCleanup(cleanup: void | Cleanup, callbacks: Cleanup[]): void {
    if (cleanup === undefined) return
    if (typeof cleanup !== 'function') throw new TypeError('Plugin lifecycle cleanup must be a function.')
    if (!this.destroyed) { callbacks.push(cleanup); return }
    cleanup()
  }
  private clean(callbacks: Cleanup[]): void {
    const errors: unknown[] = []
    this.cleanInto(callbacks, errors)
    if (errors.length) throw new AggregateError(errors, 'Plugin cleanup failed.')
  }
  private cleanInto(callbacks: Cleanup[], errors: unknown[]): void {
    for (const cleanup of callbacks.splice(0).reverse()) {
      try { cleanup() } catch (error) { errors.push(error) }
    }
  }
}
