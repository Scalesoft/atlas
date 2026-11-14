// Which blocks this browser folded (heading sections, requirement blocks): the reader's own view (contract J10.1), kept by the
// block's page and stable id (`pageId#headingId`: a heading id is only unique within its page, never its text), never
// written into the document, Yjs or awareness. Storage may be missing (a private window): the folds then last as long as the page.
const KEY = "atlas.folds2";
/** 3.6 before the page prefix: bare ids that cannot tell which page they were for, so they are dropped. */
const BARE = "atlas.folds";
/** 3.5's keys: headings as `page#headingId` (or `page#t:text`), requirements by bare id (dropped). */
const OLD_HEADINGS = "atlas.headings.collapsed";
const OLD_REQUIREMENTS = "atlas.requirements.collapsed";
// shortcut: the most recent 2 000 folds; older ones open again.
const MAX = 2000;
let folded: Set<string> | undefined;
const listeners = new Set<() => void>();

function read(key: string): string[] {
  try {
    const value: unknown = JSON.parse(localStorage.getItem(key) ?? "[]");
    return Array.isArray(value) ? value.filter((id): id is string => typeof id === "string" && !!id) : [];
  } catch { return []; }
}
function save() {
  try { localStorage.setItem(KEY, JSON.stringify([...folded!].slice(-MAX))); return true; } catch { return false; }
}

/** The folds, read once; the first time (no `atlas.folds2` yet) the 3.5 headings move in as they are (`page#id`); a text key had no id, a bare id no page. */
function load() {
  if (folded) return folded;
  let fresh = false;
  try { fresh = localStorage.getItem(KEY) === null; } catch { /* no storage */ }
  folded = new Set(read(KEY));
  if (fresh) {
    for (const entry of read(OLD_HEADINGS)) {
      const at = entry.indexOf("#");
      if (at > 0 && entry.length > at + 1 && !entry.startsWith("t:", at + 1)) folded.add(entry);
    }
    // The old keys are removed once their folds are stored under the new one.
    if (save()) try { for (const old of [OLD_HEADINGS, OLD_REQUIREMENTS, BARE]) localStorage.removeItem(old); } catch { /* kept */ }
  }
  return folded;
}

export const isFolded = (id: string | null | undefined) => !!id && load().has(id);

/** Folds or opens many blocks at once („Sbalit vše“): one write, one redraw. */
export function foldAll(ids: Iterable<string | null | undefined>, collapsed: boolean) {
  const set = load();
  let changed = false;
  for (const id of ids) {
    if (!id || set.has(id) === collapsed) continue;
    set.delete(id);
    if (collapsed) set.add(id);
    changed = true;
  }
  if (!changed) return;
  save();
  listeners.forEach(listener => listener());
}

export const setFolded = (id: string | null | undefined, collapsed: boolean) => foldAll([id], collapsed);

/** Redraws a surface whenever a fold changes; returns the unsubscribe. */
export function onFoldChange(listener: () => void) {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

/** Tests only: forget what was read, so the next call reads (and migrates) storage again. */
export const resetFoldStateForTests = () => { folded = undefined; };
