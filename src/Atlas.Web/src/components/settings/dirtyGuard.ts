import { inject, onBeforeUnmount, provide, ref, watch, type InjectionKey, type Ref } from "vue";

/**
 * Unsaved changes of the page on stage. A page with a SaveBar registers its save and discard;
 * the shell asks before leaving (Zůstat / Zahodit / Uložit) instead of blocking navigation.
 * A page without its own save through the guard (an older panel with a form of its own) registers
 * only `discard` or nothing; the question then offers Zůstat / Zahodit.
 */
export interface DirtyHandlers {
  save?: () => Promise<void> | void;
  discard?: () => void;
}
export interface DirtyGuard {
  dirty: Ref<boolean>;
  canSave: Ref<boolean>;
  register(handlers: DirtyHandlers): void;
  release(): void;
  save(): Promise<void>;
  discard(): void;
}

const key: InjectionKey<DirtyGuard> = Symbol("settings-dirty-guard");

export function createDirtyGuard(): DirtyGuard {
  const dirty = ref(false);
  const canSave = ref(false);
  let handlers: DirtyHandlers | undefined;
  return {
    dirty,
    canSave,
    register(next) {
      handlers = next;
      canSave.value = !!next.save;
    },
    release() {
      handlers = undefined;
      canSave.value = false;
      dirty.value = false;
    },
    async save() {
      await handlers?.save?.();
    },
    discard() {
      handlers?.discard?.();
      dirty.value = false;
    },
  };
}

export function provideDirtyGuard(guard: DirtyGuard) {
  provide(key, guard);
}
export function useDirtyGuard(): DirtyGuard | undefined {
  return inject(key, undefined);
}

/**
 * Hands a page's unsaved draft to the dirty guard of its surface: the settings shell asks
 * Zůstat / Zahodit / Uložit before leaving, the first-run module wizard keeps its steps closed.
 * Without a guard (a component mounted on its own) it does nothing. Returns the guard so a page can
 * register other handlers while, for example, a wizard with its own confirmation is open.
 */
export function useDraftGuard(dirty: Readonly<Ref<boolean>>, handlers: DirtyHandlers) {
  const guard = useDirtyGuard();
  watch(dirty, (value) => {
    if (guard) guard.dirty.value = value;
  });
  guard?.register(handlers);
  onBeforeUnmount(() => guard?.release());
  return guard;
}
