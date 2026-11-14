import { ref, watch } from 'vue'
import type { Editor } from '@tiptap/core'
import { dispatchCommand, queryCommand } from '../core/commands'
import type { MarkdownCommand, MarkdownProfile } from '../core/types'

export function useToolbarCommands(editor: () => Editor, profile: () => MarkdownProfile) {
  const revision = ref(0)
  watch(editor, (current, _previous, onCleanup) => {
    const refresh = () => { revision.value++ }
    current.on('transaction', refresh)
    onCleanup(() => current.off('transaction', refresh))
  }, { immediate: true })
  function query(command: MarkdownCommand) {
    void revision.value
    return queryCommand(editor(), profile(), command)
  }
  function run(command: MarkdownCommand): boolean {
    if (!query(command).enabled) return false
    editor().commands.focus()
    return dispatchCommand(editor(), profile(), command)
  }
  function isActive(name: string | Record<string, unknown>, attrs?: Record<string, unknown>): boolean {
    void revision.value
    return typeof name === 'string' ? editor().isActive(name, attrs) : editor().isActive(name)
  }
  return { query, run, isActive, revision }
}
