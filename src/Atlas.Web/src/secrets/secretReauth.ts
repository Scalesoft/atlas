import { shallowRef, type ShallowRef } from "vue";
import t from "../i18n/secrets";
import type { ReauthEnvironment } from "../components/settings/useReauth";
import { reauthReturnUrl, reauthWindowName, waitForReauthPopup } from "../reauthPopup";
import { secretsApi, type ReauthStart, type SecretsApi } from "./secretsApi";
import type { SecretReauthPrompt, SecretReauthRequest } from "./secretsHost";

/**
 * Fresh sign-in before a release (WP4 V4 contract, V6 client): POST /api/security/reauth starts a challenge bound to the
 * secret and the operation; a local account then confirms its password, a Hub account signs in again in a popup. The
 * challenge id is the one-time proof the release is retried with. The dialog (SecretReauthDialog) renders the shared
 * ConfirmWithReauth through a ReauthEnvironment built here; nothing about the password outlives the attempt.
 */
export interface PendingSecretReauth {
  request: SecretReauthRequest;
  challenge: ReauthStart;
  environment: ReauthEnvironment;
  confirm(): void;
  cancel(): void;
}

export interface SecretReauthController {
  pending: ShallowRef<PendingSecretReauth | null>;
  prompt: SecretReauthPrompt;
}

/** Opens the Hub sign-in and waits until the popup closes or returns; the server then says whether the challenge was verified. */
export async function hubSignIn(challenge: ReauthStart, api: Pick<SecretsApi, "reauthStatus"> = secretsApi, open: typeof window.open = (...args) => window.open(...args)) {
  const popup = challenge.redirectUrl ? open(challenge.redirectUrl, reauthWindowName, "popup,width=520,height=720") : null;
  if (!popup) return false;
  await waitForReauthPopup(popup, new Date(challenge.expiresAt).getTime() || Date.now() + 300_000);
  return api.reauthStatus(challenge.id).then(status => status.state === "verified", () => false);
}

export function createSecretReauth(api: SecretsApi = secretsApi): SecretReauthController {
  const pending = shallowRef<PendingSecretReauth | null>(null);
  const prompt: SecretReauthPrompt = async request => {
    if (pending.value) throw new Error(t.reauthBusy);
    const returnUrl = reauthReturnUrl;
    const challenge = await api.startReauth(request.secretId, request.operation, returnUrl);
    return new Promise<string>((resolve, reject) => {
      const finish = () => {
        request.signal.removeEventListener("abort", abort);
        if (pending.value?.challenge.id === challenge.id) pending.value = null;
      };
      const abort = () => { finish(); reject(new DOMException(t.reauthCanceled, "AbortError")); };
      request.signal.addEventListener("abort", abort, { once: true });
      pending.value = {
        request,
        challenge,
        environment: {
          method: challenge.method === "password" ? "local" : "hub",
          supported: request.requirement.supported,
          verifyPassword: password => api.verifyPassword(challenge.id, password),
          verifyHub: () => hubSignIn(challenge, api),
        },
        confirm: () => { finish(); resolve(challenge.id); },
        cancel: abort,
      };
    });
  };
  return { pending, prompt };
}
