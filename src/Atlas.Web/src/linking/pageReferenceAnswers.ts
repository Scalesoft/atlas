// The answers of POST /api/pages/references, apart from editorDirectories so that the shell can seed and forget them
// without pulling the editor's page and people services onto the library's first screen (bundle budget).
interface Titled { readonly id: string; readonly title: string; readonly type?: string }

/**
 * Answers kept for 30 s and shared by every renderer: the static page and the live editor built hidden under it (O10) asked for the same
 * links twice. The page's bundle seeds them (`?bundle=true`); a change of access forgets them all.
 */
export const referenceAnswers = new Map<string, { at: number; page: Titled | null }>();
export function seedPageReferences(pages: readonly Titled[]) {
  const at = Date.now();
  for (const page of pages) referenceAnswers.set(page.id.toLowerCase(), { at, page });
}
/** Ids whose check is on its way: a renderer asking meanwhile joins it instead of sending the same ids again. */
export const referencesOnTheWay = new Map<string, Promise<Map<string, Titled>>>();
export function forgetPageReferences() { referenceAnswers.clear(); referencesOnTheWay.clear(); }
