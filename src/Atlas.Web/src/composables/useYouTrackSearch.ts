import { onScopeDispose, ref } from 'vue';
import type { YouTrackIssue } from '@atlas/markdown/plugins/youtrack';
import { api, type Space } from '../api';

/** Asks the integration for the issues of one space; a component that has no server (the gallery) passes its own. */
export type YouTrackIssueFetcher = (query: string, spaceId: string, signal: AbortSignal) => Promise<YouTrackIssue[]>;
/** The ids of the spaces the account reads in which the integration is set up. */
export type YouTrackSpacesFetcher = () => Promise<string[]>;
const searchOnServer: YouTrackIssueFetcher = (query, spaceId, signal) => api<YouTrackIssue[]>(`/integrations/youtrack/search?q=${encodeURIComponent(query)}&spaceId=${encodeURIComponent(spaceId)}`, { signal });
const spacesOnServer: YouTrackSpacesFetcher = () => api<string[]>('/integrations/youtrack/spaces');

export function useYouTrackSearch(fetchIssues: YouTrackIssueFetcher = searchOnServer, fetchSpaces: YouTrackSpacesFetcher = spacesOnServer) {
  const issues = ref<(YouTrackIssue & { spaceId: string; spaceName: string })[]>([]);
  const pending = ref(0), failed = ref(0), attempted = ref(false);
  let controller: AbortController | undefined;
  // UX SE3-2: read once per search session, so a space without the integration costs no request per query; a failed read asks
  // every space, as before.
  let configured: Promise<ReadonlySet<string> | null> | undefined;
  function clear() { issues.value = []; failed.value = 0; attempted.value = false; }
  /** `keepShown` cancels the requests but leaves the shown issues until the next search replaces them. */
  function stop(keepShown = false) {
    controller?.abort(); controller = undefined; pending.value = 0;
    if (keepShown) return;
    configured = undefined;
    clear();
  }
  function search(query: string, spaces: Pick<Space, 'id' | 'name'>[]) {
    stop(true);
    const abort = controller = new AbortController();
    configured ??= fetchSpaces().then(ids => new Set(ids), () => null);
    void configured.then(ids => { if (!abort.signal.aborted) run(query, ids ? spaces.filter(space => ids.has(space.id)) : spaces, abort); });
  }
  function run(query: string, spaces: Pick<Space, 'id' | 'name'>[], abort: AbortController) {
    if (!spaces.length) { controller = undefined; clear(); return; }
    attempted.value = true;
    if (query.length > 256) { issues.value = []; failed.value = spaces.length; return; }
    pending.value = spaces.length;
    const queue = [...spaces], known = new Set<string>();
    // The previous issues stay on screen until the first space answers, so typing does not empty the list.
    let replaced = false;
    const replace = () => { if (!replaced) { replaced = true; issues.value = []; failed.value = 0; } };
    // Bound outgoing requests, but publish each space as soon as it completes.
    async function worker() {
      while (queue.length && !abort.signal.aborted) {
        const space = queue.shift()!;
        try {
          const found = await fetchIssues(query, space.id, abort.signal);
          if (abort.signal.aborted) return;
          replace();
          for (const issue of found) {
            const url = new URL(issue.url);
            if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password || known.has(url.href)) continue;
            known.add(url.href);
            issues.value.push({ ...issue, url: url.href, spaceId: space.id, spaceName: space.name });
          }
        } catch {
          if (!abort.signal.aborted) { replace(); failed.value++; }
        } finally {
          if (!abort.signal.aborted) pending.value--;
        }
      }
    }
    for (let i = 0; i < Math.min(3, spaces.length); i++) void worker();
  }
  onScopeDispose(() => stop());
  return { issues, pending, failed, attempted, search, stop };
}
