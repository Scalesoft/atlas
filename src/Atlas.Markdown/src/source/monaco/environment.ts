// The single owner of `globalThis.MonacoEnvironment` for every source editor in this package.
//
// Monaco asks `MonacoEnvironment.getWorker(moduleId, label)` whenever a language service starts a worker
// (`editorWorkerService` for word suggestions/links, `yaml` for monaco-yaml, `json` for JSON mode, …).
// Source views register the workers they need by label while an editor is mounted; the environment is
// installed on the first mount and restored on the last unmount (ref counting), and any environment the
// host installed before us keeps answering the labels we do not own.
//
// The Markdown source view registers nothing and disables word suggestions and links, so it never starts
// a worker; the base editor worker below is only a fallback for labels nobody registered.
import EditorWorker from './monaco-editor.worker?worker'
import type { SourceDisposable, SourceWorkerOptions } from '../index'

type WorkerEnvironment = { getWorker?: (moduleId: string, label: string) => Worker; [key: string]: unknown }
interface Registration { readonly label: string; readonly create: () => Worker; readonly hostFirst: boolean }

const workerGlobal = globalThis as typeof globalThis & { MonacoEnvironment?: WorkerEnvironment }
const registrations: Registration[] = []
let users = 0
let previousEnvironment: WorkerEnvironment | undefined
let installedEnvironment: WorkerEnvironment | undefined

function createWorker(moduleId: string, label: string): Worker {
  let own: Registration | undefined
  for (let index = registrations.length - 1; index >= 0 && !own; index--) if (registrations[index]!.label === label) own = registrations[index]
  if (own && !own.hostFirst) return own.create()
  if (previousEnvironment?.getWorker) return previousEnvironment.getWorker(moduleId, label)
  if (own) return own.create()
  return new EditorWorker({ name: label })
}

/** Installs the shared environment for one mounted editor; call the returned function on unmount. */
export function acquireMonacoEnvironment(): () => void {
  if (users++ === 0) {
    previousEnvironment = workerGlobal.MonacoEnvironment
    installedEnvironment = { ...previousEnvironment, getWorker: createWorker }
    workerGlobal.MonacoEnvironment = installedEnvironment
  }
  let released = false
  return () => {
    if (released) return
    released = true
    if (--users) return
    // A host that replaced the environment meanwhile keeps its own.
    if (workerGlobal.MonacoEnvironment === installedEnvironment) workerGlobal.MonacoEnvironment = previousEnvironment
    previousEnvironment = undefined; installedEnvironment = undefined
  }
}

/**
 * Answers Monaco's worker requests for `label` while the registration lives. The newest registration of
 * a label wins. `hostFirst` lets an environment installed by the host before us answer first (for shared
 * labels such as `json`), so the host's own Monaco keeps the workers it expects.
 */
export function registerWorker(label: string, create: () => Worker, options: SourceWorkerOptions = {}): SourceDisposable {
  const registration: Registration = { label, create, hostFirst: options.hostFirst === true }
  registrations.push(registration)
  return {
    dispose() {
      const index = registrations.indexOf(registration)
      if (index >= 0) registrations.splice(index, 1)
    },
  }
}
