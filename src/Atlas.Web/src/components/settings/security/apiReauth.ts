import { ApiError } from "../../../api";
import t from "../../../i18n/settings-security";
import { freshAuthUnsupportedCode, reauthApi, reauthWindow, type ReauthStart, type ReauthStatus } from "../../../protection";
import { openReauthPopup, reauthReturnUrl, waitForReauthPopup } from "../../../reauthPopup";
import type { ReauthEnvironment, ReauthOutcome, ReauthRequest } from "../useReauth";
import { unsupportedReason, type SecurityState } from "./securityState";

/**
 * The fresh sign-in of the real settings on the V4 API: POST /api/security/reauth starts a challenge bound to the operation and
 * its scope, then the local password (…/{id}/password) or the Hub popup verifies it. The challenge id is the proof the change
 * carries; the password never outlives the attempt. The method and support come from security/me. A verification (and security/me)
 * also reports the ten-minute re-authentication window, during which further changes of the session need no sign-in
 * (`windowUntil`, shared through protection.ts reauthWindow).
 */
type Api = Pick<typeof reauthApi, "start" | "password" | "status">;
const globalChange: ReauthRequest = { operation: "security", target: {} };

function failureOf(error: unknown): ReauthOutcome {
  if (error instanceof ApiError) {
    if (error.code === freshAuthUnsupportedCode) return { ok: false, reason: "unsupported", message: error.message };
    if (error.code === "reauth_password") return { ok: false, reason: "password" };
    if (error.status === 429) return { ok: false, reason: "throttled", message: error.message };
    if (error.status === 404) return { ok: false, reason: "expired" };
    return { ok: false, reason: "failed", message: error.message };
  }
  return { ok: false, reason: "failed" };
}

function statusOutcome(start: ReauthStart, status: ReauthStatus): ReauthOutcome {
  if (status.state === "verified") {
    // The verification also opens the ten-minute window of further security changes in this session.
    reauthWindow.sync(status.windowExpiresAt);
    return { ok: true, proofId: start.id };
  }
  if (status.failure === "auth_time_missing" || status.failure === "auth_time_stale")
    return {
      ok: false,
      reason: "unsupported",
      message: t.reauth.unsupported(start.providerName ?? "Hub"),
    };
  if (status.state === "expired") return { ok: false, reason: "expired" };
  if (status.state === "pending") return { ok: false, reason: "failed", message: t.reauth.notCompleted };
  return { ok: false, reason: "failed" };
}

export function createApiReauthEnvironment(security: SecurityState | undefined, api: Api = reauthApi, open?: typeof window.open): ReauthEnvironment {
  const me = () => security?.me.value ?? null;
  return {
    get method() {
      return me()?.freshAuthMethod === "oidc" ? "hub" : "local";
    },
    get supported() {
      return me()?.freshAuth !== "unsupported";
    },
    get unsupportedReason() {
      return unsupportedReason(me());
    },
    get notice() {
      return me()?.freshAuth === "unknown" ? t.reauth.notice : undefined;
    },
    get windowUntil() {
      return reauthWindow.until.value;
    },
    providerName: undefined,
    async verifyPassword(password: string, request: ReauthRequest = globalChange) {
      try {
        const start = await api.start(request.operation, request.target);
        const status = await api.password(start.id, password);
        return statusOutcome(start, status);
      } catch (error) {
        return failureOf(error);
      }
    },
    async verifyHub(request: ReauthRequest = globalChange) {
      // Opened inside the click, before any await, or the browser blocks it.
      const popup = openReauthPopup(open);
      try {
        const start = await api.start(request.operation, request.target, reauthReturnUrl);
        if (!popup) return { ok: false, reason: "popup" };
        if (!start.redirectUrl) {
          popup.close();
          return { ok: false, reason: "failed" };
        }
        popup.location.href = start.redirectUrl;
        await waitForReauthPopup(popup, new Date(start.expiresAt).getTime() || Date.now() + 300_000);
        return statusOutcome(start, await api.status(start.id));
      } catch (error) {
        popup?.close();
        return failureOf(error);
      }
    },
  };
}

export interface SelfTestResult {
  status: ReauthStatus | null;
  providers: import("../../../protection").FreshAuthProvider[];
  /** Why the test did not run through (popup blocked, not linked to the provider). */
  error: string;
}

/**
 * "Otestovat nové přihlášení" (WikiAdmin): signs in again at the provider in a popup, releases nothing, and records whether the
 * provider proved a fresh login (auth_time). Answers the challenge state and the per-provider verdicts afterwards.
 */
export async function runFreshAuthSelfTest(
  providerId?: string,
  api: Pick<typeof reauthApi, "startSelfTest" | "status" | "selfTests"> = reauthApi,
  open?: typeof window.open,
): Promise<SelfTestResult> {
  const popup = openReauthPopup(open);
  try {
    const start = await api.startSelfTest(providerId, reauthReturnUrl);
    if (start.method === "password" || !start.redirectUrl) {
      popup?.close();
      return { status: null, providers: await api.selfTests(), error: t.reauth.hubOnly };
    }
    if (!popup) return { status: null, providers: await api.selfTests(), error: t.reauth.popupBlocked };
    popup.location.href = start.redirectUrl;
    await waitForReauthPopup(popup, new Date(start.expiresAt).getTime() || Date.now() + 300_000);
    const [status, providers] = await Promise.all([api.status(start.id), api.selfTests()]);
    return { status, providers, error: "" };
  } catch (error) {
    popup?.close();
    return { status: null, providers: await api.selfTests().catch(() => []), error: error instanceof Error ? error.message : t.reauth.testFailed };
  }
}
