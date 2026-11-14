/**
 * The pages read ahead (App.vue): each entry is a whole page bundle (document and Yjs state), so the map never keeps what is no longer
 * usable: every new read first drops the entries older than `ttl`, and only the newest few stay.
 * shortcut: expired entries leave with the next read ahead (no timer); the last few payloads stay until then.
 */
export class PagePrefetchMap<V extends { at: number }> extends Map<string, V> {
  constructor(private readonly ttl: number, private readonly max = 3) { super(); }
  override set(key: string, value: V) {
    for (const [id, entry] of this) if (Date.now() - entry.at >= this.ttl) this.delete(id);
    super.delete(key);
    super.set(key, value);
    for (const id of this.keys()) { if (this.size <= this.max) break; this.delete(id); }
    return this;
  }
}
