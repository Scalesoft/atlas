import { SEARCH_PAGE_SIZE } from "./searchRequest";

/**
 * Restoring a scrolled view: the batches up to the selected row are independent (the server's next offset is always offset + limit),
 * so they are fetched together, not one after another. Fewer than two missing batches are left to the one-by-one loading.
 */
export async function loadBatchesTogether<T>(nextOffset: number | null, loaded: number, index: number, fetchBatch: (offset: number) => Promise<T>): Promise<T[]> {
  const batches = Math.ceil((index + 1 - loaded) / SEARCH_PAGE_SIZE);
  if (nextOffset === null || batches < 2) return [];
  return Promise.all(Array.from({ length: batches }, (_, i) => fetchBatch(nextOffset + i * SEARCH_PAGE_SIZE)));
}
