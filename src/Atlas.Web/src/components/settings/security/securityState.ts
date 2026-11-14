import { computed, inject, provide, ref, type ComputedRef, type InjectionKey, type Ref } from "vue";
import { errorMessage } from "../../../api";
import t from "../../../i18n/settings-security";
import { reauthWindow } from "../../../protection";
import { securityApi, type SecurityCapabilities } from "../../../security";

/**
 * What the signed-in account may do in the security centre (GET /api/security/me): global security, which vaults it manages,
 * and whether it can prove a fresh sign-in (freshAuth). Loaded once per settings surface; the server still authorizes every call.
 */
export interface SecurityState {
  me: Ref<SecurityCapabilities | null>;
  error: Ref<string>;
  loaded: Ref<boolean>;
  /** Networks, proxies and the instance default: every wiki administrator. */
  globalAdmin: ComputedRef<boolean>;
  load(): Promise<void>;
}

const key: InjectionKey<SecurityState> = Symbol("security-state");

export function createSecurityState(load: () => Promise<SecurityCapabilities> = securityApi.me): SecurityState {
  const me = ref<SecurityCapabilities | null>(null);
  const error = ref("");
  const loaded = ref(false);
  return {
    me,
    error,
    loaded,
    globalAdmin: computed(() => !!me.value?.globalSecurityAdmin),
    async load() {
      try {
        me.value = await load();
        // A re-authentication of this session in the last ten minutes (another tab, before a reload) still spares the password.
        reauthWindow.sync(me.value.reauthWindowExpiresAt);
        error.value = "";
      } catch (failure) {
        error.value = errorMessage(failure);
      } finally {
        loaded.value = true;
      }
    },
  };
}

export function provideSecurityState(state: SecurityState) {
  provide(key, state);
}
/** Undefined outside a settings surface (the gallery, isolated tests). */
export function useSecurityState(): SecurityState | undefined {
  return inject(key, undefined);
}

/** "nepodporováno" in the owner's words, per sign-in method. */
export function unsupportedReason(me: SecurityCapabilities | null): string | undefined {
  if (!me || me.freshAuth !== "unsupported") return undefined;
  if (me.freshAuthMethod === null)
    return t.errors.noMethod;
  return t.errors.hubNoTime;
}
