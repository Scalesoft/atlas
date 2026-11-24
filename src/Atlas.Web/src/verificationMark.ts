/**
 * The quiet mark of a search result or a link chip (plan 5.2 §3.3): a shield for a verified page, a warning one for an expired one, nothing
 * otherwise (the order of results does not change, O5). Apart from pageVerification.ts, which carries the texts of the whole feature, so
 * the shell and the editor's page links import it without them (bundle budget); the wording is the shell's.
 */
export function verificationMark(state: string | null | undefined) {
  if (state === "verified" || state === "expiring") return { icon: "shield-check", warn: false };
  if (state === "expired") return { icon: "shield-alert", warn: true };
  return null;
}
