import { detectSecrets, type TextSpan } from "./rules";

const credentialWord = /^(?:hesl\p{L}*|password\p{L}*|passwd|pwd|token\p{L}*|secret\p{L}*|login\p{L}*|api|klíč\p{L}*|key|je|is|a|na|do|pro|the|for)$/iu;

/**
 * The search text the SecretPicker opens with for „Přesunout do trezoru“: a few words of the block around the
 * detected value, never the value. Every span `detectSecrets` finds in the block is cut out first, credential words
 * are dropped, and a word that still looks like a secret on its own is dropped too, so nothing of a password reaches
 * the secrets search (GET /api/secrets/search) or the dialog.
 */
export function secretSearchText(blockText: string, spans: readonly TextSpan[] = detectSecrets(blockText).map(hit => hit.span)): string {
  let rest = "";
  let at = 0;
  for (const span of [...spans].sort((a, b) => a.start - b.start)) {
    rest += `${blockText.slice(at, Math.max(at, span.start))} `;
    at = Math.max(at, span.end);
  }
  rest += blockText.slice(at);
  const words = (rest.match(/[\p{L}\p{N}][\p{L}\p{N}.-]*/gu) ?? [])
    .map(word => word.replace(/[.-]+$/, ""))
    .filter(word => word.length >= 3 && !credentialWord.test(word) && detectSecrets(word).length === 0);
  return words.slice(0, 4).join(" ").slice(0, 120);
}
