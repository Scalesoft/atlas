import type { Editor } from '@tiptap/core'
import { imagesStrings } from '../../ui/strings/images'

const queues = new WeakMap<Editor, ReturnType<typeof createResourceQueue>>()
/** Two active resolvers per editor, shared by drops and native image uploads. */
export function runResourceTask<T>(editor: Editor, signal: AbortSignal, work: () => Promise<T>): Promise<T> {
  let queue = queues.get(editor)
  if (!queue) { queue = createResourceQueue(); queues.set(editor, queue) }
  return queue(signal, work)
}
export function createResourceQueue() {
  let active = 0
  const waiting: Array<() => void> = []
  const drain = () => { while (active < 2 && waiting.length) waiting.shift()!() }
  return <T>(signal: AbortSignal, work: () => Promise<T>): Promise<T> => new Promise((resolve, reject) => {
    const abort = () => {
      const index = waiting.indexOf(start)
      if (index >= 0) waiting.splice(index, 1)
      reject(new DOMException(imagesStrings.importCancelled, 'AbortError'))
    }
    const start = () => {
      signal.removeEventListener('abort', abort)
      if (signal.aborted) { abort(); return }
      active++
      let operation: Promise<T>
      try { operation = work() } catch (error) { operation = Promise.reject(error) }
      void Promise.resolve(operation).then(resolve, reject).finally(() => { active--; drain() })
    }
    if (signal.aborted) { abort(); return }
    signal.addEventListener('abort', abort, { once: true })
    waiting.push(start); drain()
  })
}
