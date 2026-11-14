import { shallowRef } from "vue";
import { api } from "../api";

// The modules this Atlas runs (GET /api/modules, spec §2): read once per session, before the first route. Everything of an
// optional module is `v-if` on `enabled(id)`, and its lazy chunk is imported only behind that, so a disabled module costs
// nothing. A failure reads as no optional modules.
// shortcut: no refresh during a session, a module switched on later shows after a reload; poll like useAiCapabilities if that matters.
const enabledIds = shallowRef<ReadonlySet<string>>(new Set());
let loading: Promise<void> | undefined;
let generation = 0;

function parse(body: unknown): Set<string> {
  const modules = body && typeof body === "object" ? (body as { modules?: unknown }).modules : undefined;
  return new Set(Array.isArray(modules) ? modules.flatMap(module => typeof module?.id === "string" ? [module.id as string] : []) : []);
}

function start(): Promise<void> {
  const mine = generation;
  loading ??= api<unknown>("/modules")
    .then(parse, () => new Set<string>())
    .then(ids => { if (mine === generation) enabledIds.value = ids; });
  return loading;
}

function stop() {
  generation++;
  loading = undefined;
  enabledIds.value = new Set();
}

export function useModules() {
  return { enabled: (id: string) => enabledIds.value.has(id), start, stop };
}
