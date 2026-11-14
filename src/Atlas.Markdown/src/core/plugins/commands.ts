import type { Command, Editor } from '@tiptap/core'
import type { MarkdownPluginCommand, MarkdownPluginCommandCall, MarkdownPluginCommandDefinition, MarkdownPluginCommandOptions } from '../commandTypes'

export interface PreparedCall {
  readonly definition: MarkdownPluginCommandDefinition
  readonly apply: () => Command
  readonly active: (editor: Editor) => boolean
}
const definitions = new WeakSet<MarkdownPluginCommandDefinition>()
const calls = new WeakMap<MarkdownPluginCommandCall, PreparedCall>()

export function defineMarkdownCommand<Args = void>(options: MarkdownPluginCommandOptions<Args>): MarkdownPluginCommand<Args> {
  if (!options.id?.trim() || typeof options.apply !== 'function') throw new TypeError('A plugin command requires an id and an apply function.')
  const { id, apply, active } = options
  const definition = Object.assign((args: Args) => {
    // Command arguments are values, not mutable handles into host state.
    const value = structuredClone(args)
    const call = Object.freeze({ type: 'plugin-command', id }) as MarkdownPluginCommandCall
    calls.set(call, { definition, apply: () => apply(structuredClone(value)), active: editor => active?.(editor, structuredClone(value)) ?? false })
    return call
  }, { id }) as MarkdownPluginCommand<Args>
  definitions.add(definition)
  return Object.freeze(definition)
}

export function isCommandDefinition(command: MarkdownPluginCommandDefinition): boolean {
  return definitions.has(command)
}

/** Identity is checked as well as ID: an unregistered factory cannot impersonate a plugin. */
export function resolveCommandCall(call: MarkdownPluginCommandCall, registered: ReadonlyMap<string, MarkdownPluginCommandDefinition>): PreparedCall | undefined {
  const prepared = calls.get(call)
  return prepared && registered.get(call.id) === prepared.definition ? prepared : undefined
}
