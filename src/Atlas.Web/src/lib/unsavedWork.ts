/**
 * Work typed but not saved yet outside the page's editor (P3 „Nic se neztratí potichu“), in one place the shell can ask before the
 * tab closes or the reader leaves (`hasUnsavedWork`):
 * - `drafts`: what a component keeps by a key when it goes away, to show again when it comes back (a comment by its object and
 *   anchor, a note's refused change), written through a `draftKeeper`;
 * - `holdUnsavedWork`: a view that holds unsaved work of its own while it is open (a board's queue, a comment being typed).
 * The tab's memory only: a draft never outlives the tab, whose closing asks first, nor the account it was typed under
 * (`forgetUnsavedWork` on a session reset).
 */
export const drafts = new Map<string, unknown>();
const holders = new Set<() => boolean>();
let session = 0;

/** Counts `dirty` as unsaved work until the returned release is called (when the view goes). */
export function holdUnsavedWork(dirty: () => boolean) {
  holders.add(dirty);
  return () => { holders.delete(dirty); };
}

/** Keeps (`undefined`: drops) a draft by its key for the account signed in when the keeper was taken: a view still open, or a save
 * still running, when that account's session ends keeps nothing for the next account in the tab. */
export function draftKeeper() {
  const taken = session;
  return (key: string, value: unknown) => {
    if (taken !== session) return;
    if (value === undefined) drafts.delete(key);
    else drafts.set(key, value);
  };
}

/** The account's session ended (logout, a session the server ended): its drafts go with it. The shell calls it on its session reset. */
export function forgetUnsavedWork() {
  session++;
  drafts.clear();
}

export const hasUnsavedWork = () => drafts.size > 0 || [...holders].some(dirty => dirty());
