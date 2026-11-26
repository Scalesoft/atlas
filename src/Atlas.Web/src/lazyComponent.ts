import { defineAsyncComponent, defineComponent, type Component } from "vue";
import { withTexts } from "./i18n";

const Missing = defineComponent({ name: "MissingChunk", render: () => null });
const reloadKey = "atlas.newBuildReload";

/**
 * A deploy replaces every hashed chunk, so a tab opened before it asks for files the server no longer has. Vite's import
 * wrapper reports every failed dynamic import (lazy components, loadChunk and plain `import()` alike) as `vite:preloadError`.
 * When this page's HTML, asked again, now starts another entry script, the tab loads the current build at the address it
 * already shows. No answer or the same entry (a network failure) leaves the failure to the place that asked; at most one
 * reload a minute, so servers answering with different builds cannot loop.
 */
export function reloadOnNewBuild(win: Window = window): void {
  let checking: Promise<void> | undefined;
  win.addEventListener("vite:preloadError", () => {
    checking ??= newBuildDeployed(win)
      .then((deployed) => { if (deployed && claimReload(win)) win.location.reload(); }, () => {})
      .finally(() => { checking = undefined; });
  });
}

async function newBuildDeployed(win: Window): Promise<boolean> {
  const running = win.document.querySelector<HTMLScriptElement>("script[type=module][src]")?.src;
  if (!running) return false;
  const response = await win.fetch(win.location.origin + win.location.pathname, { cache: "no-store" });
  if (!response.ok) return false;
  const current = new DOMParser().parseFromString(await response.text(), "text/html").querySelector("script[type=module][src]")?.getAttribute("src");
  return !!current && new URL(current, response.url || win.location.href).href !== running;
}

function claimReload(win: Window): boolean {
  try {
    if (Date.now() - Number(win.sessionStorage.getItem(reloadKey) ?? 0) < 60_000) return false;
    win.sessionStorage.setItem(reloadKey, String(Date.now()));
    return true;
  } catch {
    return false;
  }
}

/** Runs a chunk loader, once more when it fails (a flaky request); a second failure rethrows. */
export async function loadChunk<T>(loader: () => Promise<T>): Promise<T> {
  try {
    return await loader();
  } catch {
    return await loader();
  }
}

/**
 * Resolves a lazy component ahead of its first render, so that render is synchronous (the gesture that shows it gets its
 * answer in the same task, P17): fetching only its chunk still leaves the first instance a promise to wait for. Call it once
 * the chunk is in: a lazy component that failed resolves to its empty fallback for good.
 */
export function preloadComponent(component: Component): Promise<unknown> {
  // shortcut: `__asyncLoader` is Vue's internal entry to the loader (hydration uses it); when a Vue upgrade drops it, the
  // preload test in lazyComponent.test.ts fails and lazyComponent needs a resolved-component cache of its own.
  return (component as { __asyncLoader?: () => Promise<unknown> }).__asyncLoader?.() ?? Promise.resolve();
}

/**
 * `defineAsyncComponent` that retries a failed chunk once and flags a persistent failure. `loading`
 * shows a placeholder (LoadingSkeleton for a settings panel) from the first frame. `error` replaces the
 * empty component a chunk that stays unavailable would render, for a place that must not stay blank.
 */
export function lazyComponent<M extends Component | { default: Component }>(loader: () => Promise<M>, options: { loading?: Component; error?: Component } = {}) {
  // Typed as the component the module exports, so a caller keeps its props, emits and exposed methods.
  return defineAsyncComponent({
    ...(options.loading ? { loadingComponent: options.loading, delay: 0 } : {}),
    loader: async (): Promise<Component> => {
      try {
        // Its texts in another language arrive with the chunk (src/i18n): it renders once they are in.
        return await withTexts(await loadChunk(loader));
      } catch {
        return options.error ?? Missing;
      }
    },
  }) as unknown as M extends { default: infer C } ? C : M;
}
