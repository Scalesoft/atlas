import { inject, provide, ref, watch, type InjectionKey, type Ref } from "vue";
import type { SettingsRouter } from "./useSettingsRoute";

/**
 * The result line after a flow ("Trezor je přidaný"), shown by the shell above the page the flow
 * lands on. It belongs to that page: it is bound to the route right after the flow navigates there
 * and disappears as soon as the user moves elsewhere or dismisses it. `highlight` marks the item the
 * flow created so its list row or card can glow once.
 */
export interface SettingsFlashMessage {
  key: number;
  text: string;
  icon: string;
  path?: string;
}
export interface SettingsFlash {
  message: Ref<SettingsFlashMessage | null>;
  highlight: Ref<string | null>;
  notify(text: string, options?: { icon?: string; highlight?: string }): void;
  dismiss(): void;
}

const key: InjectionKey<SettingsFlash> = Symbol("settings-flash");

export function createSettingsFlash(router: SettingsRouter): SettingsFlash {
  const message = ref<SettingsFlashMessage | null>(null);
  const highlight = ref<string | null>(null);
  let counter = 0;
  watch(
    () => router.route.value.path,
    (path) => {
      const current = message.value;
      if (!current) return;
      if (!current.path) current.path = path;
      else if (current.path !== path) {
        message.value = null;
        highlight.value = null;
      }
    },
    { flush: "sync" },
  );
  return {
    message,
    highlight,
    notify(text, options = {}) {
      // Bound to the page it is shown on: the next route change (the flow's own navigation) sets it.
      message.value = { key: ++counter, text, icon: options.icon ?? "circle-check" };
      highlight.value = options.highlight ?? null;
      queueMicrotask(() => {
        if (message.value?.key === counter && !message.value.path) message.value.path = router.route.value.path;
      });
    },
    dismiss() {
      message.value = null;
      highlight.value = null;
    },
  };
}

export function provideSettingsFlash(flash: SettingsFlash) {
  provide(key, flash);
}
export function useSettingsFlash(): SettingsFlash {
  const flash = inject(key, undefined);
  if (!flash) throw new Error("useSettingsFlash() needs provideSettingsFlash() in an ancestor");
  return flash;
}
/** For components that also work outside a settings surface (the page's access view, the gallery). */
export function useOptionalSettingsFlash(): SettingsFlash | undefined {
  return inject(key, undefined);
}
