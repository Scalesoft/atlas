import { defineAsyncComponent, defineComponent, ref, type Component } from "vue";
import { withTexts } from "./i18n";

/**
 * Set once a lazily loaded chunk stays unavailable after a retry. A deploy replaces every
 * hashed chunk, so a tab opened before it asks for a file the server no longer has; the shell
 * watches this flag to offer a reload instead of a dead action.
 */
export const buildOutdated = ref(false);

const Missing = defineComponent({ name: "MissingChunk", render: () => null });

/** Runs a chunk loader, once more when it fails; a second failure sets buildOutdated and rethrows. */
export async function loadChunk<T>(loader: () => Promise<T>): Promise<T> {
  try {
    return await loader();
  } catch {
    try {
      return await loader();
    } catch (error) {
      buildOutdated.value = true;
      throw error;
    }
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
