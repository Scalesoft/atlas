/**
 * localStorage for a remembered choice, which must never stop the page: blocked or unavailable storage (private mode,
 * disabled site data) reads as empty and ignores writes, so the choice then lasts the visit.
 */
/** The storage object itself for code that takes one as a parameter; undefined where access throws. */
export function browserStorage(): Storage | undefined {
  try { return globalThis.localStorage; } catch { return undefined; }
}

export function removeStored(key: string): void {
  try { localStorage.removeItem(key); } catch { /* the choice lasts the visit */ }
}

export function readStored(key: string): string | null {
  try { return localStorage.getItem(key); } catch { return null; }
}

export function writeStored(key: string, value: string): void {
  try { localStorage.setItem(key, value); } catch { /* the choice lasts the visit */ }
}
