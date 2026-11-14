import { foldText } from "../lib/foldText";

/**
 * Type-ahead of a list or tree: the index of the next label (after `current`, wrapping) that starts with `typed`, case and
 * diacritics aside. While the typed letters still fit the current label it stays (typing „ct“ on Čtení keeps it). -1: none.
 */
export function typeAheadIndex(labels: readonly string[], current: number, typed: string): number {
  const prefix = foldText(typed);
  if (!prefix) return -1;
  const fits = (index: number) => foldText(labels[index] ?? "").startsWith(prefix);
  if (prefix.length > 1 && fits(current)) return current;
  for (let step = 1; step <= labels.length; step++) {
    const index = (current + step) % labels.length;
    if (fits(index)) return index;
  }
  return -1;
}

/** Collects letters typed within `ms` of each other into one type-ahead prefix. */
export function typeAheadBuffer(ms = 700) {
  let typed = "", at = 0;
  return (event: KeyboardEvent, now = performance.now()): string | undefined => {
    if (event.key.length !== 1 || event.ctrlKey || event.metaKey || event.altKey || event.isComposing || (event.key === " " && !typed)) return undefined;
    typed = now - at > ms ? event.key : typed + event.key;
    at = now;
    return typed;
  };
}

/**
 * The tab pattern on a `role="tablist"` (bind to its keydown): ←/→ move to the previous/next enabled tab and open it,
 * Home/End to the first/last; it wraps around. `tab` picks the tabs of a row that is no tablist (Knihovna's links).
 */
