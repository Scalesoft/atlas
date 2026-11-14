/**
 * "Je to jen příklad": spans the author marked as an example instead of a real secret, per page, for the browser
 * session. Only a FNV-1a hash of the span is kept, never its text, so nothing here can reveal the value.
 */
export interface SecretExamples {
  mark(pageId: string, value: string): void;
  isExample(pageId: string, value: string): boolean;
}

export function createSecretExamples(): SecretExamples {
  const pages = new Map<string, Set<string>>();
  return {
    mark(pageId, value) {
      let hashes = pages.get(pageId);
      if (!hashes) pages.set(pageId, hashes = new Set());
      hashes.add(fnv1a(value));
    },
    isExample(pageId, value) {
      return pages.get(pageId)?.has(fnv1a(value)) ?? false;
    },
  };
}

/** One memory for the session, shared by the editor suggestions (H2) and the publication check (E). */
export const sessionSecretExamples: SecretExamples = createSecretExamples();

/** 32-bit FNV-1a over the UTF-8 bytes, as 8 hex digits. */
export function fnv1a(value: string): string {
  let hash = 0x811c9dc5;
  for (const byte of new TextEncoder().encode(value)) {
    hash ^= byte;
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16).padStart(8, "0");
}
