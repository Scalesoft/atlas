import { onBeforeUnmount } from "vue";

/**
 * Answers the requests a real component makes from local data while an example is on screen (the gallery sends no request to the
 * server): call it in the example's setup, before the component mounts. A path that is not listed goes on to the real fetch.
 */
export function useGalleryFetch(routes: Record<string, () => unknown>) {
  const original = globalThis.fetch;
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    const route = Object.keys(routes).find(path => url.includes(path));
    return route ? new Response(JSON.stringify(routes[route]!()), { status: 200, headers: { "content-type": "application/json" } }) : original(input, init);
  }) as typeof fetch;
  onBeforeUnmount(() => { globalThis.fetch = original; });
}
