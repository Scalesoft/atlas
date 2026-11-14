import { ApiError, errorMessage } from "../../../api";
import t from "../../../i18n/settings-security";
import { reauthRequiredCode } from "../../../protection";

/** 409 of a CAS token (expectedVersion): someone changed the same configuration meanwhile. */
export function isConflict(error: unknown) {
  return error instanceof ApiError && error.status === 409 && error.code !== "proxy_lockout";
}

/** The text of a version conflict, read when shown. */
export const conflictMessage = () => t.errors.conflict;

/** A refused security change in the owner's words; the server's own sentence where it has one. */
export function securityErrorText(error: unknown) {
  if (error instanceof ApiError) {
    if (error.code === reauthRequiredCode) return t.errors.reauthExpired;
    if (error.code === "proxy_lockout") return t.errors.proxyLockout(error.message);
  }
  return errorMessage(error);
}
