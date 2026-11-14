import { computed, onBeforeUnmount, ref, shallowRef, type ComputedRef, type Ref } from "vue";
import { ApiError, errorMessage } from "../../api";

/**
 * The lifecycle of a settings page that edits one versioned document: load it, keep a draft next to
 * it, save the draft with the version that was loaded, say so on a conflict (HTTP 409) instead of
 * overwriting, and drop whatever is still in flight when the page goes away.
 *
 * The page keeps its draft fields, its template and its registration with the dirty guard
 * (`useDraftGuard(dirty, { save, discard: reset })`). It also decides when to load, usually
 * `onMounted(load)`. Pages whose lifecycle differs in more than the options below (several save
 * actions on one document, a document that depends on a route parameter, a re-authentication
 * proof) keep their own code.
 */
export interface SettingsDocumentOptions<T> {
  /**
   * Reads the document. It must be pure: it returns the document, with everything the page needs from
   * the same load in it (the features together with the profiles they refer to), and writes no page
   * state. The composable adopts the answer only while this load is still the current one; anything the
   * loader wrote itself would show for an obsolete load too. A loader that ignores `signal` is still
   * ignored once the page is gone or loads again.
   */
  load: (signal: AbortSignal) => Promise<T>;
  /** Sends the draft together with the version of `current`, the document that was loaded, and answers the stored document. */
  save: (current: T) => Promise<T>;
  /** Writes a document into the draft fields. Runs after every load and save and when the page resets. */
  fill: (current: T) => void;
  /** Whether the draft differs from `saved`. */
  dirty: (saved: T) => boolean;
  /**
   * Runs after the guards and before anything is sent. The page may mark its fields here; returning a
   * text refuses the save and shows the text next to the SaveBar.
   */
  beforeSave?: () => string | undefined;
  /**
   * Runs after a successful save, once the answer is in the draft and the save is over (`saving` is
   * false already). It is not part of the request: an exception it throws is no save failure, it
   * rejects `save()`. It is skipped when the page is gone.
   */
  onSaved?: (saved: T) => void;
  /**
   * Close the conflict note as soon as the next save starts. Without it the note stays while that save
   * runs; the save then replaces it with its own outcome (a conflict again, or none).
   * Reproduces an existing difference between the pages that use this composable (four close it, four
   * keep it), kept so that the extraction preserves behaviour; a candidate for removal once the owner
   * decides which one is wanted.
   */
  clearConflictOnSave?: boolean;
  /**
   * On a conflict the ConflictNote speaks alone; the SaveBar shows no error text for it.
   * Reproduces an existing difference between the pages that use this composable (two of the eight are
   * quiet, the others show the message), kept so that the extraction preserves behaviour; a candidate
   * for removal once the owner decides which one is wanted.
   */
  quietConflict?: boolean;
}

export interface SettingsDocument<T> {
  /** The document as the server stored it; its version is the one a save is based on. */
  saved: Ref<T | undefined>;
  loading: Ref<boolean>;
  loadError: Ref<string>;
  /** What the last load threw, of which `loadError` is the text; for a page that tells failures apart (a 404). */
  loadFailure: Ref<unknown>;
  saving: Ref<boolean>;
  saveError: Ref<string>;
  /** A save was answered with 409. The draft stays; `reloadAfterConflict` is the way out. */
  conflict: Ref<boolean>;
  dirty: ComputedRef<boolean>;
  /** A failed request ends up in `loadError`, not in a rejection. */
  load(): Promise<void>;
  /**
   * A failed request ends up in `saveError` and `conflict`, not in a rejection. An exception thrown by
   * `fill` or `onSaved` after the answer is stored does reject it: the document is stored, no save
   * error is shown, and the SaveBar simply follows the draft as it then is.
   */
  save(): Promise<void>;
  /** Puts the stored document back into the draft and clears the save error. */
  reset(): void;
  /** Closes the conflict note and loads again, which replaces the draft. */
  reloadAfterConflict(): void;
  /** For a request of the page's own that must not touch its state after the page is gone. */
  isDisposed(): boolean;
}

export function useSettingsDocument<T>(options: SettingsDocumentOptions<T>): SettingsDocument<T> {
  const saved = ref<T>() as Ref<T | undefined>;
  const loading = ref(true);
  const loadError = ref("");
  const loadFailure = shallowRef<unknown>();
  const saving = ref(false);
  const saveError = ref("");
  const conflict = ref(false);
  const dirty = computed(() => !!saved.value && options.dirty(saved.value));
  let disposed = false;
  /** Counts loads; a load that is no longer the latest, or whose page is gone, has nothing to say. */
  let attempt = 0;
  let request: AbortController | undefined;

  function reset() {
    const current = saved.value;
    if (!current) return;
    options.fill(current);
    saveError.value = "";
  }
  function adopt(next: T) {
    saved.value = next;
    reset();
  }
  async function load() {
    if (disposed) return;
    const mine = ++attempt;
    request?.abort();
    const controller = (request = new AbortController());
    loading.value = true;
    loadError.value = "";
    loadFailure.value = undefined;
    try {
      const result = await options.load(controller.signal);
      if (mine === attempt) adopt(result);
    } catch (failure) {
      if (mine === attempt) {
        loadError.value = errorMessage(failure);
        loadFailure.value = failure;
      }
    } finally {
      if (mine === attempt) loading.value = false;
    }
  }
  async function save() {
    const current = saved.value;
    if (!current || saving.value) return;
    const refusal = options.beforeSave?.();
    if (refusal !== undefined) {
      saveError.value = refusal;
      return;
    }
    saving.value = true;
    saveError.value = "";
    if (options.clearConflictOnSave) conflict.value = false;
    let stored: T;
    try {
      stored = await options.save(current);
    } catch (failure) {
      if (!disposed) {
        conflict.value = failure instanceof ApiError && failure.status === 409;
        saveError.value = conflict.value && options.quietConflict ? "" : errorMessage(failure);
      }
      return;
    } finally {
      saving.value = false;
    }
    // Outside the try: once the answer is stored, a callback that throws must not be reported as a failed save.
    if (disposed) return;
    adopt(stored);
    options.onSaved?.(stored);
  }
  function reloadAfterConflict() {
    conflict.value = false;
    void load();
  }

  onBeforeUnmount(() => {
    disposed = true;
    attempt++;
    request?.abort();
  });
  return { saved, loading, loadError, loadFailure, saving, saveError, conflict, dirty, load, save, reset, reloadAfterConflict, isDisposed: () => disposed };
}