export function tablistKeys(event: KeyboardEvent, tab = '[role="tab"]'): void {
  if (event.defaultPrevented || !["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key) || event.altKey || event.ctrlKey || event.metaKey) return;
  const list = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
  const tabs = [...(list?.querySelectorAll<HTMLElement>(tab) ?? [])].filter(item => !(item as HTMLButtonElement).disabled && item.getAttribute("aria-disabled") !== "true");
  const at = tabs.indexOf((event.target as Element | null)?.closest<HTMLElement>(tab) as HTMLElement);
  if (!tabs.length || at < 0) return;
  const next = event.key === "Home" ? 0 : event.key === "End" ? tabs.length - 1 : (at + (event.key === "ArrowRight" ? 1 : -1) + tabs.length) % tabs.length;
  event.preventDefault();
  tabs[next]!.focus();
  tabs[next]!.click();
}

/**
 * A plain vertical list of links and buttons (the rail's Knihovna, Nedávné…; bind to its keydown): ↑/↓ move to the
 * previous/next enabled item, Home/End to the first/last; no wrapping. Tab still enters and leaves item by item.
 */
export function listKeys(event: KeyboardEvent): void {
  if (event.defaultPrevented || !["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key) || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
  const list = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
  const items = [...(list?.querySelectorAll<HTMLElement>("a[href], button:not(:disabled)") ?? [])];
  const at = items.indexOf(document.activeElement as HTMLElement);
  if (at < 0) return;
  const next = event.key === "Home" ? 0 : event.key === "End" ? items.length - 1 : Math.max(0, Math.min(items.length - 1, at + (event.key === "ArrowDown" ? 1 : -1)));
  event.preventDefault();
  items[next]!.focus();
}

const focusable = 'a[href], button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex="0"]';

/** The keys of „Co s tím jde dělat“ everywhere (P2): Ctrl+Space and Alt+Enter as in the editor, Ctrl+., Shift+F10 and the menu key. */
export function opensActions(event: KeyboardEvent): boolean {
  const bare = !event.metaKey && !event.shiftKey;
  return (event.key === "F10" && event.shiftKey && !event.ctrlKey && !event.altKey) || event.key === "ContextMenu"
    || (bare && event.ctrlKey && !event.altKey && (event.code === "Space" || event.key === " " || event.key === "."))
    || (bare && event.altKey && !event.ctrlKey && event.key === "Enter");
}

/**
 * Rows of several controls (a contact card per row; bind to the list's keydown): ↑/↓ go to the first control of the
 * previous/next row, Home/End to the first/last row; Tab keeps walking the controls of a row. Not from inside a text field,
 * where the arrows move the caret. A row that is itself focusable (carries a `tabindex`, a list with one Tab stop) is focused as a whole.
 */
export function rowKeys(event: KeyboardEvent, rowSelector: string): void {
  if (event.defaultPrevented || !["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key) || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
  if ((event.target as Element | null)?.closest?.('input, textarea, select, [contenteditable="true"]')) return;
  const list = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
  const rows = [...(list?.querySelectorAll<HTMLElement>(rowSelector) ?? [])];
  const at = rows.findIndex(row => row.contains(document.activeElement));
  if (at < 0) return;
  const next = event.key === "Home" ? 0 : event.key === "End" ? rows.length - 1 : Math.max(0, Math.min(rows.length - 1, at + (event.key === "ArrowDown" ? 1 : -1)));
  const target = rows[next]!.hasAttribute("tabindex") ? rows[next]! : rows[next]!.querySelector<HTMLElement>(focusable);
  if (!target) return;
  event.preventDefault();
  target.focus();
}

/**
 * Cards in a wrapping grid (Knihovna's spaces; bind to the grid's keydown): ←/→ go to the previous/next card, ↑/↓ to the card
 * above/below (the nearest in the row above/below), Home/End to the first/last; focus goes to the card's first control.
 */
export function cardKeys(event: KeyboardEvent, cardSelector: string): void {
  if (event.defaultPrevented || !["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End"].includes(event.key) || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
  const grid = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
  const cards = [...(grid?.querySelectorAll<HTMLElement>(cardSelector) ?? [])];
  const at = cards.findIndex(card => card.contains(document.activeElement));
  if (at < 0) return;
  let next = event.key === "Home" ? 0 : event.key === "End" ? cards.length - 1 : event.key === "ArrowLeft" ? at - 1 : event.key === "ArrowRight" ? at + 1 : -1;
  if (event.key === "ArrowUp" || event.key === "ArrowDown") {
    // Layout positions, not the screen's: cards still rising in (a transform) stand in their rows already.
    const boxes = cards.map(card => ({ top: card.offsetTop, left: card.offsetLeft })), here = boxes[at]!, down = event.key === "ArrowDown";
    const beyond = boxes.map((_, index) => index).filter(index => down ? boxes[index]!.top > here.top + 1 : boxes[index]!.top < here.top - 1);
    const row = beyond.length ? (down ? Math.min : Math.max)(...beyond.map(index => boxes[index]!.top)) : NaN;
    next = beyond.filter(index => boxes[index]!.top === row).sort((a, b) => Math.abs(boxes[a]!.left - here.left) - Math.abs(boxes[b]!.left - here.left))[0] ?? -1;
  }
  const target = cards[next]?.querySelector<HTMLElement>(focusable);
  if (!target) return;
  event.preventDefault();
  target.focus();
}

/**
 * One tab stop for a widget of many items (roving tabindex): the item that has it keeps it, else the first; the others are
 * reached with the arrows. Call again after the items change.
 */
export function rovingInit(root: Element | null | undefined, selector: string): void {
  const items = [...(root?.querySelectorAll<HTMLElement>(selector) ?? [])];
  const keeper = items.find(item => item.getAttribute("tabindex") === "0") ?? items[0];
  for (const item of items) item.tabIndex = item === keeper ? 0 : -1;
}

/** Follows the focus (bind to the root's focusin): the item of `selector` the event is on becomes the one Tab stop of `root`. */
export function rovingTo(event: FocusEvent, root: Element | null | undefined, selector: string): void {
  const target = event.target instanceof HTMLElement && event.target.matches(selector) ? event.target : null;
  if (!target) return;
  for (const item of root?.querySelectorAll<HTMLElement>(selector) ?? []) item.tabIndex = item === target ? 0 : -1;
}

/**
 * A table of links as a grid (bind to the table's keydown, after `rovingInit(table, "a[href]")`): ←/→ along the row,
 * ↑/↓ to the nearest row with a link in the same column, Home/End to the row's ends, Ctrl+Home/End to the table's.
 */
export function gridKeys(event: KeyboardEvent): void {
  if (event.defaultPrevented || event.altKey || event.shiftKey || !["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) return;
  const table = event.currentTarget instanceof HTMLTableElement ? event.currentTarget : (event.currentTarget as Element | null)?.closest?.("table");
  const current = (event.target as Element | null)?.closest?.<HTMLElement>("a[href]");
  const cell = current?.closest<HTMLTableCellElement>("td, th");
  if (!table || !current || !cell) return;
  const rows = [...table.querySelectorAll<HTMLTableRowElement>("tbody tr")];
  const links = (row: Element) => [...row.querySelectorAll<HTMLElement>("a[href]")];
  const row = cell.parentElement as HTMLTableRowElement;
  let target: HTMLElement | undefined;
  if (event.ctrlKey || event.metaKey) {
    if (event.key === "Home") target = links(table)[0];
    else if (event.key === "End") target = links(table).at(-1);
  } else if (event.key === "Home" || event.key === "End") {
    target = event.key === "Home" ? links(row)[0] : links(row).at(-1);
  } else if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
    const inRow = links(row);
    target = inRow[inRow.indexOf(current) + (event.key === "ArrowRight" ? 1 : -1)];
  } else {
    const step = event.key === "ArrowDown" ? 1 : -1;
    for (let index = rows.indexOf(row) + step; index >= 0 && index < rows.length && !target; index += step)
      target = rows[index]!.cells[cell.cellIndex]?.querySelector<HTMLElement>("a[href]") ?? undefined;
  }
  event.preventDefault();
  if (!target) return;
  current.tabIndex = -1;
  target.tabIndex = 0;
  target.focus();
}

/**
 * Focuses the first `selector` in `root` as soon as it exists, checking once a frame for at most `frames` frames: a field that a
 * component re-creates (the comment composer after a send) or mounts after loading. Resolves whether it got focus.
 */
export function focusWhenPresent(root: Element, selector: string, frames = 30): Promise<boolean> {
  return new Promise(resolve => {
    const attempt = (left: number) => {
      const target = root.querySelector<HTMLElement>(selector);
      if (target) { target.focus({ preventScroll: true }); resolve(document.activeElement === target); return; }
      if (left <= 0) { resolve(false); return; }
      requestAnimationFrame(() => attempt(left - 1));
    };
    attempt(frames);
  });
}

/**
 * Programmatic focus without a ring: `focus()` on an input or after a key press matches :focus-visible, so a page opened by a click
 * showed the title or filter framed. The ring stays hidden (shell.css, `data-quiet-focus`) until the reader presses a key or leaves.
 */
export function focusQuietly(target: HTMLElement, options: FocusOptions = { preventScroll: true }) {
  target.setAttribute("data-quiet-focus", "");
  const end = () => { target.removeAttribute("data-quiet-focus"); target.removeEventListener("blur", end); document.removeEventListener("keydown", end, true); };
  target.addEventListener("blur", end);
  document.addEventListener("keydown", end, true);
  target.focus(options);
}

/**
 * The logical place after a navigation (a page, a space, Knihovna, settings): the element marked `data-focus-landing`, else the
 * view's first heading, else the container. Only when focus is nowhere (on the body, on the container itself, or gone with the
 * old view): where the reader already is (the tree link they pressed, a field) stays. A heading becomes focusable without a tab stop.
 */
export function landFocus(container: HTMLElement | null | undefined): boolean {
  const active = document.activeElement;
  if (!container || (active && active !== document.body && active !== container && active.isConnected)) return false;
  const target = container.querySelector<HTMLElement>("[data-focus-landing]") ?? container.querySelector<HTMLElement>("h1") ?? container;
  if (!target.hasAttribute("tabindex") && !target.matches("a[href], button, input, select, textarea")) target.tabIndex = -1;
  focusQuietly(target);
  // A text field lands with the caret at its end (a page's title): typing continues the text instead of starting it anew.
  if (target instanceof HTMLTextAreaElement) target.setSelectionRange(target.value.length, target.value.length);
  return document.activeElement === target;
}

/**
 * landFocus for a view that renders its landing after loading (real data: Kdo má přístup ~1 s). It lands now (`allowed` says whether a
 * menu, a dialog or the editor's own caret forbids it) and, until the view has a landing place (`data-focus-landing` or a heading) or
 * the reader is somewhere in it, again whenever the view changes: the view is watched, no timer chases it (P7). The reader's own key
 * or click ends the wait; the returned function ends it too.
 */
export function landFocusWhenRendered(container: HTMLElement, allowed: () => boolean = () => true): () => void {
  /** True once it is settled: it was allowed to try and the view has its landing place, or the reader is already somewhere in it. */
  const attempt = () => {
    if (!allowed()) return false;
    landFocus(container);
    return !!container.querySelector("[data-focus-landing], h1")
      || (!!document.activeElement && document.activeElement !== document.body && document.activeElement !== container);
  };
  let stop = () => {};
  if (attempt()) return stop;
  const observer = new MutationObserver(() => { if (attempt()) stop(); });
  observer.observe(container, { childList: true, subtree: true });
  // Capturing, so a key or click the view stops does not outlive the wait.
  const reader = () => stop();
  document.addEventListener("keydown", reader, true);
  document.addEventListener("pointerdown", reader, true);
  stop = () => { observer.disconnect(); document.removeEventListener("keydown", reader, true); document.removeEventListener("pointerdown", reader, true); stop = () => {}; };
  return () => stop();
}
