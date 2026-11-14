import t from "../../i18n/settings-flow";
import { computed, getCurrentScope, inject, onScopeDispose, provide, ref, watch, type InjectionKey } from "vue";
import type { ReauthOperation, ReauthTarget } from "../../protection";

/**
 * Fresh sign-in before a security change (spec 5.3). A local account re-enters its own password;
 * a Hub account goes through a forced new sign-in in a popup. When the provider cannot prove a fresh
 * sign-in the state is `unsupported` and the change is blocked: there is no silent fallback.
 *
 * The environment is injected: the settings host passes the V4 API (security/apiReauth.ts), the
 * password references the challenge of their release (secrets/secretReauth.ts). A verification
 * answers with the proof id the change is then sent with (X-Atlas-Reauth).
 *
 * The server keeps a ten-minute window after a verification (owner decision 28. 9.): until
 * `windowUntil`, security changes of the session need no new sign-in, so `windowOpen` asks for none
 * and the change goes out without a proof. The server stays the judge; its reauth_required answer
 * closes the window and the next confirmation signs in again. A release has no window.
 */
export type ReauthMethod = "local" | "hub";
export type ReauthState = "idle" | "verifying" | "failed" | "unsupported" | "verified";

/** What is being authorized: the operation and its scope (no target: the global security configuration). */
export interface ReauthRequest {
  operation: ReauthOperation;
  target: ReauthTarget;
}
export type ReauthFailureReason = "password" | "failed" | "popup" | "expired" | "unsupported" | "throttled";
/** true/false for environments without a proof of their own (the release challenge is the proof). */
export type ReauthOutcome = boolean | { ok: true; proofId: string } | { ok: false; reason: ReauthFailureReason; message?: string };

export interface ReauthEnvironment {
  method: ReauthMethod;
  /** False when the sign-in provider cannot prove a fresh sign-in. Blocks every change needing one. */
  supported: boolean;
  /** Why it is unsupported, in the owner's words; a default text otherwise. */
  unsupportedReason?: string;
  /** The Hub (provider) name for the waiting line. */
  providerName?: string;
  /** A caveat shown before verifying, e.g. a provider whose support is not known yet. */
  notice?: string;
  /** Epoch milliseconds until which the server's re-authentication window covers security changes; absent or null: none (always
   * for a release, which asks before every value). */
  windowUntil?: number | null;
  /** `request` is absent for an environment bound to one challenge already (a release). */
  verifyPassword(password: string, request?: ReauthRequest): Promise<ReauthOutcome>;
  verifyHub(request?: ReauthRequest): Promise<ReauthOutcome>;
}

const key: InjectionKey<ReauthEnvironment> = Symbol("reauth-environment");

const unconfigured: ReauthEnvironment = {
  method: "local",
  supported: false,
  verifyPassword: async () => false,
  verifyHub: async () => false,
};

export function provideReauthEnvironment(environment: ReauthEnvironment) {
  provide(key, environment);
}

export function useReauth(environment: ReauthEnvironment = inject(key, unconfigured)) {
  const state = ref<ReauthState>(environment.supported ? "idle" : "unsupported");
  const failures = ref(0);
  const proofId = ref<string | null>(null);
  const failure = ref("");
  /** The server said "nepodporováno" during this attempt, even though the environment expected support. */
  const refused = ref("");
  watch(
    () => environment.supported,
    (supported) => {
      if (!supported) state.value = "unsupported";
      else if (state.value === "unsupported" && !refused.value) state.value = "idle";
    },
  );
  // The window closes by itself at its end, so a confirmation left open asks for the sign-in again then.
  const now = ref(Date.now());
  let closing: ReturnType<typeof setTimeout> | undefined;
  watch(
    () => environment.windowUntil ?? null,
    (until) => {
      clearTimeout(closing);
      now.value = Date.now();
      if (until !== null && until > now.value) closing = setTimeout(() => (now.value = Date.now()), until - now.value);
    },
    { immediate: true },
  );
  if (getCurrentScope()) onScopeDispose(() => clearTimeout(closing));
  const windowOpen = computed(() => {
    const until = environment.windowUntil;
    return until != null && now.value < until;
  });

  async function verify(password = "", request: ReauthRequest = { operation: "security", target: {} }): Promise<boolean> {
    if (!environment.supported || refused.value) {
      state.value = "unsupported";
      return false;
    }
    if (state.value === "verifying") return false;
    state.value = "verifying";
    failure.value = "";
    proofId.value = null;
    let outcome: ReauthOutcome;
    try {
      outcome = environment.method === "local" ? await environment.verifyPassword(password, request) : await environment.verifyHub(request);
    } catch {
      outcome = { ok: false, reason: "failed" };
    }
    if (outcome === true || (typeof outcome === "object" && outcome.ok)) {
      proofId.value = typeof outcome === "object" ? outcome.proofId : null;
      state.value = "verified";
      return true;
    }
    const reason: ReauthFailureReason = typeof outcome === "object" ? outcome.reason : environment.method === "local" ? "password" : "failed";
    const message = (typeof outcome === "object" && outcome.message) || t.reauthFailures[reason];
    if (reason === "unsupported") {
      refused.value = message;
      state.value = "unsupported";
    } else {
      failure.value = message;
      failures.value++;
      state.value = "failed";
    }
    return false;
  }

  function reset() {
    proofId.value = null;
    failure.value = "";
    state.value = environment.supported && !refused.value ? "idle" : "unsupported";
  }

  return {
    state,
    failures,
    proofId,
    failure,
    method: computed(() => environment.method),
    supported: computed(() => environment.supported && !refused.value),
    providerName: computed(() => environment.providerName),
    notice: computed(() => environment.notice),
    unsupportedReason: computed(() => refused.value || environment.unsupportedReason),
    /** A security change now needs no sign-in: the session re-authenticated in the last ten minutes. */
    windowOpen,
    windowUntil: computed(() => environment.windowUntil ?? null),
    verify,
    reset,
  };
}
