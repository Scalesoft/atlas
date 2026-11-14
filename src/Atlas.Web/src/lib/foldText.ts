/**
 * Lower-cases text and drops its diacritics, so "Příliš žluťoučký" compares equal to "prilis zlutoucky".
 * The one fold behind accent-insensitive search, matching and slugs.
 *
 * `marks` says which combining marks go: every Unicode mark (the default), or only the Combining
 * Diacritical Marks block U+0300-U+036F. They agree on Latin text; the block form keeps the vowel
 * signs and points of other scripts. Callers that always used the block form keep it.
 */
export function foldText(text: string, marks: "all" | "block" = "all"): string {
  const stripped = text.normalize("NFD").replace(marks === "all" ? /\p{M}/gu : /[\u0300-\u036f]/g, "");
  return stripped.toLocaleLowerCase("cs");
}
