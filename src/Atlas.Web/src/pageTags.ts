export interface PageTag { id: string; name: string }
export function cleanTagName(value: string) { return value.normalize('NFKC').trim().replace(/\s+/gu, ' '); }
export function tagKey(value: string) { return cleanTagName(value).toLowerCase(); }
export function tagTone(value: string) {
  let hash = 0;
  for (const char of tagKey(value)) hash = (hash * 31 + char.codePointAt(0)!) | 0;
  return ['blue', 'violet', 'teal', 'amber', 'rose'][Math.abs(hash) % 5];
}
